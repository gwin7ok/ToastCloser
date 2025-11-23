using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FlaUI.Core;
using FlaUI.Core.Definitions;
using FlaUI.Core.Conditions;
using FlaUI.Core.AutomationElements;
using System.Text.RegularExpressions;
using System.Drawing;
using FlaUI.UIA3;

namespace ToastCloser
{
    class Program
    {
        // track last real user input from keyboard/mouse (Environment.TickCount)
        private static System.Threading.Mutex? _singleInstanceMutex = null;
        private static uint _lastKeyboardTick = 0;
        private static uint _lastMouseTick = 0;
        private static System.Drawing.Point _lastCursorPos = new System.Drawing.Point(0,0);
            // monitoring state: whether we've started preserve-history monitoring (cleared after idle-success)
            private static bool _monitoringStarted = false;
            // verbose debug logging flag (set via --verbose-log)
            private static bool _verboseLog = false;

        public static void Main(string[] args)
        {
            // single-instance check: prevent multiple processes
            try
            {
                var argListCheck = args?.ToString() ?? string.Empty;
                bool isBackgroundService = false;
                try { isBackgroundService = args != null && Array.Exists(args, a => string.Equals(a, "--background-service", StringComparison.OrdinalIgnoreCase)); } catch { }
                if (!isBackgroundService)
                {
                    bool createdNew = false;
                    var mutexName = "Global\\ToastCloser_mutex";
                    _singleInstanceMutex = new System.Threading.Mutex(true, mutexName, out createdNew);
                    if (!createdNew)
                    {
                        try
                        {
                            NativeMethods.MessageBoxW(IntPtr.Zero, "ToastCloser is already running.", "ToastCloser", 0x00000040);
                        }
                        catch { }
                        return;
                    }
                }
            }
            catch { }
            double minSeconds = 10.0;
            double poll = 1.0;
            bool detectOnly = false;
            // Default behaviour: preserve-history mode is enabled by default.
            // This opens the Notification Center / Quick Settings to move toasts to history
            // rather than attempting to close individual toast UI elements.
            bool preserveHistory = true;
            bool wmCloseOnly = false;
            
            int shortcutKeyWaitIdleMS = 2000; // default: require 2s idle
            int shortcutKeyMaxWaitMS = 15000; // default: 15s max monitoring
            string shortcutKeyMode = "noticecenter";
            int winShortcutKeyIntervalMS = 300;
            // detection timeout (ms) for UIA searches to avoid long blocking calls after close actions
            int detectionTimeoutMS = 2000; // default: 2000ms

            // CLI flags are deprecated: load all settings from `ToastCloser.ini` via `Config`.
            var argList = args?.ToList() ?? new List<string>();
            var cfg = Config.Load();
            // Apply config values (unit conversion where necessary)
            minSeconds = cfg.DisplayLimitSeconds;
            poll = cfg.PollIntervalSeconds;
            detectOnly = cfg.DetectOnly;
            shortcutKeyMode = cfg.ShortcutKeyMode ?? "noticecenter";
            shortcutKeyWaitIdleMS = cfg.ShortcutKeyWaitIdleMS;
            // Config stores max wait in seconds; convert to milliseconds for internal usage
            shortcutKeyMaxWaitMS = Math.Max(0, cfg.ShortcutKeyMaxWaitSeconds * 1000);
            detectionTimeoutMS = cfg.DetectionTimeoutMS;
            winShortcutKeyIntervalMS = cfg.WinShortcutKeyIntervalMS;
            _verboseLog = cfg.VerboseLog;
            Logger.IsDebugEnabled = _verboseLog;

            // If the legacy --skip-fallback flag is present, warn that it's currently unused
            // Note: skipFallback option is currently unused; fallback search paths removed.
            Logger.Instance?.Info($"ToastCloser starting (displayLimitSeconds={minSeconds} pollIntervalSeconds={poll} detectOnly={detectOnly} preserveHistory={preserveHistory} shortcutKeyMode={shortcutKeyMode} wmCloseOnly={wmCloseOnly} detectionTimeoutMS={detectionTimeoutMS} winShortcutKeyIntervalMS={winShortcutKeyIntervalMS})");

            var tracked = new Dictionary<string, TrackedInfo>();
            var groups = new Dictionary<int, DateTime>();
            int nextGroupId = 1;

            // setup log folder under executable and log file path
            var exeFolder = AppContext.BaseDirectory;
            var logsDir = System.IO.Path.Combine(exeFolder, "logs");
            try { System.IO.Directory.CreateDirectory(logsDir); } catch { }
            var logPath = System.IO.Path.Combine(logsDir, "auto_closer.log");
            // startup log rotation: if a prior log exists, rename it using its creation timestamp
            try
            {
                if (System.IO.File.Exists(logPath))
                {
                    var ctime = System.IO.File.GetCreationTime(logPath); // use file creation time (local)
                    var ts = ctime.ToString("yyyy-MM-dd-HH-mm-ss");
                    var dir = System.IO.Path.GetDirectoryName(logPath) ?? string.Empty;
                    var baseName = System.IO.Path.GetFileNameWithoutExtension(logPath);
                    var ext = System.IO.Path.GetExtension(logPath);
                    var destName = baseName + "." + ts + ext; // baseName.YYYY-MM-DD-HH-MM-SS.ext
                    var dest = System.IO.Path.Combine(dir, destName);
                    try
                    {
                        System.IO.File.Move(logPath, dest);
                    }
                    catch { /* safe to ignore rotation failures */ }
                    // Prune old archived logs if configured
                    try
                    {
                        try
                        {
                            int limit = cfg?.LogArchiveLimit ?? 100;
                            if (limit > 0)
                            {
                                var dirInfo = new System.IO.DirectoryInfo(dir);
                                var pattern = baseName + ".*" + ext; // matches baseName.YYYY-MM-DD-HH-mm-ss.ext
                                var files = dirInfo.GetFiles()
                                    .Where(f => System.Text.RegularExpressions.Regex.IsMatch(f.Name, System.Text.RegularExpressions.Regex.Escape(baseName) + @"\..+" + System.Text.RegularExpressions.Regex.Escape(ext) + "$"))
                                    .OrderBy(f => f.CreationTimeUtc)
                                    .ToList();
                                if (files.Count > limit)
                                {
                                    int toDelete = files.Count - limit;
                                    for (int i = 0; i < toDelete; i++)
                                    {
                                        try { files[i].Delete(); } catch { }
                                    }
                                }
                            }
                        }
                        catch { }
                    }
                    catch { }
                }
            }
            catch { }
            var logger = new Logger(logPath);
            // expose logger for static helpers to use when writing diagnostic entries
            Logger.Instance = logger;
            // control debug-level emission: --verbose-log sets IsDebugEnabled
            Logger.IsDebugEnabled = _verboseLog;

            // UIA automation instances are reinitializable on timeout. Keep them in mutable variables
            UIA3Automation? automation = new UIA3Automation();
            ConditionFactory? cf = new ConditionFactory(new UIA3PropertyLibrary());
            FlaUI.Core.AutomationElements.AutomationElement? desktop = automation?.GetDesktop();
            var automationLock = new object();

            // helper to initialize or reinitialize automation (thread-safe)
            Action InitializeAutomation = () =>
            {
                lock (automationLock)
                {
                    try
                    {
                        try { automation?.Dispose(); } catch { }
                        automation = new UIA3Automation();
                        cf = new ConditionFactory(new UIA3PropertyLibrary());
                        desktop = automation?.GetDesktop();
                    }
                    catch { desktop = automation?.GetDesktop(); }
                }
            };

            // initialize automation first time
            InitializeAutomation();

            // initialize cursor position
            try { NativeMethods.GetCursorPos(out _lastCursorPos); } catch { }

            while (true)
            {
                try
                {
                    // NOTE: Do NOT perform regular keyboard/mouse polling on every scan.
                    // Monitoring for preserve-history is started only when the oldest tracked
                    // toast's elapsed time reaches (displayLimitMS - shortcutKeyWaitIdleMS).
                    // If monitoring should start, enter the monitoring loop (which performs
                    // immediate poll and then 200ms-interval polling) and block Toast search
                    // until monitoring finishes.
                    if (preserveHistory && !_monitoringStarted && tracked.Count > 0)
                    {
                        try
                        {
                            int displayLimitMS = (int)(minSeconds * 1000);
                            int monitorThresholdMS = Math.Max(0, displayLimitMS - shortcutKeyWaitIdleMS);
                            var oldest = tracked.Values.OrderBy(t => t.FirstSeen).FirstOrDefault();
                            if (oldest != null)
                            {
                                var oldestElapsedMS = (int)(DateTime.UtcNow - oldest.FirstSeen).TotalMilliseconds;
                                if (oldestElapsedMS >= monitorThresholdMS)
                                {
                                    _monitoringStarted = true;
                                    var monitoringStart = DateTime.UtcNow;
                                    Logger.Instance?.Info($"Started preserve-history monitoring (oldestElapsedMS={oldestElapsedMS} monitorThresholdMS={monitorThresholdMS} maxMonitorMS={shortcutKeyMaxWaitMS})");

                                    // Immediate one-shot poll to capture very recent input
                                    try
                                    {
                                        if (NativeMethods.GetCursorPos(out var ipos))
                                        {
                                            _lastCursorPos = ipos;
                                            _lastMouseTick = (uint)Environment.TickCount;
                                            if (_verboseLog) Logger.Instance?.Debug($"ImmediatePoll: Mouse at {ipos.X},{ipos.Y}");
                                        }
                                    }
                                    catch { }

                                    try
                                    {
                                        // limited key scan: transition bit only, limited to likely keyboard VKs + mouse buttons
                                        for (int vk = 0x01; vk <= 0xFE; vk++)
                                        {
                                            try
                                            {
                                                short s = NativeMethods.GetAsyncKeyState(vk);
                                                bool transition = (s & 0x0001) != 0;
                                                if (transition && (IsKeyboardVirtualKey(vk) || vk == 0x01 || vk == 0x02 || vk == 0x04))
                                                {
                                                    _lastKeyboardTick = (uint)Environment.TickCount;
                                                    if (_verboseLog) Logger.Instance?.Debug($"ImmediatePoll: Detected vk={vk}");
                                                    break;
                                                }
                                            }
                                            catch { }
                                        }
                                    }
                                    catch { }

                                    // Monitoring loop: poll every 200ms until idle condition satisfied or max-monitor timeout
                                    while (true)
                                    {
                                        try { Thread.Sleep(200); } catch { }

                                        try
                                        {
                                            if (NativeMethods.GetCursorPos(out var cur))
                                            {
                                                if (cur.X != _lastCursorPos.X || cur.Y != _lastCursorPos.Y)
                                                {
                                                    _lastCursorPos = cur;
                                                    _lastMouseTick = (uint)Environment.TickCount;
                                                    if (_verboseLog) Logger.Instance?.Debug($"Detected mouse movement during monitoring: {cur.X},{cur.Y}");
                                                }
                                            }
                                        }
                                        catch { }

                                        try
                                        {
                                            for (int vk = 0x01; vk <= 0xFE; vk++)
                                            {
                                                try
                                                {
                                                    short s = NativeMethods.GetAsyncKeyState(vk);
                                                    bool transition = (s & 0x0001) != 0;
                                                    if (transition && (IsKeyboardVirtualKey(vk) || vk == 0x01 || vk == 0x02 || vk == 0x04))
                                                    {
                                                        _lastKeyboardTick = (uint)Environment.TickCount;
                                                        if (_verboseLog) Logger.Instance?.Debug($"Detected keyboard activity during monitoring (vk={vk})");
                                                        break;
                                                    }
                                                }
                                                catch { }
                                            }
                                        }
                                        catch { }

                                        try
                                        {
                                            // Check for max monitor timeout first
                                            var monitorElapsedMS = (int)(DateTime.UtcNow - monitoringStart).TotalMilliseconds;
                                            if (shortcutKeyMaxWaitMS > 0 && monitorElapsedMS >= shortcutKeyMaxWaitMS)
                                            {
                                                Logger.Instance?.Info($"Preserve-history monitor timed out after {monitorElapsedMS}ms (max {shortcutKeyMaxWaitMS}ms); proceeding to send shortcut");
                                                // Treat as idle: toggle and clear tracked
                                                if (string.Equals(shortcutKeyMode, "noticecenter", StringComparison.OrdinalIgnoreCase))
                                                {
                                                    ToggleShortcutWithDetection('N', IsNotificationCenterOpen, winShortcutKeyIntervalMS);
                                                    Logger.Instance?.Info("Notification Center toggled (preserve-history: timeout)");
                                                }
                                                else
                                                {
                                                    ToggleShortcutWithDetection('A', IsActionCenterOpen, winShortcutKeyIntervalMS);
                                                    Logger.Instance?.Info("Action Center toggled (preserve-history: timeout)");
                                                }
                                                try
                                                {
                                                    var dedup = tracked.Keys.ToList();
                                                    foreach (var k in dedup)
                                                    {
                                                        try { tracked.Remove(k); } catch { }
                                                    }
                                                    groups.Clear();
                                                }
                                                catch { }
                                                _monitoringStarted = false;
                                                break;
                                            }

                                            uint elapsedSinceLastInput = (uint)(Environment.TickCount - Math.Max(_lastKeyboardTick, _lastMouseTick));
                                            if (elapsedSinceLastInput >= (uint)shortcutKeyWaitIdleMS)
                                            {
                                                // Idle satisfied: toggle Action/Notification Center and clear tracked
                                                if (string.Equals(shortcutKeyMode, "noticecenter", StringComparison.OrdinalIgnoreCase))
                                                {
                                                    ToggleShortcutWithDetection('N', IsNotificationCenterOpen, winShortcutKeyIntervalMS);
                                                    Logger.Instance?.Info("Notification Center toggled (preserve-history)");
                                                }
                                                else
                                                {
                                                    ToggleShortcutWithDetection('A', IsActionCenterOpen, winShortcutKeyIntervalMS);
                                                    Logger.Instance?.Info("Action Center toggled (preserve-history)");
                                                }

                                                // Mark all tracked as handled
                                                try
                                                {
                                                    var dedup = tracked.Keys.ToList();
                                                    foreach (var k in dedup)
                                                    {
                                                        try { tracked.Remove(k); } catch { }
                                                    }
                                                    groups.Clear();
                                                }
                                                catch { }

                                                _monitoringStarted = false;
                                                break; // exit monitoring loop
                                            }
                                        }
                                        catch { }
                                    }

                                    // After monitoring finishes, wait one poll interval before resuming normal scans
                                    try { Thread.Sleep(TimeSpan.FromSeconds(poll)); } catch { }
                                }
                            }
                        }
                        catch { }
                    }

                    lock (automationLock)
                    {
                        try { desktop = automation?.GetDesktop(); } catch { desktop = automation?.GetDesktop(); }
                    }

                    // Log search start time for diagnostics
                    var searchStart = DateTime.UtcNow;
                    Logger.Instance?.Info("Toast search: start");

                    // Primary search: prefer CoreWindow -> ScrollViewer -> FlexibleToastView chain
                    // and only select toasts whose Attribution TextBlock contains 'youtube' (or 'www.youtube.com').
                    var foundList = new List<FlaUI.Core.AutomationElements.AutomationElement>();

                    // Run the CoreWindow -> ScrollViewer -> FlexibleToastView discovery on a worker task
                    // and enforce a timeout to avoid long blocking UIA calls immediately after close actions.
                    Task<List<FlaUI.Core.AutomationElements.AutomationElement>> searchTask = Task.Run(() =>
                    {
                        var localFound = new List<FlaUI.Core.AutomationElements.AutomationElement>();
                        try
                        {
                            // capture local automation references to avoid races
                            var localCf = cf;
                            var localDesktop = desktop;
                            if (localCf == null || localDesktop == null)
                            {
                                Logger.Instance?.Info("UIA not initialized for search; skipping local search");
                                return localFound;
                            }
                            // try CoreWindow by name '新しい通知' first
                            var coreByNameCond = localCf.ByClassName("Windows.UI.Core.CoreWindow").And(localCf.ByName("新しい通知"));
                            // Direct UIA search for CoreWindow by name (do not rely on native EnumWindows pre-check).
                            AutomationElement? coreElement = null;
                            try
                            {
                                Logger.Instance?.Debug($"Calling desktop.FindFirstChild(CoreWindow by name) (elapsed={(DateTime.UtcNow - searchStart).TotalMilliseconds:0.0}ms)");
                                coreElement = localDesktop.FindFirstChild(coreByNameCond);
                            }
                            catch (Exception ex)
                            {
                                Logger.Instance?.Error("Exception during UIA CoreWindow search: " + ex.Message + $" (elapsed={(DateTime.UtcNow - searchStart).TotalMilliseconds:0.0}ms)");
                            }
                                Logger.Instance?.Debug($"CoreWindow found={(coreElement != null)} (elapsed={(DateTime.UtcNow - searchStart).TotalMilliseconds:0.0}ms)");

                            if (coreElement == null)
                            {
                                // Named CoreWindow not present: end search here
                                Logger.Instance?.Debug($"CoreWindow(Name='新しい通知') not found; ending CoreWindow-based search. (elapsed={(DateTime.UtcNow - searchStart).TotalMilliseconds:0.0}ms)");
                            }
                            else
                            {
                                Logger.Instance?.Debug($"Finding ScrollViewer under CoreWindow (elapsed={(DateTime.UtcNow - searchStart).TotalMilliseconds:0.0}ms)");
                                var scroll = coreElement.FindFirstDescendant(localCf.ByClassName("ScrollViewer"));
                                Logger.Instance?.Debug($"ScrollViewer found={(scroll != null)} (elapsed={(DateTime.UtcNow - searchStart).TotalMilliseconds:0.0}ms)");

                                if (scroll != null)
                                {
                                    Logger.Instance?.Debug($"Enumerating FlexibleToastView under ScrollViewer (elapsed={(DateTime.UtcNow - searchStart).TotalMilliseconds:0.0}ms)");
                                    var toasts = scroll.FindAllDescendants(cf.ByClassName("FlexibleToastView"));
                                    Logger.Instance?.Debug($"FlexibleToastView count={(toasts?.Length ?? 0)} (elapsed={(DateTime.UtcNow - searchStart).TotalMilliseconds:0.0}ms)");

                                    if (toasts != null && toasts.Length > 0)
                                    {
                                        foreach (var t in toasts)
                                        {
                                            try
                                            {
                                                            Logger.Instance?.Debug($"Inspecting FlexibleToastView candidate (elapsed={(DateTime.UtcNow - searchStart).TotalMilliseconds:0.0}ms)");
                                                            var tbAttrCond = localCf.ByClassName("TextBlock").And(localCf.ByAutomationId("Attribution")).And(localCf.ByControlType(ControlType.Text));
                                                            var tbAttr = t.FindFirstDescendant(tbAttrCond);
                                                Logger.Instance?.Debug($"Attribution found={(tbAttr != null)} (elapsed={(DateTime.UtcNow - searchStart).TotalMilliseconds:0.0}ms)");
                                                if (tbAttr != null)
                                                {
                                                    var attr = SafeGetName(tbAttr);
                                                    Logger.Instance?.Debug($"Attribution.Name=\"{attr}\" (elapsed={(DateTime.UtcNow - searchStart).TotalMilliseconds:0.0}ms)");
                                                    if (!string.IsNullOrEmpty(attr))
                                                    {
                                                        // Respect user configuration: when YoutubeOnly is true, require exact match
                                                        if (cfg?.YoutubeOnly ?? true)
                                                        {
                                                            if (string.Equals(attr.Trim(), "www.youtube.com", StringComparison.OrdinalIgnoreCase))
                                                            {
                                                                localFound.Add(t);
                                                                Logger.Instance?.Debug($"Added FlexibleToastView candidate (Attribution equals 'www.youtube.com') (elapsed={(DateTime.UtcNow - searchStart).TotalMilliseconds:0.0}ms)");
                                                            }
                                                        }
                                                        else
                                                        {
                                                            // Not limited to YouTube: any attribution present qualifies
                                                            localFound.Add(t);
                                                            Logger.Instance?.Debug($"Added FlexibleToastView candidate (Attribution present) (elapsed={(DateTime.UtcNow - searchStart).TotalMilliseconds:0.0}ms)");
                                                        }
                                                    }
                                                }
                                            }
                                            catch (Exception ex)
                                            {
                                                Logger.Instance?.Error("Error while inspecting toast: " + ex.Message + $" (elapsed={(DateTime.UtcNow - searchStart).TotalMilliseconds:0.0}ms)");
                                            }
                                        }
                                    }
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            Logger.Instance?.Error("Exception during CoreWindow path: " + ex.Message + $" (elapsed={(DateTime.UtcNow - searchStart).TotalMilliseconds:0.0}ms)");
                        }
                        return localFound;
                    });

                    // Wait up to detectionTimeoutMS for the UIA search to complete
                    if (searchTask.Wait(detectionTimeoutMS))
                    {
                        foundList = searchTask.Result;
                    }
                    else
                    {
                        Logger.Instance?.Warn($"CoreWindow search timed out after {detectionTimeoutMS}ms; skipping this scan to avoid long blocking. (elapsed={(DateTime.UtcNow - searchStart).TotalMilliseconds:0.0}ms)");
                        Logger.Instance?.Debug($"CoreWindow search timed out after {detectionTimeoutMS}ms and was cancelled for this poll (durationMS={detectionTimeoutMS})");
                        foundList = new List<FlaUI.Core.AutomationElements.AutomationElement>();

                        // Attempt to reinitialize UIA automation after a timeout. Run InitializeAutomation() with the same timeout.
                        var reinitSw = System.Diagnostics.Stopwatch.StartNew();
                        var reinitTask = Task.Run(() =>
                        {
                            try
                            {
                                InitializeAutomation();
                                return true;
                            }
                            catch (Exception ex)
                            {
                                try { logger.Error($"UIA reinitialization failed: {ex.Message}"); } catch { }
                                return false;
                            }
                        });

                        bool reinitCompleted = reinitTask.Wait(detectionTimeoutMS);
                        reinitSw.Stop();
                        if (reinitCompleted && reinitTask.Result)
                        {
                            LogConsole($"UIA reinitialization completed in {reinitSw.ElapsedMilliseconds}ms");
                            logger.Info($"UIA reinitialized in {reinitSw.ElapsedMilliseconds}ms after search timeout");
                        }
                        else
                        {
                            LogConsole($"UIA reinitialization timed out after {detectionTimeoutMS}ms; will wait until next poll before retrying.");
                            logger.Debug($"UIA reinitialization timed out after {detectionTimeoutMS}ms");
                            // Wait a small backoff equal to detection timeout to avoid immediate retry
                            try { Thread.Sleep(detectionTimeoutMS); } catch { }
                        }
                    }

                    // Use only CoreWindow->ScrollViewer->FlexibleToastView discovery results; no heavy fallbacks
                    FlaUI.Core.AutomationElements.AutomationElement[] found = foundList.ToArray();
                    if (found == null || found.Length == 0)
                    {
                        LogConsole($"No toasts found by CoreWindow-based search; ending search for this scan. (elapsed={(DateTime.UtcNow - searchStart).TotalMilliseconds:0.0}ms)");
                        found = new FlaUI.Core.AutomationElements.AutomationElement[0];
                    }

                    var searchEnd = DateTime.UtcNow;
                    var searchMS = (searchEnd - searchStart).TotalMilliseconds;
                    LogConsole($"Toast search: end (duration={searchMS:0.0}ms) found={found.Length}");
                    logger.Debug($"Scan found {found.Length} candidates durationMS={searchMS:0.0}");
                    for (int _i = 0; _i < found.Length; _i++)
                    {
                        var w = found[_i];
                        try
                        {
                            // compute key early so logs can be prefixed with it
                            string keyCandidate = MakeKey(w);
                            var n = SafeGetName(w);
                            var cn = w.ClassName ?? string.Empty;
                            var aidx = w.Properties.AutomationId.ValueOrDefault ?? string.Empty;
                            var pid = SafeGetProcessId(w);
                            var rect = w.BoundingRectangle;
                            // attempt to read RuntimeId (may be array)
                            var runtimeIdStr = SafeGetRuntimeIdString(w);

                            // Candidate details previously logged here as a separate DEBUG line.
                            // We'll fold candidate metadata into the single combined message emitted when a new Found is processed.
                        }
                        catch (Exception ex)
                        {
                            try
                            {
                                var keyCandidate = MakeKey(w);
                                logger.Debug($"key={keyCandidate} Candidate[{_i}]: failed to read properties: {ex.Message}");
                            }
                            catch
                            {
                                logger.Debug($"Candidate[{_i}]: failed to read properties: {ex.Message}");
                            }
                        }

                        // proceed with existing processing for w
                    }

                    // Re-iterate through found for existing processing (we will process again below)
                    // postedHwnds: per-scan set of HWNDs we've already sent WM_CLOSE to, so we only post once
                    var postedHwnds = new HashSet<long>();
                    // actionCenterToggled: ensure we only toggle Action Center once per scan
                    var actionCenterToggled = false;
                    foreach (var w in found)
                    {
                        string key = MakeKey(w);
                        if (!tracked.ContainsKey(key))
                        {
                            // Determine group: if any existing tracked item has firstSeen within 1s, join that group, otherwise create new group
                            int assignedGroup = -1;
                            var now = DateTime.UtcNow;
                            foreach (var kv in tracked)
                            {
                                if ((now - kv.Value.FirstSeen).TotalSeconds <= 1.0)
                                {
                                    assignedGroup = kv.Value.GroupId;
                                    break;
                                }
                            }
                            if (assignedGroup == -1)
                            {
                                assignedGroup = nextGroupId++;
                                groups[assignedGroup] = now;
                            }
                            var methodStr = "priority";
                            string contentSummary = string.Empty;
                            string contentDisplay = string.Empty;
                            try
                            {
                                var textNodes = w.FindAllDescendants(cf.ByControlType(ControlType.Text));
                                var parts = new List<string>();
                                foreach (var tn in textNodes)
                                {
                                    try
                                    {
                                        var tname = SafeGetName(tn);
                                        if (!string.IsNullOrWhiteSpace(tname)) parts.Add(tname.Trim());
                                    }
                                    catch { }
                                }
                                if (parts.Count > 0)
                                {
                                    // full summary (may duplicate name)
                                    contentSummary = string.Join(" || ", parts);
                                    // filter out parts that are contained in the window name to avoid duplicate display
                                    try
                                    {
                                        var nameLower = SafeGetName(w).ToLowerInvariant();
                                        var filtered = parts.Where(p => !nameLower.Contains((p ?? string.Empty).ToLowerInvariant())).ToList();
                                        if (filtered.Count == 0)
                                        {
                                            // if everything duplicated, keep only the last meaningful token (e.g., domain or '閉じる')
                                            filtered = parts.Where(p => p.IndexOf("www.", StringComparison.OrdinalIgnoreCase) >= 0 || p.IndexOf("閉じる", StringComparison.OrdinalIgnoreCase) >= 0).ToList();
                                        }
                                        if (filtered.Count == 0) filtered = parts.Take(1).ToList();
                                        contentDisplay = string.Join(" || ", filtered);
                                        if (contentDisplay.Length > 800) contentDisplay = contentDisplay.Substring(0, 800) + "...";
                                    }
                                    catch { contentDisplay = contentSummary; }
                                    if (contentSummary.Length > 800) contentSummary = contentSummary.Substring(0, 800) + "...";
                                }
                            }
                            catch { }

                            var pidVal2 = SafeGetProcessId(w);
                            var safeName2 = SafeGetName(w).Replace('\n', ' ').Replace('\r', ' ').Trim();
                            var cleanName = CleanNotificationName(safeName2, contentSummary);
                            tracked[key] = new TrackedInfo { FirstSeen = now, GroupId = assignedGroup, Method = methodStr, Pid = pidVal2, ShortName = cleanName };

                            // Use contentDisplay (filtered) to avoid duplicating name content
                            var msg = $"key={key} | Found | group={assignedGroup} | method={methodStr} | pid={pidVal2} | name=\"{safeName2}\"";
                            if (!string.IsNullOrEmpty(contentDisplay)) msg += $" | content=\"{contentDisplay}\"";
                            // Combine candidate metadata and found details into a single line,
                            // then emit that message both as DEBUG and as INFO (per user request).
                            try
                            {
                                var rid2 = SafeGetRuntimeIdString(w);
                                var rect2 = w.BoundingRectangle;
                                var cn2 = w.ClassName ?? string.Empty;
                                var aidx2 = w.Properties.AutomationId.ValueOrDefault ?? string.Empty;
                                // INFO: concise, user-facing message
                                var infoMsg = $"key={key} | Found | group={assignedGroup} | method={methodStr} | pid={pidVal2} | name=\"{cleanName}\"";
                                if (!string.IsNullOrEmpty(contentDisplay)) infoMsg += $" | content=\"{contentDisplay}\"";

                                // DEBUG: append raw name, contentSummary, UIA metadata and a text node count
                                string rawNameDbg = safeName2 ?? string.Empty;
                                string contentSummaryDbg = contentSummary ?? string.Empty;
                                int textCount = 0;
                                try
                                {
                                    var tnodes = w.FindAllDescendants(cf.ByControlType(ControlType.Text));
                                    textCount = tnodes?.Length ?? 0;
                                }
                                catch { }

                                var debugMsg = infoMsg + $" | rawName=\"{rawNameDbg}\" | contentSummary=\"{contentSummaryDbg}\" | class={cn2} aid={aidx2} rid={rid2} rect={rect2.Left}-{rect2.Top}-{rect2.Right}-{rect2.Bottom} | textCount={textCount}";

                                logger.Debug(() => debugMsg);
                                logger.Info(infoMsg);
                            }
                            catch
                            {
                                // Fallback: safe minimal messages
                                logger.Debug(() => msg);
                                logger.Info($"新しい通知があります。key={key} | Found | group={assignedGroup} | method={methodStr} | pid={pidVal2} | name=\"{cleanName}\"");
                            }
                            continue;
                        }

                        var groupId = tracked[key].GroupId;
                        var groupStart = groups.ContainsKey(groupId) ? groups[groupId] : tracked[key].FirstSeen;
                        var elapsed = (DateTime.UtcNow - groupStart).TotalSeconds;
                        var msgElapsed = $"key={key} | group={groupId} | elapsed={elapsed:0.0}s";
                        // Single DEBUG output for elapsed; avoid duplicating INFO
                        logger.Debug(() => msgElapsed);

                        // File: log a concise message indicating the notification is still present
                        try
                        {
                            var stored = tracked[key];
                            var methodStored = stored.Method ?? "priority";
                            var pidStored = stored.Pid;
                            var nameStored = stored.ShortName ?? string.Empty;
                            var stillMsg = $"閉じられていない通知があります　key={key} | Found | group={groupId} | method={methodStored} | pid={pidStored} | name=\"{nameStored}\" (elapsed {elapsed:0.0})";
                            // Single INFO write (logger writes both file and console)
                            logger.Info(stillMsg);
                        }
                        catch { }

                        // Also log detailed descendant text for already-tracked candidates
                        try
                        {
                            var textNodesEx = w.FindAllDescendants(cf.ByControlType(ControlType.Text));
                            var partsEx = new System.Collections.Generic.List<string>();
                            foreach (var tn in textNodesEx)
                            {
                                try
                                {
                                    var tname = SafeGetName(tn);
                                    if (!string.IsNullOrWhiteSpace(tname)) partsEx.Add(tname.Trim());
                                }
                                catch { }
                            }
                                if (partsEx.Count > 0)
                                {
                                    var contentEx = string.Join(" || ", partsEx);
                                    if (contentEx.Length > 800) contentEx = contentEx.Substring(0, 800) + "...";
                                    logger.Info($"key={key} | Details: {contentEx}");
                                }
                        }
                        catch { }

                        if (elapsed >= minSeconds)
                        {
                            var closeMsg = $"key={key} Attempting to close group={groupId} (elapsed {elapsed:0.0})";
                            LogConsole(closeMsg);
                            logger.Info(closeMsg);

                            if (detectOnly)
                            {
                                var skipMsg = $"key={key} Detect-only mode: not closing group={groupId}";
                                LogConsole(skipMsg);
                                logger.Info(skipMsg);
                                // do not remove tracked entry; continue monitoring
                                continue;
                            }

                            bool closed = false;
                            if (preserveHistory)
                            {
                                try
                                {
                                    // Prefer real keyboard/mouse timestamps over system LastInput to avoid DirectInput noise.
                                    uint lastSystemTick = 0;
                                    try
                                    {
                                        var li2 = new NativeMethods.LASTINPUTINFO();
                                        li2.cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf(typeof(NativeMethods.LASTINPUTINFO));
                                        if (NativeMethods.GetLastInputInfo(ref li2)) lastSystemTick = li2.dwTime;
                                    }
                                    catch { }

                                    uint lastKbMouseTick = Math.Max(_lastKeyboardTick, _lastMouseTick);

                                    // If the user is active (idle < shortcutKeyWaitIdleMS), wait and retry until idle condition is met.
                                    bool treatAsActive = false;
                                    try
                                    {
                                        while (true)
                                        {
                                            // Prefer keyboard/mouse ticks (_lastKeyboardTick/_lastMouseTick) to determine activity
                                            uint curLastKbMouseTick = Math.Max(_lastKeyboardTick, _lastMouseTick);
                                            uint curLastSystemTick = 0;
                                            try
                                            {
                                                var li2 = new NativeMethods.LASTINPUTINFO();
                                                li2.cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf(typeof(NativeMethods.LASTINPUTINFO));
                                                if (NativeMethods.GetLastInputInfo(ref li2)) curLastSystemTick = li2.dwTime;
                                            }
                                            catch { }

                                            // Diagnostic: log current tick values to verify updates
                                            try
                                            {
                                                var dbgSys = curLastSystemTick;
                                                LogConsole($"key={key} DebugTicks: EnvTick={Environment.TickCount} lastKb={_lastKeyboardTick} lastMouse={_lastMouseTick} curLastKbMouseTick={curLastKbMouseTick} lastSystemInput={dbgSys}");
                                                logger.Debug($"key={key} DebugTicks: EnvTick={Environment.TickCount} lastKb={_lastKeyboardTick} lastMouse={_lastMouseTick} curLastKbMouseTick={curLastKbMouseTick} lastSystemInput={dbgSys}");
                                            }
                                            catch { }

                                            bool isActiveNow = false;
                                            // Primary check: use Environment.TickCount-based keyboard/mouse ticks (consistent base)
                                            if (curLastKbMouseTick != 0)
                                            {
                                                uint elapsedSinceLastInput = (uint)(Environment.TickCount - curLastKbMouseTick);
                                                if (elapsedSinceLastInput <= (uint)shortcutKeyWaitIdleMS)
                                                {
                                                    isActiveNow = true;
                                                }
                                            }
                                            else
                                            {
                                                // No keyboard/mouse ticks available; do NOT use GetIdleMilliseconds() fallback (can be noisy).
                                                // In this case, treat as idle so preserve-history proceeds.
                                                LogConsole($"key={key} No keyboard/mouse ticks available; proceeding without GetIdleMilliseconds() fallback");
                                                logger.Debug($"key={key} No keyboard/mouse ticks available; proceeding without GetIdleMilliseconds() fallback");
                                                isActiveNow = false;
                                            }

                                            if (!isActiveNow)
                                            {
                                                // idle condition satisfied — proceed to toggle Action Center
                                                LogConsole($"key={key} User idle (based on keyboard/mouse ticks and system idle) — proceeding to preserve-history");
                                                logger.Debug($"key={key} User idle (based on keyboard/mouse ticks and system idle) — proceeding to preserve-history");
                                                treatAsActive = false;
                                                break;
                                            }

                                            // still active: wait in short intervals and poll keyboard/mouse so input during wait updates ticks
                                            LogConsole($"key={key} User active: waiting up to {shortcutKeyWaitIdleMS}ms while polling for keyboard/mouse activity (preserve-history)");
                                            logger.Debug($"key={key} User active: waiting up to {shortcutKeyWaitIdleMS}ms while polling for keyboard/mouse activity (preserve-history)");
                                            treatAsActive = true;

                                            int waited = 0;
                                            int step = Math.Min(200, Math.Max(50, shortcutKeyWaitIdleMS / 10));
                                            bool innerIdleSatisfied = false;
                                            while (waited < shortcutKeyWaitIdleMS)
                                            {
                                                Thread.Sleep(step);
                                                waited += step;

                                                // Poll mouse position
                                                try
                                                {
                                                    if (NativeMethods.GetCursorPos(out var curPos))
                                                    {
                                                        if (curPos.X != _lastCursorPos.X || curPos.Y != _lastCursorPos.Y)
                                                        {
                                                            _lastCursorPos = curPos;
                                                            _lastMouseTick = (uint)Environment.TickCount;
                                                            LogConsole($"key={key} Detected mouse movement during wait; updating lastMouseTick");
                                                            // activity detected; re-evaluate outer loop
                                                            break; // re-evaluate outer loop
                                                        }
                                                    }
                                                }
                                                catch { }

                                                // Poll keyboard: check for transitions or down states on common keyboard VKs
                                                try
                                                {
                                                    for (int vk = 0x01; vk <= 0xFE; vk++)
                                                    {
                                                        try
                                                        {
                                                            short s = NativeMethods.GetAsyncKeyState(vk);
                                                            bool transition = (s & 0x0001) != 0;
                                                            bool down = (s & 0x8000) != 0;
                                                            if (transition || (down && IsKeyboardVirtualKey(vk)))
                                                            {
                                                                _lastKeyboardTick = (uint)Environment.TickCount;
                                                                LogConsole($"key={key} Detected keyboard activity (vk={vk}) during wait; updating lastKeyboardTick");
                                                                // activity detected; re-evaluate outer loop
                                                                break;
                                                            }
                                                        }
                                                        catch { }
                                                    }
                                                }
                                                catch { }

                                                // After polling, if no activity has occurred for shortcutKeyWaitIdleMS, proceed immediately
                                                try
                                                {
                                                    uint elapsedSinceLastInput = (uint)(Environment.TickCount - Math.Max(_lastKeyboardTick, _lastMouseTick));
                                                    if (elapsedSinceLastInput >= (uint)shortcutKeyWaitIdleMS)
                                                    {
                                                        innerIdleSatisfied = true;
                                                        break; // exit inner wait loop and proceed to preserve-history
                                                    }
                                                }
                                                catch { }
                                            }
                                            // If inner loop observed sustained idle, treat as not active and proceed
                                            if (innerIdleSatisfied)
                                            {
                                                isActiveNow = false;
                                            }
                                        }
                                    }
                                    catch (ThreadInterruptedException) { }
                                    if (treatAsActive)
                                    {
                                        closed = false; // do not mark closed; retry next poll
                                    }
                                    else
                                    {
                                        if (!actionCenterToggled)
                                        {
                                            // collect current visible toasts (from 'found') and log them
                                            var present = new List<(string key, string name)>();
                                            foreach (var fe in found)
                                            {
                                                try
                                                {
                                                    var k = MakeKey(fe);
                                                    var nm = SafeGetName(fe).Replace('\n', ' ').Replace('\r', ' ').Trim();
                                                    present.Add((k, nm));
                                                }
                                                catch { }
                                            }
                                            var dedup = present.GroupBy(p => p.key).Select(g => g.First()).ToList();
                                            var summary = string.Join(" | ", dedup.Select(d => $"key={d.key} name=\"{d.name}\""));
                                            LogConsole($"key={key} Opening Action Center to preserve history for {dedup.Count} toasts: {summary}");
                                            logger.Info($"key={key} Opening Action Center to preserve history for {dedup.Count} toasts: {summary}");

                                            // Choose preserve-history toggle mode based on shortcutKeyMode
                                            if (string.Equals(shortcutKeyMode, "noticecenter", StringComparison.OrdinalIgnoreCase))
                                            {
                                                // Win+N -> Notification Center
                                                ToggleShortcutWithDetection('N', IsNotificationCenterOpen, winShortcutKeyIntervalMS);
                                                LogConsole($"key={key} Notification Center toggled (preserve-history)");
                                                logger.Info($"key={key} Notification Center toggled (preserve-history)");
                                            }
                                            else
                                            {
                                                // default: Win+A -> Quick Settings / Action Center
                                                ToggleShortcutWithDetection('A', IsActionCenterOpen, winShortcutKeyIntervalMS);
                                                LogConsole($"key={key} Action Center toggled (preserve-history)");
                                                logger.Info($"key={key} Action Center toggled (preserve-history)");
                                            }

                                            // mark all present toasts as closed by preserve-history and remove from tracked
                                            foreach (var d in dedup)
                                            {
                                                try
                                                {
                                                    if (tracked.ContainsKey(d.key))
                                                    {
                                                        tracked.Remove(d.key);
                                                        var cbMsg = $"key={d.key} ClosedBy=PreserveHistory | name=\"{d.name}\"";
                                                        LogConsole(cbMsg);
                                                        logger.Info(cbMsg);
                                                    }
                                                }
                                                catch { }
                                            }
                                            actionCenterToggled = true;
                                            closed = true;
                                        }
                                        else
                                        {
                                            // Action Center already toggled this scan; assume this toast moved too
                                            LogConsole($"key={key} Action Center already toggled this scan; assuming toast moved to history");
                                            logger.Info($"key={key} Action Center already toggled this scan; assuming toast moved to history");
                                            if (tracked.ContainsKey(key)) tracked.Remove(key);
                                            closed = true;
                                        }
                                    }
                                }
                                catch (Exception ex)
                                {
                                    logger.Error($"key={key} preserve-history failed: {ex.Message}");
                                }
                            }
                                else
                                {
                                    // NEW: Do NOT use WM_CLOSE posting as a fallback. Instead:
                                    // 1) Try WindowPattern.Close() if supported by the element
                                    // 2) If that fails, fall back to invoking the '閉じる' / 'Close' button via UIA
                                    string? closedBy = null;
                                    try
                                    {
                                        // Try WindowPattern.Close() only. Do NOT fall back to Invoke or WM_CLOSE here.
                                        bool attempted = false;
                                        try
                                        {
                                            if (w.Patterns != null && w.Patterns.Window != null && w.Patterns.Window.IsSupported)
                                            {
                                                attempted = true;
                                                try
                                                {
                                                    w.Patterns.Window.Pattern.Close();
                                                    closed = true;
                                                    closedBy = "WindowPattern.Close";
                                                    LogConsole($"key={key} Attempted WindowPattern.Close");
                                                    logger.Info($"key={key} Attempted WindowPattern.Close");
                                                }
                                                catch (Exception ex)
                                                {
                                                    logger.Debug($"key={key} WindowPattern.Close threw: {ex.Message}");
                                                }
                                            }
                                        }
                                        catch { }

                                        if (!attempted)
                                        {
                                            // Log that WindowPattern was not present/supported for diagnostics
                                            LogConsole($"key={key} WindowPattern not supported on element; not attempting Invoke or WM_CLOSE as per policy");
                                            logger.Info($"key={key} WindowPattern not supported on element; skipping other fallbacks");

                                            // Additional diagnostics: log element metadata to help root-cause analysis
                                            try
                                            {
                                                IntPtr nativeHwnd = IntPtr.Zero;
                                                try
                                                {
                                                    var nv = w.Properties.NativeWindowHandle.ValueOrDefault;
                                                    if (nv != 0) nativeHwnd = new IntPtr(nv);
                                                }
                                                catch { }
                                                var className = w.ClassName ?? string.Empty;
                                                var aid = string.Empty;
                                                try { aid = w.Properties.AutomationId.ValueOrDefault ?? string.Empty; } catch { }
                                                var rid = SafeGetRuntimeIdString(w);
                                                var pid = SafeGetProcessId(w);
                                                var rect = w.BoundingRectangle;
                                                logger.Info($"key={key} Diagnostics: class={className} aid={aid} nativeHandle=0x{nativeHwnd.ToInt64():X} pid={pid} rid={rid} rect={rect.Left}-{rect.Top}-{rect.Right}-{rect.Bottom}");

                                                int textCount = 0;
                                                try
                                                {
                                                    var tnodes = w.FindAllDescendants(cf.ByControlType(ControlType.Text));
                                                    textCount = tnodes?.Length ?? 0;
                                                }
                                                catch { }
                                                logger.Info($"key={key} Diagnostics: textNodeCount={textCount}");

                                                // Check for a close button descendant (exists but we will NOT invoke)
                                                bool hasCloseBtn = false;
                                                try
                                                {
                                                    var btnCond = cf.ByControlType(ControlType.Button).And(cf.ByName("閉じる").Or(cf.ByName("Close")));
                                                    var btn = w.FindFirstDescendant(btnCond);
                                                    hasCloseBtn = btn != null;
                                                }
                                                catch { }
                                                logger.Info($"key={key} Diagnostics: hasCloseButton={hasCloseBtn}");

                                                // Attempt to locate a host HWND (for debugging only)
                                                try
                                                {
                                                    var hostHwnd = FindHostWindowHandle(w);
                                                    if (hostHwnd != IntPtr.Zero)
                                                    {
                                                        var csb = new System.Text.StringBuilder(256);
                                                        var clenHost = NativeMethods.GetClassName(hostHwnd, csb, csb.Capacity);
                                                        var hostClass = clenHost > 0 ? csb.ToString() : string.Empty;
                                                        var titleSb = new System.Text.StringBuilder(256);
                                                        NativeMethods.GetWindowText(hostHwnd, titleSb, titleSb.Capacity);
                                                        var hostTitle = titleSb.ToString() ?? string.Empty;
                                                        logger.Info($"key={key} Diagnostics: hostHwnd=0x{hostHwnd.ToInt64():X} hostClass={hostClass} hostTitle=\"{hostTitle}\"");
                                                    }
                                                    else
                                                    {
                                                        logger.Info($"key={key} Diagnostics: hostHwnd=0 (none found)");
                                                    }
                                                }
                                                catch (Exception ex)
                                                {
                                                    logger.Debug($"key={key} Diagnostics: FindHostWindowHandle error: {ex.Message}");
                                                }
                                            }
                                            catch (Exception ex)
                                            {
                                                logger.Debug($"key={key} Diagnostics logging failed: {ex.Message}");
                                            }
                                        }
                                    }
                                    catch (Exception ex)
                                    {
                                        logger.Error($"key={key} Error during WindowPattern attempt: {ex.Message}");
                                    }

                                    if (closed && !string.IsNullOrEmpty(closedBy))
                                    {
                                        var cbMsg = $"key={key} ClosedBy={closedBy}";
                                        LogConsole(cbMsg);
                                        logger.Info(cbMsg);
                                    }
                                }
                            // NOTE: hard timeout behavior (posting WM_CLOSE) was removed
                            // was removed to avoid forced closes that may leave Quick Settings open
                            // or otherwise disrupt the desktop state. If needed, implement a
                            // conservative verification loop after ToggleActionCenterViaWinA instead.

                            if (closed)
                            {
                                tracked.Remove(key);
                                // if group has no more members, remove group
                                if (!tracked.Values.Any(t => t.GroupId == groupId))
                                {
                                    groups.Remove(groupId);
                                }
                            }
                        }
                    }

                    // Cleanup tracked entries not present
                    var presentKeys = new HashSet<string>(found.Select(f => MakeKey(f)));
                    foreach (var k in tracked.Keys.ToList())
                    {
                        if (!presentKeys.Contains(k) && (DateTime.UtcNow - tracked[k].FirstSeen).TotalSeconds > 5.0)
                        {
                            var gid = tracked[k].GroupId;
                            tracked.Remove(k);
                            if (!tracked.Values.Any(t => t.GroupId == gid))
                                groups.Remove(gid);
                        }
                    }
                }
                catch (Exception ex)
                {
                    LogConsole("Exception during scan: " + ex);
                }

                Thread.Sleep(TimeSpan.FromSeconds(poll));
            }
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
        static string MakeKey(FlaUI.Core.AutomationElements.AutomationElement w)
        {
            try
            {
                // Prefer RuntimeId if available (unique per toast)
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

                // Fallback to process id + bounding rect
                var rect = w.BoundingRectangle;
                var pid = w.Properties.ProcessId.ValueOrDefault;
                return $"{pid}:{rect.Left}-{rect.Top}-{rect.Right}-{rect.Bottom}";
            }
            catch { return Guid.NewGuid().ToString(); }
        }

        // Safely get the Name of an AutomationElement without throwing when the property is unsupported
        static string SafeGetName(FlaUI.Core.AutomationElements.AutomationElement e)
        {
            if (e == null) return string.Empty;
            try
            {
                var v = e.Properties.Name.ValueOrDefault;
                if (v != null) return v;
            }
            catch { }
            try
            {
                return e.Name ?? string.Empty;
            }
            catch { }
            return string.Empty;
        }

        // Safely get ProcessId without throwing when UIA provider fails
        static int SafeGetProcessId(FlaUI.Core.AutomationElements.AutomationElement e)
        {
            if (e == null) return 0;
            try
            {
                return e.Properties.ProcessId.ValueOrDefault;
            }
            catch { return 0; }
        }

        // Safely get RuntimeId as a string if available
        static string SafeGetRuntimeIdString(FlaUI.Core.AutomationElements.AutomationElement e)
        {
            if (e == null) return string.Empty;
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
        private static void LogConsole(string m)
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

        // Action Center helper: detect if Action Center window is present and toggle it via Win+A using SendInput
        private static bool IsActionCenterOpen()
        {
            // Use UIA direct-desktop child lookup (more reliable than EnumWindows in some cases)
            try
            {
                using var automation = new UIA3Automation();
                var cf = new ConditionFactory(new UIA3PropertyLibrary());
                var desktop = automation.GetDesktop();
                var cond = cf.ByClassName("ControlCenterWindow").And(cf.ByName("クイック設定"));
                var el = desktop.FindFirstChild(cond);
                return el != null;
            }
            catch
            {
                // Fall back to conservative false on any error
                return false;
            }
        }

        private static bool IsNotificationCenterOpen()
        {
            try
            {
                using var automation = new UIA3Automation();
                var cf = new ConditionFactory(new UIA3PropertyLibrary());
                var desktop = automation.GetDesktop();
                var cond = cf.ByClassName("Windows.UI.Core.CoreWindow").And(cf.ByName("通知センター"));
                var el = desktop.FindFirstChild(cond);
                return el != null;
            }
            catch
            {
                return false;
            }
        }

        private static void ToggleShortcutWithDetection(char keyChar, Func<bool> isOpenFunc, int waitMS = 700)
        {
            bool alreadyOpen = false;
            try { alreadyOpen = isOpenFunc(); } catch { alreadyOpen = false; }
            int sends = alreadyOpen ? 3 : 2;
            ushort vk = (ushort)char.ToUpperInvariant(keyChar);
            for (int i = 0; i < sends; i++)
            {
                var inputs = new NativeMethods.INPUT[4];
                inputs[0].type = NativeMethods.INPUT_KEYBOARD;
                inputs[0].U.ki.wVk = NativeMethods.VK_LWIN;

                inputs[1].type = NativeMethods.INPUT_KEYBOARD;
                inputs[1].U.ki.wVk = vk;

                inputs[2].type = NativeMethods.INPUT_KEYBOARD;
                inputs[2].U.ki.wVk = vk;
                inputs[2].U.ki.dwFlags = NativeMethods.KEYEVENTF_KEYUP;

                inputs[3].type = NativeMethods.INPUT_KEYBOARD;
                inputs[3].U.ki.wVk = NativeMethods.VK_LWIN;
                inputs[3].U.ki.dwFlags = NativeMethods.KEYEVENTF_KEYUP;

                NativeMethods.SendInput((uint)inputs.Length, inputs, System.Runtime.InteropServices.Marshal.SizeOf(typeof(NativeMethods.INPUT)));
                try
                {
                    var ts = DateTime.Now;
                    var msg = $"Sent Win+{char.ToUpperInvariant(keyChar)} #{i+1}/{sends} (at {ts:HH:mm:ss.fff})";
                    LogConsole(msg);
                }
                catch { }
                Thread.Sleep(waitMS);
            }
        }

        private static void ToggleActionCenterViaWinA(int waitMS = 700)
        {
            // Backwards-compatible wrapper that toggles Action Center via Win+A
            ToggleShortcutWithDetection('A', IsActionCenterOpen, waitMS);
        }

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

        // Try to find the host HWND for a toast element by using WindowFromPoint and climbing ancestors
        private static IntPtr FindHostWindowHandle(FlaUI.Core.AutomationElements.AutomationElement w)
        {
            try
            {
                var rect = w.BoundingRectangle;
                var cx = (int)((rect.Left + rect.Right) / 2);
                var cy = (int)((rect.Top + rect.Bottom) / 2);
                var hwnd = NativeMethods.WindowFromPoint(new Point(cx, cy));
                if (hwnd == IntPtr.Zero) return IntPtr.Zero;

                var cur = hwnd;
                for (int i = 0; i < 8; i++)
                {
                    try
                    {
                        var className = new System.Text.StringBuilder(256);
                        var clen = NativeMethods.GetClassName(cur, className, className.Capacity);
                        var cls = clen > 0 ? className.ToString() : string.Empty;
                        var titleSb = new System.Text.StringBuilder(256);
                        NativeMethods.GetWindowText(cur, titleSb, titleSb.Capacity);
                        var title = titleSb.ToString() ?? string.Empty;

                        if (string.Equals(cls, "Windows.UI.Core.CoreWindow", StringComparison.OrdinalIgnoreCase)
                            && title.IndexOf("新しい通知", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            return cur;
                        }
                    }
                    catch { }

                    cur = NativeMethods.GetAncestor(cur, NativeMethods.GA_PARENT);
                    if (cur == IntPtr.Zero) break;
                }
            }
            catch { }
            return IntPtr.Zero;
        }

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
            public static Logger? Instance { get; set; }
            public event Action<string>? OnLogLine;
            // When true, log file lines will also be written to Console (same format)
            public static bool IsDebugEnabled = false;
            private readonly object _lock = new object();
            private readonly System.IO.StreamWriter _writer;
            public Logger(string path)
            {
                // Open the file with shared read/write so other writers (e.g., File.AppendAllText)
                // can append concurrently for diagnostic entries.
                var fs = new System.IO.FileStream(path, System.IO.FileMode.Append, System.IO.FileAccess.Write, System.IO.FileShare.ReadWrite);
                _writer = new System.IO.StreamWriter(fs) { AutoFlush = true };
                Info($"===== log start: {DateTime.Now:yyyy/MM/dd HH:mm:ss} =====");
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

        static bool TryInvokeCloseButton(FlaUI.Core.AutomationElements.AutomationElement w, ConditionFactory cf)
        {
            try
            {
                var btnCond = cf.ByControlType(ControlType.Button).And(cf.ByName("閉じる").Or(cf.ByName("Close")));
                var btn = w.FindFirstDescendant(btnCond);
                if (btn != null)
                {
                    var asButton = btn.AsButton();
                    if (asButton != null)
                    {
                        asButton.Invoke();
                        LogConsole("Invoked close button via FlaUI");
                        return true;
                    }
                }
            }
            catch (Exception ex)
            {
                LogConsole("Error in TryInvokeCloseButton: " + ex.Message);
            }
            return false;
        }

        // Helper: classify whether a virtual-key code is a likely keyboard key
        private static bool IsKeyboardVirtualKey(int vk)
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
