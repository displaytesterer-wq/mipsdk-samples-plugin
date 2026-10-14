using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Microsoft.Win32;
using VideoOS.Platform;
using VideoOS.Platform.Client;
using VideoOS.Platform.UI;
using VideoOS.Platform.Util;

namespace AudioWaveformView.Client
{
    /// <summary>
    /// Camera-free Smart Client view. A WAV excerpt drives the amplitude display.
    /// It is deliberately NOT represented as a complete recording index.
    /// </summary>
    public partial class MicrophoneViewItemWpfUserControl : ViewItemWpfUserControl
    {
        private readonly MicrophoneViewItemManager _manager;
        private readonly MediaPlayer _player = new MediaPlayer();
        private VideoOS.Platform.Client.AudioPlayer _serverAudioPlayer;
        private CancellationTokenSource _operation;
        private string _previewFile;
        private double[] _peaks = new double[0];
        private bool _closing;
        private bool _busy;
        private DateTime _recordingStartUtc, _recordingEndUtc;
        private List<TimeInterval> _recordingIntervals = new List<TimeInterval>();
        private bool _recordingQueryCompleted;

        public MicrophoneViewItemWpfUserControl(MicrophoneViewItemManager manager)
        {
            _manager = manager;
            InitializeComponent();
            DateTime now = DateTime.Now;
            FromText.Text = now.AddMinutes(-1).ToString("yyyy-MM-dd HH:mm:ss");
            ToText.Text = now.ToString("yyyy-MM-dd HH:mm:ss");
        }

        public override void Init()
        {
            SelectedMicrophoneText.Text = _manager.Microphone == null
                ? "No microphone selected"
                : _manager.Microphone.Name;
            _player.MediaFailed += Player_MediaFailed;
            _manager.Timeline.StatusChanged += Timeline_StatusChanged;
        }

        public override void Close()
        {
            _closing = true;
            if (_operation != null) _operation.Cancel();
            _player.Stop();
            _player.Close();
            _player.MediaFailed -= Player_MediaFailed;
            DisconnectServerAudio();
            _manager.Timeline.StatusChanged -= Timeline_StatusChanged;
            DeletePreview();
        }

        public override bool Selectable { get { return true; } }
        public override bool Maximizable { get { return true; } }

