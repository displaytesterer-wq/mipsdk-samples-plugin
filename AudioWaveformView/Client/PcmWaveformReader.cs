using System;
using System.Collections.Generic;
using System.Threading;
using VideoOS.Platform;
using VideoOS.Platform.Data;

namespace AudioWaveformView.Client
{
    /// <summary>
    /// Reads an amplitude envelope directly from Recording Server PCM microphone
    /// frames. No WAV export or camera relationship is needed for the waveform.
    /// A null / missing frame is not misrepresented as silence or as recording.
    /// </summary>
    internal static class PcmWaveformReader
    {
        public static double[] Read(Item microphone, DateTime startUtc, DateTime endUtc,
            int buckets, CancellationToken token)
        {
            if (microphone == null || microphone.FQID.Kind != Kind.Microphone)
                throw new ArgumentException("PcmWaveformReader requires a microphone.");
            if (endUtc <= startUtc || endUtc - startUtc > PcmRecordingProbe.MaxSpan)
                throw new ArgumentException("PCM waveform span must be 0-5 minutes.");
            if (buckets < 1 || buckets > 2000) throw new ArgumentOutOfRangeException("buckets");

            var peaks = new double[buckets];
            long totalFrames = 0;
            using (var adapter = new PcmSourceAdapter(microphone))
            {
                for (DateTime cursor = startUtc; cursor < endUtc; cursor = cursor.AddSeconds(15))
                {
                    token.ThrowIfCancellationRequested();
                    DateTime to = cursor.AddSeconds(15);
                    if (to > endUtc) to = endUtc;
                    var groups = adapter.Get(cursor, to - cursor, 8000);
                    if (groups == null) continue;
                    if (groups.Count >= 8000)
                        throw new InvalidOperationException("PCM waveform response truncated; use a shorter range.");
                    foreach (object obj in groups)
                    {
                        var dataList = obj as PcmAudioDataList;
                        if (dataList != null)
                        {
                            if (dataList.DataList != null)
                            {
                                foreach (PcmAudioData frame in dataList.DataList)
                                {
                                    token.ThrowIfCancellationRequested();
                                    if (frame == null || frame.Data == null || frame.Data.Length == 0) continue;
                                    AddPeak(peaks, frame.DateTime, frame.Data,
                                        frame.PcmSourceSettings == null ? 16 :
                                        (int)frame.PcmSourceSettings.BitsPerSample,
                                        startUtc, endUtc);
                                    totalFrames++;
                                }
                            }
                        }
                        else
                        {
                            var frame = obj as PcmAudioData;
                            if (frame != null && frame.Data != null && frame.Data.Length != 0)
                            {
                                AddPeak(peaks, frame.DateTime, frame.Data,
                                    frame.PcmSourceSettings == null ? 16 :
                                    (int)frame.PcmSourceSettings.BitsPerSample, startUtc, endUtc);
                                totalFrames++;
                            }
                        }
                    }
                }
            }
            if (totalFrames == 0)
                throw new InvalidOperationException("PCM microphone audio frames were not returned for this range.");
            return peaks;
        }

        private static void AddPeak(double[] peaks, DateTime timestamp, byte[] bytes,
            int bitsPerSample, DateTime startUtc, DateTime endUtc)
        {
            if (timestamp.Kind == DateTimeKind.Local) timestamp = timestamp.ToUniversalTime();
            else if (timestamp.Kind == DateTimeKind.Unspecified)
                timestamp = DateTime.SpecifyKind(timestamp, DateTimeKind.Utc);
            if (timestamp < startUtc || timestamp >= endUtc) return;
            long ticks = (timestamp - startUtc).Ticks;
            long length = (endUtc - startUtc).Ticks;
            int index = Math.Min(peaks.Length - 1, (int)(ticks * peaks.Length / length));

            // Approximate visual peak only: cap samples inspected per server packet.
            double max = 0;
            int step = bitsPerSample == 8 ? 1 : 2;
            int totalSamples = bytes.Length / step;
            int stride = Math.Max(1, totalSamples / 512);
            for (int i = 0; i < totalSamples; i += stride)
            {
                int offset = i * step;
                double value;
                if (step == 1)
                    value = Math.Abs((bytes[offset] - 128.0) / 128.0);
                else
                    value = Math.Abs(BitConverter.ToInt16(bytes, offset) / 32768.0);
                if (value > max) max = value;
            }
            if (max > peaks[index]) peaks[index] = Math.Min(1.0, max);
        }

        private sealed class PcmSourceAdapter : IDisposable
        {
            private readonly PcmAudioSource _source;
            public PcmSourceAdapter(Item microphone)
            {
                _source = new PcmAudioSource(microphone);
                _source.Init();
            }
            public List<object> Get(DateTime start, TimeSpan span, int maxItems)
            {
                return _source.Get(start, span, maxItems);
            }
            public void Dispose() { _source.Close(); }
        }
    }
}
