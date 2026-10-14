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
    /// A server-originated recording index lookup for a microphone Item.
    /// No camera association, WAV exports, audio-detection events or synthetic time blocks.
    /// When the MIP server doesn't expose an index, the result is explicitly UNSUPPORTED,
    /// rather than an invented continuous recording ribbon.
    /// </summary>
    internal static class RecordingIntervalProvider
    {
        internal sealed class Result
        {
            public bool Supported;
            public string Message;
            public string SequenceType;
            public List<TimeInterval> Intervals = new List<TimeInterval>();
            public int Requests;
        }

        // Keep requests bounded to avoid expensive unbounded multi-day SequenceDataSource queries.
        private static readonly TimeSpan ChunkSize = TimeSpan.FromHours(6);
        private static readonly TimeSpan BoundaryLookback = TimeSpan.FromHours(24);
        private const int MaxItemsPerDirection = 5000;

        public static Result Query(Item microphone, DateTime startUtc, DateTime endUtc,
            CancellationToken cancellationToken)
        {
            if (microphone == null || microphone.FQID.Kind != Kind.Microphone)
                throw new ArgumentException("Only XProtect microphone Items can be indexed.");
            startUtc = AsUtc(startUtc);
            endUtc = AsUtc(endUtc);
            if (endUtc <= startUtc) throw new ArgumentException("Invalid UTC recording range.");

            var output = new Result();
            var source = new SequenceDataSource(microphone);
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                // SDK supports GetTypes for Camera, Microphone and Speaker.
                // Device/server configuration determines which types are actually available.
                List<DataType> supportedTypes = source.GetTypes();
                if (supportedTypes == null || supportedTypes.Count == 0)
                {
                    output.Message = "SequenceDataSource.GetTypes() returned no sequence types for this microphone. " +
                        "A reliable recording timeline is not available via this SDK path.";
                    return output;
                }

                var supported = supportedTypes.Select(t => t.Id).ToList();
                Guid type;
                // TimelineRecording is 'recorded data available' and is preferred to event-derived sequences.
                if (supported.Contains(DataType.SequenceTypeGuids.TimelineRecording))
                {
                    type = DataType.SequenceTypeGuids.TimelineRecording;
                    output.SequenceType = "TimelineRecording / RecordedDataAvailable";
                }
                else if (supported.Contains(DataType.SequenceTypeGuids.RecordingSequence))
                {
                    type = DataType.SequenceTypeGuids.RecordingSequence;
                    output.SequenceType = "RecordingSequence";
                }
                else
                {
                    output.Message = "Microphone sequence types do not include TimelineRecording or RecordingSequence. " +
                        "Available: " + string.Join(", ", supportedTypes.Select(t => t.Name + "=" + t.Id));
                    return output;
                }
                output.Supported = true;

                var raw = new List<TimeInterval>();
                // Each requested area is queried individually; never assume an empty result means
                // uninterrupted audio. Throw on a likely truncated result rather than silently
                // losing entries. Long timeline ranges are paged automatically.
                for (DateTime cursor = startUtc; cursor < endUtc;)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    DateTime chunkEnd = endUtc - cursor < ChunkSize ? endUtc : cursor.Add(ChunkSize);
                    TimeSpan before = cursor - DateTime.MinValue.ToUniversalTime() < BoundaryLookback
                        ? cursor - DateTime.MinValue.ToUniversalTime() : BoundaryLookback;
                    // Query centered at each chunk's beginning with lookback to catch
                    // intervals that started shortly before the viewport.
                    List<object> data = source.GetData(cursor,
                        before, MaxItemsPerDirection,
                        chunkEnd - cursor, MaxItemsPerDirection,
                        type, true);

                    if (data == null)
                        throw new InvalidOperationException("SequenceDataSource returned null (not a verified empty recording range).");
                    if (data.Count >= MaxItemsPerDirection)
                        throw new InvalidOperationException("Sequence query hit the item safety cap. Zoom into a shorter range.");

                    output.Requests++;
                    foreach (object obj in data)
                    {
                        var sequence = obj as SequenceData;
                        if (sequence == null || sequence.EventSequence == null) continue;
                        DateTime begin = AsUtc(sequence.EventSequence.StartDateTime);
                        DateTime end = AsUtc(sequence.EventSequence.EndDateTime);
                        if (begin >= end) continue;

                        // Intersection with the actual requested chunk prevents falsely creating
                        // a bar in neighboring periods based on the 24-hour lookback.
                        if (begin < cursor) begin = cursor;
                        if (end > chunkEnd) end = chunkEnd;
                        if (begin < end) raw.Add(new TimeInterval(begin, end));
                    }
                    cursor = chunkEnd;
                }

                output.Intervals = MergeOverlapping(raw);
                output.Message = output.Intervals.Count + " server-reported recording interval(s), " +
                    output.Requests + " SDK query window(s), type: " + output.SequenceType + ".";
                return output;
            }
            finally
            {
                source.Close();
            }
        }

        private static DateTime AsUtc(DateTime input)
        {
            if (input.Kind == DateTimeKind.Utc) return input;
            if (input.Kind == DateTimeKind.Local) return input.ToUniversalTime();
            // XProtect event timestamps are returned in UTC; preserve unspecified values as UTC,
            // rather than moving them by the Windows workstation's timezone.
            return DateTime.SpecifyKind(input, DateTimeKind.Utc);
        }

        public static List<TimeInterval> MergeOverlapping(IEnumerable<TimeInterval> areas)
        {
            // Merge only truly overlapping or exactly contiguous timestamps.
            // Even a small real gap must remain visible in the recording track.
            var ordered = areas.OrderBy(x => x.StartTime).ThenBy(x => x.EndTime).ToList();
            var merged = new List<TimeInterval>();
            foreach (var next in ordered)
            {
                if (merged.Count == 0) { merged.Add(next); continue; }
                TimeInterval last = merged[merged.Count - 1];
                if (next.StartTime <= last.EndTime)
                {
                    if (next.EndTime > last.EndTime)
                        merged[merged.Count - 1] = new TimeInterval(last.StartTime, next.EndTime);
                }
                else merged.Add(next);
            }
            return merged;
        }
    }
}
