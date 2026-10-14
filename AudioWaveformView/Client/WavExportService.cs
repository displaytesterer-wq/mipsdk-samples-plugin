using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using VideoOS.Platform;
using VideoOS.Platform.Data;

namespace AudioWaveformView.Client
{
    // Isolated audio-only WAV export: no camera relationship is ever consulted.
    internal static class WavExportService
    {
        public static string ExportToTemporaryWav(Item microphone, DateTime startUtc, DateTime endUtc,
            CancellationToken cancellation)
        {
            if (microphone == null || microphone.FQID.Kind != Kind.Microphone)
                throw new ArgumentException("Select an XProtect microphone.");
            if (endUtc <= startUtc) throw new ArgumentException("End must be after start.");

            string folder = Path.Combine(Path.GetTempPath(), "AudioWaveformView", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            WAVExporter exporter = null;
            try
            {
                // Match the working SCAudioExport sample; do not require a CameraList.
                exporter = new WAVExporter();
                exporter.Init();
                string[] codecs = exporter.CodecList;
                if (codecs == null || codecs.Length == 0)
                    throw new InvalidOperationException("WAVExporter returned no supported codecs.");
                string codec = codecs.FirstOrDefault(c => c.IndexOf("PCM", StringComparison.OrdinalIgnoreCase) >= 0)
                               ?? codecs[0];
                exporter.Codec = codec;
                exporter.AudioSampleRate = 16000;
                exporter.AudioList = new List<Item> { microphone };
                exporter.Path = folder;
                exporter.FileName = "microphone_preview";
                bool started = exporter.StartExport(startUtc, endUtc);
                if (!started)
                    throw new InvalidOperationException("WAVExporter refused to start: " + exporter.LastErrorString);

                DateTime deadline = DateTime.UtcNow.AddMinutes(5);
                while (exporter.Progress < 100)
                {
                    cancellation.ThrowIfCancellationRequested();
                    if (exporter.LastError != 0)
                        throw new InvalidOperationException("WAVExporter error: " + exporter.LastErrorString);
                    if (DateTime.UtcNow > deadline)
                        throw new TimeoutException("WAV export exceeded five minutes.");
                    Thread.Sleep(250);
                }
                if (exporter.LastError != 0)
                    throw new InvalidOperationException("WAVExporter error: " + exporter.LastErrorString);
            }
            catch
            {
                if (exporter != null) exporter.Cancel();
                TryDeleteFolder(folder);
                throw;
            }
            finally
            {
                if (exporter != null)
                {
                    try { exporter.EndExport(); } catch { /* original exception takes precedence */ }
                    try { exporter.Close(); } catch { }
                }
            }
            string output = Directory.GetFiles(folder, "*.wav", SearchOption.AllDirectories)
                .OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
            if (output == null)
            {
                TryDeleteFolder(folder);
                throw new FileNotFoundException("Exporter completed but no WAV file was produced.");
            }
            return output;
        }

        public static void TryDeleteFolder(string folder)
        {
            try { if (!string.IsNullOrWhiteSpace(folder) && Directory.Exists(folder)) Directory.Delete(folder, true); }
            catch { }
        }
    }
}
