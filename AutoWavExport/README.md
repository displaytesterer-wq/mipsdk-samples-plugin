# AutoWavExport (pre-production)

Stand-alone, no Smart Client dependency. Based on the Milestone component AudioExportSample API (WAVExporter).

**Status: implementation prepared, not compiled against local XProtect 2025 R3 SDK and not tested on the user's server.** Do NOT enable the task until a one-microphone test passes.

## Behavior
- Login as XProtect Basic user `exporter` with local DPAPI-encrypted password.
- Enumerates microphone Items from the **physical/system-defined** SDK tree, without requiring a related camera.
- Exports microphones individually to WAV. A missed job can be rerun manually with `--end 2026-10-27T06:00:00`; the end time is the **local** six-hour boundary, not the start.
- Every invocation uses the last completed 6-hour boundary. Recommended Task Scheduler triggers: **00:30, 06:30, 12:30, 18:30** local Windows time, because archive starts at 00/06/12/18. The 30-minute margin is not proof archiving completed.
- Only produces final file after exporter reports 100% and output exists. Does not overwrite existing files. Retries failures. Per-microphone failures do not stop other devices. Log file in `OutputRoot\_logs`.
- Output `MicrophoneName__FullDeviceGUID\yyyy-MM-dd\HHmm-HHmm.wav`. A full GUID prevents collisions.

## Microphone identification
No Hardware/camera relationship or manual mapping is required. The current SDK microphone name plus full device GUID identifies each folder. Inventory is written to `microphones.csv` with ID, current name and folder. An append-only `device_history.csv` records first-observed names and timestamps, and historical re-export selects the last name observed before the requested window ended. Since this is polling and not a Milestone audit event, it does NOT establish the exact rename instant. Earlier unknown periods use `MIC_<GUID>_historical_unknown` rather than falsely attributing a location. The initially observed name is used for the just-completed window (within 2 hours). On renaming, the full GUID links old and new folders.

## Build/install
1. Install compatible MIP SDK and Visual Studio/.NET Framework 4.7.2 targeting pack on a development workstation. Restore/build `AutoWavExport.csproj` targeting x64. Ensure MIP SDK exporter native dependencies are available on the server.
2. Publish/copy executable and required SDK assemblies to `C:\MilestoneAutoWav`. Copy `settings.example.json` as `settings.json` and replace management URL, storage location; **do not commit settings.json**.
3. On the Windows server, in Windows PowerShell 5.1 as the Windows account running the task, run `Set-Password.ps1`. Do not paste password into chat, GitHub or script. Keep the same task-running account to decrypt DPAPI.
4. Run `AutoWavExport.exe --config C:\MilestoneAutoWav\settings.json --inventory` to list allowed microphones' GUIDs. Review the generated `microphones.csv`. If zero microphones are shown, verify role and hierarchy.
5. Keep `DryRun: true` initially and test `--end 2026-10-27T06:00:00`. Then change DryRun to false, perform a single microphone pilot (`--device <guid>`), verify playable WAV, duration, dates and permissions.
6. Run `Install-Task.ps1` from elevated PowerShell **under the same Windows account** and configure Task Scheduler `Run whether user is logged on or not`; this may require entering that Windows account password. Confirm Task history and permissions.
7. Ensure recording retention covers delayed retries, and put exports on a volume **other than** the recording database volume.

## Limitations
- Exporter consumes recorded media via SDK rather than directly reading storage/archive directories. Exact transition behavior must be tested on site.
- A 6-hour window is requested; missing recorded audio within a window cannot be filled by the exporter.
- Exact WAV codec availability varies by installed SDK; empty Codec means first codec in available codec list.
- Avoid excessive concurrent exports on the recording server (this version exports **sequentially**).
- Archive processing may exceed the 30-minute delay; inspect logs and reschedule as needed.
- No confirmed build or real-server test; treat as pilot release.
- Settings file is deliberately local; storage and secret ACLs should restrict access.

## Outstanding pre-production verification
- Compile against the exact 2025 R3 MIP SDK; validate BuildCredentialCache, microphone discovery and exporter APIs.
- Validate actual Basic login URL/certificate and exported WAV output naming/duration with one unrelated microphone.
- Test first scheduled run, archive boundary, rename, historical retry, DST/timezone, duplicate name/path and incomplete output handling.
- Verify the actual archive completion delay, recording retention, disk capacity, access-control lists and unattended Windows task identity.
- Add a durable missed-interval recovery queue and monitoring/alerting before production: a 4-hour blocked export can cause later windows to be skipped.
- User has not supplied Management Server endpoint, local export disk size or microphone count.
