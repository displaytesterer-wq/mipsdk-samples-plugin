using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using VideoOS.Platform;
using VideoOS.Platform.Client;
using VideoOS.Platform.Util;

namespace AudioWaveformView.Client
{
    /// <summary>
    /// Smart Client ADDITIONAL DATA ribbon made exclusively from the microphone's
    /// server-reported recording sequences. This does not modify the native camera timeline.
    /// Actual intervals are loaded for whichever time windows the Smart Client requests.
    /// </summary>
    public class RecordingTimelineSource : TimelineSequenceSource
    {
        private readonly Guid _id = Guid.NewGuid();
        private readonly object _sync = new object();
        private Item _microphone;
        private CancellationTokenSource _selectionCancellation = new CancellationTokenSource();
        private readonly SemaphoreSlim _requestGate = new SemaphoreSlim(1, 1);
        private int _generation;
        private string _lastStatus = "Choose a microphone to check recording sequences.";

        public event Action<string> StatusChanged;
        public override Guid Id { get { return _id; } }
        public override TimelineSequenceSourceType SourceType { get { return TimelineSequenceSourceType.Ribbon; } }
        public override string Title { get { return "Microphone audio (server sequences / verified PCM)"; } }
        public override Brush RibbonContentColorBrush { get { return Brushes.MediumSeaGreen; } }
        public string LastStatus { get { lock (_sync) return _lastStatus; } }

        public void SelectMicrophone(Item microphone)
        {
            lock (_sync)
            {
                ++_generation;
                _selectionCancellation.Cancel();
                _selectionCancellation = new CancellationTokenSource();
                _microphone = microphone;
                _lastStatus = microphone == null
                    ? "Select a microphone."
                    : "Selected " + microphone.Name + ". Waiting for server recording index query.";
            }
        }

        internal RecordingIntervalProvider.Result QuerySelected(DateTime startUtc, DateTime endUtc, CancellationToken userToken)
        {
            Item selected;
            CancellationToken selectionToken;
            lock (_sync)
            {
                selected = _microphone;
                selectionToken = _selectionCancellation.Token;
            }
            if (selected == null) throw new InvalidOperationException("Select a microphone first.");
            using (var joined = CancellationTokenSource.CreateLinkedTokenSource(userToken, selectionToken))
            {
                RecordingIntervalProvider.Result result = QueryWithPcmFallback(
                    selected, startUtc, endUtc, joined.Token);
                PublishStatus(result.Supported ? result.Message : "Recording index unsupported: " + result.Message);
                return result;
            }
        }

        // SequenceDataSource may not expose recording intervals for standalone microphones,
        // even though native Smart Client Audio and PcmAudioSource can play them.
        // Fall back to positively verified decoded PCM audio frames within short windows.
        // Never claim a window was searched unless PcmAudioSource actually returned frames.
        private static RecordingIntervalProvider.Result QueryWithPcmFallback(
            Item microphone, DateTime startUtc, DateTime endUtc, CancellationToken token)
        {
            RecordingIntervalProvider.Result index = null;
            string originalError = null;
            try
            {
                index = RecordingIntervalProvider.Query(microphone, startUtc, endUtc, token);
                if (index.Supported && index.Intervals.Count > 0) return index;
                originalError = index.Message;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                originalError = "Sequence index error: " + ex.Message;
            }
            if (endUtc - startUtc > PcmRecordingProbe.MaxSpan)
            {
                return index ?? new RecordingIntervalProvider.Result
                {
                    Supported = false,
                    Message = originalError + ". Zoom to 5 minutes to try direct PCM microphone data."
                };
            }
            try
            {
                var pcm = PcmRecordingProbe.Query(microphone, startUtc, endUtc, token);
                if (pcm.Supported) return pcm;
                pcm.Message = "Recording sequence: " + originalError +
                    "; direct PCM: " + pcm.Message;
                return pcm;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                return new RecordingIntervalProvider.Result
                {
                    Supported = false,
                    Message = "Recording sequence: " + originalError +
                        "; direct PCM lookup failed: " + ex.Message
                };
            }
        }

        private void PublishStatus(string message)
        {
            Action<string> listener;
            lock (_sync)
            {
                _lastStatus = message;
                listener = StatusChanged;
            }
            if (listener != null) listener(message);
        }

        public override void StartGetSequences(IEnumerable<TimeInterval> intervals)
        {
            // MIP TimelineSequenceSource is asynchronously queried on viewport changes.
            var requested = intervals.ToArray();
            Item selected;
            CancellationToken selectionToken;
            int generation;
            lock (_sync)
            {
                selected = _microphone;
                selectionToken = _selectionCancellation.Token;
                generation = _generation;
            }

            Task.Run(async () =>
            {
                bool entered = false;
                try
                {
                    await _requestGate.WaitAsync(selectionToken).ConfigureAwait(false);
                    entered = true;
                    var results = new List<TimelineSourceQueryResult>();
                    if (selected == null)
                    {
                        foreach (var window in requested)
                            results.Add(new TimelineSourceQueryResult(window)
                            { Sequences = new List<TimelineDataArea>() });
                    }
                    else
                    {
                        foreach (var window in requested)
                        {
                            selectionToken.ThrowIfCancellationRequested();
                            RecordingIntervalProvider.Result query = QueryWithPcmFallback(
                                selected, window.StartTime, window.EndTime, selectionToken);
                            if (!query.Supported)
                            {
                                PublishStatus("Timeline sequence unsupported: " + query.Message);
                                // Fail closed: never fabricate a recording ribbon on unsupported SDK.
                                results.Add(new TimelineSourceQueryResult(window)
                                { Sequences = new List<TimelineDataArea>() });
                                continue;
                            }
                            results.Add(new TimelineSourceQueryResult(window)
                            {
                                Sequences = query.Intervals.Select(t =>
                                    new TimelineDataArea(new TimeInterval(t.StartTime, t.EndTime))).ToList()
                            });
                            PublishStatus(query.Message);
                        }
                    }
                    lock (_sync)
                    {
                        if (_generation != generation) return;
                    }
                    OnSequencesRetrieved(results);
                }
                catch (OperationCanceledException) { /* stale request after device change */ }
                catch (Exception ex)
                {
                    PublishStatus("Recording index query failed: " + ex.Message +
                        " (no recording bars have been invented)");
                    var empty = requested.Select(w => new TimelineSourceQueryResult(w)
                    { Sequences = new List<TimelineDataArea>() }).ToList();
                    lock (_sync) { if (_generation != generation) return; }
                    OnSequencesRetrieved(empty);
                }
                finally
                {
                    if (entered) _requestGate.Release();
                }
            });
        }
    }
}
