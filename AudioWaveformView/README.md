# AudioWaveformView: standalone microphones, recorded sequence timeline, WAV waveform and export

**Status: compiles in GitHub Actions; requires validation on an actual XProtect 2025 R3 Recording Server and microphone-only device before production use.**

**Current diagnostic change:** Milestone's public SDK documents `SequenceDataSource` mainly for camera/metadata indexing, whereas `PcmAudioSource` explicitly accepts `Kind.Microphone`. Therefore the plugin now tries decoded PCM packets directly for windows of at most **5 minutes** when its sequence index is absent or empty. This is the same XProtect microphone *device and Recording Server* audio source as Smart Client Audio, but it is not a claim to invoke the Smart Client Audio pane's private internal implementation.

## What it does

This is a separate Smart Client MIP SDK ViewItem for microphones **without any camera relationship**, including AXIS P8221 and other XProtect `Kind.Microphone` devices.

1. Select a microphone directly from `Configuration.Instance.GetItemsByKind(Kind.Microphone)`. No camera lookup, camera `GetRelated()`, `CameraList`, Axis-specific API, or dummy video stream.
2. Show recording availability above the waveform in the View. First attempt the server's `SequenceDataSource(microphone)` recording index; if unavailable or empty **and the selected time range is at most five minutes**, try `PcmAudioSource(microphone)` and retrieve actual recorded PCM audio frames. Green means server-provided sequence ranges or verified decoded PCM packet presentation time. PCM ranges have up to **50 milliseconds** of rendering merge tolerance and must not be mistaken for a comprehensive recording index. A blank result means **unknown / not verified**, not "no recording".
3. Populate a **Smart Client Additional data ribbon** from the same microphone sequence index or verified PCM frames (for requested windows of at most five minutes). Zoom into five minutes in the main timeline to enable the PCM fallback. The main native recording track remains unchanged. Scanning whole days/months from audio packets is deliberately not performed automatically because it is expensive and would be misleading if partially scanned.
4. For **Load waveform**, first try `PcmAudioSource` directly, without exporting a WAV. If direct PCM does not work, fall back to a short recorded WAV excerpt (maximum five minutes) and render its samples. This waveform is amplitude, not a recording index.
5. Use **Listen from XProtect** to connect `AudioPlayer` directly to the selected microphone's FQID and to follow the main Smart Client Live/Playback controller; choose the correct playback time in Smart Client. **Disconnect audio** disconnects. If `Play` has a WAV excerpt it plays the WAV; otherwise it connects to direct XProtect audio when PCM waveform is loaded.
6. Export selected time ranges as WAV using `WAVExporter` (based on the existing working `SCAudioExport` example). The original project has not been edited.

## Crucial differences between timeline and waveform

- The green **server recording strips** and **Additional data timeline ribbon** show the *recording-index time intervals*, **not** an audio amplitude waveform.
- The teal **waveform** displays amplitude sampled from the exported WAV excerpt, currently maximum five minutes.
- The plugin **does not insert a native microphone recording track** into the built-in XProtect recording track. The main timeline output is an **Additional data ribbon** attached to this plugin ViewItem; enable Smart Client settings **Timeline > Additional data > Show** and reselect the View or navigate to a time period.
- The entire 90-day retention period is **not exported into one WAV** and **not downloaded all at startup**. The timeline requests visible time windows from the server on demand. The View can probe manually selected ranges.

## How the server recording index works

The SDK's `SequenceDataSource.GetTypes()` mentions types for **Camera/Microphone/Speaker**, but the general description is for camera and metadata items and the sequence GUIDs are labeled built-in camera identifiers. A microphone recording index is not guaranteed. The provider first prefers `DataType.SequenceTypeGuids.TimelineRecording` (recorded data availability) and falls back to `RecordingSequence` only when the chosen type is listed by that microphone's server.

When this path is supported, it queries the microphone Item in 6-hour chunks with a 24-hour preceding lookup overlap. Only the intersection of each returned `SequenceData.EventSequence.StartDateTime/EndDateTime` and the requested time window is displayed. Adjacent exact intervals are merged, **real gaps are retained**, and query exceptions, unsupported sequence types, or likely truncation are surfaced as failures instead of inventing recording spans.

