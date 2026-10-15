using System;
using System.Collections.Generic;
using System.Drawing;
using VideoOS.Platform;
using VideoOS.Platform.Client;

namespace AudioWaveformView
{
    public class AudioWaveformViewDefinition : PluginDefinition
    {
        internal static readonly Guid PluginId = new Guid("12e5617f-3c98-424f-82fa-2c5512250db0");
        internal static readonly Guid ViewId = new Guid("4697eae9-df48-4867-9d24-283d93266d38");
        private readonly List<ViewItemPlugin> _views = new List<ViewItemPlugin>();

        public override void Init()
        {
            _views.Add(new Client.MicrophoneViewItemPlugin());
        }

        public override void Close() { _views.Clear(); }
        public override Guid Id { get { return PluginId; } }
        public override Guid SharedNodeId { get { return PluginSamples.Common.SampleTopNode; } }
        public override string SharedNodeName { get { return PluginSamples.Common.SampleNodeName; } }
        public override string Name { get { return "Microphone Waveform"; } }
        public override string Manufacturer { get { return PluginSamples.Common.ManufacturerName; } }
        public override string VersionString { get { return "0.1.0"; } }
        public override Image Icon
        {
            get { return VideoOS.Platform.UI.Util.ImageList.Images[VideoOS.Platform.UI.Util.PluginIx]; }
        }
        public override List<ViewItemPlugin> ViewItemPlugins { get { return _views; } }
    }
}
