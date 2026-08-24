[CmdletBinding()]
param(
    [string]$AdminEmail = "parent@kidtime.local",
    [string]$AdminPassword,
    [string]$AgentUrl = "https://${env:COMPUTERNAME}:5081"
)

$ErrorActionPreference = "Stop"
$projectRoot = Split-Path -Parent $PSScriptRoot
$envPath = Join-Path $projectRoot ".env"
$certificateDirectory = Join-Path $projectRoot ".data\certs"
$certificatePath = Join-Path $certificateDirectory "kidtime.pfx"
$pinPath = Join-Path $certificateDirectory "kidtime.sha256"

function New-Secret([int]$bytes = 36) {
    $buffer = [byte[]]::new($bytes)
    [Security.Cryptography.RandomNumberGenerator]::Fill($buffer)
    return [Convert]::ToBase64String($buffer).Replace("+", "-").Replace("/", "_").TrimEnd("=")
}

if (Test-Path -LiteralPath $envPath) {
    Write-Host "KidTime server configuration already exists at $envPath"
    exit 0
}

if ([string]::IsNullOrWhiteSpace($AdminPassword)) {
    $AdminPassword = New-Secret 18
}

New-Item -ItemType Directory -Force -Path $certificateDirectory | Out-Null
$postgresPassword = New-Secret
$jwtKey = New-Secret 48
$certificatePassword = New-Secret 24

dotnet dev-certs https -ep $certificatePath -p $certificatePassword | Out-Null
$certificate = [Security.Cryptography.X509Certificates.X509CertificateLoader]::LoadPkcs12FromFile(
    $certificatePath,
    $certificatePassword,
    [Security.Cryptography.X509Certificates.X509KeyStorageFlags]::EphemeralKeySet)
$pin = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($certificate.RawData))
[IO.File]::WriteAllText($pinPath, $pin)

$configuration = @(
    "POSTGRES_PASSWORD=$postgresPassword"
    "KIDTIME_JWT_KEY=$jwtKey"
    "KIDTIME_ADMIN_EMAIL=$AdminEmail"
    "KIDTIME_ADMIN_PASSWORD=$AdminPassword"
    "KIDTIME_CERT_PASSWORD=$certificatePassword"
    "KIDTIME_AGENT_URL=$AgentUrl"
) -join [Environment]::NewLine
[IO.File]::WriteAllText($envPath, $configuration + [Environment]::NewLine)

Write-Host "KidTime server configuration created."
Write-Host "Parent email: $AdminEmail"
Write-Host "The generated parent password is stored only in .env."
Write-Host "Certificate pin written to .data\certs\kidtime.sha256."
Write-Host "Windows agent enrollment URL: $AgentUrl"
