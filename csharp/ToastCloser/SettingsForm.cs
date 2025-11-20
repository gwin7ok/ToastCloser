using System;
using System.Windows.Forms;

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
            // Larger layout so labels and inputs are not truncated
            this.txtDisplayLimit = new TextBox() { Left = 380, Top = 20, Width = 140 };
            this.txtPollInterval = new TextBox() { Left = 380, Top = 60, Width = 140 };
            this.chkDetectOnly = new CheckBox() { Left = 20, Top = 100, Text = "検出のみ (DetectOnly)", AutoSize = true };
            this.cmbShortcutKeyMode = new ComboBox() { Left = 380, Top = 140, Width = 160, DropDownStyle = ComboBoxStyle.DropDownList };
            this.cmbShortcutKeyMode.Items.AddRange(new object[] { "noticecenter", "quicksetting" });
            this.txtIdleMS = new TextBox() { Left = 380, Top = 180, Width = 140 };
            this.txtMaxMonitorSeconds = new TextBox() { Left = 380, Top = 220, Width = 140 };
            this.txtDetectionTimeoutMS = new TextBox() { Left = 380, Top = 260, Width = 140 };
            this.txtWinShortcutKeyIntervalMS = new TextBox() { Left = 380, Top = 300, Width = 140 };
            this.chkVerbose = new CheckBox() { Left = 20, Top = 340, Text = "VerboseLog", AutoSize = true };
            this.btnSave = new Button() { Text = "保存", Left = 200, Width = 100, Top = 380 };
            this.btnCancel = new Button() { Text = "キャンセル", Left = 320, Width = 140, Top = 380 };

            var lbl1 = new Label() { Left = 20, Top = 22, Width = 250, Text = "DisplayLimitSeconds:" , AutoSize = false};
            var lbl2 = new Label() { Left = 20, Top = 62, Width = 250, Text = "PollIntervalSeconds:", AutoSize = false };
            var lbl3 = new Label() { Left = 20, Top = 182, Width = 340, Text = "ShortcutKeyWaitIdleMS:", AutoSize = false };
            var lbl4 = new Label() { Left = 20, Top = 222, Width = 340, Text = "ShortcutKeyMaxWaitSeconds:", AutoSize = false };
            var lbl5 = new Label() { Left = 20, Top = 262, Width = 340, Text = "DetectionTimeoutMS:", AutoSize = false };
            var lbl6 = new Label() { Left = 20, Top = 302, Width = 340, Text = "WinShortcutKeyIntervalMS:", AutoSize = false };
            var lblMode = new Label() { Left = 20, Top = 142, Width = 340, Text = "ShortcutKeyMode:", AutoSize = false };

            this.ClientSize = new System.Drawing.Size(760, 460);
            this.Controls.AddRange(new Control[] { lbl1, lbl2, lblMode, lbl3, lbl4, lbl5, lbl6, txtDisplayLimit, txtPollInterval, cmbShortcutKeyMode, txtIdleMS, txtMaxMonitorSeconds, txtDetectionTimeoutMS, txtWinShortcutKeyIntervalMS, chkDetectOnly, chkVerbose, btnSave, btnCancel });
            this.Text = "ToastCloser 設定";
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.MaximizeBox = false;
            this.MinimizeBox = false;

            btnSave.Click += btnSave_Click;
            btnCancel.Click += btnCancel_Click;
        }
        #endregion
    }
}
