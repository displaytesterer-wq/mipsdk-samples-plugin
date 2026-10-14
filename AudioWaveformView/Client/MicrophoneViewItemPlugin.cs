using System;
using VideoOS.Platform.Client;
using VideoOS.Platform.UI.Controls;

namespace AudioWaveformView.Client
{
    public class MicrophoneViewItemPlugin : ViewItemPlugin
    {
        public override Guid Id { get { return AudioWaveformViewDefinition.ViewId; } }
        public override string Name { get { return "Microphone Waveform"; } }
        public override VideoOSIconSourceBase IconSource { get { return null; } }
        public override ViewItemManager GenerateViewItemManager() { return new MicrophoneViewItemManager(); }
        public override void Init() { }
        public override void Close() { }
    }
}
