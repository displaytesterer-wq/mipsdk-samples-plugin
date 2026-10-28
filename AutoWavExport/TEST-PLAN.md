# Release gate: XProtect Expert 2025 R3

**This is an unexecuted test plan. NO real XProtect server tests have been performed.**

## Static/build checks
- Restore and build x64 against compatible Milestone SDK and .NET Framework 4.7.2.
- Verify actual compiler APIs, including login, item hierarchy, WAVExporter fields and exporter file-name behavior.
- Run test fixtures for valid PCM WAV, truncated RIFF, missing data, incorrect chunk size and corrupt WAV. Test WAV codecs returned by the server.
- Verify password helper and Task Scheduler task identity on the same Windows account.
- Check config values: BackfillDays 1-90, MaxIntervalsPerRun 1-100, MinimumFreeGB positive; Retention deletion disabled by default.

## On-server checks (to be run by operator)
1. HTTPS Management Server certificate and Basic account `exporter` authentication works. No credentials are committed.
2. Inventory shows all microphones in SDK, including one with no related camera.
3. Start with DryRun true; run `--inventory` and a historical `--end` preview.
4. Enable real export for one microphone `--device <GUID>`. Validate actual playable audio, channels, duration and expected gaps. RIFF structural validation alone is insufficient.
5. Disconnect microphone / stop recording briefly: verify what an empty or partial 6-hour window produces.
6. Archive at 00/06/12/18; validate playback and export from recording versus archived retention.
7. Intentionally miss one schedule; confirm previous 6-hour windows are backfilled and filenames correct. Verify windows older than BackfillDays raise an operational concern rather than silently accepting full coverage.
8. Rename microphone; confirm history and GUID association, with ambiguous windows safely marked.
9. Force disk-low error; verify nonzero exit code and CSV report.
10. Check no WAV is overwritten, corrupted files halt instead of being replaced, and retry behavior.
11. With EnableRetentionDelete false confirm no deletion; with test-only sandbox paths and 2-day retention test only old YYYY-MM-DD subfolder WAV deletion. Do NOT enable on production before backup and legal retention policy approval.
12. Verify Task Scheduler runs unattended and captures exit code, file ACLs and DPAPI identity.
13. Confirm the produced file name: check whether WAVExporter appends `.wav` to `FileName` (the exporter now locates either form; log shows the result).
14. While the scheduled task is running, start a manual run from an RDP session: it must exit with code 2 (global lock).
15. Set MaxRunMinutes=30 and ExportTimeoutMinutes=10 on a test box: verify DEFERRED rows, report CSV complete, and no `*.working-*` files left after the next run.
16. Pick a window with no recorded audio: after 3 runs it must show GAVE_UP, exit code returns to 0, and a manual `--end ... --device ...` still attempts it.
17. Name a test microphone with only spaces/dots: run must continue with folder `MIC_unnamed__<GUID>`.

## Known unresolved requirements
- No verification of real 2025 R3 build, runtime, SSL, login or recordings.
- No email/webhook notification endpoint configured. Alerts currently consist of log/CSV and nonzero task exit code, suitable for integration with Windows monitoring. Configure a real alert channel separately.
- No audio continuity proof; recorded audio gaps must be reconciled with Milestone playback/sequence data.
- Backfill is bounded by BackfillDays and MaxIntervalsPerRun. With too many devices, oldest-first order can defer newer windows; run frequency, capacity and SLA must be planned.
- Optional retention removes old exported WAV only; it does not alter Milestone recording/archive retention. Never put OutputRoot inside a recording database path.
