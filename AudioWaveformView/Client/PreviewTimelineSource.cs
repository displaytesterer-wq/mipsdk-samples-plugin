using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Windows.Media;
using VideoOS.Platform.Client;
using VideoOS.Platform.Util;

namespace AudioWaveformView.Client
{
    // IMPORTANT: Shows only a user-previewed WAV range; NOT a recording index.
    // Never claim this ribbon represents every recorded segment in the server.
    public class PreviewTimelineSource : TimelineSequenceSource
    {
        private readonly Guid _id = Guid.NewGuid();
        private readonly object _sync = new object();
        private TimeInterval _preview;

        public override Guid Id { get { return _id; } }
        public override TimelineSequenceSourceType SourceType { get { return TimelineSequenceSourceType.Ribbon; } }
        public override string Title { get { return "Loaded WAV preview (not recording index)"; } }
        public override Brush RibbonContentColorBrush { get { return Brushes.DodgerBlue; } }

        public void SetPreview(DateTime utcStart, DateTime utcEnd)
        {
            lock (_sync) _preview = new TimeInterval(utcStart, utcEnd);
        }
        public void Clear() { lock (_sync) _preview = null; }

        public override void StartGetSequences(IEnumerable<TimeInterval> intervals)
        {
            var requested = intervals.ToArray();
            ThreadPool.QueueUserWorkItem(_ =>
            {
                TimeInterval? preview;
                lock (_sync) preview = _preview;
                var results = new List<TimelineSourceQueryResult>();
                foreach (TimeInterval interval in requested)
                {
                    var areas = new List<TimelineDataArea>();
                    if (preview != null)
                    {
                        DateTime begin = interval.StartTime > preview.StartTime ? interval.StartTime : preview.Value.StartTime;
                        DateTime end = interval.EndTime < preview.EndTime ? interval.EndTime : preview.Value.EndTime;
                        if (begin < end)
                            areas.Add(new TimelineDataArea(new TimeInterval(begin, end)));
                    }
                    results.Add(new TimelineSourceQueryResult(interval) { Sequences = areas });
                }
                OnSequencesRetrieved(results);
            });
        }
    }
}
