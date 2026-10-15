using System;
using System.Collections.Generic;
using VideoOS.Platform;
using VideoOS.Platform.Client;

namespace AudioWaveformView.Client
{
    public class MicrophoneViewItemManager : ViewItemManager
    {
        private Item _microphone;
        private readonly RecordingTimelineSource _timeline = new RecordingTimelineSource();

        public MicrophoneViewItemManager() : base("MicrophoneWaveformManager") { }

        public Item Microphone
        {
            get { return _microphone; }
            set
            {
                if (value != null && value.FQID.Kind != Kind.Microphone)
                    throw new ArgumentException("A microphone item is required.");
                _microphone = value;
                SetProperty("MicrophoneFQID", value == null ? "" : value.FQID.ToXmlNode().OuterXml);
                SaveProperties();
                _timeline.SelectMicrophone(value);
            }
        }

        public override void PropertiesLoaded()
        {
            string saved = GetProperty("MicrophoneFQID");
            if (string.IsNullOrWhiteSpace(saved)) return;
            try
            {
                var item = Configuration.Instance.GetItem(new FQID(saved));
                _microphone = item != null && item.FQID.Kind == Kind.Microphone ? item : null;
            }
            catch { _microphone = null; }
            _timeline.SelectMicrophone(_microphone);
        }

        public RecordingTimelineSource Timeline { get { return _timeline; } }

        public override IEnumerable<TimelineSequenceSource> TimelineSequenceSources
        {
            get { return new TimelineSequenceSource[] { _timeline }; }
        }

        public override string DisplayName
        {
            get { return _microphone == null ? "Microphone preview" : _microphone.Name + " preview"; }
        }

        public override ViewItemWpfUserControl GenerateViewItemWpfUserControl()
        {
            return new MicrophoneViewItemWpfUserControl(this);
        }
    }
}
