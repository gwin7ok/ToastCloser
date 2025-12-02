using System;
using System.IO;
using System.Reflection;
using System.Globalization;
using System.Runtime.Loader;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
// FlaUI types are contained in UiaEngine.cs; keep Program.cs free of direct FlaUI references.
using System.Text.RegularExpressions;
using Microsoft.Win32;
using System.Drawing;
// using FlaUI.UIA3; // not referenced here

namespace ToastCloser
{
    class Program
    {
        // track last real user input from keyboard/mouse (Environment.TickCount)
        internal static uint _lastKeyboardTick = 0;
        internal static uint _lastMouseTick = 0;
        internal static System.Drawing.Point _lastCursorPos = new System.Drawing.Point(0,0);

            // Static constructor: runs before Main and before the type is JIT-compiled.
            // Register assembly resolve handlers here so any assembly load during JIT
            // or early startup can be resolved from the `dll\` folder.
            static Program() { }

        public static void Main(string[] args)
        {
            var cfg = Config.Load() ?? new Config();
            string exeFolder = string.Empty;
            try { exeFolder = System.IO.Path.GetDirectoryName(System.Environment.GetCommandLineArgs()?.FirstOrDefault() ?? string.Empty) ?? string.Empty; } catch { }
            try { if (string.IsNullOrEmpty(exeFolder)) exeFolder = System.IO.Path.GetDirectoryName(System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName ?? string.Empty) ?? string.Empty; } catch { }
            try { if (string.IsNullOrEmpty(exeFolder)) exeFolder = System.IO.Path.GetDirectoryName(System.Reflection.Assembly.GetEntryAssembly()?.Location ?? string.Empty) ?? string.Empty; } catch { }
            if (string.IsNullOrEmpty(exeFolder)) exeFolder = AppContext.BaseDirectory ?? System.IO.Directory.GetCurrentDirectory();
            string logsDir = System.IO.Path.Combine(exeFolder, "logs");
            try { System.IO.Directory.CreateDirectory(logsDir); } catch { }

            // Initialize logger here (was previously done in TrayBootstrap). Keep non-fatal.
            try
            {
                var logPath = System.IO.Path.Combine(logsDir, "auto_closer.log");
                Program.Logger.IsDebugEnabled = cfg.VerboseLog;
                if (Program.Logger.Instance == null)
                {
                    Program.Logger.Instance = new Program.Logger(logPath, cfg.LogArchiveLimit);
                }
            }
            catch { }

            int minSeconds = (int)cfg.DisplayLimitSeconds;
            int poll = (int)cfg.PollIntervalSeconds;
            int detectionTimeoutMS = cfg.DetectionTimeoutMS;
            bool detectOnly = cfg.DetectOnly;
            int shortcutKeyWaitIdleMS = cfg.ShortcutKeyWaitIdleMS;
            int shortcutKeyMaxWaitMS = cfg.ShortcutKeyMaxWaitSeconds * 1000;
            int winShortcutKeyIntervalMS = cfg.WinShortcutKeyIntervalMS;
            string shortcutKeyMode = cfg.ShortcutKeyMode ?? "noticecenter";
            bool wmCloseOnly = false;

            // Emit startup INFO (match v1.0.0 behavior)
            // NOTE: The legacy `preserveHistory` flag was removed from the codebase.
            // Historically the flag selected between two close strategies:
            //  - shortcut-key send (closes the toast but leaves it in Action Center / notification history)
            //  - direct UIA/WindowPattern close (closes without leaving history)
            // Current behavior: only the shortcut-key send method is used (history is preserved by design),
            // so the `preserveHistory` toggle is unnecessary and therefore removed.
            try { Logger.Instance?.Info($"ToastCloser starting (displayLimitSeconds={minSeconds} pollIntervalSeconds={poll} detectOnly={detectOnly} shortcutKeyMode={shortcutKeyMode} wmCloseOnly={wmCloseOnly} detectionTimeoutMS={detectionTimeoutMS} winShortcutKeyIntervalMS={winShortcutKeyIntervalMS})"); } catch { }

            using var cts = new CancellationTokenSource();
            // hook standard shutdown signals to cancel RunLoop so it can exit cleanly
            AppDomain.CurrentDomain.ProcessExit += (s, e) => { try { Logger.Instance?.Info("ProcessExit received: cancelling RunLoop"); } catch { } try { cts.Cancel(); } catch { } };
            Console.CancelKeyPress += (s, e) => { try { Logger.Instance?.Info("CancelKeyPress received: cancelling RunLoop"); } catch { } try { cts.Cancel(); } catch { } };
            try { SystemEvents.SessionEnding += (s, e) => { try { Logger.Instance?.Info("SessionEnding received: cancelling RunLoop"); } catch { } try { cts.Cancel(); } catch { } }; } catch { }

            // Wait briefly for logger rotation/open to complete so startup archive processing
            // does not race with other components that read the log files. If logger is not
            // ready within the timeout, continue but log a warning.
            try
            {
                if (!Program.Logger.WaitUntilReady(5000))
                {
                    try { Logger.Instance?.Warn("Logger initialization/rotation did not complete within 5s; continuing startup anyway"); } catch { }
                }
            }
            catch { }

            UiaEngine.RunLoop(cfg, exeFolder, logsDir, minSeconds, poll, detectionTimeoutMS, detectOnly, shortcutKeyWaitIdleMS, shortcutKeyMaxWaitMS, winShortcutKeyIntervalMS, shortcutKeyMode, wmCloseOnly, cts.Token);

            try { Logger.Instance?.Info("RunLoop exited, performing shutdown cleanup"); } catch { }
            // brief pause to ensure final log lines are flushed to disk before disposing
            try { System.Threading.Thread.Sleep(150); } catch { }
            try { Logger.Instance?.Dispose(); } catch { }
        }

