[CmdletBinding()]
param(
    [string]$EnrollmentToken,
    [Parameter(Mandatory)][string]$ServerUrl,
    [Parameter(Mandatory)][string]$CertificatePin,
    [string]$ComputerName = "192.168.0.201",
    [string]$UserName = "maksim",
    [switch]$SkipBuild
)

$ErrorActionPreference = "Stop"
$projectRoot = Split-Path -Parent $PSScriptRoot
if (-not $SkipBuild) { & (Join-Path $PSScriptRoot "build-agent.ps1") }
$package = Join-Path $projectRoot "artifacts\agent"
$remoteStage = "C:/Users/$UserName/KidTimeDeploy"
$remoteStageWindows = "C:\Users\$UserName\KidTimeDeploy"

ssh "$UserName@$ComputerName" "cmd /c if not exist $remoteStageWindows mkdir $remoteStageWindows"
scp -r (Join-Path $package "*") "${UserName}@${ComputerName}:$remoteStage/"
$remoteArguments = @(
    "powershell", "-NoProfile", "-ExecutionPolicy", "Bypass",
    "-File", "$remoteStageWindows\install-service.ps1",
    "-Source", $remoteStageWindows,
    "-ServerUrl", $ServerUrl,
    "-CertificatePin", $CertificatePin
)
if (-not [string]::IsNullOrWhiteSpace($EnrollmentToken)) { $remoteArguments += @("-EnrollmentToken", $EnrollmentToken) }
ssh "$UserName@$ComputerName" @remoteArguments
