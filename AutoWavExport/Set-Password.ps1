param(
    [string]$Output = 'C:\ProgramData\MilestoneAutoWav\exporter.secret'
)
$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Force -Path (Split-Path $Output) | Out-Null
$secret = Read-Host 'Milestone exporter Basic User password' -AsSecureString
# DPAPI protects it for this Windows user on this computer.
$secret | ConvertFrom-SecureString | Set-Content -Path $Output -Encoding ASCII
Write-Host 'Saved encrypted credential for current Windows account.'
