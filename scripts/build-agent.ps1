# Builds the Windows agent, update package, and setup executable.
# This is the one script that has to run on Windows: the SessionAgent and Setup
# projects are WPF, and the single-file installer is assembled with IExpress.
[CmdletBinding()]
param([ValidateSet("Debug", "Release")][string]$Configuration = "Release")

$ErrorActionPreference = "Stop"
$projectRoot = Split-Path -Parent $PSScriptRoot
$output = Join-Path $projectRoot "artifacts\agent"
$sessionOutput = Join-Path $output "SessionAgent"
$releaseOutput = Join-Path $projectRoot "artifacts\releases"
$setupOutput = Join-Path $projectRoot "artifacts\setup"
$setupPayloadOutput = Join-Path $projectRoot "artifacts\setup-payload"

foreach ($directory in @($output, $setupOutput, $setupPayloadOutput)) {
    if (Test-Path -LiteralPath $directory) {
        $resolved = [IO.Path]::GetFullPath((Resolve-Path -LiteralPath $directory).Path)
        $expected = [IO.Path]::GetFullPath($directory)
        if (-not [string]::Equals($resolved, $expected, [StringComparison]::OrdinalIgnoreCase)) {
            throw "Refusing to clear unexpected build directory $resolved"
        }
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}

dotnet publish (Join-Path $projectRoot "src\KidTime.ControlService\KidTime.ControlService.csproj") `
    -c $Configuration -r win-x64 --self-contained true -o $output `
    -p:PublishSingleFile=true -p:DebugType=None -p:DebugSymbols=false
dotnet publish (Join-Path $projectRoot "src\KidTime.SessionAgent\KidTime.SessionAgent.csproj") `
    -c $Configuration -r win-x64 --self-contained true -o $sessionOutput `
    -p:PublishSingleFile=true -p:DebugType=None -p:DebugSymbols=false

$version = (& dotnet msbuild (Join-Path $projectRoot "src\KidTime.ControlService\KidTime.ControlService.csproj") -nologo -getProperty:Version).Trim()
$parsedVersion = $null
if (-not [Version]::TryParse($version, [ref]$parsedVersion)) { throw "Could not determine the agent version." }
New-Item -ItemType Directory -Path $releaseOutput -Force | Out-Null
$packageName = "kidtime-agent-$version.zip"
$packagePath = Join-Path $releaseOutput $packageName
Compress-Archive -Path (Join-Path $output "*") -DestinationPath $packagePath -CompressionLevel Optimal -Force
$package = Get-Item -LiteralPath $packagePath
$hash = (Get-FileHash -LiteralPath $packagePath -Algorithm SHA256).Hash.ToLowerInvariant()
[pscustomobject]@{
    version = $version
    fileName = $packageName
    sha256 = $hash
    sizeBytes = $package.Length
} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $releaseOutput "latest.json") -Encoding utf8

dotnet publish (Join-Path $projectRoot "src\KidTime.Setup\KidTime.Setup.csproj") `
    -c $Configuration -r win-x64 --self-contained true -o $setupPayloadOutput `
    -p:PublishSingleFile=false -p:DebugType=None -p:DebugSymbols=false `
    "-p:KidTimePayloadPath=$packagePath"
$setupApplication = Join-Path $setupPayloadOutput "KidTimeSetup.exe"
if (-not (Test-Path -LiteralPath $setupApplication)) { throw "KidTime Setup was not published." }
New-Item -ItemType Directory -Path $setupOutput -Force | Out-Null

# WPF native libraries do not load reliably from .NET 10's single-file bundle on all
# supported Windows builds. IExpress keeps the user-facing download to one EXE while
# running the normal, fully self-contained WPF publish from a temporary directory.
$setupExecutable = Join-Path $setupOutput "KidTimeSetup.exe"
$directivePath = Join-Path $setupOutput "KidTimeSetup.sed"
$payloadFiles = @(Get-ChildItem -LiteralPath $setupPayloadOutput -File | Sort-Object Name)
$directive = [Collections.Generic.List[string]]::new()
$directive.AddRange([string[]]@(
    "[Version]",
    "Class=IEXPRESS",
    "SEDVersion=3",
    "[Options]",
    "PackagePurpose=InstallApp",
    "ShowInstallProgramWindow=0",
    "HideExtractAnimation=1",
    "UseLongFileName=1",
    "InsideCompressed=0",
    "CAB_FixedSize=0",
    "CAB_ResvCodeSigning=0",
    "RebootMode=N",
    "InstallPrompt=",
    "DisplayLicense=",
    "FinishMessage=",
    "TargetName=$setupExecutable",
    "FriendlyName=KidTime Setup",
    "AppLaunched=KidTimeSetup.exe",
    "PostInstallCmd=<None>",
    "AdminQuietInstCmd=KidTimeSetup.exe",
    "UserQuietInstCmd=KidTimeSetup.exe",
    "SourceFiles=SourceFiles",
    "[Strings]"
))
for ($index = 0; $index -lt $payloadFiles.Count; $index++) {
    $directive.Add("FILE$index=`"$($payloadFiles[$index].Name)`"")
}
$directive.Add("[SourceFiles]")
$directive.Add("SourceFiles0=$setupPayloadOutput\")
$directive.Add("[SourceFiles0]")
for ($index = 0; $index -lt $payloadFiles.Count; $index++) {
    $directive.Add("%FILE$index%=")
}
[IO.File]::WriteAllLines($directivePath, $directive, [Text.Encoding]::ASCII)

$iexpress = Join-Path $env:SystemRoot "System32\iexpress.exe"
$iexpressProcess = Start-Process -FilePath $iexpress -ArgumentList @("/N", "/Q", $directivePath) -Wait -PassThru -WindowStyle Hidden
if ($iexpressProcess.ExitCode -ne 0 -or -not (Test-Path -LiteralPath $setupExecutable)) {
    throw "The single-file KidTime Setup package could not be created."
}
Remove-Item -LiteralPath $directivePath -Force
Copy-Item -LiteralPath $setupExecutable -Destination (Join-Path $releaseOutput "KidTimeSetup.exe") -Force
Write-Host "Self-contained agent $version, automatic update package, and KidTimeSetup.exe were created in $releaseOutput"
Write-Host "Upload that directory to the Ubuntu server and publish it with scripts/publish-agent-release.sh"
