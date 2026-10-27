using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using VideoOS.Platform;
using VideoOS.Platform.Data;
using VideoOS.Platform.UI;
using VideoOS.Platform.SDK.UI.LoginDialog;

namespace AudioExportDaily4x
{
    internal static class Program
    {
        private static bool _connected;
        private static readonly Guid IntegrationId = new Guid("4AD46D2C-E582-4E66-BAC3-F584A0BB1ED4");

        [STAThread]
        private static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            VideoOS.Platform.SDK.Environment.Initialize();
            VideoOS.Platform.SDK.UI.Environment.Initialize();
            VideoOS.Platform.SDK.Export.Environment.Initialize();
            try
            {
                var login = new DialogLoginForm(
                    connected => _connected = connected,
                    IntegrationId, "Audio Export Daily 4x", "1.0", "Custom MIP SDK sample");
                Application.Run(login);
                if (_connected) Application.Run(new FourSegmentExportForm());
            }
            finally { VideoOS.Platform.SDK.Environment.RemoveAllServers(); }
        }
    }

    internal sealed class FourSegmentExportForm : Form
    {
        private Item _microphone;
        private string _outputFolder;
        private readonly Label _deviceLabel = new Label();
        private readonly Label _folderLabel = new Label();
        private readonly Label _statusLabel = new Label();
        private readonly ComboBox _codecBox = new ComboBox();
        private readonly ComboBox _rateBox = new ComboBox();
        private readonly Button _startButton = new Button();
        private readonly Button _cancelButton = new Button();
        private readonly ProgressBar _progress = new ProgressBar();
        private readonly System.Windows.Forms.Timer _timer = new System.Windows.Forms.Timer();
        private WAVExporter _exporter;
        private DateTime _previousDay;
        private int _segmentIndex;
        private bool _exporting;

        public FourSegmentExportForm()
        {
            Text = "Milestone: Previous-day WAV export (4 x 6 hours)";
            Width = 650;
            Height = 400;
            MinimumSize = new Size(640, 350);
            StartPosition = FormStartPosition.CenterScreen;
            var layout = new FlowLayoutPanel {
                Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown,
                WrapContents = false, AutoScroll = true, Padding = new Padding(16)
            };
            Controls.Add(layout);
            layout.Controls.Add(new Label {
                AutoSize = true, Font = new Font(Font, FontStyle.Bold),
                Text = "Previous calendar day: 00-06, 06-12, 12-18, 18-24"
            });

            var chooseMic = new Button { Text = "Choose microphone", Width = 170 };
            chooseMic.Click += ChooseMicrophone;
            layout.Controls.Add(chooseMic);
            _deviceLabel.AutoSize = true;
            _deviceLabel.Text = "Microphone: not selected";
            layout.Controls.Add(_deviceLabel);

            var chooseFolder = new Button { Text = "Choose output folder", Width = 170 };
            chooseFolder.Click += ChooseFolder;
            layout.Controls.Add(chooseFolder);
            _folderLabel.AutoSize = true;
            _folderLabel.Text = "Folder: not selected";
            layout.Controls.Add(_folderLabel);

            var formatPanel = new FlowLayoutPanel { Width = 580, Height = 40 };
            formatPanel.Controls.Add(new Label { Text = "Codec", Width = 55, TextAlign = ContentAlignment.MiddleLeft });
            _codecBox.DropDownStyle = ComboBoxStyle.DropDownList;
            _codecBox.Width = 220;
            formatPanel.Controls.Add(_codecBox);
            formatPanel.Controls.Add(new Label { Text = "Sample rate", Width = 90, TextAlign = ContentAlignment.MiddleLeft });
            _rateBox.DropDownStyle = ComboBoxStyle.DropDownList;
            _rateBox.Width = 100;
            _rateBox.Items.AddRange(new object[] { "8000", "16000", "44100" });
            _rateBox.SelectedIndex = 0;
            formatPanel.Controls.Add(_rateBox);
            layout.Controls.Add(formatPanel);

            var actions = new FlowLayoutPanel { Width = 580, Height = 40 };
            _startButton.Text = "Export previous day (4 WAV)";
            _startButton.Width = 245;
            _startButton.Click += StartDailyExport;
            actions.Controls.Add(_startButton);
            _cancelButton.Text = "Cancel";
            _cancelButton.Width = 100;
            _cancelButton.Enabled = false;
            _cancelButton.Click += (sender, args) => StopExport("Cancelled");
            actions.Controls.Add(_cancelButton);
            layout.Controls.Add(actions);
            _progress.Width = 565;
            layout.Controls.Add(_progress);
            _statusLabel.AutoSize = true;
            _statusLabel.MaximumSize = new Size(570, 0);
            _statusLabel.Text = "Select microphone and destination, then export.";
            layout.Controls.Add(_statusLabel);

            _timer.Interval = 500;
            _timer.Tick += PollExport;
            FormClosing += (sender, args) => StopExport(null);
            try
            {
                var probe = new WAVExporter();
                try
                {
                    probe.Init();
                    foreach (string codec in probe.CodecList) _codecBox.Items.Add(codec);
                }
                finally { probe.Close(); }
                if (_codecBox.Items.Count > 0) _codecBox.SelectedIndex = 0;
            }
            catch (Exception ex) { _statusLabel.Text = "Cannot load WAV codecs: " + ex.Message; }
        }

        private void ChooseMicrophone(object sender, EventArgs e)
        {
            if (_exporting) return;
            var picker = new ItemPickerWpfWindow {
                KindsFilter = new List<Guid> { Kind.Microphone },
                Items = Configuration.Instance.GetItems(ItemHierarchy.UserDefined)
            };
            if (picker.ShowDialog() == true)
            {
                _microphone = picker.SelectedItems.FirstOrDefault();
                _deviceLabel.Text = "Microphone: " +
                    (_microphone == null ? "not selected" : _microphone.Name);
            }
        }

        private void ChooseFolder(object sender, EventArgs e)
        {
            if (_exporting) return;
            using (var dialog = new FolderBrowserDialog())
            {
                if (dialog.ShowDialog(this) == DialogResult.OK)
                {
                    _outputFolder = dialog.SelectedPath;
                    _folderLabel.Text = "Folder: " + _outputFolder;
                }
            }
        }

        private void StartDailyExport(object sender, EventArgs e)
        {
            if (_exporting) return;
            if (_microphone == null || string.IsNullOrWhiteSpace(_outputFolder) ||
                _codecBox.SelectedItem == null || _rateBox.SelectedItem == null)
            {
                MessageBox.Show(this, "Choose microphone, output folder and codec first.");
                return;
            }
            // Local calendar-day boundaries; 24:00 is next day's 00:00, exclusive.
            _previousDay = DateTime.Today.AddDays(-1);
            _segmentIndex = 0;
            _exporting = true;
            _startButton.Enabled = false;
            _cancelButton.Enabled = true;
            ExportNextSegment();
        }

        private void ExportNextSegment()
        {
            if (!_exporting) return;
            if (_segmentIndex >= 4)
            {
                _exporting = false;
                _timer.Stop();
                _startButton.Enabled = true;
                _cancelButton.Enabled = false;
                _progress.Value = 100;
                _statusLabel.Text = "Done. Four WAV exports completed for " +
                    _previousDay.ToString("yyyy-MM-dd");
                return;
            }
            DateTime startLocal = _previousDay.AddHours(_segmentIndex * 6);
            DateTime endLocal = _previousDay.AddHours((_segmentIndex + 1) * 6);
            string folder = Path.Combine(_outputFolder, _previousDay.ToString("yyyy-MM-dd"));
            string name = string.Format("{0:yyyyMMdd}_{1:00}00-{2:00}00",
                _previousDay, _segmentIndex * 6, (_segmentIndex + 1) * 6);
            try
            {
                Directory.CreateDirectory(folder);
                _progress.Value = 0;
                _statusLabel.Text = string.Format("Export {0}/4: {1:HH:mm} - {2:HH:mm} | {3}",
                    _segmentIndex + 1, startLocal, endLocal, name);
                _exporter = new WAVExporter {
                    FileName = name, Path = folder, Codec = (string)_codecBox.SelectedItem,
                    AudioSampleRate = int.Parse((string)_rateBox.SelectedItem),
                    AudioList = new List<Item> { _microphone }
                };
                _exporter.Init();
                bool started = _exporter.StartExport(
                    startLocal.ToUniversalTime(), endLocal.ToUniversalTime());
                if (!started)
                {
                    string error = _exporter.LastErrorString + " (" + _exporter.LastError + ")";
                    StopExport("Failed segment " + (_segmentIndex + 1) + ": " + error);
                    return;
                }
                _timer.Start();
            }
            catch (Exception ex)
            {
                StopExport("Failed segment " + (_segmentIndex + 1) + ": " + ex.Message);
            }
        }

        private void PollExport(object sender, EventArgs e)
        {
            if (!_exporting || _exporter == null) return;
            try
            {
                int error = _exporter.LastError;
                if (error != 0)
                {
                    StopExport("Export error (segment " + (_segmentIndex + 1) + "): " +
                        _exporter.LastErrorString + " (" + error + ")");
                    return;
                }
                int progress = _exporter.Progress;
                if (progress >= 0) _progress.Value = Math.Min(100, progress);
                if (progress >= 100)
                {
                    _timer.Stop();
                    FinishCurrentExporter(cancel: false);
                    _segmentIndex++;
                    ExportNextSegment();
                }
            }
            catch (Exception ex) { StopExport("Export failed: " + ex.Message); }
        }

        private void FinishCurrentExporter(bool cancel)
        {
            if (_exporter == null) return;
            try
            {
                if (cancel) _exporter.Cancel();
                _exporter.EndExport();
            }
            finally
            {
                try { _exporter.Close(); }
                finally { _exporter = null; }
            }
        }

        private void StopExport(string message)
        {
            _timer.Stop();
            _exporting = false;
            try { FinishCurrentExporter(cancel: true); }
            catch (Exception ex)
            {
                if (message != null) message += " | Cleanup: " + ex.Message;
            }
            _startButton.Enabled = true;
            _cancelButton.Enabled = false;
            if (message != null) _statusLabel.Text = message;
        }
    }
}
