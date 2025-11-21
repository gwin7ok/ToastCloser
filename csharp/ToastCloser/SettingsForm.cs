using System;
using System.Windows.Forms;
using System.Diagnostics;
using System.IO;
using System.Drawing;

namespace ToastCloser
{
    public partial class SettingsForm : Form
    {
        private Config _config;
        public event EventHandler<Config>? ConfigSaved;

        public SettingsForm(Config cfg)
        {
            _config = cfg;
            InitializeComponent();
            LoadValues();
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            try
            {
                ApplySavedWindowGeometry(_config);
            }
            catch { }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            try
            {
                // Persist current geometry even if user cancelled
                SaveCurrentWindowGeometry(_config);
                try { _config.Save(); } catch { }
            }
            catch { }
            base.OnFormClosing(e);
        }

        private void LoadValues()
        {
            txtDisplayLimit.Text = _config.DisplayLimitSeconds.ToString();
            txtPollInterval.Text = _config.PollIntervalSeconds.ToString();
            chkDetectOnly.Checked = _config.DetectOnly;
            cmbShortcutKeyMode.SelectedItem = _config.ShortcutKeyMode ?? "noticecenter";
            txtIdleMS.Text = _config.ShortcutKeyWaitIdleMS.ToString();
            txtMaxMonitorSeconds.Text = _config.ShortcutKeyMaxWaitSeconds.ToString();
            txtDetectionTimeoutMS.Text = _config.DetectionTimeoutMS.ToString();
            txtWinShortcutKeyIntervalMS.Text = _config.WinShortcutKeyIntervalMS.ToString();
            txtLogArchiveLimit.Text = _config.LogArchiveLimit.ToString();
            chkYoutubeOnly.Checked = _config.YoutubeOnly;
            chkVerbose.Checked = _config.VerboseLog;
        }

        private void SaveValues()
        {
            double.TryParse(txtDisplayLimit.Text, out var d); _config.DisplayLimitSeconds = d;
            double.TryParse(txtPollInterval.Text, out var p); _config.PollIntervalSeconds = p;
            _config.DetectOnly = chkDetectOnly.Checked;
            _config.ShortcutKeyMode = cmbShortcutKeyMode.SelectedItem?.ToString() ?? "noticecenter";
            int.TryParse(txtIdleMS.Text, out var im); _config.ShortcutKeyWaitIdleMS = im;
            int.TryParse(txtMaxMonitorSeconds.Text, out var mm); _config.ShortcutKeyMaxWaitSeconds = mm;
            int.TryParse(txtDetectionTimeoutMS.Text, out var dt); _config.DetectionTimeoutMS = dt;
            int.TryParse(txtWinShortcutKeyIntervalMS.Text, out var wd); _config.WinShortcutKeyIntervalMS = wd;
            int.TryParse(txtLogArchiveLimit.Text, out var lal); _config.LogArchiveLimit = lal;
            _config.YoutubeOnly = chkYoutubeOnly.Checked;
            _config.VerboseLog = chkVerbose.Checked;
        }

        private void btnSave_Click(object? sender, EventArgs e)
        {
            SaveValues();
            try { _config.Save(); } catch { }
            ConfigSaved?.Invoke(this, _config);
            this.Close();
        }

        private void btnCancel_Click(object? sender, EventArgs e)
        {
            this.Close();
        }

        #region Designer
        private TextBox txtDisplayLimit = null!;
        private TextBox txtPollInterval = null!;
        private TextBox txtLogArchiveLimit = null!;
        private Button btnOpenLogs = null!;
        private CheckBox chkYoutubeOnly = null!;
        private TextBox txtIdleMS = null!;
        private TextBox txtMaxMonitorSeconds = null!;
        private TextBox txtDetectionTimeoutMS = null!;
        private TextBox txtWinShortcutKeyIntervalMS = null!;
        private ComboBox cmbShortcutKeyMode = null!;
        private CheckBox chkDetectOnly = null!;
        private CheckBox chkVerbose = null!;
        private Button btnSave = null!;
        private Button btnCancel = null!;

