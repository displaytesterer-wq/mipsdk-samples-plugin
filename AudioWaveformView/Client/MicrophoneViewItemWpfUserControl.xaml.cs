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
        private CancellationTokenSource _operation;
        private string _previewFile;
        private double[] _peaks = new double[0];
        private bool _closing;
        private bool _busy;

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
        }

        public override void Close()
        {
            _closing = true;
            if (_operation != null) _operation.Cancel();
            _player.Stop();
            _player.Close();
            _player.MediaFailed -= Player_MediaFailed;
            DeletePreview();
        }

        public override bool Selectable { get { return true; } }
        public override bool Maximizable { get { return true; } }

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
            DeletePreview();
            _peaks = new double[0];
            _manager.Microphone = chosen;
            SelectedMicrophoneText.Text = chosen.Name;
            DrawWaveform();
            StatusText.Text = "Selected microphone: " + chosen.Name;
        }

        private bool TryGetRange(out DateTime start, out DateTime end)
        {
            start = end = DateTime.MinValue;
            DateTime localStart, localEnd;
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
                _manager.Timeline.SetPreview(startUtc, endUtc);
                DrawWaveform();
                StatusText.Text = "Loaded recorded WAV preview from " +
                    startUtc.ToLocalTime().ToString("HH:mm:ss") + " to " +
                    endUtc.ToLocalTime().ToString("HH:mm:ss") +
                    ". Blue timeline ribbon indicates this preview only.";
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
                if (newFile != null) WavExportService.TryDeleteFolder(Path.GetDirectoryName(newFile));
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
            PreviewButton.IsEnabled = !busy;
            ExportButton.IsEnabled = !busy;
        }

        private void DeletePreview()
        {
            if (string.IsNullOrEmpty(_previewFile)) return;
            string folder = Path.GetDirectoryName(_previewFile);
            _previewFile = null;
            WavExportService.TryDeleteFolder(folder);
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
