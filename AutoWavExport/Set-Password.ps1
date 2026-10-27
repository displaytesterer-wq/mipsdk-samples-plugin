param(
    [string]$Output = 'C:\ProgramData\MilestoneAutoWav\exporter.secret'
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Security
New-Item -ItemType Directory -Force -Path (Split-Path $Output) | Out-Null
$secret = Read-Host 'Milestone exporter Basic User password' -AsSecureString
$bstr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secret)
try {
    $plain = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($bstr)
    $bytes = [Text.Encoding]::Unicode.GetBytes($plain)
    try {
        $cipher = [Security.Cryptography.ProtectedData]::Protect($bytes, $null, [Security.Cryptography.DataProtectionScope]::CurrentUser)
        ([BitConverter]::ToString($cipher) -replace "-", "") | Set-Content -Path $Output -Encoding ascii
    } finally { [Array]::Clear($bytes,0,$bytes.Length) }
} finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr) }
Write-Host 'Encrypted for current Windows account; run scheduled task as this same account.'
