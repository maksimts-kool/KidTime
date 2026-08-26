# Builds a Windows agent release and publishes it on the server in one command.
#
# A release used to be three commands: build here, copy the result over, then run the publish
# script there. This wraps the same three steps, and the version bump every release needs with
# them - without a bump the update manifest never overtakes what the PCs already run, so the
# release quietly reaches nobody.
#
# It runs on the Windows build machine, because build-agent.ps1 has to. The server side needs
# only an SSH login: nothing is assumed to be checked out there, so the publish script travels
# with the payload.
[CmdletBinding()]
param(
    # user@host of the Ubuntu server. Remembered after the first run, so later releases are just
    # ./scripts/release-agent.ps1
    [string]$Server,
    [ValidateSet("major", "minor", "patch", "none")][string]$Bump = "patch",
    # An explicit version wins over -Bump, for a release that has to carry a chosen number.
    [string]$Version,
    [ValidateSet("Debug", "Release")][string]$Configuration = "Release",
    [string]$ReleaseDirectory = "/opt/kidtime/releases",
    # Build only. Useful for checking a release before it can reach any controlled PC.
    [switch]$SkipPublish
)

$ErrorActionPreference = "Stop"
$projectRoot = Split-Path -Parent $PSScriptRoot
$versionFile = Join-Path $projectRoot "Directory.Build.props"
$targetFile = Join-Path $projectRoot "artifacts\deploy-target.txt"

function Invoke-Step([string]$Description, [scriptblock]$Action) {
    Write-Host "==> $Description" -ForegroundColor Cyan
    # A stale exit code from an earlier command would otherwise fail the first step that runs no
    # native process of its own.
    $global:LASTEXITCODE = 0
    & $Action
    if ($LASTEXITCODE -ne 0) { throw "$Description failed with exit code $LASTEXITCODE." }
}

function Resolve-Server {
    if ($Server) { return $Server }
    if ($env:KIDTIME_SERVER) { return $env:KIDTIME_SERVER }
    if (Test-Path -LiteralPath $targetFile) {
        $saved = (Get-Content -LiteralPath $targetFile -Raw).Trim()
        if ($saved) { return $saved }
    }
    throw "No server was given. Run it once with -Server user@host (or set KIDTIME_SERVER); it is remembered after that."
}

# The agent compares the published manifest version with its own assembly version, so a release
# that reuses the current number is invisible to every PC already running it.
function Set-ReleaseVersion {
    $content = Get-Content -LiteralPath $versionFile -Raw
    $match = [regex]::Match($content, '<Version>(?<value>[^<]+)</Version>')
    if (-not $match.Success) { throw "Directory.Build.props does not contain a <Version> element." }
    $current = $match.Groups['value'].Value.Trim()

    if ($Version) { $next = $Version }
    elseif ($Bump -eq "none") { return $current }
    else {
        $parsed = $null
        if (-not [Version]::TryParse($current, [ref]$parsed)) { throw "The current version '$current' is not a version number." }
        $major = $parsed.Major
        $minor = [Math]::Max($parsed.Minor, 0)
        $patch = [Math]::Max($parsed.Build, 0)
        switch ($Bump) {
            "major" { $major++; $minor = 0; $patch = 0 }
            "minor" { $minor++; $patch = 0 }
            "patch" { $patch++ }
        }
        $next = "$major.$minor.$patch"
    }

    $parsedNext = $null
    if (-not [Version]::TryParse($next, [ref]$parsedNext)) { throw "'$next' is not a version number." }
    if ($next -ne $current) {
        $updated = $content -replace '<Version>[^<]+</Version>', "<Version>$next</Version>"
        [IO.File]::WriteAllText($versionFile, $updated)
        Write-Host "Version $current -> $next" -ForegroundColor Green
    }
    return $next
}

if (-not $SkipPublish) {
    foreach ($tool in @("ssh", "scp")) {
        if (-not (Get-Command $tool -ErrorAction SilentlyContinue)) {
            throw "$tool was not found. Install the Windows OpenSSH client, or use -SkipPublish to build only."
        }
    }
}

$target = if ($SkipPublish) { $null } else { Resolve-Server }
$releaseVersion = Set-ReleaseVersion

Invoke-Step "Building the agent, update package, and setup executable" {
    & (Join-Path $PSScriptRoot "build-agent.ps1") -Configuration $Configuration
}

# Relative, forward-slash paths from the project root: scp reads a Windows path as a remote target
# the moment it can, and the drive letter's colon is exactly what it looks for.
Push-Location -LiteralPath $projectRoot
try {
    $uploads = @(
        "artifacts/releases/latest.json",
        "artifacts/releases/kidtime-agent-$releaseVersion.zip",
        "artifacts/releases/KidTimeSetup.exe",
        "scripts/publish-agent-release.sh"
    )
    foreach ($file in $uploads) {
        if (-not (Test-Path -LiteralPath $file)) { throw "The build did not produce $file." }
    }

    if ($SkipPublish) {
        Write-Host "Agent $releaseVersion is in artifacts/releases. Publishing was skipped." -ForegroundColor Green
        return
    }

    $staging = "/tmp/kidtime-release"

    Invoke-Step "Preparing $target" {
        ssh $target "rm -rf '$staging' && mkdir -p '$staging'"
    }

    Invoke-Step "Uploading agent $releaseVersion" {
        scp @uploads "${target}:$staging/"
    }

    Invoke-Step "Publishing agent $releaseVersion" {
        # publish-agent-release.sh does the verifying: it refuses a package whose size or SHA-256
        # does not match the manifest, and writes latest.json last so the server never advertises
        # a release whose package has not fully landed. The staging directory is left in place if
        # it fails, so the upload can be inspected. The carriage-return strip covers a checkout
        # that ignored .gitattributes - a shell script with CRLF fails on its very first line.
        ssh $target "sed -i 's/\r`$//' '$staging/publish-agent-release.sh' && bash '$staging/publish-agent-release.sh' '$staging' --release-dir '$ReleaseDirectory' && rm -rf '$staging'"
    }
}
finally { Pop-Location }

New-Item -ItemType Directory -Path (Split-Path -Parent $targetFile) -Force | Out-Null
Set-Content -LiteralPath $targetFile -Value $target -Encoding utf8

Write-Host ""
Write-Host "Agent $releaseVersion is published on $target." -ForegroundColor Green
Write-Host "Controlled PCs install it on their next update check; nothing there has to be restarted."
Write-Host "Commit the version bump in Directory.Build.props together with the change it ships."
