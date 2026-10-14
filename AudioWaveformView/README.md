# AudioWaveformView: standalone microphones, recorded sequence timeline, WAV waveform and export

**Status: compiles in GitHub Actions; requires validation on an actual XProtect 2025 R3 Recording Server and microphone-only device before production use.**

## What it does

This is a separate Smart Client MIP SDK ViewItem for microphones **without any camera relationship**, including AXIS P8221 and other XProtect `Kind.Microphone` devices.

1. Select a microphone directly from `Configuration.Instance.GetItemsByKind(Kind.Microphone)`. No camera lookup, camera `GetRelated()`, `CameraList`, Axis-specific API, or dummy video stream.
2. Show a **server-indexed recording availability strip** above the WAV waveform in the custom View. The strip is populated by real `SequenceDataSource(microphone)` queries for the selected time range. Green bars represent only intervals that the server reports. Each bar tooltip shows its exact local start and end. Unindexed / unsupported ranges are not silently painted green.
3. Populate a **Smart Client Additional data ribbon** by implementing `ViewItemManager.TimelineSequenceSources` and `TimelineSequenceSource.StartGetSequences`. It retrieves the requested visible time windows on demand from the same microphone sequence index, rather than showing fabricated 1-minute or entire-preview intervals.
4. Load a short recorded WAV (maximum five minutes) to generate a PCM amplitude waveform, play and seek within the WAV.
5. Export a selected recorded range as WAV using `WAVExporter` (based on the existing working `SCAudioExport` example). The original `SCAudioExport` project has not been edited.

## Crucial differences between timeline and waveform

- The green **server recording strips** and **Additional data timeline ribbon** show the *recording-index time intervals*, **not** an audio amplitude waveform.
- The teal **waveform** displays amplitude sampled from the exported WAV excerpt, currently maximum five minutes.
- The plugin **does not insert a native microphone recording track** into the built-in XProtect recording track. The main timeline output is an **Additional data ribbon** attached to this plugin ViewItem; enable Smart Client settings **Timeline > Additional data > Show** and reselect the View or navigate to a time period.
- The entire 90-day retention period is **not exported into one WAV** and **not downloaded all at startup**. The timeline requests visible time windows from the server on demand. The View can probe manually selected ranges.

## How the server recording index works

The SDK's `SequenceDataSource.GetTypes()` documents valid sequence types for **Camera/Microphone/Speaker**. The provider prefers `DataType.SequenceTypeGuids.TimelineRecording` (recorded data availability) and falls back to `RecordingSequence` only when the chosen type is listed by that microphone's server.

It then queries the microphone Item in 6-hour chunks with a 24-hour preceding lookup overlap. Only the intersection of each returned `SequenceData.EventSequence.StartDateTime/EndDateTime` and the requested time window is displayed. Adjacent exact intervals are merged, **real gaps are retained**, and query exceptions, unsupported sequence types, or likely truncation are surfaced as failures instead of inventing recording spans.

**Important:** Whether the XProtect 2025 R3 server *actually exposes* reliable `TimelineRecording` or `RecordingSequence` for the camera-free P8221 is not known until running the test on that server. The SDK docs mention microphones, but this isn't proof a specific recorder will return complete audio intervals. The 24-hour lookback also requires validation for exceptionally long single recording sequences; otherwise an overlap that starts more than a day before a requested window might be omitted. The query count cap protects the server but means exceptionally dense sequences can fail closed.

A successful build confirms only C#/XAML/API compilation, not accuracy of the recording index on your production server.

## Building from GitHub (no local Visual Studio required)

GitHub Actions workflow: [Build AudioWaveformView](../.github/workflows/build-audio-waveform.yml)

- Checkout repository source
- Run Windows MSBuild against `AudioWaveformView/AudioWaveformView.csproj`
- Upload an `AudioWaveformView-Release` artifact containing `AudioWaveformView.dll`, `plugin.def` and dependencies.

## Installation and on-server verification

1. Download the latest successful `AudioWaveformView-Release` artifact from Actions.
2. Extract into a **separate** Smart Client MIPPlugins subfolder alongside `plugin.def` (verify the correct MIPPlugins path for the installed client). Leave the existing `SCAudioExport` deployment unchanged.
3. Restart Smart Client, enter Setup, and add the **Microphone Waveform** view item to a View.
4. Select **P8221** directly, with **no camera association**. Set a local-time interval for which you already verified audio recording exists (e.g. a portion of yesterday).
5. Click **Check recording times**. If the server supports microphone sequences, green bars appear in the local strip and an interval count plus used sequence type is shown.
6. Show the Smart Client timeline's **Additional data** setting, navigate to that interval, and confirm the plugin ribbon follows the actual indexed spans (it is not the standard green/red camera track).
7. Choose a small recorded interval (up to five minutes), click **Load waveform**, listen with **Play** and verify amplitude bars. Try **Export WAV** and compare the output with the existing working `SCAudioExport`.
8. Check a **known recording gap**, date/time zone boundaries, a date outside retention, and a long continuously recording day. Compare the server-index spans with observed playback. **No false green bars and no omitted known recordings** is the acceptance criterion.
9. If the plugin reports `unsupported`, an SDK error, or zero intervals despite audible stored audio, do **not** assume no recordings exist. Verify microphone **Read sequences**, Playback and Export role rights, and capture the status text; microphone server index support may need an alternate SDK/API path.

## Non-production caveats

- Playback of short exported WAV excerpts uses WPF `MediaPlayer`, **not** a direct live `AudioPlayer` feed or native Smart Client playback synchronization. Those remain future enhancements.
- PCM/IEEE-float WAVE files can generate waveform peaks; unsupported compressed WAV codecs give an explicit decoding error.
- The export helper has a five-minute watchdog. Very large exports need a different timeout/cancellation model.
- Forensic or production evidence use requires actual server/device verification of sequence coverage and permissions before deployment.

## Milestone API documentation

- [SequenceDataSource](https://doc.developer.milestonesys.com/mipsdk/miphelp/class_video_o_s_1_1_platform_1_1_data_1_1_sequence_data_source.html) — `GetTypes`, `GetData`, microphone type support.
- [EventSequence](https://doc.developer.milestonesys.com/mipsdk/miphelp/class_video_o_s_1_1_platform_1_1_data_1_1_event_sequence.html) — `StartDateTime` / `EndDateTime`.
- [TimelineSequenceSource](https://doc.developer.milestonesys.com/mipsdk/miphelp/class_video_o_s_1_1_platform_1_1_client_1_1_timeline_sequence_source.html) — view-item Additional data ribbon.
- [TimelineViewItem sample](../TimelineViewItem/README.md) — view item ribbons/markers.
- [SCAudioExport sample](../SCAudioExport/README.md) — audio-only WAV export.
