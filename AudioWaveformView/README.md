# AudioWaveformView (camera-free microphone) — preview implementation

This is an experimental Smart Client MIP SDK view item for **XProtect microphones, including microphone-only devices**. It does **not** require a camera association. Built independently from the existing, working `SCAudioExport` project.

## Capabilities in this version

- Insert a **Microphone Waveform** view item in Smart Client Setup.
- Select a microphone through `Kind.Microphone` and `Configuration.Instance.GetItemsByKind(Kind.Microphone)`. No `CameraList` or camera `GetRelated()`.
- Choose a local-time recording range and export it as WAV with `WAVExporter`.
- Load up to five minutes of recorded audio as a WAV **preview**, plot the PCM amplitude envelope, play/stop the WAV and click to seek within it.
- Supply an **additional ribbon** to the Smart Client main timeline through `ViewItemManager.TimelineSequenceSources`. This ribbon represents only the WAV **preview interval**, not all Recording Server audio recordings.

## Important limitations

1. **Not yet validated against the live XProtect 2025 R3 server/device**. A successful GitHub Actions build means the code compiles, not that P8221 or other devices were integration-tested.
2. The existing `SCAudioExport` project is unchanged. This new view independently reuses the same `WAVExporter` API.
3. The **standard native microphone recording track** in the Smart Client main Playback timeline is **not modified** by this plugin. Additional data needs to be enabled in Smart Client timeline settings to show the preview ribbon.
4. This is not a complete server-side recording index. Displaying every recorded segment requires an independently validated sequence retrieval API for microphone-only sources. Do not treat the blue preview ribbon as proof of uninterrupted recording.
5. WAV decoding here supports RIFF WAVE PCM 8/16/24/32-bit or IEEE 32-bit float, not compressed audio formats. If XProtect offers multiple codecs, select the codec yielding a standard PCM WAV for waveform preview. Export of another supported WAV codec can still succeed while waveform decoding is unsupported.
6. The waveform is rendered from an exported temporary WAV file (up to five minutes) and therefore starts after the temporary WAV export completes. Playback is local WPF media playback of that file, not the live `AudioPlayer` stream.
7. Export is synchronous within a dedicated background task; large exports can take time. This prototype uses a 5-minute watchdog for exports, which should be redesigned for very large exports.

## Build

The repository includes [Build AudioWaveformView](../.github/workflows/build-audio-waveform.yml), a Windows GitHub Actions workflow (MSBuild, .NET Framework 4.7 and Milestone SDK package). Download the `AudioWaveformView-Release` artifact once the workflow succeeds.

## Install for a test workstation

1. Keep the original `SCAudioExport` plugin installed.
2. Extract `AudioWaveformView-Release` into a **separate** plugin folder in the Smart Client plugin directory (e.g. `MIPPlugins/AudioWaveformView`; verify the installation-specific MIPPlugins path).
3. Verify `plugin.def` sits next to `AudioWaveformView.dll` and required dependency assemblies.
4. Restart Smart Client. In Setup, add a view item named **Microphone Waveform**.
5. Select a *standalone microphone* (P8221 or another manufacturer), choose a short known recorded interval, click **Load waveform**.
6. Verify WAV playback, generated envelope and export of the same interval; check Smart Client settings: Timeline > Additional data > Show for the experimental preview ribbon.
7. Compare the preview with playback/audio evidence, including gaps and silent portions.

## Next verification milestones

- Verify the chosen output codec is parseable by the preview waveform implementation on XProtect 2025 R3.
- Confirm standalone microphone enumeration in the actual system and test recording permission errors.
- Add genuine `AudioPlayer` live/recorded streaming and XProtect playback synchronization, without camera lookup.
- Probe `SequenceDataSource(microphone)` and its `GetTypes()` on a real recording server to identify whether audio recording segments can be obtained and shown as accurate ribbons rather than preview-only ribbons.
- Replace temporary full-file waveform generation with chunked/pyramid cache for long spans; improve task cancellation, retention and error reporting.

## Source design choices

- `SCAudioExport/Client/SCAudioExportSidePanelWpfUserControl.xaml.cs`: standalone `Kind.Microphone` + `WAVExporter`.
- `SCIndependentPlayback/Client/SCIndependentPlaybackViewItemWpfUserControl.xaml.cs`: independent playback controller, but **uses camera.GetRelated()**; do not copy that camera dependency.
- `TimelineViewItem/Client/TimelineViewItemViewItemManager.cs`: supplies `TimelineSequenceSources` from a custom ViewItem.
- `TimelineViewItem/Client/TimelineRibbonSource.cs`: ribbon sequencing API, but **returns synthetic intervals**. Our ribbon returns only the chosen preview interval.

**Status: experimental, do not deploy as a production evidentiary or forensic recording index without validation.**
