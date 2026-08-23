[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Source,
    [Parameter(Mandatory)][string]$ServerUrl,
    [string]$EnrollmentToken,
    [Parameter(Mandatory)][string]$CertificatePin,
    [string]$InstallPath = "C:\Program Files\KidTime"
)

$ErrorActionPreference = "Stop"
$principal = [Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw "Service installation requires an elevated administrator SSH session."
}

$serviceName = "KidTimeControl"
$existing = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
if ($existing) {
    Stop-Service -Name $serviceName -Force -ErrorAction SilentlyContinue
    $existing.WaitForStatus([ServiceProcess.ServiceControllerStatus]::Stopped, [TimeSpan]::FromSeconds(20))
}
$sessionAgents = @(Get-Process -Name "KidTime.SessionAgent" -ErrorAction SilentlyContinue)
$sessionAgents | Stop-Process -Force
if ($sessionAgents.Count -gt 0) {
    $sessionAgents | Wait-Process -Timeout 15 -ErrorAction SilentlyContinue
}

New-Item -ItemType Directory -Path $InstallPath -Force | Out-Null
$copied = $false
for ($attempt = 1; $attempt -le 5 -and -not $copied; $attempt++) {
    try {
        Copy-Item -Path (Join-Path $Source "*") -Destination $InstallPath -Recurse -Force
        $copied = $true
    }
    catch [System.IO.IOException] {
        if ($attempt -eq 5) { throw }
        Start-Sleep -Milliseconds 750
    }
}
$serviceExecutable = Join-Path $InstallPath "KidTime.ControlService.exe"

& icacls.exe $InstallPath /inheritance:r `
    /grant:r '*S-1-5-18:(OI)(CI)F' '*S-1-5-32-544:(OI)(CI)F' '*S-1-5-32-545:(OI)(CI)RX' | Out-Null
if ($LASTEXITCODE -ne 0) { throw "Could not harden the KidTime installation directory ACL." }
& icacls.exe $InstallPath /grant:r '*S-1-5-18:F' '*S-1-5-32-544:F' '*S-1-5-32-545:RX' /T /C | Out-Null
if ($LASTEXITCODE -ne 0) { throw "Could not propagate the hardened KidTime installation ACL." }
@($InstallPath) + @(Get-ChildItem -LiteralPath $InstallPath -Directory -Recurse | ForEach-Object FullName) | ForEach-Object {
    & icacls.exe $_ /inheritance:r `
        /grant:r '*S-1-5-18:(OI)(CI)F' '*S-1-5-32-544:(OI)(CI)F' '*S-1-5-32-545:(OI)(CI)RX' | Out-Null
}

$dataPath = "C:\ProgramData\KidTime"
New-Item -ItemType Directory -Path $dataPath -Force | Out-Null
& icacls.exe $dataPath /inheritance:r `
    /grant:r '*S-1-5-18:(OI)(CI)F' '*S-1-5-32-544:(OI)(CI)F' | Out-Null
if ($LASTEXITCODE -ne 0) { throw "Could not harden the KidTime data directory ACL." }
& icacls.exe $dataPath /grant:r '*S-1-5-18:F' '*S-1-5-32-544:F' /T /C | Out-Null
if ($LASTEXITCODE -ne 0) { throw "Could not propagate the hardened KidTime data ACL." }
@($dataPath) + @(Get-ChildItem -LiteralPath $dataPath -Directory -Recurse | ForEach-Object FullName) | ForEach-Object {
    & icacls.exe $_ /inheritance:r `
        /grant:r '*S-1-5-18:(OI)(CI)F' '*S-1-5-32-544:(OI)(CI)F' | Out-Null
}

$credentialPath = "C:\ProgramData\KidTime\device.credential"
if (-not (Test-Path -LiteralPath $credentialPath)) {
    if ([string]::IsNullOrWhiteSpace($EnrollmentToken)) { throw "An enrollment token is required for the first installation." }
    & $serviceExecutable enroll --server $ServerUrl --token $EnrollmentToken --pin $CertificatePin
    if ($LASTEXITCODE -ne 0) { throw "Agent enrollment failed with exit code $LASTEXITCODE." }
}

if (-not $existing) {
    & sc.exe create $serviceName binPath= ('"' + $serviceExecutable + '"') start= auto obj= LocalSystem DisplayName= "KidTime Control Service" | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "sc.exe create failed with exit code $LASTEXITCODE." }
}
& sc.exe description $serviceName "Enforces KidTime PC and application limits using cached offline rules." | Out-Null
& sc.exe failure $serviceName reset= 86400 actions= restart/5000/restart/15000/restart/60000 | Out-Null
& sc.exe failureflag $serviceName 1 | Out-Null
& sc.exe sdset $serviceName 'D:(A;;CCDCLCSWRPWPDTLOCRSDRCWDWO;;;SY)(A;;CCDCLCSWRPWPDTLOCRSDRCWDWO;;;BA)(A;;CCLCSWLORC;;;IU)' | Out-Null
if ($LASTEXITCODE -ne 0) { throw "Could not harden the KidTime service control ACL." }
Start-Service -Name $serviceName
(Get-Service -Name $serviceName).WaitForStatus([ServiceProcess.ServiceControllerStatus]::Running, [TimeSpan]::FromSeconds(20))
Write-Host "KidTimeControl is running."
