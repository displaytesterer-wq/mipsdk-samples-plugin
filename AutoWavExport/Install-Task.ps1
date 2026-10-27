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
$settings = New-ScheduledTaskSettingsSet -MultipleInstances IgnoreNew -ExecutionTimeLimit (New-TimeSpan -Hours 5) -StartWhenAvailable
# Run this script as the dedicated Windows account that owns the DPAPI secret.
# Interactive registration below uses that same user's credentials, not stored in GitHub.
Register-ScheduledTask -TaskName $TaskName -Action $action -Trigger $triggers -Settings $settings -Description 'XProtect scheduled microphone WAV export' -Force | Out-Null
Write-Host 'Task created. Configure Task Scheduler > General > Run whether user is logged on or not under the SAME account as exporter.secret.'
