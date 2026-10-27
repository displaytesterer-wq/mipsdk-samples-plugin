# Milestone AudioExportDaily4x

Standalone C# Windows Forms sample using Milestone XProtect MIP SDK's `WAVExporter`.
It calculates **the previous local calendar day** and produces **four sequential 6-hour WAV exports**.

| Segment | Start | End (exclusive) | Example WAV base name |
|---|---|---|---|
| 1 | 00:00 | 06:00 | `20261026_0000-0600` |
| 2 | 06:00 | 12:00 | `20261026_0600-1200` |
| 3 | 12:00 | 18:00 | `20261026_1200-1800` |
| 4 | 18:00 | next-day 00:00 | `20261026_1800-2400` |

## How to use

1. Open `AudioExportDaily4x.csproj` in Visual Studio 2022 on Windows with .NET Framework 4.7.2 targeting pack, MIP SDK NuGet and compatible build tools.
2. Build x64 and run. Sign in with the normal Milestone SDK login window.
3. Pick the **microphone**, destination folder, WAV codec, and sample rate.
4. Click **Export previous day (4 WAV)**. The files go into a dated subfolder.
5. The next segment starts only after the previous `WAVExporter` reports 100% progress. An error stops the sequence.

## Important limitations

- This is a **one-click, four-part exporter**, not an unattended Task Scheduler service. Login and microphone selection are interactive.
- Archive/Recording storage files are **not** parsed directly; SDK requests the recordings via XProtect.
- The Windows PC's time zone controls the previous day. DST changes may make UTC elapsed durations unequal.
- Ensure the account has microphone export/playback permissions; validate each file's actual duration and content.
- No live XProtect integration test or Windows build was performed while creating this project.
- For reliable automatic unattended nightly export, additional secure non-interactive SDK login, microphone selection persistence, retry logging, and scheduling are still needed.

The original sample/API reference is https://github.com/milestonesys/mipsdk-samples-component/tree/main/AudioExportSample . This is a community adaptation, **not an official Milestone release**.