**Important:** Whether the XProtect 2025 R3 server *actually exposes* reliable `TimelineRecording` or `RecordingSequence` for the camera-free P8221 is not known until running the test on that server. The alternate PCM probe reads packets directly with `PcmAudioSource.Get`, but **only within five minutes** and only confirms the frames received. The SDK docs mention microphones, but this isn't proof a specific recorder will return complete audio intervals. The 24-hour lookback also requires validation for exceptionally long single recording sequences; otherwise an overlap that starts more than a day before a requested window might be omitted. The query count cap protects the server but means exceptionally dense sequences can fail closed.

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
5. Set **From** and **To** to a **known audible recording** no longer than five minutes, then click **Check recording times**. If server sequence index is missing, the plugin now falls back to `PcmAudioSource`. Check the status label for the actual result. Green shows verified server-derived intervals, not inferred recording for the entire retention period.
6. Show the Smart Client timeline's **Additional data** setting, navigate to that interval, and confirm the plugin ribbon follows the actual indexed spans (it is not the standard green/red camera track).
7. Click **Listen from XProtect** and move the Smart Client main Playback time to the same recorded interval: this uses native `AudioPlayer` by microphone FQID. To avoid hearing two copies, turn off one of the built-in or plugin audio players. Click **Load waveform**: it tries direct PCM first, then WAV export fallback. If PCM worked, `Play` uses direct server listening. Try **Export WAV** and compare with your working `SCAudioExport`.
8. Check a **known recording gap**, date/time zone boundaries, a date outside retention, and a long continuously recording day. Compare the server-index spans with observed playback. **No false green bars and no omitted known recordings** is the acceptance criterion.
9. If the plugin reports `unsupported`, an SDK error, or zero intervals despite audible stored audio, do **not** assume no recordings exist. Verify microphone **Read sequences**, Playback and Export role rights, and capture the status text; microphone server index support may need an alternate SDK/API path.

## Non-production caveats

- Direct `AudioPlayer` listening now follows the main Smart Client Live/Playback time via `PlaybackControllerFQID = null`; the PCM/WAV waveform itself is a selected time excerpt, not automatically synchronized with the main clock. WAV excerpt playback still uses WPF `MediaPlayer`.
- PCM waveform is generated directly from the server when possible; fallback WAV decoder supports PCM/IEEE-float WAV. The 8-bit and 16-bit PCM amplitude displays are estimates and not forensic signal analyses.
- The export helper has a five-minute watchdog. Very large exports need a different timeout/cancellation model.
- Forensic or production evidence use requires actual server/device verification of sequence coverage and permissions before deployment.

## Milestone API documentation

- [SequenceDataSource](https://doc.developer.milestonesys.com/mipsdk/miphelp/class_video_o_s_1_1_platform_1_1_data_1_1_sequence_data_source.html) — `GetTypes`, `GetData`, microphone type support.
- [EventSequence](https://doc.developer.milestonesys.com/mipsdk/miphelp/class_video_o_s_1_1_platform_1_1_data_1_1_event_sequence.html) — `StartDateTime` / `EndDateTime`.
- [TimelineSequenceSource](https://doc.developer.milestonesys.com/mipsdk/miphelp/class_video_o_s_1_1_platform_1_1_client_1_1_timeline_sequence_source.html) — view-item Additional data ribbon.
- [TimelineViewItem sample](../TimelineViewItem/README.md) — view item ribbons/markers.
- [SCAudioExport sample](../SCAudioExport/README.md) — audio-only WAV export.

## The two real audio APIs

- [AudioPlayer](https://doc.developer.milestonesys.com/mipsdk/MIPhelp/class_video_o_s_1_1_platform_1_1_client_1_1_audio_player.html): live/recorded listening from the microphone's Recording Server. No camera linkage required.
- [PcmAudioSource](https://doc.developer.milestonesys.com/mipsdk/MIPhelp/class_video_o_s_1_1_platform_1_1_data_1_1_pcm_audio_source.html): recorded PCM frames for `Kind.Microphone`; [PcmAudioDataList](https://doc.developer.milestonesys.com/mipsdk/MIPhelp/class_video_o_s_1_1_platform_1_1_data_1_1_pcm_audio_data_list.html) contains timestamps/duration. PCM ranges are limited to five minutes per query until proven correct and performant on your server.
