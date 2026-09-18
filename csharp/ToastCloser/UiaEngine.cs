using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Conditions;
using FlaUI.Core.Definitions;
using FlaUI.UIA3;

namespace ToastCloser
{
    // Encapsulates all FlaUI-dependent code so Program.cs can remain free of FlaUI type references.
    public static class UiaEngine
    {
        public static void RunLoop(Config cfg, string exeFolder, string logsDir, int minSeconds, int poll, int detectionTimeoutMS, bool detectOnly, int shortcutKeyWaitIdleMS, int shortcutKeyMaxWaitMS, int winShortcutKeyIntervalMS, string shortcutKeyMode, bool wmCloseOnly, CancellationToken ct = default)
        {
            var logger = Program.Logger.Instance;

            var tracked = new Dictionary<string, TrackedInfo>();
            var groups = new Dictionary<int, DateTime>();
            int nextGroupId = 1;
            // Display timer: when a target toast is first discovered, set this timer
            // and when it elapses, a background worker will perform the idle-check -> send-shortcut flow.
            DateTime? displayDeadline = null;
            bool displayTimerActive = false;
            // Single lock to protect shared state: tracked, groups, nextGroupId,
            // displayTimerActive, displayDeadline. Keep critical sections small.
            var stateLock = new object();

            // UIA automation instances are reinitializable on timeout. Keep them in mutable variables
            UIA3Automation? automation = new UIA3Automation();
            ConditionFactory? cf = new ConditionFactory(new UIA3PropertyLibrary());
            AutomationElement? desktop = automation?.GetDesktop();
            var automationLock = new object();

            // Track display-timer worker tasks so we can wait for them on shutdown
            var workerTasks = new List<Task>();
            var workerTasksLock = new object();

            // Grace period (ms) to allow quick shutdown cleanup in workers
            // 1000 ms = 1 second
            const int ShutdownGraceMS = 1000;

            void InitializeAutomation()
            {
                lock (automationLock)
                {
                    try
                    {
                        try { automation?.Dispose(); } catch (Exception ex) { try { logger?.Debug("InitializeAutomation dispose failed: " + ex.Message); } catch { } }
                        automation = new UIA3Automation();
                        cf = new ConditionFactory(new UIA3PropertyLibrary());
                        desktop = automation?.GetDesktop();
                    }
                    catch (Exception ex) { try { logger?.Error("InitializeAutomation failed: " + ex.Message); } catch { } desktop = automation?.GetDesktop(); }
                }
            }

            InitializeAutomation();

            // initialize cursor position and tick timestamps
            try { NativeMethods.GetCursorPos(out Program._lastCursorPos); } catch (Exception ex) { try { logger?.Debug("GetCursorPos failed during init: " + ex.Message); } catch { } }
            Program._lastKeyboardTick = (uint)Environment.TickCount;
            Program._lastMouseTick = (uint)Environment.TickCount;

            // Local copy of config flags used inside the loop
            var localCfg = cfg ?? new Config();

            while (true)
            {
                if (ct.IsCancellationRequested) break;
                // If feature disabled via tray or external toggle, pause scanning at the poll level.
                // Clear any tracked state so workers won't send for stale entries.
                if (Program.DisableFeature)
                {
                    try
                    {
                        lock (stateLock)
                        {
                            displayTimerActive = false;
                            displayDeadline = null;
                            tracked.Clear();
                            groups.Clear();
                        }
                        try { logger?.Info("RunLoop paused: feature disabled"); } catch { }
                    }
                    catch { }
                    goto NextIteration;
                }
                try
                {
                    lock (automationLock)
                    {
                        try { desktop = automation?.GetDesktop(); } catch { desktop = automation?.GetDesktop(); }
                    }

                    var searchStart = DateTime.UtcNow;
                    logger?.Info("Toast search: start");

                    var foundList = new List<AutomationElement>();

                    Task<List<AutomationElement>> searchTask = Task.Run(() =>
                    {
                        var localFound = new List<AutomationElement>();
                        try
                        {
                            var localCf = cf;
                            var localDesktop = desktop;
                            if (localCf == null || localDesktop == null)
                            {
                                logger?.Info("UIA not initialized for search; skipping local search");
                                return localFound;
                            }
                            var coreByNameCond = localCf.ByClassName("Windows.UI.Core.CoreWindow").And(localCf.ByName("新しい通知"));
                            AutomationElement? coreElement = null;
                            try
                            {
                                logger?.Debug($"Calling desktop.FindFirstChild(CoreWindow by name) (elapsed={(DateTime.UtcNow - searchStart).TotalMilliseconds:0.0}ms)");
                                coreElement = localDesktop.FindFirstChild(coreByNameCond);
                            }
                            catch (Exception ex)
                            {
                                logger?.Error("Exception during UIA CoreWindow search: " + ex.Message + $" (elapsed={(DateTime.UtcNow - searchStart).TotalMilliseconds:0.0}ms)");
                            }
                            logger?.Debug($"CoreWindow found={(coreElement != null)} (elapsed={(DateTime.UtcNow - searchStart).TotalMilliseconds:0.0}ms)");

                            if (coreElement == null)
                            {
                                logger?.Debug($"CoreWindow(Name='新しい通知') not found; ending CoreWindow-based search. (elapsed={(DateTime.UtcNow - searchStart).TotalMilliseconds:0.0}ms)");
                            }
                            else
                            {
                                logger?.Debug($"Finding ScrollViewer under CoreWindow (elapsed={(DateTime.UtcNow - searchStart).TotalMilliseconds:0.0}ms)");
                                var scroll = coreElement.FindFirstDescendant(localCf.ByClassName("ScrollViewer"));
                                logger?.Debug($"ScrollViewer found={(scroll != null)} (elapsed={(DateTime.UtcNow - searchStart).TotalMilliseconds:0.0}ms)");

                                if (scroll != null)
                                {
                                    logger?.Debug($"Enumerating FlexibleToastView under ScrollViewer (elapsed={(DateTime.UtcNow - searchStart).TotalMilliseconds:0.0}ms)");
                                    var toasts = scroll.FindAllDescendants(localCf.ByClassName("FlexibleToastView"));
                                    logger?.Debug($"FlexibleToastView count={(toasts?.Length ?? 0)} (elapsed={(DateTime.UtcNow - searchStart).TotalMilliseconds:0.0}ms)");

                                    if (toasts != null && toasts.Length > 0)
                                    {
                                        foreach (var t in toasts)
                                        {
                                            try
                                            {
                                                logger?.Debug($"Inspecting FlexibleToastView candidate (elapsed={(DateTime.UtcNow - searchStart).TotalMilliseconds:0.0}ms)");
                                                var tbAttrCond = localCf.ByClassName("TextBlock").And(localCf.ByAutomationId("Attribution")).And(localCf.ByControlType(ControlType.Text));
                                                var tbAttr = t.FindFirstDescendant(tbAttrCond);
                                                logger?.Debug($"Attribution found={(tbAttr != null)} (elapsed={(DateTime.UtcNow - searchStart).TotalMilliseconds:0.0}ms)");
                                                if (tbAttr != null)
                                                {
                                                    var attr = SafeGetName(tbAttr);
                                                    logger?.Debug($"Attribution.Name=\"{attr}\" (elapsed={(DateTime.UtcNow - searchStart).TotalMilliseconds:0.0}ms)");
                                                    if (!string.IsNullOrEmpty(attr))
                                                    {
                                                        if (localCfg.YoutubeOnly)
                                                        {
                                                            if (string.Equals(attr.Trim(), "www.youtube.com", StringComparison.OrdinalIgnoreCase))
                                                            {
                                                                localFound.Add(t);
                                                                logger?.Debug($"Added FlexibleToastView candidate (Attribution equals 'www.youtube.com') (elapsed={(DateTime.UtcNow - searchStart).TotalMilliseconds:0.0}ms)");
                                                            }
                                                        }
                                                        else
                                                        {
                                                            localFound.Add(t);
                                                            logger?.Debug($"Added FlexibleToastView candidate (Attribution present) (elapsed={(DateTime.UtcNow - searchStart).TotalMilliseconds:0.0}ms)");
                                                        }
                                                    }
                                                }
                                            }
                                            catch (Exception ex)
                                            {
                                                logger?.Error("Error while inspecting toast: " + ex.Message + $" (elapsed={(DateTime.UtcNow - searchStart).TotalMilliseconds:0.0}ms)");
                                            }
                                        }
                                    }
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            logger?.Error("Exception during CoreWindow path: " + ex.Message + $" (elapsed={(DateTime.UtcNow - searchStart).TotalMilliseconds:0.0}ms)");
                        }
                        return localFound;
                    }, ct);

                    if (searchTask.Wait(detectionTimeoutMS, ct) && searchTask.Status == TaskStatus.RanToCompletion)
                    {
                        foundList = searchTask.Result ?? [];
                    }
                    else
                    {
                        logger?.Warn($"CoreWindow search timed out after {detectionTimeoutMS}ms; skipping this scan to avoid long blocking. (elapsed={(DateTime.UtcNow - searchStart).TotalMilliseconds:0.0}ms)");
                        logger?.Debug($"CoreWindow search timed out after {detectionTimeoutMS}ms and was cancelled for this poll (durationMS={detectionTimeoutMS})");
                        foundList = [];

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
                                try { logger?.Error($"UIA reinitialization failed: {ex.Message}"); } catch { }
                                return false;
                            }
                        }, ct);

                        bool reinitCompleted = reinitTask.Wait(detectionTimeoutMS, ct);
                        reinitSw.Stop();
                        if (reinitCompleted && reinitTask.Result)
                        {
                            logger?.Info($"UIA reinitialized in {reinitSw.ElapsedMilliseconds}ms after search timeout");
                        }
                        else
                        {
                            logger?.Debug($"UIA reinitialization timed out after {detectionTimeoutMS}ms");
                            try { Thread.Sleep(detectionTimeoutMS); } catch (Exception ex) { try { logger?.Debug("Thread.Sleep during reinit wait failed: " + ex.Message); } catch { } }
                        }
                    }

                    AutomationElement[] found = [.. foundList];
                    var cfSafe = cf ?? new ConditionFactory(new UIA3PropertyLibrary());
                    if (found.Length == 0)
                    {
                        logger?.Info($"No toasts found by CoreWindow-based search; ending search for this scan. (elapsed={(DateTime.UtcNow - searchStart).TotalMilliseconds:0.0}ms)");
                        logger?.Info($"Toast search: end (duration={(DateTime.UtcNow - searchStart).TotalMilliseconds:0.0}ms) found=0");

                        try
                        {
                            lock (stateLock)
                            {
                                var keysToRemove = tracked.Keys.ToList();
                                foreach (var k in keysToRemove)
                                {
                                    try
                                    {
                                        var gid = tracked[k].GroupId;
                                        tracked.Remove(k);
                                        if (!tracked.Values.Any(t => t.GroupId == gid)) groups.Remove(gid);
                                    }
                                    catch (Exception ex) { try { logger?.Debug("Error removing tracked key during cleanup: " + ex.Message); } catch { } }
                                }
                            }
                            logger?.Info("No toasts present: cleaned tracked/groups");
                        }
                        catch (Exception ex)
                        {
                            try { logger?.Error($"Error cleaning tracked on empty scan: {ex.Message}"); } catch { }
                        }

                        goto NextIteration;
                    }

                    var searchEnd = DateTime.UtcNow;
                    var searchMS = (searchEnd - searchStart).TotalMilliseconds;
                    logger?.Debug($"Scan found {found.Length} candidates durationMS={searchMS:0.0}");
                    logger?.Info($"Toast search: end (duration={searchMS:0.0}ms) found={found.Length}");

                    foreach (var w in found)
                    {
                        string key = MakeKey(w);
                        bool isNewKey = false;
                        lock (stateLock) { isNewKey = !tracked.ContainsKey(key); }
                        if (isNewKey)
                        {
                            int assignedGroup = -1;
                            var now = DateTime.UtcNow;
                            lock (stateLock)
                            {
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
                            }
                            var methodStr = "priority";
                            string contentSummary = string.Empty;
                            string contentDisplay = string.Empty;
                            try
                            {
                                var textNodes = w.FindAllDescendants(cfSafe.ByControlType(ControlType.Text));
                                var parts = new List<string>();
                                foreach (var tn in textNodes)
                                {
                                    try
                                    {
                                        var tname = SafeGetName(tn);
                                        if (!string.IsNullOrWhiteSpace(tname)) parts.Add(tname.Trim());
                                    }
                                    catch (Exception ex) { try { logger?.Debug("UiaEngine: SafeGetName(tn) failed: " + ex.ToString()); } catch { } }
                                }
                                if (parts.Count > 0)
                                {
                                    contentSummary = string.Join(" || ", parts);
                                    try
                                    {
                                        var nameLower = SafeGetName(w);
                                        var filtered = parts.Where(p => string.IsNullOrEmpty(p) || !nameLower.Contains(p, StringComparison.OrdinalIgnoreCase)).ToList();
                                        if (filtered.Count == 0)
                                        {
                                            filtered = parts.Where(p => p.Contains("www.", StringComparison.OrdinalIgnoreCase) || p.Contains("閉じる", StringComparison.OrdinalIgnoreCase)).ToList();
                                        }
                                        if (filtered.Count == 0) filtered = [.. parts.Take(1)];
                                        contentDisplay = string.Join(" || ", filtered);
                                        if (contentDisplay.Length > 800) contentDisplay = string.Concat(contentDisplay.AsSpan(0, 800), "...");
                                    }
                                    catch (Exception ex) { try { logger?.Debug("UiaEngine: building contentDisplay failed: " + ex.ToString()); } catch { } contentDisplay = contentSummary; }
                                    if (contentSummary.Length > 800) contentSummary = string.Concat(contentSummary.AsSpan(0, 800), "...");
                                }
                            }
                            catch (Exception ex) { try { logger?.Debug("UiaEngine: extracting toast text failed: " + ex.ToString()); } catch { } }

                            var pidVal2 = SafeGetProcessId(w);
                            var safeName2 = SafeGetName(w).Replace('\n', ' ').Replace('\r', ' ').Trim();
                            var cleanName = CleanNotificationName(safeName2, contentSummary);
                            lock (stateLock)
                            {
                                tracked[key] = new TrackedInfo { FirstSeen = now, GroupId = assignedGroup, Method = methodStr, Pid = pidVal2, ShortName = cleanName };
                            }

                            try
                            {
                                bool shouldStartWorker = false;
                                DateTime workerDeadline = DateTime.MinValue;
                                lock (stateLock)
                                {
                                    if (!displayTimerActive)
                                    {
                                        displayTimerActive = true;
                                        displayDeadline = DateTime.UtcNow.AddSeconds(minSeconds);
                                        try { logger?.Info($"Display timer set (deadline={displayDeadline.Value.ToLocalTime():yyyy-MM-dd HH:mm:ss.fff zzz}, displayLimitSeconds={minSeconds})"); } catch (Exception ex) { try { logger?.Debug("UiaEngine: logging Display timer set failed: " + ex.ToString()); } catch { } }
                                        workerDeadline = displayDeadline.Value;
                                        shouldStartWorker = true;
                                    }
                                }

                                if (shouldStartWorker)
                                {
                                    var workerTask = Task.Run(async () =>
                                    {
                                        CancellationTokenRegistration reg = default;
                                        try
                                        {
                                            try
                                            {
                                                reg = ct.Register(() =>
                                                {
                                                    try
                                                    {
                                                        lock (stateLock)
                                                        {
                                                            displayTimerActive = false;
                                                            displayDeadline = null;
                                                            tracked.Clear();
                                                            groups.Clear();
                                                        }
                                                        try { logger?.Info("Shutdown handler in worker: cleared tracked/groups"); } catch (Exception ex) { try { logger?.Debug("UiaEngine: logging shutdown handler info failed: " + ex.ToString()); } catch { } }
                                                    }
                                                    catch (Exception ex) { try { logger?.Debug("Shutdown handler in worker failed: " + ex.Message); } catch { } }
                                                    try { Thread.Sleep(ShutdownGraceMS); } catch (Exception ex) { try { logger?.Debug("UiaEngine: Sleep in shutdown handler failed: " + ex.ToString()); } catch { } }
                                                });
                                            }
                                            catch (Exception ex) { try { logger?.Debug("Registering shutdown handler failed: " + ex.Message); } catch { } }

                                            var waitMs = (int)Math.Max(0, (workerDeadline - DateTime.UtcNow).TotalMilliseconds);
                                            if (waitMs > 0) await Task.Delay(waitMs, ct).ConfigureAwait(false);

                                            try { logger?.Info($"Display timer worker awakened (deadline={workerDeadline.ToLocalTime():yyyy-MM-dd HH:mm:ss.fff zzz})"); } catch (Exception ex) { try { logger?.Debug("UiaEngine: logging worker awakened failed: " + ex.ToString()); } catch { } }

                                            var monitoringStart = DateTime.UtcNow;

                                            Program._lastKeyboardTick = (uint)Environment.TickCount;
                                            Program._lastMouseTick = (uint)Environment.TickCount;

                                            try
                                            {
                                                if (NativeMethods.GetCursorPos(out var ipos))
                                                {
                                                    Program._lastCursorPos = ipos;
                                                    Program._lastMouseTick = (uint)Environment.TickCount;
                                                    if (Program.Logger.IsDebugEnabled) logger?.Debug($"DisplayTimerWorker: Mouse at {ipos.X},{ipos.Y}");
                                                }
                                            }
                                            catch (Exception ex) { try { logger?.Debug("UiaEngine: exception during GetCursorPos in worker: " + ex.ToString()); } catch { } }

                                            try
                                            {
                                                for (int vk = 0x01; vk <= 0xFE; vk++)
                                                {
                                                    try
                                                    {
                                                        short s = NativeMethods.GetAsyncKeyState(vk);
                                                        bool transition = (s & 0x8000) != 0 || (s & 0x0001) != 0;
                                                        if (transition && (Program.IsKeyboardVirtualKey(vk) || vk == 0x01 || vk == 0x02 || vk == 0x04))
                                                        {
                                                            Program._lastKeyboardTick = (uint)Environment.TickCount;
                                                            if (Program.Logger.IsDebugEnabled) logger?.Debug($"DisplayTimerWorker: Detected vk={vk}");
                                                            break;
                                                        }
                                                    }
                                                    catch (Exception ex) { try { logger?.Debug("UiaEngine: GetAsyncKeyState inner exception during monitoring: " + ex.ToString()); } catch { } }
                                                }
                                            }
                                            catch (Exception ex) { try { logger?.Debug("UiaEngine: exception during GetAsyncKeyState loop in worker: " + ex.ToString()); } catch { } }

                                            try
                                            {
                                                while (true)
                                                {
                                                    if (ct.IsCancellationRequested) break;
                                                    try { await Task.Delay(500, ct).ConfigureAwait(false); } catch (OperationCanceledException) { break; }

                                                    // マウス移動検知
                                                    try
                                                    {
                                                        if (NativeMethods.GetCursorPos(out var cur))
                                                        {
                                                            if (cur.X != Program._lastCursorPos.X || cur.Y != Program._lastCursorPos.Y)
                                                            {
                                                                Program._lastCursorPos = cur;
                                                                Program._lastMouseTick = (uint)Environment.TickCount;
                                                                if (Program.Logger.IsDebugEnabled) logger?.Debug($"DisplayTimerWorker: Detected mouse movement during monitoring: {cur.X},{cur.Y}");
                                                            }
                                                        }
                                                    }
                                                    catch (Exception ex) { try { logger?.Debug("UiaEngine: exception checking async key state in worker: " + ex.ToString()); } catch { } }

                                                    // キーボード入力検知
                                                    try
                                                    {
                                                        for (int vk = 0x01; vk <= 0xFE; vk++)
                                                        {
                                                            try
                                                            {
                                                                short s = NativeMethods.GetAsyncKeyState(vk);
                                                                bool transition = (s & 0x8000) != 0 || (s & 0x0001) != 0;
                                                                if (transition && (Program.IsKeyboardVirtualKey(vk) || vk == 0x01 || vk == 0x02 || vk == 0x04))
                                                                {
                                                                    Program._lastKeyboardTick = (uint)Environment.TickCount;
                                                                    if (Program.Logger.IsDebugEnabled) logger?.Debug($"DisplayTimerWorker: Detected keyboard activity during monitoring (vk={vk})");
                                                                    break;
                                                                }
                                                            }
                                                            catch (Exception ex) { try { logger?.Debug("UiaEngine: GetAsyncKeyState inner exception: " + ex.ToString()); } catch { } }
                                                        }
                                                    }
                                                    catch (Exception ex) { try { logger?.Debug("UiaEngine: exception in keyboard-check loop in worker: " + ex.ToString()); } catch { } }

                                                    // IMEの未確定文字列（Composition）検知
                                                    bool isComposing = Program.IsComposing;
                                                    if (isComposing)
                                                    {
                                                        try { logger?.Info("IME composing detected (Japanese input state with unconfirmed text); suppressing shortcut send"); } catch { }
                                                        // 日本語入力・変換中の場合は最終キー入力を現在時刻に更新して待機を維持
                                                        Program._lastKeyboardTick = (uint)Environment.TickCount;
                                                    }
                                                    else
                                                    {
                                                        try { logger?.Debug("IME composing not detected (fallback: no unconfirmed composition string); proceeding with idle check"); } catch { }
                                                    }

                                                    try
                                                    {
                                                        // 自前のキー・マウス監視経過時間
                                                        uint localElapsed = (uint)(Environment.TickCount - Math.Max(Program._lastKeyboardTick, Program._lastMouseTick));
                                                        // OS全体の無操作時間（GetLastInputInfo）
                                                        uint osIdle = Program.GetIdleMilliseconds();

                                                        // 双方ともに指定アイドル時間を満たしている場合のみ実行
                                                        uint effectiveIdle = Math.Min(localElapsed, osIdle);

                                                        var monitorElapsedMS = (int)(DateTime.UtcNow - monitoringStart).TotalMilliseconds;
                                                        if (shortcutKeyMaxWaitMS > 0 && monitorElapsedMS >= shortcutKeyMaxWaitMS)
                                                        {
                                                            // IME変換中またはキー入力直後の場合はタイムアウトでも強制送信せず待機を延長
                                                            if (isComposing || effectiveIdle < (uint)shortcutKeyWaitIdleMS)
                                                            {
                                                                monitoringStart = DateTime.UtcNow.AddMilliseconds(-shortcutKeyMaxWaitMS + 2000);
                                                                continue;
                                                            }

                                                            logger?.Info($"DisplayTimerWorker: monitor timed out after {monitorElapsedMS}ms (max {shortcutKeyMaxWaitMS}ms); considering send");
                                                            bool shouldSendTimeout = false;
                                                            try { lock (stateLock) { shouldSendTimeout = tracked.Count > 0; } } catch { shouldSendTimeout = true; }
                                                            if (shouldSendTimeout)
                                                            {
                                                                if (string.Equals(shortcutKeyMode, "noticecenter", StringComparison.OrdinalIgnoreCase))
                                                                {
                                                                    var prev = NativeMethods.GetForegroundWindow();
                                                                    ToggleShortcutWithDetection('N', IsNotificationCenterOpen, winShortcutKeyIntervalMS);
                                                                    logger?.Info("Notification Center toggled (display-timer: timeout)");
                                                                    try { Thread.Sleep(150); } catch { }
                                                                    TryRestoreForegroundWindow(prev);
                                                                }
                                                                else
                                                                {
                                                                    var prev = NativeMethods.GetForegroundWindow();
                                                                    ToggleShortcutWithDetection('A', IsActionCenterOpen, winShortcutKeyIntervalMS);
                                                                    logger?.Info("Action Center toggled (display-timer: timeout)");
                                                                    try { Thread.Sleep(150); } catch { }
                                                                    TryRestoreForegroundWindow(prev);
                                                                }
                                                            }
                                                            else
                                                            {
                                                                logger?.Info("DisplayTimerWorker: tracked empty at timeout; skipping send");
                                                            }

                                                            break;
                                                        }

                                                        if (effectiveIdle >= (uint)shortcutKeyWaitIdleMS)
                                                        {
                                                            // IME変換中の場合は送信を保留
                                                            if (isComposing)
                                                            {
                                                                continue;
                                                            }

                                                            bool shouldSendIdle = false;
                                                            try { lock (stateLock) { shouldSendIdle = tracked.Count > 0; } } catch { shouldSendIdle = true; }
                                                            if (shouldSendIdle)
                                                            {
                                                                if (string.Equals(shortcutKeyMode, "noticecenter", StringComparison.OrdinalIgnoreCase))
                                                                {
                                                                    var prev = NativeMethods.GetForegroundWindow();
                                                                    ToggleShortcutWithDetection('N', IsNotificationCenterOpen, winShortcutKeyIntervalMS);
                                                                    logger?.Info("Notification Center toggled (display-timer)");
                                                                    try { Thread.Sleep(150); } catch { }
                                                                    TryRestoreForegroundWindow(prev);
                                                                }
                                                                else
                                                                {
                                                                    var prev = NativeMethods.GetForegroundWindow();
                                                                    ToggleShortcutWithDetection('A', IsActionCenterOpen, winShortcutKeyIntervalMS);
                                                                    logger?.Info("Action Center toggled (display-timer)");
                                                                    try { Thread.Sleep(150); } catch { }
                                                                    TryRestoreForegroundWindow(prev);
                                                                }
                                                            }
                                                            else
                                                            {
                                                                logger?.Info("DisplayTimerWorker: tracked empty at idle-check; skipping send");
                                                            }
                                                            break;
                                                        }
                                                    }
                                                    catch { }
                                                }
                                            }
                                            catch (ThreadInterruptedException) { }
                                        }
                                        catch (Exception ex)
                                        {
                                            try { logger?.Error($"DisplayTimerWorker failed: {ex.Message}"); } catch { }
                                        }
                                        finally
                                        {
                                            try { reg.Dispose(); } catch { }
                                            try { lock (stateLock) { displayTimerActive = false; displayDeadline = null; } } catch { }
                                            try
                                            {
                                                lock (stateLock)
                                                {
                                                    try { tracked.Clear(); groups.Clear(); } catch { }
                                                }
                                            }
                                            catch { }
                                            try { logger?.Info("Display timer worker completed and cleared tracked/groups"); } catch { }
                                        }
                                    }, ct);
                                    lock (workerTasksLock) { workerTasks.Add(workerTask); }
                                    _ = workerTask.ContinueWith(t => { lock (workerTasksLock) { workerTasks.Remove(t); } }, TaskScheduler.Default);
                                }
                            }
                            catch { }

                            var msg = $"key={key} | Found | group={assignedGroup} | method={methodStr} | pid={pidVal2} | name=\"{safeName2}\"";
                            if (!string.IsNullOrEmpty(contentDisplay)) msg += $" | content=\"{contentDisplay}\"";
                            try
                            {
                                var rid2 = SafeGetRuntimeIdString(w);
                                var rect2 = w.BoundingRectangle;
                                var cn2 = w.ClassName ?? string.Empty;
                                var aidx2 = w.Properties.AutomationId.ValueOrDefault ?? string.Empty;
                                var infoMsg = $"key={key} | Found | group={assignedGroup} | method={methodStr} | pid={pidVal2} | name=\"{cleanName}\"";
                                if (!string.IsNullOrEmpty(contentDisplay)) infoMsg += $" | content=\"{contentDisplay}\"";

                                string rawNameDbg = safeName2 ?? string.Empty;
                                string contentSummaryDbg = contentSummary ?? string.Empty;
                                int textCount = 0;
                                try
                                {
                                    var tnodes = w.FindAllDescendants(cfSafe.ByControlType(ControlType.Text));
                                    textCount = tnodes?.Length ?? 0;
                                }
                                catch { }

                                var debugMsg = infoMsg + $" | rawName=\"{rawNameDbg}\" | contentSummary=\"{contentSummaryDbg}\" | class={cn2} aid={aidx2} rid={rid2} rect={rect2.Left}-{rect2.Top}-{rect2.Right}-{rect2.Bottom} | textCount={textCount}";

                                logger?.Debug(() => debugMsg);
                                logger?.Info(infoMsg);
                            }
                            catch
                            {
                                logger?.Debug(() => msg);
                                logger?.Info($"新しい通知があります。key={key} | Found | group={assignedGroup} | method={methodStr} | pid={pidVal2} | name=\"{cleanName}\"");
                            }
                            goto NextIteration;
                        }

                        int groupId;
                        DateTime groupStart;
                        TrackedInfo stored;
                        lock (stateLock)
                        {
                            groupId = tracked[key].GroupId;
                            groupStart = groups.TryGetValue(groupId, out var grpTime) ? grpTime : tracked[key].FirstSeen;
                            stored = tracked[key];
                        }
                        var elapsed = (DateTime.UtcNow - groupStart).TotalSeconds;
                        var msgElapsed = $"key={key} | group={groupId} | elapsed={elapsed:0.0}s";
                        logger?.Debug(() => msgElapsed);

                        try
                        {
                            var methodStored = stored.Method ?? "priority";
                            var pidStored = stored.Pid;
                            var nameStored = stored.ShortName ?? string.Empty;
                            var stillMsg = $"閉じられていない通知があります　key={key} | Found | group={groupId} | method={methodStored} | pid={pidStored} | name=\"{nameStored}\" (elapsed {elapsed:0.0})";
                            logger?.Info(stillMsg);
                        }
                        catch { }

                        try
                        {
                            var textNodesEx = w.FindAllDescendants(cfSafe.ByControlType(ControlType.Text));
                            var partsEx = new List<string>();
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
                                if (contentEx.Length > 800) contentEx = string.Concat(contentEx.AsSpan(0, 800), "...");
                                logger?.Info($"key={key} | Details: {contentEx}");
                            }
                        }
                        catch { }
                    }

                    var presentKeys = new HashSet<string>(found.Select(MakeKey));
                    lock (stateLock)
                    {
                        var keysSnapshot = tracked.Keys.ToList();
                        foreach (var k in keysSnapshot)
                        {
                            if (!presentKeys.Contains(k))
                            {
                                var gid = tracked[k].GroupId;
                                tracked.Remove(k);
                                if (!tracked.Values.Any(t => t.GroupId == gid)) groups.Remove(gid);
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    logger?.Error("Exception during scan: " + ex);
                }
            NextIteration:
                try { ct.WaitHandle.WaitOne(TimeSpan.FromSeconds(poll)); } catch { }
                if (ct.IsCancellationRequested) break;
            }

            // Shutdown: wait briefly for any display-timer worker tasks to finish
            try
            {
                try { logger?.Info("RunLoop exiting: waiting for worker tasks to complete"); } catch { }
                List<Task> tasksCopy;
                lock (workerTasksLock) { tasksCopy = [.. workerTasks]; }
                if (tasksCopy.Count > 0)
                {
                    try { Task.WaitAll([.. tasksCopy], TimeSpan.FromSeconds(5)); } catch { }
                }
            }
            catch { }

            try { lock (automationLock) { try { automation?.Dispose(); } catch { } automation = null; desktop = null; cf = null; } } catch { }
        }

        static string CleanNotificationName(string rawName, string contentSummary)
        {
            if (string.IsNullOrWhiteSpace(rawName)) return string.Empty;
            var s = rawName;
            s = s.Replace("からの新しい通知があります", "");
            s = s.Replace("からの新しい通知があります。。", "");
            s = s.Replace("。。", " ");
            s = s.Replace("。", " ");
            s = s.Replace("操作。", "");
            s = Regex.Replace(s, "\\s+", " ").Trim();
            if (!string.IsNullOrEmpty(contentSummary) && contentSummary.Contains("www.youtube.com", StringComparison.OrdinalIgnoreCase) && !s.Contains("www.youtube.com", StringComparison.OrdinalIgnoreCase))
            {
                s += " www.youtube.com";
            }
            if (s.Length > 200) s = string.Concat(s.AsSpan(0, 200), "...");
            return s;
        }

        static string MakeKey(AutomationElement w)
        {
            try
            {
                if (w == null) return Guid.NewGuid().ToString();
                try
                {
                    var rid = w.Properties.RuntimeId.ValueOrDefault;
                    if (rid != null)
                    {
                        if (rid is System.Collections.IEnumerable ie)
                        {
                            var parts = new List<string>();
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

        static string SafeGetName(AutomationElement e)
        {
            if (e == null) return string.Empty;
            try
            {
                var v = e.Properties.Name.ValueOrDefault;
                if (v != null) return v;
            }
            catch { }
            try { return (string?)(e.Name ?? string.Empty) ?? string.Empty; } catch { }
            return string.Empty;
        }

        static int SafeGetProcessId(AutomationElement e)
        {
            if (e == null) return 0;
            try { return (int)(e.Properties.ProcessId.ValueOrDefault); } catch { return 0; }
        }

        static string SafeGetRuntimeIdString(AutomationElement e)
        {
            if (e == null) return string.Empty;
            try
            {
                var rid = e.Properties.RuntimeId.ValueOrDefault;
                if (rid != null)
                {
                    if (rid is System.Collections.IEnumerable ie)
                    {
                        var parts = new List<string>();
                        foreach (var x in ie) parts.Add(x?.ToString() ?? string.Empty);
                        return string.Join("_", parts);
                    }
                    return rid.ToString() ?? string.Empty;
                }
            }
            catch { }
            return string.Empty;
        }

        static IntPtr FindHostWindowHandle(AutomationElement w)
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
                        var className = new StringBuilder(256);
                        var clen = NativeMethods.GetClassName(cur, className, className.Capacity);
                        var cls = clen > 0 ? className.ToString() : string.Empty;
                        var titleSb = new StringBuilder(256);
                        _ = NativeMethods.GetWindowText(cur, titleSb, titleSb.Capacity);
                        var title = titleSb.ToString();

                        if (string.Equals(cls, "Windows.UI.Core.CoreWindow", StringComparison.OrdinalIgnoreCase)
                            && title.Contains("新しい通知", StringComparison.OrdinalIgnoreCase))
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

        static bool TryInvokeCloseButton(AutomationElement w, ConditionFactory cf)
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
                        Program.LogConsole("Invoked close button via FlaUI");
                        return true;
                    }
                }
            }
            catch (Exception ex)
            {
                Program.LogConsole("Error in TryInvokeCloseButton: " + ex.Message);
            }
            return false;
        }

        static bool IsActionCenterOpen()
        {
            try
            {
                using var automation = new UIA3Automation();
                var cf = new ConditionFactory(new UIA3PropertyLibrary());
                var desktop = automation.GetDesktop();
                if (desktop == null) return false;
                var cond = cf.ByClassName("ControlCenterWindow").And(cf.ByName("クイック設定"));
                var el = desktop.FindFirstChild(cond);
                return el != null;
            }
            catch { return false; }
        }

        static bool IsNotificationCenterOpen()
        {
            try
            {
                using var automation = new UIA3Automation();
                var cf = new ConditionFactory(new UIA3PropertyLibrary());
                var desktop = automation.GetDesktop();
                if (desktop == null) return false;
                var cond = cf.ByClassName("Windows.UI.Core.CoreWindow").And(cf.ByName("通知センター"));
                var el = desktop.FindFirstChild(cond);
                return el != null;
            }
            catch { return false; }
        }

        static void ToggleShortcutWithDetection(char keyChar, Func<bool> isOpenFunc, int waitMS = 700)
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

                NativeMethods.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf(typeof(NativeMethods.INPUT)));
                try { Program.Logger.Instance?.Info($"Sent Win+{char.ToUpperInvariant(keyChar)} #{i + 1}/{sends}"); } catch { }
                Thread.Sleep(waitMS);
            }
        }