        private void InitializeComponent()
        {
            // Use a TableLayoutPanel for consistent two-column layout (label / control) and an optional third column
            var tl = new TableLayoutPanel();
            tl.ColumnCount = 3;
            tl.RowCount = 12;
            tl.AutoSize = true;
            tl.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            tl.Location = new System.Drawing.Point(10, 10);
            tl.Padding = new Padding(6);
            tl.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 380F)); // label column (widened to avoid wrapping)
            tl.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 220F)); // control column
            tl.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));  // extra (e.g., open logs button)
            // Set explicit row heights to increase vertical spacing and avoid cramped labels
            for (int i = 0; i < tl.RowCount; i++)
            {
                tl.RowStyles.Add(new RowStyle(SizeType.Absolute, 36F));
            }

            // Labels
            var lbl1 = new Label() { Text = "DisplayLimitSeconds:", Anchor = AnchorStyles.Left, AutoSize = true };
            var lbl2 = new Label() { Text = "PollIntervalSeconds:", Anchor = AnchorStyles.Left, AutoSize = true };
            var lblLogLimit = new Label() { Text = "LogArchiveLimit (max archived files):", Anchor = AnchorStyles.Left, AutoSize = true };
            var lblMode = new Label() { Text = "ShortcutKeyMode:", Anchor = AnchorStyles.Left, AutoSize = true };
            var lblIdle = new Label() { Text = "ShortcutKeyWaitIdleMS:", Anchor = AnchorStyles.Left, AutoSize = true };
            var lblMaxWait = new Label() { Text = "ShortcutKeyMaxWaitSeconds:", Anchor = AnchorStyles.Left, AutoSize = true };
            var lblDetect = new Label() { Text = "DetectionTimeoutMS:", Anchor = AnchorStyles.Left, AutoSize = true };
            var lblWin = new Label() { Text = "WinShortcutKeyIntervalMS:", Anchor = AnchorStyles.Left, AutoSize = true };

            // Controls (ensure existing instances are used)
            this.txtDisplayLimit = new TextBox() { Width = 180, Anchor = AnchorStyles.Left };
            this.txtPollInterval = new TextBox() { Width = 180, Anchor = AnchorStyles.Left };
            this.txtLogArchiveLimit = new TextBox() { Width = 180, Anchor = AnchorStyles.Left };
            this.btnOpenLogs = new Button() { Text = "ログフォルダを開く", Width = 200, Height = 30, Anchor = AnchorStyles.Left, TextAlign = ContentAlignment.MiddleCenter };
            this.chkYoutubeOnly = new CheckBox() { Text = "YouTube の通知のみを対象にする", AutoSize = true, Anchor = AnchorStyles.Left };
            this.cmbShortcutKeyMode = new ComboBox() { Width = 180, DropDownStyle = ComboBoxStyle.DropDownList, Anchor = AnchorStyles.Left };
            this.cmbShortcutKeyMode.Items.AddRange(new object[] { "noticecenter", "quicksetting" });
            this.txtIdleMS = new TextBox() { Width = 180, Anchor = AnchorStyles.Left };
            this.txtMaxMonitorSeconds = new TextBox() { Width = 180, Anchor = AnchorStyles.Left };
            this.txtDetectionTimeoutMS = new TextBox() { Width = 180, Anchor = AnchorStyles.Left };
            this.txtWinShortcutKeyIntervalMS = new TextBox() { Width = 180, Anchor = AnchorStyles.Left };
            this.chkDetectOnly = new CheckBox() { Text = "検出のみ (DetectOnly)", AutoSize = true, Anchor = AnchorStyles.Left };
            this.chkVerbose = new CheckBox() { Text = "VerboseLog", AutoSize = true, Anchor = AnchorStyles.Left };

            // Buttons
            this.btnSave = new Button() { Text = "保存", Width = 100, Height = 32, TextAlign = ContentAlignment.MiddleCenter };
            this.btnCancel = new Button() { Text = "キャンセル", Width = 140, Height = 32, TextAlign = ContentAlignment.MiddleCenter };

            // Add rows
            tl.Controls.Add(lbl1, 0, 0); tl.Controls.Add(this.txtDisplayLimit, 1, 0);
            tl.Controls.Add(lbl2, 0, 1); tl.Controls.Add(this.txtPollInterval, 1, 1);
            tl.Controls.Add(lblLogLimit, 0, 2); tl.Controls.Add(this.txtLogArchiveLimit, 1, 2); tl.Controls.Add(this.btnOpenLogs, 2, 2);
            tl.Controls.Add(this.chkYoutubeOnly, 0, 3); tl.SetColumnSpan(this.chkYoutubeOnly, 3);
            tl.Controls.Add(lblMode, 0, 4); tl.Controls.Add(this.cmbShortcutKeyMode, 1, 4);
            tl.Controls.Add(lblIdle, 0, 5); tl.Controls.Add(this.txtIdleMS, 1, 5);
            tl.Controls.Add(lblMaxWait, 0, 6); tl.Controls.Add(this.txtMaxMonitorSeconds, 1, 6);
            tl.Controls.Add(lblDetect, 0, 7); tl.Controls.Add(this.txtDetectionTimeoutMS, 1, 7);
            tl.Controls.Add(lblWin, 0, 8); tl.Controls.Add(this.txtWinShortcutKeyIntervalMS, 1, 8);
            tl.Controls.Add(this.chkDetectOnly, 0, 9); tl.SetColumnSpan(this.chkDetectOnly, 3);
            tl.Controls.Add(this.chkVerbose, 0, 10); tl.SetColumnSpan(this.chkVerbose, 3);

            // Buttons panel
            var fl = new FlowLayoutPanel() { FlowDirection = FlowDirection.LeftToRight, AutoSize = true, Anchor = AnchorStyles.None };
            fl.Controls.Add(this.btnSave); fl.Controls.Add(this.btnCancel);
            tl.Controls.Add(fl, 0, 11); tl.SetColumnSpan(fl, 3);

            // Finalize form
            this.ClientSize = new System.Drawing.Size(980, 560);
            this.Controls.Clear();
            this.Controls.Add(tl);
            this.Text = "ToastCloser 設定";
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.MaximizeBox = false;
            this.MinimizeBox = false;

            // Wire events
            btnSave.Click += btnSave_Click;
            btnCancel.Click += btnCancel_Click;
            btnOpenLogs.Click += btnOpenLogs_Click;
        }

        private void btnOpenLogs_Click(object? sender, EventArgs e)
        {
            try
            {
                var logsDir = Path.Combine(AppContext.BaseDirectory, "logs");
                try { Directory.CreateDirectory(logsDir); } catch { }
                var psi = new ProcessStartInfo("explorer.exe", logsDir) { UseShellExecute = true };
                Process.Start(psi);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"ログフォルダを開けませんでした: {ex.Message}", "エラー", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
        
        private void ApplySavedWindowGeometry(Config cfg)
        {
            try
            {
                // Restore only position (Left/Top). Size is fixed by the program.
                if (cfg.SettingsLeft != 0 || cfg.SettingsTop != 0)
                {
                    this.StartPosition = FormStartPosition.Manual;
                    var left = cfg.SettingsLeft;
                    var top = cfg.SettingsTop;
                    // Use current size for visibility checks
                    var rect = new System.Drawing.Rectangle(left, top, this.Width, this.Height);
                    rect = EnsureVisible(rect);
                    this.Location = rect.Location;
                }
                if (!string.IsNullOrEmpty(cfg.SettingsWindowState))
                {
                    try
                    {
                        if (string.Equals(cfg.SettingsWindowState, "Maximized", StringComparison.OrdinalIgnoreCase)) this.WindowState = FormWindowState.Maximized;
                        else if (string.Equals(cfg.SettingsWindowState, "Minimized", StringComparison.OrdinalIgnoreCase)) this.WindowState = FormWindowState.Minimized;
                        else this.WindowState = FormWindowState.Normal;
                    }
                    catch { }
                }
            }
            catch { }
        }

        private void SaveCurrentWindowGeometry(Config cfg)
        {
            try
            {
                var useBounds = (this.WindowState == FormWindowState.Normal) ? this.Bounds : this.RestoreBounds;
                cfg.SettingsLeft = useBounds.Left;
                cfg.SettingsTop = useBounds.Top;
                // Do NOT save width/height for settings window; size is fixed by program.
                cfg.SettingsWindowState = this.WindowState.ToString();
            }
            catch { }
        }

        private static System.Drawing.Rectangle EnsureVisible(System.Drawing.Rectangle rect)
        {
            try
            {
                foreach (var s in Screen.AllScreens)
                {
                    var wa = s.WorkingArea;
                    if (wa.IntersectsWith(rect))
                    {
                        int left = Math.Max(wa.Left, Math.Min(rect.Left, wa.Right - Math.Min(rect.Width, wa.Width)));
                        int top = Math.Max(wa.Top, Math.Min(rect.Top, wa.Bottom - Math.Min(rect.Height, wa.Height)));
                        int width = Math.Min(rect.Width, wa.Width);
                        int height = Math.Min(rect.Height, wa.Height);
                        return new System.Drawing.Rectangle(left, top, width, height);
                    }
                }
                var primary = Screen.PrimaryScreen;
                var p = primary != null ? primary.WorkingArea : (Screen.AllScreens.Length > 0 ? Screen.AllScreens[0].WorkingArea : new System.Drawing.Rectangle(0, 0, rect.Width, rect.Height));
                int w = Math.Min(rect.Width, p.Width);
                int h = Math.Min(rect.Height, p.Height);
                return new System.Drawing.Rectangle(p.Left, p.Top, w, h);
            }
            catch { return rect; }
        }
        #endregion
    }
}
