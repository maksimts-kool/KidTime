[CmdletBinding()]
param([string]$ComputerName = "192.168.0.201", [string]$UserName = "maksim")
$remoteCommand = "Restart-Service KidTimeControl; (Get-Service KidTimeControl).WaitForStatus('Running',[TimeSpan]::FromSeconds(20)); Get-Service KidTimeControl | Select-Object Name,Status"
ssh "$UserName@$ComputerName" powershell -NoProfile -NonInteractive -Command $remoteCommand