        private void Timeline_StatusChanged(string message)
        {
            if (_closing) return;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (!_closing && !_busy) StatusText.Text = message;
            }));
        }

        private void Player_MediaFailed(object sender, ExceptionEventArgs e)
        {
            StatusText.Text = "Playback failed: " + e.ErrorException.Message;
        }

        private void SelectMicrophone_Click(object sender, RoutedEventArgs e)
        {
            var picker = new ItemPickerWpfWindow
            {
                KindsFilter = new List<Guid> { Kind.Microphone },
                SelectionMode = SelectionModeOptions.AutoCloseOnSelect,
                Items = Configuration.Instance.GetItemsByKind(Kind.Microphone)
            };
            picker.ShowDialog();
            if (picker.SelectedItems == null || !picker.SelectedItems.Any()) return;

            var chosen = picker.SelectedItems.First();
            _player.Stop();
            _player.Close();
            DisconnectServerAudio();
            DeletePreview();
            _peaks = new double[0];
            _manager.Microphone = chosen;
            _recordingIntervals.Clear();
            _recordingQueryCompleted = false;
            RecordingInfoText.Text = "Not queried for this microphone";
            DrawRecordingIntervals();
            SelectedMicrophoneText.Text = chosen.Name;
            DrawWaveform();
            StatusText.Text = "Selected microphone: " + chosen.Name;
        }

        private void DisconnectServerAudio()
        {
            if (_serverAudioPlayer == null) return;
            try { _serverAudioPlayer.Disconnect(); }
            catch { }
            try { _serverAudioPlayer.Close(); }
            catch { }
            _serverAudioPlayer = null;
        }

        private void ServerListen_Click(object sender, RoutedEventArgs e)
        {
            if (_manager.Microphone == null)
            {
                StatusText.Text = "Select a microphone first.";
                return;
            }
            try
            {
                _player.Stop();
                DisconnectServerAudio();
                // Same microphone FQID used by Smart Client Audio > Microphones.
                // null controller means the player's time follows Smart Client's
                // main Playback controller in the main window.
                _serverAudioPlayer = new VideoOS.Platform.Client.AudioPlayer();
                _serverAudioPlayer.MicrophoneFQID = _manager.Microphone.FQID;
                _serverAudioPlayer.PlaybackControllerFQID = null;
                _serverAudioPlayer.Initialize();
                _serverAudioPlayer.Connect();
                StatusText.Text = "Connected direct AudioPlayer to " + _manager.Microphone.Name +
                    ". Follow Smart Client's Live/Playback controls. Connection state: " +
                    _serverAudioPlayer.ConnectionState;
            }
            catch (Exception ex)
            {
                DisconnectServerAudio();
                StatusText.Text = "XProtect direct audio connection failed: " + ex.Message;
            }
        }

        private void ServerDisconnect_Click(object sender, RoutedEventArgs e)
        {
            DisconnectServerAudio();
            StatusText.Text = "XProtect direct audio disconnected.";
        }

        private bool TryGetRange(out DateTime start, out DateTime end)
        {
            start = end = DateTime.MinValue;
            DateTime localStart = DateTime.MinValue, localEnd = DateTime.MinValue;
            const string format = "yyyy-MM-dd HH:mm:ss";
            bool parsed = DateTime.TryParseExact(FromText.Text.Trim(), format,
                CultureInfo.InvariantCulture, DateTimeStyles.None, out localStart)
                && DateTime.TryParseExact(ToText.Text.Trim(), format,
                    CultureInfo.InvariantCulture, DateTimeStyles.None, out localEnd);
            if (!parsed)
            {
                StatusText.Text = "Enter local times in yyyy-MM-dd HH:mm:ss format.";
                return false;
            }
            if (localEnd <= localStart)
            {
                StatusText.Text = "End must be later than start.";
                return false;
            }
            start = DateTime.SpecifyKind(localStart, DateTimeKind.Local).ToUniversalTime();
            end = DateTime.SpecifyKind(localEnd, DateTimeKind.Local).ToUniversalTime();
            return true;
        }

        private async void CheckRecording_Click(object sender, RoutedEventArgs e)
        {
            if (_manager.Microphone == null)
            {
                StatusText.Text = "Select a microphone first.";
                return;
            }
            DateTime startUtc, endUtc;
            if (!TryGetRange(out startUtc, out endUtc)) return;
            if (_busy)
            {
                StatusText.Text = "Please wait for the current operation.";
                return;
            }

            _busy = true;
            SetBusy(true);
            _operation = new CancellationTokenSource();
            try
            {
                StatusText.Text = "Querying recording sequences for the selected microphone...";
                CancellationToken token = _operation.Token;
                var query = await Task.Run(() =>
                    _manager.Timeline.QuerySelected(startUtc, endUtc, token), token);
                if (_closing) return;

                _recordingIntervals = query.Supported
                    ? query.Intervals
                    : new List<TimeInterval>();
                _recordingQueryCompleted = query.Supported;
                _recordingStartUtc = startUtc;
                _recordingEndUtc = endUtc;
                RecordingInfoText.Text = query.Supported
                    ? query.Intervals.Count + " exact server interval(s) / " + query.Requests + " request(s)"
                    : "Unsupported sequence index";
                DrawRecordingIntervals();
                StatusText.Text = query.Supported ? query.Message
                    : "Cannot verify recording intervals: " + query.Message;
            }
            catch (OperationCanceledException)
            {
                if (!_closing) StatusText.Text = "Recording index query cancelled.";
            }
            catch (Exception ex)
            {
                _recordingIntervals.Clear();
                _recordingQueryCompleted = false;
                RecordingInfoText.Text = "Recording index query failed";
                DrawRecordingIntervals();
                if (!_closing) StatusText.Text = "Recording index unavailable: " + ex.Message;
            }
            finally
            {
                if (!_closing) SetBusy(false);
                _busy = false;
                if (_operation != null) { _operation.Dispose(); _operation = null; }
            }
        }

        private async void Preview_Click(object sender, RoutedEventArgs e)
        {
            if (_manager.Microphone == null)
            {
                StatusText.Text = "Select a microphone first.";
                return;
            }
            DateTime startUtc, endUtc;
            if (!TryGetRange(out startUtc, out endUtc)) return;
            if ((endUtc - startUtc) > TimeSpan.FromMinutes(5))
            {
                StatusText.Text = "Waveform preview is limited to five minutes. Narrow the selected range.";
                return;
            }
            await ExportAndLoadAsync(_manager.Microphone, startUtc, endUtc, false);
        }

        private async void Export_Click(object sender, RoutedEventArgs e)
        {
            if (_manager.Microphone == null)
            {
                StatusText.Text = "Select a microphone first.";
                return;
            }
            DateTime startUtc, endUtc;
            if (!TryGetRange(out startUtc, out endUtc)) return;

            var dialog = new SaveFileDialog
            {
                Filter = "WAV audio|*.wav",
                DefaultExt = ".wav",
                AddExtension = true,
                FileName = "microphone-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".wav"
            };
            if (dialog.ShowDialog() != true) return;
            await ExportAndLoadAsync(_manager.Microphone, startUtc, endUtc, true, dialog.FileName);
        }

        private async Task ExportAndLoadAsync(Item microphone, DateTime startUtc, DateTime endUtc,
            bool exportToFile, string destination = null)
        {
            if (_busy) { StatusText.Text = "Another export is already running."; return; }
            _busy = true;
            SetBusy(true);
            _operation = new CancellationTokenSource();
            string newFile = null;
            try
            {
                StatusText.Text = "Exporting microphone audio from XProtect Recording Server...";
                var token = _operation.Token;
                newFile = await Task.Run(() => WavExportService.ExportToTemporaryWav(
                    microphone, startUtc, endUtc, token), token);
                if (_closing) return;

                if (exportToFile)
                {
                    File.Copy(newFile, destination, true);
                    StatusText.Text = "WAV exported: " + destination;
                    return;
                }

                var localFile = newFile;
                double[] peaks = await Task.Run(() => WavPeakReader.ReadPeaks(localFile), token);
                if (_closing) return;

                _player.Stop();
                _player.Close();
                DeletePreview();
                _previewFile = newFile;
                newFile = null;
                _peaks = peaks;
                _player.Open(new Uri(_previewFile, UriKind.Absolute));
                                DrawWaveform();
                StatusText.Text = "Loaded recorded WAV preview from " +
                    startUtc.ToLocalTime().ToString("HH:mm:ss") + " to " +
                    endUtc.ToLocalTime().ToString("HH:mm:ss") +
                    ". Recording intervals come from the server index, not the exported WAV.";
            }
            catch (OperationCanceledException)
            {
                if (!_closing) StatusText.Text = "Operation cancelled.";
            }
            catch (Exception ex)
            {
                if (!_closing) StatusText.Text = "Audio operation failed: " + ex.Message;
            }
            finally
            {
                if (newFile != null) WavExportService.TryDeleteFolder(System.IO.Path.GetDirectoryName(newFile));
                if (!_closing) SetBusy(false);
                _busy = false;
                if (_operation != null) { _operation.Dispose(); _operation = null; }
            }
        }

        private void Play_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(_previewFile) || !File.Exists(_previewFile))
            {
                StatusText.Text = "Load a waveform first.";
                return;
            }
            DisconnectServerAudio();
            _player.Play();
            StatusText.Text = "Playing the selected WAV preview.";
        }

        private void Stop_Click(object sender, RoutedEventArgs e)
        {
            _player.Stop();
            StatusText.Text = "Playback stopped.";
        }

        private void SetBusy(bool busy)
        {
            SelectMicrophoneButton.IsEnabled = !busy;
            CheckRecordingButton.IsEnabled = !busy;
            PreviewButton.IsEnabled = !busy;
            ExportButton.IsEnabled = !busy;
        }

        private void DeletePreview()
        {
            if (string.IsNullOrEmpty(_previewFile)) return;
            string folder = System.IO.Path.GetDirectoryName(_previewFile);
            _previewFile = null;
            WavExportService.TryDeleteFolder(folder);
        }

        private void RecordingCanvas_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            DrawRecordingIntervals();
        }

        private void DrawRecordingIntervals()
        {
            if (RecordingCanvas == null) return;
            RecordingCanvas.Children.Clear();
            double width = RecordingCanvas.ActualWidth;
            double height = RecordingCanvas.ActualHeight;
            if (!_recordingQueryCompleted || width <= 0 || height <= 0 ||
                _recordingEndUtc <= _recordingStartUtc) return;

            double totalTicks = (_recordingEndUtc - _recordingStartUtc).Ticks;
            foreach (TimeInterval span in _recordingIntervals)
            {
                double x = (span.StartTime - _recordingStartUtc).Ticks / totalTicks * width;
                double end = (span.EndTime - _recordingStartUtc).Ticks / totalTicks * width;
                x = Math.Max(0, Math.Min(width, x));
                end = Math.Max(0, Math.Min(width, end));
                if (end <= x) continue;
                var bar = new Rectangle
                {
                    Width = Math.Max(0.5, end - x),
                    Height = height,
                    Fill = new SolidColorBrush(Color.FromRgb(57, 180, 118)),
                    ToolTip = span.StartTime.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") +
                        " — " + span.EndTime.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")
                };
                Canvas.SetLeft(bar, x);
                Canvas.SetTop(bar, 0);
                RecordingCanvas.Children.Add(bar);
            }
        }

        private void WaveformCanvas_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            DrawWaveform();
        }

        private void DrawWaveform()
        {
            if (WaveformCanvas == null) return;
            WaveformCanvas.Children.Clear();
            double width = WaveformCanvas.ActualWidth;
            double height = WaveformCanvas.ActualHeight;
            if (width <= 0 || height <= 0) return;
            var center = new Line
            {
                X1 = 0, X2 = width, Y1 = height / 2, Y2 = height / 2,
                Stroke = new SolidColorBrush(Color.FromRgb(55, 79, 95)), StrokeThickness = 1
            };
            WaveformCanvas.Children.Add(center);
            if (_peaks == null || _peaks.Length == 0) return;
            double barWidth = width / _peaks.Length;
            for (int i = 0; i < _peaks.Length; i++)
            {
                double size = Math.Max(2, _peaks[i] * (height - 14));
                var bar = new Rectangle
                {
                    Width = Math.Max(1, barWidth * 0.67), Height = size,
                    Fill = new SolidColorBrush(Color.FromRgb(59, 209, 169))
                };
                Canvas.SetLeft(bar, i * barWidth);
                Canvas.SetTop(bar, (height - size) / 2);
                WaveformCanvas.Children.Add(bar);
            }
        }

        private void WaveformCanvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            FireClickEvent();
            if (string.IsNullOrEmpty(_previewFile) || WaveformCanvas.ActualWidth <= 0
                || !_player.NaturalDuration.HasTimeSpan) return;
            double ratio = Math.Max(0, Math.Min(1,
                e.GetPosition(WaveformCanvas).X / WaveformCanvas.ActualWidth));
            _player.Position = TimeSpan.FromTicks((long)(_player.NaturalDuration.TimeSpan.Ticks * ratio));
            _player.Play();
        }
    }
}