        static string CleanNotificationName(string rawName, string contentSummary)
        {
            if (string.IsNullOrWhiteSpace(rawName)) return string.Empty;
            var s = rawName;
            // Remove common noisy phrases
            s = s.Replace("からの新しい通知があります", "");
            s = s.Replace("からの新しい通知があります。。", "");
            s = s.Replace("。。", " ");
            s = s.Replace("。", " ");
            s = s.Replace("操作。", "");
            s = Regex.Replace(s, "\\s+", " ").Trim();
            // Ensure youtube domain appears if present in content but not in name
            if (!string.IsNullOrEmpty(contentSummary) && contentSummary.IndexOf("www.youtube.com", StringComparison.OrdinalIgnoreCase) >= 0 && s.IndexOf("www.youtube.com", StringComparison.OrdinalIgnoreCase) < 0)
            {
                s = s + " www.youtube.com";
            }
            if (s.Length > 200) s = s.Substring(0, 200) + "...";
            return s;
        }
        static string MakeKey(object wObj)
        {
            try
            {
                if (wObj == null) return Guid.NewGuid().ToString();
                dynamic w = wObj;
                try
                {
                    var rid = w.Properties.RuntimeId.ValueOrDefault;
                    if (rid != null)
                    {
                        if (rid is System.Collections.IEnumerable ie)
                        {
                            var parts = new System.Collections.Generic.List<string>();
                            foreach (var x in ie) parts.Add(x?.ToString() ?? string.Empty);
                            return "rid:" + string.Join("_", parts);
                        }
                        else
                        {
                            return "rid:" + rid.ToString();
                        }
                    }
                }
                catch { }

                try
                {
                    var rect = w.BoundingRectangle;
                    var pid = w.Properties.ProcessId.ValueOrDefault;
                    return $"{pid}:{rect.Left}-{rect.Top}-{rect.Right}-{rect.Bottom}";
                }
                catch { return Guid.NewGuid().ToString(); }
            }
            catch { return Guid.NewGuid().ToString(); }
        }

        // Safely get the Name of an AutomationElement without throwing when the property is unsupported
        static string SafeGetName(object eObj)
        {
            if (eObj == null) return string.Empty;
            dynamic e = eObj;
            try
            {
                var v = e.Properties.Name.ValueOrDefault;
                if (v != null) return v;
            }
            catch { }
            try
            {
                return (string?)(e.Name ?? string.Empty) ?? string.Empty;
            }
            catch { }
            return string.Empty;
        }

