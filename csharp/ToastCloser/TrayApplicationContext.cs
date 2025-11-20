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

        public TrayApplicationContext(Config cfg)
        {
            _config = cfg;

            var iconPath = Path.Combine(AppContext.BaseDirectory, "ToastCloser.ico");
            Icon icon = SystemIcons.Application;
            try { if (File.Exists(iconPath)) icon = new Icon(iconPath); } catch { }

            _trayIcon = new NotifyIcon()
            {
                Icon = icon,
                Text = "ToastCloser",
                Visible = true
            };

            _menu = new ContextMenuStrip();
            var settingsItem = new ToolStripMenuItem("設定...");
            settingsItem.Click += (s, e) => ShowSettings();
            var consoleItem = new ToolStripMenuItem("コンソールを表示");
            consoleItem.Click += (s, e) => ToggleConsole();
            var reloadItem = new ToolStripMenuItem("設定を再読み込み");
            reloadItem.Click += (s, e) => ReloadConfig();
            var exitItem = new ToolStripMenuItem("終了");
            exitItem.Click += (s, e) => ExitApplication();

            _menu.Items.Add(settingsItem);
            _menu.Items.Add(consoleItem);
            _menu.Items.Add(reloadItem);
            _menu.Items.Add(new ToolStripSeparator());
            _menu.Items.Add(exitItem);

            // Attach Opened to adjust position after the menu is shown so we can keep default closing behaviour
            _menu.Opened += Menu_Opened;

            // Assign ContextMenuStrip to the tray icon so default closing behaviour remains (we cancel Opening and show manually)
            _trayIcon.ContextMenuStrip = _menu;
            _trayIcon.DoubleClick += (s, e) => ToggleConsole();

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

        private void ReloadConfig()
        {
            try
            {
                _config = Config.Load();
                MessageBox.Show("設定を再読み込みしました。", "ToastCloser", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show("設定の再読み込みに失敗しました: " + ex.Message);
            }
        }

        private void ExitApplication()
        {
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
            Application.Exit();
        }
    }
}