        // Attempt to restore previously focused window. Uses AttachThreadInput to
        // increase likelihood SetForegroundWindow succeeds across threads.
        static void TryRestoreForegroundWindow(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return;
            try
            {
                uint targetTid = NativeMethods.GetWindowThreadProcessId(hwnd, out _);
                uint currentTid = NativeMethods.GetCurrentThreadId();
                bool attached = false;
                try
                {
                    attached = NativeMethods.AttachThreadInput(currentTid, targetTid, true);
                }
                catch { attached = false; }

                try { NativeMethods.ShowWindow(hwnd, 5); } catch { }
                try { NativeMethods.SetForegroundWindow(hwnd); } catch { }
                try { NativeMethods.BringWindowToTop(hwnd); } catch { }
                try { NativeMethods.SetFocus(hwnd); } catch { }

                try
                {
                    if (attached)
                    {
                        NativeMethods.AttachThreadInput(currentTid, targetTid, false);
                    }
                }
                catch { }
            }
            catch { }
        }

        static bool IsCoreNotificationWindowPresentNative()
        {
            bool found = false;
            try
            {
                NativeMethods.EnumWindows((h, l) =>
                {
                    try
                    {
                        if (!NativeMethods.IsWindowVisible(h)) return true;
                        var className = new StringBuilder(256);
                        var clen = NativeMethods.GetClassName(h, className, className.Capacity);
                        if (clen > 0)
                        {
                            var cls = className.ToString();
                            if (string.Equals(cls, "Windows.UI.Core.CoreWindow", StringComparison.OrdinalIgnoreCase))
                            {
                                var titleSb = new StringBuilder(256);
                                _ = NativeMethods.GetWindowText(h, titleSb, titleSb.Capacity);
                                var title = titleSb.ToString();
                                if (title.Contains("新しい通知", StringComparison.OrdinalIgnoreCase))
                                {
                                    found = true;
                                    return false;
                                }
                            }
                        }
                    }
                    catch { }
                    return true;
                }, IntPtr.Zero);
            }
            catch { }
            return found;
        }

        class TrackedInfo
        {
            public DateTime FirstSeen { get; set; }
            public int GroupId { get; set; }
            public string? Method { get; set; }
            public int Pid { get; set; }
            public string? ShortName { get; set; }
        }
    }
}