        // Safely get ProcessId without throwing when UIA provider fails
        static int SafeGetProcessId(object eObj)
        {
            if (eObj == null) return 0;
            dynamic e = eObj;
            try
            {
                return (int)(e.Properties.ProcessId.ValueOrDefault);
            }
            catch { return 0; }
        }

        // Safely get RuntimeId as a string if available
        static string SafeGetRuntimeIdString(object eObj)
        {
            if (eObj == null) return string.Empty;
            dynamic e = eObj;
            try
            {
                var rid = e.Properties.RuntimeId.ValueOrDefault;
                if (rid != null)
                {
                    if (rid is System.Collections.IEnumerable ie)
                    {
                        var parts = new System.Collections.Generic.List<string>();
                        foreach (var x in ie) parts.Add(x?.ToString() ?? string.Empty);
                        return string.Join("_", parts);
                    }
                    return rid.ToString() ?? string.Empty;
                }
            }
            catch { }
            return string.Empty;
        }

        // Console output helper that prefixes the human-friendly timestamp
        internal static void LogConsole(string m)
        {
            // Delegate LogConsole to Logger.Info to unify output
            try { Logger.Instance?.Info(m); } catch { }
        }

        class TrackedInfo
        {
            public DateTime FirstSeen { get; set; }
            public int GroupId { get; set; }
            public string? Method { get; set; }
            public int Pid { get; set; }
            public string? ShortName { get; set; }
        }

        // Action Center helpers moved to UiaEngine.cs

        private static uint GetIdleMilliseconds()
        {
            var li = new NativeMethods.LASTINPUTINFO();
            li.cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf(typeof(NativeMethods.LASTINPUTINFO));
            if (!NativeMethods.GetLastInputInfo(ref li)) return 0;
            uint tick = (uint)Environment.TickCount;
            if (tick >= li.dwTime) return tick - li.dwTime;
            // wrap-around
            return (uint)((uint.MaxValue - li.dwTime) + tick);
        }

        // FindHostWindowHandle moved to UiaEngine.cs (FlaUI-dependent helper)

        // Fast native check: enumerate top-level HWNDs and look for a CoreWindow whose title contains '新しい通知'.
        // This is much faster and more robust than calling UIA's FindFirstDescendant when the UIA provider
        // (Action Center, Shell, etc.) is in a state that blocks UIA calls.
        private static bool IsCoreNotificationWindowPresentNative()
        {
            bool found = false;
            try
            {
                NativeMethods.EnumWindows((h, l) =>
                {
                    try
                    {
                        if (!NativeMethods.IsWindowVisible(h)) return true; // continue
                        var className = new System.Text.StringBuilder(256);
                        var clen = NativeMethods.GetClassName(h, className, className.Capacity);
                        if (clen > 0)
                        {
                            var cls = className.ToString();
                            if (string.Equals(cls, "Windows.UI.Core.CoreWindow", StringComparison.OrdinalIgnoreCase))
                            {
                                var titleSb = new System.Text.StringBuilder(256);
                                NativeMethods.GetWindowText(h, titleSb, titleSb.Capacity);
                                var title = titleSb.ToString() ?? string.Empty;
                                if (title.IndexOf("新しい通知", StringComparison.OrdinalIgnoreCase) >= 0)
                                {
                                    found = true;
                                    return false; // stop enumeration
                                }
                            }
                        }
                    }
                    catch { }
                    return true; // continue
                }, IntPtr.Zero);
            }
            catch { }
            return found;
        }

