using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using VideoOS.Platform;
using VideoOS.Platform.Data;
using VideoOS.Platform.Util;

namespace AudioWaveformView.Client
{
    /// <summary>
    /// Read recorded PCM frames directly from the microphone's Recording Server.
    /// Unlike SequenceDataSource, PcmAudioSource explicitly requires Kind.Microphone.
    /// A positive result proves time-stamped audio frames were retrieved, even if the
    /// microphone has no camera and no recording-sequence index.
    /// </summary>
    internal static class PcmRecordingProbe
    {
        internal static readonly TimeSpan MaxSpan = TimeSpan.FromMinutes(5);
        private static readonly TimeSpan Chunk = TimeSpan.FromSeconds(15);
        private static readonly TimeSpan JoinTolerance = TimeSpan.FromMilliseconds(50);
        private const int FrameCap = 8000;

        public static RecordingIntervalProvider.Result Query(
            Item microphone, DateTime startUtc, DateTime endUtc, CancellationToken token)
        {
            if (microphone == null || microphone.FQID.Kind != Kind.Microphone)
                throw new ArgumentException("Select a microphone Item.");
            if (endUtc <= startUtc) throw new ArgumentException("End must be after start.");
            var output = new RecordingIntervalProvider.Result
            {
                SequenceType = "PCM packet timestamps",
                Supported = false
            };
            if (endUtc - startUtc > MaxSpan)
            {
                output.Message = "Direct PCM scan limited to 5 minutes per request. " +
                    "Zoom in to 5 minutes, or use the WAV export for a longer period.";
                return output;
            }

            var areas = new List<TimeInterval>();
            var source = new PcmAudioSource(microphone);
            try
            {
                // Keep all source methods on one background thread and close the session.
                source.Init();
                for (DateTime cursor = startUtc; cursor < endUtc; cursor = cursor.Add(Chunk))
                {
                    token.ThrowIfCancellationRequested();
                    DateTime to = cursor.Add(Chunk);
                    if (to > endUtc) to = endUtc;
                    List<object> frames = source.Get(cursor, to - cursor, FrameCap);
                    output.Requests++;
                    if (frames == null) continue;
                    if (frames.Count >= FrameCap)
                        throw new InvalidOperationException("PCM frame count cap reached; use a shorter window.");

                    foreach (object obj in frames)
                    {
                        var group = obj as PcmAudioDataList;
                        if (group != null)
                        {
                            // Actual decoded PCM samples carry their own capture timestamps.
                            bool gotFrame = false;
                            if (group.DataList != null)
                            {
                                foreach (PcmAudioData frame in group.DataList)
                                {
                                    if (frame == null || frame.Data == null || frame.Data.Length == 0) continue;
                                    AddFrame(areas, frame.DateTime, frame.EndDateTime, cursor, to);
                                    gotFrame = true;
                                }
                            }
                            // For SDK versions that return a grouped PCM block without
                            // populated DataList, use its documented presentation interval.
                            if (!gotFrame && group.AccumulatedData != null && group.AccumulatedData.Length > 0)
                                AddFrame(areas, group.DateTime, group.EndDateTime, cursor, to);
                        }
                        else
                        {
                            var frame = obj as PcmAudioData;
                            if (frame != null && frame.Data != null && frame.Data.Length > 0)
                                AddFrame(areas, frame.DateTime, frame.EndDateTime, cursor, to);
                        }
                    }
                }
            }
            finally
            {
                source.Close();
            }

            if (areas.Count == 0)
            {
                output.Message = "PCM API returned no audio frames for this time window. " +
                    "This does NOT prove no recording exists; check the same time in Smart Client Audio.";
                return output;
            }

            output.Intervals = MergeNearContiguous(areas);
            output.Supported = true;
            output.Message = "PCM decoded audio found: " + areas.Count +
                " time-stamped frame(s), " + output.Intervals.Count +
                " interval(s). Direct microphone Recording Server data; " +
                "only this scanned window was checked.";
            return output;
        }

        private static void AddFrame(List<TimeInterval> areas,
            DateTime originalBegin, DateTime originalEnd, DateTime chunkStart, DateTime chunkEnd)
        {
            DateTime begin = Utc(originalBegin);
            DateTime end = Utc(originalEnd);
            if (end <= begin) return;
            if (begin < chunkStart) begin = chunkStart;
            if (end > chunkEnd) end = chunkEnd;
            if (begin < end) areas.Add(new TimeInterval(begin, end));
        }

        private static DateTime Utc(DateTime time)
        {
            if (time.Kind == DateTimeKind.Utc) return time;
            if (time.Kind == DateTimeKind.Local) return time.ToUniversalTime();
            return DateTime.SpecifyKind(time, DateTimeKind.Utc);
        }

        private static List<TimeInterval> MergeNearContiguous(IEnumerable<TimeInterval> intervals)
        {
            // Maximum smoothing is 50ms to account for packet timing jitter.
            // This is a packet-presence indicator, NOT a certified recording index.
            var sorted = intervals.OrderBy(x => x.StartTime).ToList();
            var result = new List<TimeInterval>();
            foreach (var current in sorted)
            {
                if (result.Count == 0) { result.Add(current); continue; }
                int lastIdx = result.Count - 1;
                var last = result[lastIdx];
                if (current.StartTime <= last.EndTime.Add(JoinTolerance))
                {
                    if (current.EndTime > last.EndTime)
                        result[lastIdx] = new TimeInterval(last.StartTime, current.EndTime);
                }
                else result.Add(current);
            }
            return result;
        }
    }
}
