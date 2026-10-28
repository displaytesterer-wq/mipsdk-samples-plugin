param(
    [string]$Exe = 'C:\MilestoneAutoWav\AutoWavExport.exe',
    [string]$Settings = 'C:\MilestoneAutoWav\settings.json',
    [string]$TaskName = 'Milestone-AutoWavExport'
)
$ErrorActionPreference = 'Stop'
if (!(Test-Path $Exe)) { throw "EXE not found: $Exe" }
if (!(Test-Path $Settings)) { throw "Settings not found: $Settings" }
$action = New-ScheduledTaskAction -Execute $Exe -Argument ('--config "' + $Settings + '"') -WorkingDirectory (Split-Path $Exe)
$triggers = @('00:30','06:30','12:30','18:30') | ForEach-Object { New-ScheduledTaskTrigger -Daily -At $_ }
# ExecutionTimeLimit (5 цаг) нь settings.json-ийн MaxRunMinutes-ээс их байх ёстой (дээд 285, анхдагч 270).
# PowerShell-д хувьсагчийн нэр том жижиг үсэг ялгадаггүй: $settings нь [string]$Settings параметртэй давхцаж
# string болж хувирдаг байсан. Тиймээс $taskSettings гэж нэрлэв.
$taskSettings = New-ScheduledTaskSettingsSet -MultipleInstances IgnoreNew -ExecutionTimeLimit (New-TimeSpan -Hours 5) -StartWhenAvailable
# Энэ скриптийг XProtect role-д нэмсэн тусгай Windows account-аар ажиллуулна (Basic горимд DPAPI файлыг үүсгэсэн account).
# Доорх бүртгэл тэр хэрэглэгчийн нэвтрэх мэдээллийг ашиглана; GitHub-д хадгалагдахгүй.
Register-ScheduledTask -TaskName $TaskName -Action $action -Trigger $triggers -Settings $taskSettings -Description 'XProtect scheduled microphone WAV export' -Force | Out-Null
Write-Host 'Task created. In Task Scheduler > General choose "Run whether user is logged on or not" for the XProtect Windows account, and leave "Do not store password" UNCHECKED (network login needs it).'