        public class Logger : IDisposable
        {
            // Set when logger construction and rotation handling completed and writer is open.
            private static readonly System.Threading.ManualResetEventSlim _ready = new System.Threading.ManualResetEventSlim(false);
            public static bool IsReady => _ready.IsSet;
            /// <summary>
            /// Wait until the logger is ready (rotation and open completed) or timeout elapses.
            /// </summary>
            public static bool WaitUntilReady(int millisecondsTimeout)
            {
                try { return _ready.Wait(millisecondsTimeout); } catch { return false; }
            }
            public static Logger? Instance { get; set; }
            public event Action<string>? OnLogLine;
            // When true, log file lines will also be written to Console (same format)
            public static bool IsDebugEnabled = false;
            private readonly object _lock = new object();
            private readonly System.IO.StreamWriter _writer;
            private readonly int _archiveLimit;
            public Logger(string path, int archiveLimit = 0)
            {
                _archiveLimit = archiveLimit;
                // Open the file with shared read/write so other writers (e.g., File.AppendAllText)
                // can append concurrently for diagnostic entries.
                // Perform rotation BEFORE opening the log for append.
                try
                {
                    if (System.IO.File.Exists(path))
                    {
                        try
                        {
                            var ctime = System.IO.File.GetCreationTimeUtc(path);
                            var ts = ctime.ToString("yyyy-MM-dd-HH-mm-ss");
                            var dir = System.IO.Path.GetDirectoryName(path) ?? string.Empty;
                            var baseName = System.IO.Path.GetFileNameWithoutExtension(path);
                            var ext = System.IO.Path.GetExtension(path);
                            var destName = baseName + "." + ts + ext;
                            var dest = System.IO.Path.Combine(dir, destName);
                            try { System.IO.File.Move(path, dest); } catch { }

                            // Prune archives according to provided limit (archiveLimit <= 0 => no pruning)
                            if (_archiveLimit > 0)
                            {
                                try
                                {
                                    var dirInfo = new System.IO.DirectoryInfo(dir);
                                    var files = dirInfo.GetFiles()
                                        .Where(f => System.Text.RegularExpressions.Regex.IsMatch(f.Name, System.Text.RegularExpressions.Regex.Escape(baseName) + @"\..+" + System.Text.RegularExpressions.Regex.Escape(ext) + "$"))
                                        .OrderBy(f => f.CreationTimeUtc)
                                        .ToList();
                                    if (files.Count > _archiveLimit)
                                    {
                                        int toDelete = files.Count - _archiveLimit;
                                        for (int i = 0; i < toDelete; i++)
                                        {
                                            try { files[i].Delete(); } catch { }
                                        }
                                    }
                                }
                                catch { }
                            }
                        }
                        catch { }
                    }
                }
                catch { }

                var fs = new System.IO.FileStream(path, System.IO.FileMode.Append, System.IO.FileAccess.Write, System.IO.FileShare.ReadWrite);
                _writer = new System.IO.StreamWriter(fs) { AutoFlush = true };
                try
                {
                    var diagOpen = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"toastcloser_logger_open_{System.DateTime.Now:yyyyMMddHHmmss}_pid{System.Diagnostics.Process.GetCurrentProcess().Id}.txt");
                    var exists = System.IO.File.Exists(path);
                    long len = -1;
                    try { len = exists ? new System.IO.FileInfo(path).Length : -1; } catch { }
                    var text = $"local={System.DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff zzz")}\r\nlogPath={path}\r\nfileExists={exists}\r\nlength={len}\r\n";
                    try { System.IO.File.WriteAllText(diagOpen, text); } catch { }
                }
                catch { }
                // Signal ready after writer is created and initial INFO header written.
                Info($"===== log start: {DateTime.Now:yyyy/MM/dd HH:mm:ss} =====");
                try { _ready.Set(); } catch { }
            }
            public void Info(string m) => Write("INFO", m);
            public void Debug(string m) => Write("DEBUG", m);
            public void Debug(Func<string> mf)
            {
                if (!IsDebugEnabled) return;
                try { Debug(mf()); } catch { }
            }
            public void Warn(string m) => Write("WARN", m);
            public void Error(string m) => Write("ERROR", m);
            private void Write(string level, string m)
            {
                // If DEBUG and debug disabled, skip writing anywhere
                if (string.Equals(level, "DEBUG", StringComparison.OrdinalIgnoreCase) && !IsDebugEnabled) return;
                var line = $"{DateTime.Now:yyyy/MM/dd HH:mm:ss} [{level}] {m}";
                lock (_lock)
                {
                    try { _writer.WriteLine(line); } catch { }
                    try { Console.WriteLine(line); } catch { }
                    try { OnLogLine?.Invoke(line); } catch { }
                }
            }
            public void Dispose() => _writer?.Dispose();
        }

        // TryInvokeCloseButton moved to UiaEngine.cs (FlaUI-dependent helper)

        // Helper: classify whether a virtual-key code is a likely keyboard key
        internal static bool IsKeyboardVirtualKey(int vk)
        {
            // 0x30-0x5A: 0-9, A-Z
            if (vk >= 0x30 && vk <= 0x5A) return true;
            // 0x60-0x6F: Numpad 0-9 and ops
            if (vk >= 0x60 && vk <= 0x6F) return true;
            // 0x70-0x87: Function keys
            if (vk >= 0x70 && vk <= 0x87) return true;
            // common control keys: SHIFT, CTRL, ALT, SPACE, TAB, ENTER, BACK
            if (vk == 0x10 || vk == 0x11 || vk == 0x12) return true; // SHIFT, CTRL, ALT
            if (vk == 0x20 || vk == 0x09 || vk == 0x0D || vk == 0x08) return true; // SPACE, TAB, ENTER, BACK
            // arrows
            if (vk >= 0x25 && vk <= 0x28) return true;
            // punctuation and OEM keys often used on keyboards
            if ((vk >= 0xBA && vk <= 0xC0) || (vk >= 0xDB && vk <= 0xDF)) return true;
            return false;
            }
        }

        static class NativeMethods
    {
        public const uint WM_CLOSE = 0x0010;
        public const int INPUT_MOUSE = 0;
        public const int INPUT_KEYBOARD = 1;
        public const int INPUT_HARDWARE = 2;
        public const uint KEYEVENTF_KEYUP = 0x0002;
        public const ushort VK_LWIN = 0x5B;
        public const ushort VK_A = 0x41;
        [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
        public static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        public struct LASTINPUTINFO
        {
            public uint cbSize;
            public uint dwTime;
        }
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        public static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);

        public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
        [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true, CharSet = System.Runtime.InteropServices.CharSet.Auto)]
        public static extern int GetWindowTextLength(IntPtr hWnd);
        [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true, CharSet = System.Runtime.InteropServices.CharSet.Auto)]
        public static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder lpString, int nMaxCount);
        [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true, CharSet = System.Runtime.InteropServices.CharSet.Auto)]
        public static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder lpClassName, int nMaxCount);
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        public static extern bool IsWindowVisible(IntPtr hWnd);

        [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
        public static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        public struct INPUT
        {
            public int type;
            public InputUnion U;
        }

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Explicit)]
        public struct InputUnion
        {
            [System.Runtime.InteropServices.FieldOffset(0)] public MOUSEINPUT mi;
            [System.Runtime.InteropServices.FieldOffset(0)] public KEYBDINPUT ki;
            [System.Runtime.InteropServices.FieldOffset(0)] public HARDWAREINPUT hi;
        }

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        public struct MOUSEINPUT { public int dx; public int dy; public uint mouseData; public uint dwFlags; public uint time; public IntPtr dwExtraInfo; }
        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        public struct KEYBDINPUT { public ushort wVk; public ushort wScan; public uint dwFlags; public uint time; public IntPtr dwExtraInfo; }
        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        public struct HARDWAREINPUT { public uint uMsg; public ushort wParamL; public ushort wParamH; }
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        public static extern IntPtr WindowFromPoint(System.Drawing.Point p);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        public static extern IntPtr GetAncestor(IntPtr hwnd, uint gaFlags);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        public static extern short GetAsyncKeyState(int vKey);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        public static extern bool GetCursorPos(out System.Drawing.Point pt);

        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
        public static extern int MessageBoxW(IntPtr hWnd, string lpText, string lpCaption, uint uType);

        public const uint GA_PARENT = 1;
    }

    
}
