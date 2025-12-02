using System;
using System.Windows.Forms;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace ToastCloser
{
    public partial class ConsoleForm : Form
    {
        private RichTextBox _rtb = null!;
        private bool _autoScroll = true;
        // When true, ignore scroll events as they are caused by programmatic updates
        private volatile bool _programmaticScroll = false;
        private Button _btnAutoScroll = null!;
        private Panel _scrollAccentPanel = null!;
        // When autoscroll is OFF, newly arrived log lines are stored here until user re-enables
        private readonly object _pendingLock = new object();
        private readonly System.Collections.Generic.List<string> _pendingLines = new System.Collections.Generic.List<string>();

        public ConsoleForm()
        {
            InitializeComponent();
            try
            {
                Program.Logger.Instance?.Debug("ConsoleForm: constructor start");
            }
            catch { }

            // Defer loading past logs and wiring logger/scroll detection until OnLoad
            try
            {
                Program.Logger.Instance?.Debug("ConsoleForm: deferring LoadPastLogs/SubscribeLogger/WireScrollDetection until OnLoad");
            }
            catch { }

            try
            {
                Program.Logger.Instance?.Debug("ConsoleForm: constructor done");
            }
            catch { }
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);

            // Ensure logger finished rotation/initialization before reading logs
            try
            {
                Program.Logger.Instance?.Debug("ConsoleForm.OnLoad: waiting for Logger readiness");
                Program.Logger.WaitUntilReady(3000);
            }
            catch { }

            // Load past logs first, then subscribe for live updates
            try
            {
                Program.Logger.Instance?.Debug("ConsoleForm: starting LoadPastLogs()");
                LoadPastLogs();
                Program.Logger.Instance?.Debug("ConsoleForm: LoadPastLogs() completed");
            }
            catch (Exception ex)
            {
                try { Program.Logger.Instance?.Error("ConsoleForm: LoadPastLogs() failed: " + ex.Message); } catch { }
            }

            try
            {
                Program.Logger.Instance?.Debug("ConsoleForm: subscribing to logger events");
                SubscribeLogger();
                Program.Logger.Instance?.Debug("ConsoleForm: subscribed to logger events");
            }
            catch (Exception ex)
            {
                try { Program.Logger.Instance?.Error("ConsoleForm: SubscribeLogger() failed: " + ex.Message); } catch { }
            }

            try
            {
                Program.Logger.Instance?.Debug("ConsoleForm: wiring scroll detection");
                WireScrollDetection();
                Program.Logger.Instance?.Debug("ConsoleForm: wired scroll detection");
            }
            catch (Exception ex)
            {
                try { Program.Logger.Instance?.Error("ConsoleForm: WireScrollDetection() failed: " + ex.Message); } catch { }
            }
            try
            {
                // Apply saved geometry if present
                var cfg = Config.Load();
                ApplySavedWindowGeometry(cfg);
            }
            catch { }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            try
            {
                // Save current geometry to config
                var cfg = Config.Load();
                SaveCurrentWindowGeometry(cfg);
                cfg.Save();
            }
            catch { }
            base.OnFormClosing(e);
        }

        private void InitializeComponent()
        {
            // Create the auto-scroll toggle button with a subtle background to indicate the
            // area is interactive and to make scroll position more visible to users.
            this._btnAutoScroll = new Button()
            {
                Dock = DockStyle.Top,
                Height = 26,
                FlatStyle = FlatStyle.Flat,
                BackColor = System.Drawing.SystemColors.ControlLight,
                UseVisualStyleBackColor = false
            };
            // Remove default border for a cleaner look
            this._btnAutoScroll.FlatAppearance.BorderSize = 0;
            this._btnAutoScroll.Click += (s, e) => { SetAutoScroll(true); };

            // Accent panel at the right edge to make the scrollbar area visually distinct
            this._scrollAccentPanel = new Panel()
            {
                Dock = DockStyle.Right,
                Width = 18,
                BackColor = System.Drawing.SystemColors.ControlLight,
                Enabled = false
            };

            this._rtb = new ScrollAwareRichTextBox() { Dock = DockStyle.Fill, ReadOnly = true };
            this.ClientSize = new System.Drawing.Size(800, 400);
            this.Controls.Add(_rtb);
            this.Controls.Add(_scrollAccentPanel);
            this.Controls.Add(_btnAutoScroll);
            this.Text = "ToastCloser Console";
            UpdateAutoScrollButton();
        }

        private void SubscribeLogger()
        {
                try
                {
                    Program.Logger.Instance?.Debug("ConsoleForm.SubscribeLogger: attempting subscribe");
                    if (Program.Logger.Instance != null)
                    {
                        Program.Logger.Instance.OnLogLine += Instance_OnLogLine;
                        Program.Logger.Instance?.Debug("ConsoleForm.SubscribeLogger: subscribe OK");
                    }
                    else
                    {
                        // logger not available
                        try { /* no-op when logger not present */ } catch { }
                    }
                }
                catch { }
        }

        private void Instance_OnLogLine(string line)
        {
            if (this.IsDisposed) return;
            if (this.InvokeRequired)
            {
                try { this.BeginInvoke(new Action(() => HandleLogLine(line))); } catch { }
            }
            else HandleLogLine(line);
        }

        // Decide whether to display immediately or buffer
        private void HandleLogLine(string line)
        {
            try
            {
                if (!_autoScroll)
                {
                    lock (_pendingLock)
                    {
                        _pendingLines.Add(line);
                    }
                }
                else
                {
                    AppendLine(line);
                }
            }
            catch { }
        }

        private void AppendLine(string line)
        {
            int oldFirstVisible = 0;
            int selStart = 0;
            int selLen = 0;
            bool needRestoreView = false;
            try
            {
                // If autoscroll is disabled, capture current viewport and selection so we can restore it
                if (!_autoScroll)
                {
                    try
                    {
                        oldFirstVisible = GetFirstVisibleLine(_rtb);
                        selStart = _rtb.SelectionStart;
                        selLen = _rtb.SelectionLength;
                        needRestoreView = true;
                    }
                    catch { needRestoreView = false; }
                }

                // Use programmatic scroll scope so all programmatic scrolls are visible to handlers
                using (BeginProgrammaticScroll())
                {
                    // If the control supports suppressing programmatic VScroll notifications, set it
                    if (_rtb is ScrollAwareRichTextBox sar)
                    {
                        sar.SuppressVScroll = true;
                    }
                    _rtb.AppendText(line + Environment.NewLine);
                }

                if (_rtb.Lines.Length > 5000)
                {
                    var lines = _rtb.Lines;
                    var keep = new string[4000];
                    Array.Copy(lines, lines.Length - 4000, keep, 0, 4000);
                    _rtb.Lines = keep;
                }

                if (_autoScroll)
                {
                    // Move caret to end and request caret-based scrolling
                    _rtb.SelectionStart = _rtb.Text.Length;
                    _rtb.SelectionLength = 0;
                    _rtb.ScrollToCaret();

                    // ScrollToCaret requested; rely on caret-based scrolling only.
                }
                else if (needRestoreView)
                {
                    try
                    {
                        // Compute how much the first visible line moved and scroll back
                        int newFirst = GetFirstVisibleLine(_rtb);
                        int delta = oldFirstVisible - newFirst;

                        // Restore previous selection (caret) position
                        _rtb.SelectionStart = Math.Min(selStart, Math.Max(0, _rtb.Text.Length));
                        _rtb.SelectionLength = Math.Min(selLen, Math.Max(0, _rtb.Text.Length - _rtb.SelectionStart));

                        if (delta != 0)
                        {
                            ScrollLines(_rtb, delta);
                        }
                    }
                    catch { }
                }
                else
                {
                    // intentionally not logging here to avoid re-entrant logging loops
                }
            }
            catch (Exception ex)
            {
                try { Program.Logger.Instance?.Error("ConsoleForm.AppendLine exception: " + ex.Message); } catch { }
            }
            finally
            {
                try
                {
                    if (_rtb is ScrollAwareRichTextBox sar2)
                    {
                        sar2.SuppressVScroll = false;
                    }
                }
                catch { }
            }
        }

        private void WireScrollDetection()
        {
                try
                {
                    Program.Logger.Instance?.Debug("ConsoleForm.WireScrollDetection: wiring events");
                    // Use ScrollAwareRichTextBox's VScrolled event for robust scroll detection
                    if (_rtb is ScrollAwareRichTextBox sar)
                    {
                        sar.VScrolled += (s, e) =>
                        {
                            if (_programmaticScroll) return; // ignore programmatic scrolls
                            // Treat VScrolled as explicit user scroll action. Disable autoscroll.
                            if (_autoScroll)
                            {
                                _autoScroll = false;
                                UpdateAutoScrollButton();
                            }
                        };
                    }
                    // Keep mouse/key handlers as a fallback, with logging
                    _rtb.MouseWheel += (s, e) =>
                    {
                        if (_programmaticScroll) return;
                        // User used mouse wheel — disable autoscroll
                        if (_autoScroll)
                        {
                            _autoScroll = false;
                            UpdateAutoScrollButton();
                        }
                    };
                    _rtb.MouseDown += (s, e) =>
                    {
                        if (_programmaticScroll) return;
                        // User mouse interaction — disable autoscroll
                        if (_autoScroll)
                        {
                            _autoScroll = false;
                            UpdateAutoScrollButton();
                        }
                    };
                    _rtb.KeyDown += (s, e) =>
                    {
                        if (_programmaticScroll) return;
                        // User key interaction — disable autoscroll
                        if (_autoScroll)
                        {
                            _autoScroll = false;
                            UpdateAutoScrollButton();
                        }
                    };
                    Program.Logger.Instance?.Debug("ConsoleForm.WireScrollDetection: events wired");
                }
                catch { }
        }

        // Begin a programmatic scroll scope. While the returned IDisposable is not disposed,
        // scroll-related event handlers should ignore events as they are programmatic.
        private IDisposable BeginProgrammaticScroll()
        {
            _programmaticScroll = true;
            return new DisposableAction(() => _programmaticScroll = false);
        }

        // Simple disposable action helper
        private class DisposableAction : IDisposable
        {
            private readonly Action _onDispose;
            private bool _disposed = false;
            public DisposableAction(Action onDispose) { _onDispose = onDispose ?? (() => { }); }
            public void Dispose()
            {
                if (_disposed) return;
                _disposed = true;
                try { _onDispose(); } catch { }
            }
        }

        // GetScrollInfo-based helpers removed — autoscroll is controlled only by explicit user input

        private void SetAutoScroll(bool enabled)
        {
            _autoScroll = enabled;
            UpdateAutoScrollButton();
            if (_autoScroll)
            {
                // Flush any pending lines that arrived while autoscroll was disabled.
                // Do a single batched AppendText to avoid repeated UI updates that can
                // cause visible flicker or temporary blanking when many lines are flushed.
                string[] pending = Array.Empty<string>();
                lock (_pendingLock)
                {
                    if (_pendingLines.Count > 0)
                    {
                        pending = _pendingLines.ToArray();
                        _pendingLines.Clear();
                    }
                }
                if (pending.Length > 0)
                {
                    try
                    {
                        using (BeginProgrammaticScroll())
                        {
                            if (_rtb is ScrollAwareRichTextBox sar)
                            {
                                sar.SuppressVScroll = true;
                            }

                            var sb = new System.Text.StringBuilder(pending.Length * 128);
                            foreach (var l in pending) sb.AppendLine(l);
                            _rtb.AppendText(sb.ToString());

                            // Trim if needed (same logic as AppendLine)
                            if (_rtb.Lines.Length > 5000)
                            {
                                var lines = _rtb.Lines;
                                var keep = new string[4000];
                                Array.Copy(lines, lines.Length - 4000, keep, 0, 4000);
                                _rtb.Lines = keep;
                            }

                            if (_autoScroll)
                            {
                                _rtb.SelectionStart = _rtb.Text.Length;
                                _rtb.SelectionLength = 0;
                                _rtb.ScrollToCaret();

                                // ScrollToCaret performed; rely on caret-based scrolling only.
                            }

                            if (_rtb is ScrollAwareRichTextBox sar2)
                            {
                                sar2.SuppressVScroll = false;
                            }
                        }
                    }
                    catch { }
                }
                // Even if there were no pending lines, when the user enables autoscroll
                // explicitly we should move the view to the bottom immediately so the
                // user sees the most recent content.
                try
                {
                    using (BeginProgrammaticScroll())
                    {
                        if (_rtb is ScrollAwareRichTextBox sar3)
                        {
                            sar3.SuppressVScroll = true;
                        }

                        _rtb.SelectionStart = _rtb.Text.Length;
                        _rtb.SelectionLength = 0;
                        _rtb.ScrollToCaret();

                        if (_rtb is ScrollAwareRichTextBox sar4)
                        {
                            sar4.SuppressVScroll = false;
                        }
                    }
                }
                catch { }
            }
        }

        private void UpdateAutoScrollButton()
        {
            try
            {
                if (_btnAutoScroll == null) return;
                if (_autoScroll)
                {
                    _btnAutoScroll.Text = "オートスクロール: ON";
                    _btnAutoScroll.Enabled = false;
                }
                else
                {
                    _btnAutoScroll.Text = "オートスクロール: OFF (クリックで有効化)";
                    _btnAutoScroll.Enabled = true;
                }
            }
            catch { }
        }

        // Get the index of the first visible line in the RichTextBox
        private static int GetFirstVisibleLine(RichTextBox rtb)
        {
            try
            {
                return SendMessage(rtb.Handle, EM_GETFIRSTVISIBLELINE, IntPtr.Zero, IntPtr.Zero).ToInt32();
            }
            catch { return 0; }
        }

        // Scroll the RichTextBox by the given number of lines (positive = down, negative = up)
        private static void ScrollLines(RichTextBox rtb, int lines)
        {
            try
            {
                if (lines == 0) return;
                SendMessage(rtb.Handle, EM_LINESCROLL, IntPtr.Zero, new IntPtr(lines));
            }
            catch { }
        }

        // GetScrollInfo and SCROLLINFO removed — display position is now managed via EM_* SendMessage and selection/caret logic.

        private const int EM_GETFIRSTVISIBLELINE = 0x00CE;
        private const int EM_LINESCROLL = 0x00B6;

        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Auto)]
        private static extern IntPtr SendMessage(IntPtr hWnd, int Msg, IntPtr wParam, IntPtr lParam);

        // Small RichTextBox subclass that exposes a VScrolled event by intercepting
        // WM_VSCROLL and WM_MOUSEWHEEL messages so we can reliably detect user scrolling.
        private class ScrollAwareRichTextBox : RichTextBox
        {
            public event EventHandler? VScrolled;
            // When true, suppress raising VScrolled for scroll messages (used to ignore programmatic scrolls)
            public bool SuppressVScroll { get; set; } = false;
            private const int WM_VSCROLL = 0x0115;
            private const int WM_MOUSEWHEEL = 0x020A;

            protected override void WndProc(ref Message m)
            {
                base.WndProc(ref m);
                try
                {
                    if ((m.Msg == WM_VSCROLL || m.Msg == WM_MOUSEWHEEL) && !SuppressVScroll)
                    {
                        VScrolled?.Invoke(this, EventArgs.Empty);
                    }
                }
                catch { }
            }
        }

        private void LoadPastLogs()
        {
            // Load only the tail of log files to avoid huge memory/CPU usage
            // Overall maximum lines to keep in initial load (protect UI/memory)
            const int MaxTotalLines = 5000;
            const int perFileTail = 2000;
            try
            {
                var logsDir = Path.Combine(AppContext.BaseDirectory, "logs");
                if (!Directory.Exists(logsDir)) return;
                // Wait a short time for logger rotation to complete so we don't race with startup archive move.
                try
                {
                    if (!Program.Logger.WaitUntilReady(3000))
                    {
                        try { Program.Logger.Instance?.Debug("ConsoleForm.LoadPastLogs: logger not ready within 3s, proceeding to read logs (may race with rotation)"); } catch { }
                    }
                }
                catch { }

                // Read only the main live log file to avoid iterating over many archived files.
                var mainLog = Path.Combine(logsDir, "auto_closer.log");
                var combined = new System.Collections.Generic.List<string>();
                try
                {
                    if (File.Exists(mainLog))
                    {
                        var tail = ReadLastLines(mainLog, perFileTail);
                        if (tail != null && tail.Any()) combined.AddRange(tail);
                        // Enforce overall max total lines to protect UI/memory
                        if (combined.Count > MaxTotalLines)
                        {
                            combined = combined.Skip(Math.Max(0, combined.Count - MaxTotalLines)).ToList();
                        }
                    }
                    else
                    {
                        try { Program.Logger.Instance?.Debug("ConsoleForm.LoadPastLogs: main log not found, skipping past logs"); } catch { }
                    }
                }
                catch (Exception ex)
                {
                    try { Program.Logger.Instance?.Error($"ConsoleForm.LoadPastLogs: error reading {mainLog}: {ex.Message}"); } catch { }
                }

                // Append lines to UI in a single batched update so the control opens already scrolled to the bottom.
                if (combined.Count > 0)
                {
                    try
                    {
                        this.BeginInvoke(new Action(() =>
                        {
                            try
                            {
                                using (BeginProgrammaticScroll())
                                {
                                    if (_rtb is ScrollAwareRichTextBox sar)
                                    {
                                        sar.SuppressVScroll = true;
                                    }

                                    var sb = new System.Text.StringBuilder(combined.Count * 128);
                                    foreach (var line in combined) sb.AppendLine(line);
                                    _rtb.AppendText(sb.ToString());

                                    // After bulk append, move caret to end so the view is at latest line.
                                    _rtb.SelectionStart = _rtb.Text.Length;
                                    _rtb.SelectionLength = 0;
                                    _rtb.ScrollToCaret();

                                    if (_rtb is ScrollAwareRichTextBox sar2)
                                    {
                                        sar2.SuppressVScroll = false;
                                    }
                                }
                            }
                            catch { }
                        }));
                    }
                    catch (Exception ex)
                    {
                        try { Program.Logger.Instance?.Error("ConsoleForm.LoadPastLogs: BeginInvoke failed: " + ex.Message); } catch { }
                    }
                }
            }
            catch (Exception ex)
            {
                try { AppendLine($"(ログ読み取りエラー: {ex.Message})"); } catch { }
            }
            finally { _autoScroll = true; }
        }

        private IEnumerable<string> ReadLastLines(string path, int maxLines)
        {
            var lines = new List<string>();
            // Try with read/write share by default and a few retries to handle rotation/writer races
            int[] delays = new[] { 50, 100, 200 };
            for (int attempt = 0; attempt <= delays.Length; attempt++)
            {
                try
                {
                    using (var fs = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    using (var sr = new StreamReader(fs))
                    {
                        var all = sr.ReadToEnd();
                        if (!string.IsNullOrEmpty(all))
                        {
                            var arr = all.Replace("\r\n", "\n").Split('\n');
                            var start = Math.Max(0, arr.Length - maxLines);
                            for (int i = start; i < arr.Length; i++) if (!string.IsNullOrEmpty(arr[i])) lines.Add(arr[i]);
                        }
                    }
                    // success
                    break;
                }
                catch (Exception ex)
                {
                    try { Program.Logger.Instance?.Error("ReadLastLines error: " + ex.ToString()); } catch { }
                    if (attempt < delays.Length)
                    {
                        try { Thread.Sleep(delays[attempt]); } catch { }
                    }
                }
            }
            return lines;
        }

        private DateTime? ParseLogLineTimestamp(string line)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(line) || line.Length < 19) return null;
                var prefix = line.Substring(0, 19);
                if (DateTime.TryParseExact(prefix, "yyyy/MM/dd HH:mm:ss", null, System.Globalization.DateTimeStyles.None, out var dt)) return dt;
            }
            catch { }
            return null;
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            try { if (Program.Logger.Instance != null) Program.Logger.Instance.OnLogLine -= Instance_OnLogLine; } catch { }
            base.OnFormClosed(e);
        }

        private void ApplySavedWindowGeometry(Config cfg)
        {
            try
            {
                if (cfg.ConsoleWidth > 0 && cfg.ConsoleHeight > 0)
                {
                    this.StartPosition = FormStartPosition.Manual;
                    var left = cfg.ConsoleLeft;
                    var top = cfg.ConsoleTop;
                    var width = cfg.ConsoleWidth;
                    var height = cfg.ConsoleHeight;
                    var rect = new System.Drawing.Rectangle(left, top, width, height);
                    rect = EnsureVisible(rect);
                    this.Bounds = rect;
                }
                if (!string.IsNullOrEmpty(cfg.ConsoleWindowState))
                {
                    try
                    {
                        if (string.Equals(cfg.ConsoleWindowState, "Maximized", StringComparison.OrdinalIgnoreCase)) this.WindowState = FormWindowState.Maximized;
                        else if (string.Equals(cfg.ConsoleWindowState, "Minimized", StringComparison.OrdinalIgnoreCase)) this.WindowState = FormWindowState.Minimized;
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
                // If window is maximized, store RestoreBounds so we can restore normal geometry
                var useBounds = (this.WindowState == FormWindowState.Normal) ? this.Bounds : this.RestoreBounds;
                cfg.ConsoleLeft = useBounds.Left;
                cfg.ConsoleTop = useBounds.Top;
                cfg.ConsoleWidth = useBounds.Width;
                cfg.ConsoleHeight = useBounds.Height;
                cfg.ConsoleWindowState = this.WindowState.ToString();
            }
            catch { }
        }

        private static System.Drawing.Rectangle EnsureVisible(System.Drawing.Rectangle rect)
        {
            try
            {
                // Ensure the rectangle intersects at least one screen's working area; if not, move to primary screen
                foreach (var s in Screen.AllScreens)
                {
                    var wa = s.WorkingArea;
                    if (wa.IntersectsWith(rect))
                    {
                        // Clamp to working area
                        int left = Math.Max(wa.Left, Math.Min(rect.Left, wa.Right - Math.Min(rect.Width, wa.Width)));
                        int top = Math.Max(wa.Top, Math.Min(rect.Top, wa.Bottom - Math.Min(rect.Height, wa.Height)));
                        int width = Math.Min(rect.Width, wa.Width);
                        int height = Math.Min(rect.Height, wa.Height);
                        return new System.Drawing.Rectangle(left, top, width, height);
                    }
                }
                // fallback to primary screen
                var primary = Screen.PrimaryScreen;
                var p = primary != null ? primary.WorkingArea : (Screen.AllScreens.Length > 0 ? Screen.AllScreens[0].WorkingArea : new System.Drawing.Rectangle(0, 0, rect.Width, rect.Height));
                int w = Math.Min(rect.Width, p.Width);
                int h = Math.Min(rect.Height, p.Height);
                return new System.Drawing.Rectangle(p.Left, p.Top, w, h);
            }
            catch { return rect; }
        }
    }
}
