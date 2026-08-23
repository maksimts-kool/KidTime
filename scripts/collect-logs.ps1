[CmdletBinding()]
param([string]$ComputerName = "192.168.0.201", [string]$UserName = "maksim", [int]$Lines = 200)
$remoteCommand = "Get-Content -Tail $Lines -LiteralPath 'C:\ProgramData\KidTime\logs\control-service.ndjson' -ErrorAction SilentlyContinue; Get-Content -Tail $Lines -LiteralPath 'C:\Users\$UserName\AppData\Local\KidTime\logs\session-agent.ndjson' -ErrorAction SilentlyContinue"
ssh "$UserName@$ComputerName" powershell -NoProfile -NonInteractive -Command $remoteCommand
