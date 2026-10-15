using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using VideoOS.Platform;
using VideoOS.Platform.Data;

namespace AudioWaveformView.Client
{
    /// <summary>
    /// Uses the same WAVExporter setup as the working SCAudioExport Smart Client
    /// sample. Smart Client manages the WAVExporter wrapper lifecycle:
    /// WAVExporter.Init()/Close() are for standalone integrations, not for this
    /// plugin's per-export instance.
    /// </summary>
    internal static class WavExportService
    {
        private const int SampleRate = 8000;
        private static readonly TimeSpan ExportTimeout = TimeSpan.FromMinutes(5);

        internal static string FormatExporterError(WAVExporter exporter, string stage)
        {
            string description = exporter.LastErrorString;
            return "WAVExporter " + stage +
                " (LastError=" + exporter.LastError +
                ", Progress=" + exporter.Progress +
                ", Codec=" + (exporter.Codec ?? "(none)") +
                ", SampleRate=" + exporter.AudioSampleRate +
                "): " + (string.IsNullOrWhiteSpace(description)
                    ? "SDK did not supply an error description." : description);
        }

        public static string ExportToTemporaryWav(Item microphone, DateTime startUtc, DateTime endUtc,
            CancellationToken cancellation)
        {
            if (microphone == null || microphone.FQID.Kind != Kind.Microphone)
                throw new ArgumentException("Select an XProtect microphone.");
            if (endUtc <= startUtc)
                throw new ArgumentException("End must be after start.");

            string folder = Path.Combine(Path.GetTempPath(), "AudioWaveformView",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);

            WAVExporter exporter = null;
            bool started = false;
            try
            {
                // The known-working SCAudioExport sample initializes only a
                // TEMP exporter for CodecList, then uses a FRESH export instance.
                // This is important inside Smart Client.
                string[] codecs;
                var codecProbe = new WAVExporter();
                try
                {
                    codecProbe.Init();
                    codecs = codecProbe.CodecList;
                }
                finally
                {
                    codecProbe.Close();
                }
                if (codecs == null || codecs.Length == 0)
                    throw new InvalidOperationException("WAVExporter supplied no codec choices.");

                // Follow the already-working sample's first codec and 8000 Hz
                // defaults instead of guessing that all sources support 16000 Hz.
                exporter = new WAVExporter
                {
                    FileName = "microphone_preview",
                    Codec = codecs[0],
                    AudioSampleRate = SampleRate,
                    AudioList = new List<Item> { microphone },
                    Path = folder,
                    ExportToDisk = true
                };

                cancellation.ThrowIfCancellationRequested();
                started = exporter.StartExport(startUtc, endUtc);
                if (!started)
                    throw new InvalidOperationException(FormatExporterError(exporter, "did not start"));

                DateTime deadline = DateTime.UtcNow.Add(ExportTimeout);
                while (true)
                {
                    cancellation.ThrowIfCancellationRequested();

                    // Milestone documents >=100 as completed and any value
                    // outside 0..100 as an error. Do not hide negative status.
                    int progress = exporter.Progress;
                    if (progress >= 100)
                        break;
                    if (progress < 0 || exporter.LastError != 0)
                        throw new InvalidOperationException(FormatExporterError(exporter, "failed"));

                    if (DateTime.UtcNow >= deadline)
                        throw new TimeoutException("WAV export timed out after " +
                            ExportTimeout.TotalMinutes + " minutes; " +
                            FormatExporterError(exporter, "timeout"));

                    Thread.Sleep(250);
                }

                if (exporter.LastError != 0)
                    throw new InvalidOperationException(FormatExporterError(exporter, "completed with error"));

                // EndExport finalizes the output before probing the generated WAV.
                exporter.EndExport();
                started = false;
            }
            catch
            {
                if (exporter != null && started)
                {
                    try { exporter.Cancel(); } catch { }
                    try { exporter.EndExport(); } catch { }
                }
                TryDeleteFolder(folder);
                throw;
            }

            // The Smart Client is responsible for the exporter lifecycle.
            // Its output may be nested in a timestamped export subfolder.
            string output = Directory.GetFiles(folder, "*.wav", SearchOption.AllDirectories)
                .OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
            if (output == null)
            {
                TryDeleteFolder(folder);
                throw new FileNotFoundException("Export reached 100%, but no WAV file was " +
                    "found under " + folder + ". Check WAVExporter Codec/FileName/Path.");
            }
            return output;
        }

        public static void TryDeleteFolder(string folder)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(folder) && Directory.Exists(folder))
                    Directory.Delete(folder, true);
            }
            catch { /* another audio component may still hold the output file */ }
        }
    }
}
