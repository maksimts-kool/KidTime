[CmdletBinding()]
param([ValidateSet("Debug", "Release")][string]$Configuration = "Release")

$ErrorActionPreference = "Stop"
$projectRoot = Split-Path -Parent $PSScriptRoot
$output = Join-Path $projectRoot "artifacts\agent"
$sessionOutput = Join-Path $output "SessionAgent"
$releaseOutput = Join-Path $projectRoot "artifacts\releases"

foreach ($directory in @($output)) {
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
Write-Host "Self-contained agent $version and automatic update package were created in $releaseOutput"
