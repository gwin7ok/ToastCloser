using System;
using System.Windows.Forms;
using System.Runtime.InteropServices;
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
        private TextBoxBase txtDisplayLimit = null!;
        private TextBoxBase txtPollInterval = null!;
        private TextBoxBase txtLogArchiveLimit = null!;
        private Button btnOpenLogs = null!;
        private CheckBox chkYoutubeOnly = null!;
        private TextBoxBase txtIdleMS = null!;
        private TextBoxBase txtMaxMonitorSeconds = null!;
        private TextBoxBase txtDetectionTimeoutMS = null!;
        private TextBoxBase txtWinShortcutKeyIntervalMS = null!;
        private ComboBox cmbShortcutKeyMode = null!;
        private CheckBox chkDetectOnly = null!;
        private CheckBox chkVerbose = null!;
        private Button btnSave = null!;
        private Button btnCancel = null!;
        // (kept minimal) no recursion guard — keep logic small to preserve current appearance

        private void InitializeComponent()
        {
            // Use a TableLayoutPanel for consistent two-column layout (label / control) and an optional third column
            var tl = new TableLayoutPanel();
            tl.ColumnCount = 3;
            tl.RowCount = 13;
            tl.AutoSize = false;
            tl.AutoSizeMode = AutoSizeMode.GrowOnly;
            tl.Dock = DockStyle.Fill;
            tl.Location = new System.Drawing.Point(10, 10);
            tl.Padding = new Padding(12);
            tl.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 373F)); // label column (reduced to ~2/3 of previous width)
            tl.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 280F)); // control column
            tl.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));  // extra (e.g., open logs button)
            // Set explicit row heights to increase vertical spacing and avoid cramped labels
            for (int i = 0; i < tl.RowCount; i++)
            {
                if (i == 11) // buttons row
                    tl.RowStyles.Add(new RowStyle(SizeType.Absolute, 48F));
                else if (i == 12) // note row
                    tl.RowStyles.Add(new RowStyle(SizeType.Absolute, 48F));
                else
                    tl.RowStyles.Add(new RowStyle(SizeType.Absolute, 48F));
            }

            // Labels
            // Labels: use fixed height and MiddleLeft alignment so text is vertically centered in the row
            var lbl1 = new Label() { Text = "DisplayLimitSeconds:", AutoSize = false, Height = 28, TextAlign = ContentAlignment.MiddleLeft, Dock = DockStyle.Fill, Padding = new Padding(6,0,0,0) };
            var lbl2 = new Label() { Text = "PollIntervalSeconds:", AutoSize = false, Height = 28, TextAlign = ContentAlignment.MiddleLeft, Dock = DockStyle.Fill, Padding = new Padding(6,0,0,0) };
            var lblLogLimit = new Label() { Text = "LogArchiveLimit (max archived files):", AutoSize = false, Height = 28, TextAlign = ContentAlignment.MiddleLeft, Dock = DockStyle.Fill, Padding = new Padding(6,0,0,0) };
            var lblMode = new Label() { Text = "ShortcutKeyMode:", AutoSize = false, Height = 28, TextAlign = ContentAlignment.MiddleLeft, Dock = DockStyle.Fill, Padding = new Padding(6,0,0,0) };
            var lblIdle = new Label() { Text = "ShortcutKeyWaitIdleMS:", AutoSize = false, Height = 28, TextAlign = ContentAlignment.MiddleLeft, Dock = DockStyle.Fill, Padding = new Padding(6,0,0,0) };
            var lblMaxWait = new Label() { Text = "ShortcutKeyMaxWaitSeconds:", AutoSize = false, Height = 28, TextAlign = ContentAlignment.MiddleLeft, Dock = DockStyle.Fill, Padding = new Padding(6,0,0,0) };
            var lblDetect = new Label() { Text = "DetectionTimeoutMS:", AutoSize = false, Height = 28, TextAlign = ContentAlignment.MiddleLeft, Dock = DockStyle.Fill, Padding = new Padding(6,0,0,0) };
            var lblWin = new Label() { Text = "WinShortcutKeyIntervalMS:", AutoSize = false, Height = 28, TextAlign = ContentAlignment.MiddleLeft, Dock = DockStyle.Fill, Padding = new Padding(6,0,0,0) };

            // Controls (ensure existing instances are used)
            this.txtDisplayLimit = new TextBox() { Width = 180, Anchor = AnchorStyles.Left, Margin = new Padding(0,12,0,12), Multiline = true, AcceptsReturn = false, WordWrap = false, BorderStyle = BorderStyle.FixedSingle, Height = 28 };
            this.txtPollInterval = new TextBox() { Width = 180, Anchor = AnchorStyles.Left, Margin = new Padding(0,12,0,12), Multiline = true, AcceptsReturn = false, WordWrap = false, BorderStyle = BorderStyle.FixedSingle, Height = 28 };
            this.txtLogArchiveLimit = new TextBox() { Width = 180, Anchor = AnchorStyles.Left, Margin = new Padding(0,12,0,12), Multiline = true, AcceptsReturn = false, WordWrap = false, BorderStyle = BorderStyle.FixedSingle, Height = 28 };
            this.btnOpenLogs = new Button() { Text = "ログフォルダを開く", Width = 200, Height = 30, Anchor = AnchorStyles.Left, TextAlign = ContentAlignment.MiddleCenter };
            this.chkYoutubeOnly = new CheckBox() { Text = "YouTube の通知のみを対象にする", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(12,12,0,12) };
            this.cmbShortcutKeyMode = new ComboBox() { DropDownStyle = ComboBoxStyle.DropDownList, Margin = new Padding(2) };
            this.cmbShortcutKeyMode.Items.AddRange(new object[] { "noticecenter", "quicksetting" });
            // Use owner-draw to vertically center the combo text reliably; draw using font metrics and nudge upward
            this.cmbShortcutKeyMode.DrawMode = DrawMode.OwnerDrawFixed;
            this.cmbShortcutKeyMode.DrawItem += (s, e) =>
            {
                try
                {
                    e.DrawBackground();
                    string text = (e.Index >= 0 && e.Index < this.cmbShortcutKeyMode.Items.Count) ? this.cmbShortcutKeyMode.Items[e.Index]?.ToString() ?? string.Empty : this.cmbShortcutKeyMode.Text ?? string.Empty;
                    float textH = e.Bounds.Height;
                    try
                    {
                        var ff = this.cmbShortcutKeyMode.Font.FontFamily;
                        var style = this.cmbShortcutKeyMode.Font.Style;
                        float emHeight = ff.GetEmHeight(style);
                        float cellAscent = ff.GetCellAscent(style);
                        float cellDescent = ff.GetCellDescent(style);
                        float dpi = e.Graphics.DpiY;
                        float ascentPx = this.cmbShortcutKeyMode.Font.Size * (cellAscent / emHeight) * (dpi / 72f);
                        float descentPx = this.cmbShortcutKeyMode.Font.Size * (cellDescent / emHeight) * (dpi / 72f);
                        textH = ascentPx + descentPx;
                    }
                    catch { }

                    // center vertically within the bounds, then nudge up slightly to match TextBox baseline
                    float y = e.Bounds.Top + (e.Bounds.Height - textH) / 2f - ComboBoxVerticalNudge;
                    var drawRect = new RectangleF(e.Bounds.Left + 2, y, e.Bounds.Width - 4, textH);
                    using (var brush = new SolidBrush(e.ForeColor))
                    {
                        e.Graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
                        var sf = new StringFormat() { Alignment = StringAlignment.Near, LineAlignment = StringAlignment.Near };
                        e.Graphics.DrawString(text, this.cmbShortcutKeyMode.Font, brush, drawRect, sf);
                    }
                    e.DrawFocusRectangle();
                }
                catch { }
            };
            // Wrap the ComboBox in a bordered panel so it visually matches textboxes with a single-line border
            var pnlComboWrap = new Panel() { Width = 180, Height = 28, BorderStyle = BorderStyle.FixedSingle, Margin = new Padding(0,12,0,12), Anchor = AnchorStyles.Left };
            this.cmbShortcutKeyMode.Dock = DockStyle.Fill;
            pnlComboWrap.Controls.Add(this.cmbShortcutKeyMode);
            // Ensure ItemHeight matches the panel's client height so DrawItem bounds align with visual area
            try { this.cmbShortcutKeyMode.ItemHeight = Math.Max(1, pnlComboWrap.ClientSize.Height); } catch { }
            pnlComboWrap.SizeChanged += (s, e) => { try { this.cmbShortcutKeyMode.ItemHeight = Math.Max(1, pnlComboWrap.ClientSize.Height); } catch { } };
            this.txtIdleMS = new TextBox() { Width = 180, Anchor = AnchorStyles.Left, Margin = new Padding(0,12,0,12), Multiline = true, AcceptsReturn = false, WordWrap = false, BorderStyle = BorderStyle.FixedSingle, Height = 28 };
            this.txtMaxMonitorSeconds = new TextBox() { Width = 180, Anchor = AnchorStyles.Left, Margin = new Padding(0,12,0,12), Multiline = true, AcceptsReturn = false, WordWrap = false, BorderStyle = BorderStyle.FixedSingle, Height = 28 };
            this.txtDetectionTimeoutMS = new TextBox() { Width = 180, Anchor = AnchorStyles.Left, Margin = new Padding(0,12,0,12), Multiline = true, AcceptsReturn = false, WordWrap = false, BorderStyle = BorderStyle.FixedSingle, Height = 28 };
            this.txtWinShortcutKeyIntervalMS = new TextBox() { Width = 180, Anchor = AnchorStyles.Left, Margin = new Padding(0,12,0,12), Multiline = true, AcceptsReturn = false, WordWrap = false, BorderStyle = BorderStyle.FixedSingle, Height = 28 };
            this.chkDetectOnly = new CheckBox() { Text = "検出のみ (DetectOnly)", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(12,12,0,12) };
            this.chkVerbose = new CheckBox() { Text = "VerboseLog", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(12,12,0,12) };

            // Buttons
            this.btnSave = new Button() { Text = "保存", Width = 100, Height = 32, TextAlign = ContentAlignment.MiddleCenter, Margin = new Padding(6,8,6,8) };
            this.btnCancel = new Button() { Text = "キャンセル", Width = 140, Height = 32, TextAlign = ContentAlignment.MiddleCenter, Margin = new Padding(6,8,6,8) };

            // Add rows
            tl.Controls.Add(lbl1, 0, 0); tl.Controls.Add(this.txtDisplayLimit, 1, 0);
            tl.Controls.Add(lbl2, 0, 1); tl.Controls.Add(this.txtPollInterval, 1, 1);
            tl.Controls.Add(lblLogLimit, 0, 2); tl.Controls.Add(this.txtLogArchiveLimit, 1, 2); tl.Controls.Add(this.btnOpenLogs, 2, 2);
            tl.Controls.Add(this.chkYoutubeOnly, 0, 3); tl.SetColumnSpan(this.chkYoutubeOnly, 3);
            tl.Controls.Add(lblMode, 0, 4); tl.Controls.Add(pnlComboWrap, 1, 4);
            tl.Controls.Add(lblIdle, 0, 5); tl.Controls.Add(this.txtIdleMS, 1, 5);
            tl.Controls.Add(lblMaxWait, 0, 6); tl.Controls.Add(this.txtMaxMonitorSeconds, 1, 6);
            tl.Controls.Add(lblDetect, 0, 7); tl.Controls.Add(this.txtDetectionTimeoutMS, 1, 7);
            tl.Controls.Add(lblWin, 0, 8); tl.Controls.Add(this.txtWinShortcutKeyIntervalMS, 1, 8);
            tl.Controls.Add(this.chkDetectOnly, 0, 9); tl.SetColumnSpan(this.chkDetectOnly, 3);
            tl.Controls.Add(this.chkVerbose, 0, 10); tl.SetColumnSpan(this.chkVerbose, 3);

            // Buttons + note container: buttons on top, informational note directly beneath
            var buttonsContainer = new TableLayoutPanel() { ColumnCount = 1, RowCount = 2, AutoSize = true, Dock = DockStyle.Fill };
            buttonsContainer.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            buttonsContainer.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            var fl = new FlowLayoutPanel() { FlowDirection = FlowDirection.LeftToRight, AutoSize = true, Anchor = AnchorStyles.None };
            fl.Controls.Add(this.btnSave); fl.Controls.Add(this.btnCancel);
            buttonsContainer.Controls.Add(fl, 0, 0);
            // Informational note label will be added into the container below the buttons
            tl.Controls.Add(buttonsContainer, 0, 11); tl.SetColumnSpan(buttonsContainer, 3);

            // Adjust textboxes to vertically center their text
            AdjustTextBoxVertical(this.txtDisplayLimit);
            AdjustTextBoxVertical(this.txtPollInterval);
            AdjustTextBoxVertical(this.txtLogArchiveLimit);
            AdjustTextBoxVertical(this.txtIdleMS);
            AdjustTextBoxVertical(this.txtMaxMonitorSeconds);
            AdjustTextBoxVertical(this.txtDetectionTimeoutMS);
            AdjustTextBoxVertical(this.txtWinShortcutKeyIntervalMS);

            // Prevent Enter from inserting newlines (defense-in-depth)
            Action<TextBoxBase> suppressEnter = tb => tb.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; } };
            suppressEnter(this.txtDisplayLimit);
            suppressEnter(this.txtPollInterval);
            suppressEnter(this.txtLogArchiveLimit);
            suppressEnter(this.txtIdleMS);
            suppressEnter(this.txtMaxMonitorSeconds);
            suppressEnter(this.txtDetectionTimeoutMS);
            suppressEnter(this.txtWinShortcutKeyIntervalMS);

            // Informational note under the buttons: place into tl row 12 so it sits directly beneath the buttons row
            var lblNote = new Label()
            {
                Text = "保存された設定は、アプリ再起動後に有効になります",
                AutoSize = false,
                TextAlign = ContentAlignment.MiddleCenter,
                Dock = DockStyle.Fill,
                ForeColor = System.Drawing.SystemColors.ControlDark,
                Height = 22,
            };
            tl.Controls.Add(lblNote, 0, 12);
            tl.SetColumnSpan(lblNote, 3);

            // Finalize form
            this.ClientSize = new System.Drawing.Size(980, 640);
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
        
        // --- Helpers for vertically centering text inside TextBox ---
        private const int EM_SETRECT = 0x00B3;
        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int left; public int top; public int right; public int bottom; }
        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern IntPtr SendMessage(IntPtr hWnd, int Msg, IntPtr wParam, ref RECT lParam);

        private void AdjustTextBoxVertical(TextBoxBase tb)
        {
            try
            {
                if (tb == null) return;
                // TextBoxBase exposes Multiline; AcceptsReturn exists on TextBoxBase as well
                tb.Multiline = true;
                // AcceptsReturn is declared on TextBox, not TextBoxBase in some frameworks; try-catch
                try { dynamic d = tb; d.AcceptsReturn = false; } catch { }
                // Update rect when handle created or resized
                tb.HandleCreated += (s, e) => UpdateTextBoxRect(tb);
                tb.SizeChanged += (s, e) => UpdateTextBoxRect(tb);
                tb.FontChanged += (s, e) => UpdateTextBoxRect(tb);
                tb.TextChanged += (s, e) => UpdateTextBoxRect(tb);
                // Initial set
                if (tb.IsHandleCreated) UpdateTextBoxRect(tb);
            }
            catch { }
        }

        // Small vertical nudge values: TextBox moves down, ComboBox moves up
        private const int TextBoxVerticalNudge = 4; // pixels down (adjusted for visual test)
        private const int ComboBoxVerticalNudge = 3; // pixels up (tuned to final)

        private void UpdateTextBoxRect(TextBoxBase tb)
        {
            if (tb == null) return;
            try
            {
                if (!tb.IsHandleCreated) return;
                using (var g = tb.CreateGraphics())
                {
                    float dpi = g.DpiY;
                    var ff = tb.Font.FontFamily;
                    var style = tb.Font.Style;
                    float emHeight = ff.GetEmHeight(style);
                    float cellAscent = ff.GetCellAscent(style);
                    float cellDescent = ff.GetCellDescent(style);
                    float ascentPx = tb.Font.Size * (cellAscent / emHeight) * (dpi / 72f);
                    float descentPx = tb.Font.Size * (cellDescent / emHeight) * (dpi / 72f);
                    int textH = (int)Math.Round(ascentPx + descentPx);
                    int top = Math.Max(0, (tb.ClientSize.Height - textH) / 2 + TextBoxVerticalNudge);
                    var rc = new RECT { left = 0, top = top, right = tb.ClientSize.Width, bottom = Math.Max(top + textH, top + 1) };
                    SendMessage(tb.Handle, EM_SETRECT, IntPtr.Zero, ref rc);
                }
            }
            catch { }
        }

        // (no logging helper required)
        
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
