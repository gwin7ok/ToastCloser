using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace ToastCloser
{
    public class TrayApplicationContext : ApplicationContext
    {
        private NotifyIcon _trayIcon;
        private ContextMenuStrip _menu;
        private Config _config;
        private SettingsForm? _settingsForm;
        private ConsoleForm? _consoleForm;
        // Track middle-button down/up to ensure both occur on this icon (no longer needed)

        public TrayApplicationContext(Config cfg)
        {
            _config = cfg;

            var pngPath = Path.Combine(AppContext.BaseDirectory, "ToastCloser.png");
            var iconPath = Path.Combine(AppContext.BaseDirectory, "ToastCloser.ico");
            // Also check Resources subfolder where build may copy Content files
            var pngPathResources = Path.Combine(AppContext.BaseDirectory, "Resources", "ToastCloser.png");
            var iconPathResources = Path.Combine(AppContext.BaseDirectory, "Resources", "ToastCloser.ico");
            Icon icon = SystemIcons.Application;
            try
            {
                Program.Logger.Instance?.Info($"Tray icon candidates: ico='{iconPath}', icoRes='{iconPathResources}', png='{pngPath}', pngRes='{pngPathResources}'");
                // First, try to load ICO embedded as an assembly resource (works for single-file publish)
                try
                {
                    var asm = typeof(TrayApplicationContext).Assembly;
                    var resNames = asm.GetManifestResourceNames();
                    Program.Logger.Instance?.Info("Assembly manifest resources: " + string.Join(",", resNames));
                    string? match = null;
                    foreach (var rn in resNames)
                    {
                        if (rn.EndsWith("ToastCloser.ico", StringComparison.OrdinalIgnoreCase)) { match = rn; break; }
                    }
                    if (match != null)
                    {
                        Program.Logger.Instance?.Info($"Loading ICO from embedded resource: {match}");
                        using (var s = asm.GetManifestResourceStream(match))
                        {
                            if (s != null)
                            {
                                try
                                {
                                    icon = new Icon(s);
                                    Program.Logger.Instance?.Info("Loaded ICO from resource successfully");
                                }
                                catch (Exception ex)
                                {
                                    Program.Logger.Instance?.Error("Failed to load ICO from resource: " + ex.Message);
                                }
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Program.Logger.Instance?.Error("Embedded resource ICO load error: " + ex.Message);
                }
                // If not loaded from resource, prefer a provided ICO file (preserves proper alpha) if present in either location
                if (icon == SystemIcons.Application && (File.Exists(iconPath) || File.Exists(iconPathResources)))
                {
                    var realIco = File.Exists(iconPath) ? iconPath : iconPathResources;
                    Program.Logger.Instance?.Info($"Loading ICO from: {realIco}");
                    try
                    {
                        icon = new Icon(realIco);
                        Program.Logger.Instance?.Info("Loaded ICO successfully");
                        try
                        {
                            bool hasAlpha = ValidateIcoAlpha(realIco);
                            Program.Logger.Instance?.Info($"ICO alpha present: {hasAlpha}");
                        }
                        catch (Exception ex)
                        {
                            Program.Logger.Instance?.Error("ValidateIcoAlpha failed: " + ex.Message);
                        }
                    }
                    catch (Exception ex)
                    {
                        Program.Logger.Instance?.Error("Failed to load ICO: " + ex.Message);
                    }
                }
                // Note: we intentionally do not fall back to runtime PNG->HICON conversion.
                // The build step generates `Resources\ToastCloser.ico` from the PNG and that
                // ICO will be copied into the app's output. Using the ICO preserves proper
                // alpha and avoids platform-dependent HICON issues. If no ICO is found,
                // we keep the default SystemIcons.Application value.
            }
            catch (Exception ex)
            {
                Program.Logger.Instance?.Error("Unexpected error selecting tray icon: " + ex.Message);
            }

            _trayIcon = new NotifyIcon()
            {
                Icon = icon,
                Text = "ToastCloser",
                Visible = true
            };

            _menu = new ContextMenuStrip();
            try { _menu.ShowItemToolTips = true; } catch { }
            var settingsItem = new ToolStripMenuItem("設定...");
            try { settingsItem.Font = new Font(settingsItem.Font, FontStyle.Regular); } catch { }
            settingsItem.Click += (s, e) => ShowSettings();

            var consoleItem = new ToolStripMenuItem("コンソールを表示");
            try { consoleItem.Font = new Font(consoleItem.Font, FontStyle.Bold); } catch { }
            consoleItem.Click += (s, e) => ToggleConsole();


            var exitItem = new ToolStripMenuItem("終了");
            try { exitItem.Font = new Font(exitItem.Font, FontStyle.Regular); } catch { }
            exitItem.Click += (s, e) => ExitApplication();
            try { exitItem.ToolTipText = "終了のショートカット: アイコンをミドルクリック"; } catch { }

            _menu.Items.Add(settingsItem);
            _menu.Items.Add(consoleItem);
            _menu.Items.Add(new ToolStripSeparator());
            _menu.Items.Add(exitItem);

            // Attach Opened to adjust position after the menu is shown so we can keep default closing behaviour
            _menu.Opened += Menu_Opened;

            // Assign ContextMenuStrip to the tray icon so default closing behaviour remains (we cancel Opening and show manually)
            _trayIcon.ContextMenuStrip = _menu;
            _trayIcon.DoubleClick += (s, e) => ToggleConsole();
            // Middle-click the tray icon to exit (same as selecting "終了" from the menu)
            // Use MouseClick so WinForms determines a valid click (Down+Up) and reduces false negatives.
            _trayIcon.MouseClick += (s, e) =>
            {
                try
                {
                    if (e is MouseEventArgs me && me.Button == MouseButtons.Middle)
                    {
                        ExitApplication();
                    }
                }
                catch { }
            };

            // Show a balloon tip on first run
            try { _trayIcon.ShowBalloonTip(2000, "ToastCloser", "Tray mode: 右クリックで設定・コンソールを開けます", ToolTipIcon.Info); } catch { }
        }

        private void Menu_Opened(object? sender, EventArgs e)
        {
            try
            {
                var cursorPos = Cursor.Position;
                var screen = Screen.FromPoint(cursorPos);
                var working = screen.WorkingArea;
                var bounds = screen.Bounds;

                // Current menu bounds
                var menuBounds = _menu.Bounds;
                int menuW = menuBounds.Width;
                int menuH = menuBounds.Height;

                bool taskbarAtTop = working.Top > bounds.Top;

                int x = menuBounds.Left;
                int y = menuBounds.Top;

                int spaceBelow = working.Bottom - cursorPos.Y;
                int spaceAbove = cursorPos.Y - working.Top;

                if (taskbarAtTop)
                {
                    if (spaceBelow >= menuH) y = cursorPos.Y + 10;
                    else y = Math.Max(working.Top, working.Bottom - menuH);
                }
                else
                {
                    if (spaceAbove >= menuH) y = cursorPos.Y - menuH;
                    else if (spaceBelow >= menuH) y = cursorPos.Y + 10;
                    else y = Math.Max(working.Top, working.Bottom - menuH);
                }

                if (x + menuW > working.Right) x = Math.Max(working.Left, working.Right - menuW);
                if (x < working.Left) x = working.Left;

                // Move the menu if needed
                if (x != menuBounds.Left || y != menuBounds.Top)
                {
                    // Re-show the menu at the desired screen location to reposition it
                    _menu.Show(new System.Drawing.Point(x, y));
                }
            }
            catch { }
        }

        

        private void ShowSettings()
        {
            if (_settingsForm == null || _settingsForm.IsDisposed)
            {
                _settingsForm = new SettingsForm(_config);
                _settingsForm.ConfigSaved += (s, cfg) =>
                {
                    _config = cfg;
                    _config.Save();
                    // Optionally notify running scanner to apply new config (future)
                };
                _settingsForm.Show();
            }
            else
            {
                _settingsForm.BringToFront();
            }
        }

        private void ToggleConsole()
        {
            if (_consoleForm == null || _consoleForm.IsDisposed)
            {
                _consoleForm = new ConsoleForm();
                _consoleForm.Show();
            }
            else
            {
                if (_consoleForm.Visible) _consoleForm.Hide(); else _consoleForm.Show();
            }
        }

        private void ExitApplication()
        {
            // Signal the RunLoop to stop and wait briefly for it to finish.
            try { Program.ShutdownCts?.Cancel(); } catch { }
            try
            {
                var t = Program.RunLoopThread;
                if (t != null && t.IsAlive)
                {
                    try { t.Join(5000); } catch { }
                }
            }
            catch { }

            _trayIcon.Visible = false;
            _trayIcon.Dispose();
            Application.Exit();
        }

        // Validate ICO by parsing image entries and looking for embedded PNG frames.
        // If a PNG frame is found, load it and check for any pixel with alpha != 255.
        private static bool ValidateIcoAlpha(string icoPath)
        {
            if (!File.Exists(icoPath)) return false;
            using (var fs = new FileStream(icoPath, FileMode.Open, FileAccess.Read))
            using (var br = new BinaryReader(fs))
            {
                // ICONDIR header
                ushort reserved = br.ReadUInt16();
                ushort type = br.ReadUInt16();
                ushort count = br.ReadUInt16();
                for (int i = 0; i < count; i++)
                {
                    byte width = br.ReadByte();
                    byte height = br.ReadByte();
                    byte colors = br.ReadByte();
                    byte reservedEntry = br.ReadByte();
                    ushort planes = br.ReadUInt16();
                    ushort bitCount = br.ReadUInt16();
                    uint bytesInRes = br.ReadUInt32();
                    uint imageOffset = br.ReadUInt32();

                    long pos = fs.Position;
                    fs.Seek(imageOffset, SeekOrigin.Begin);
                    byte[] header = br.ReadBytes((int)Math.Min(8, bytesInRes));
                    // PNG signature: 89 50 4E 47 0D 0A 1A 0A
                    if (header.Length >= 8 && header[0] == 0x89 && header[1] == 0x50 && header[2] == 0x4E && header[3] == 0x47 && header[4] == 0x0D && header[5] == 0x0A && header[6] == 0x1A && header[7] == 0x0A)
                    {
                        fs.Seek(imageOffset, SeekOrigin.Begin);
                        byte[] pngData = br.ReadBytes((int)bytesInRes);
                        try
                        {
                            using (var ms = new MemoryStream(pngData))
                            using (var img = Image.FromStream(ms))
                            using (var bmp = new Bitmap(img))
                            {
                                for (int y = 0; y < bmp.Height; y++)
                                {
                                    for (int x = 0; x < bmp.Width; x++)
                                    {
                                        if (bmp.GetPixel(x, y).A != 255) return true;
                                    }
                                }
                            }
                        }
                        catch { /* ignore parse failures and try next entry */ }
                    }
                    fs.Seek(pos, SeekOrigin.Begin);
                }
            }
            return false;
        }
    }
}
