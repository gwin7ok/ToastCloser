using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FlaUI.Core;
using FlaUI.Core.Definitions;
using FlaUI.Core.Conditions;
using FlaUI.Core.AutomationElements;
using FlaUI.UIA3;

namespace ToastCloser
{
    // Encapsulates all FlaUI-dependent code so Program.cs can remain free of FlaUI type references.
    public static class UiaEngine
    {
        public static void RunLoop(Config cfg, string exeFolder, string logsDir, int minSeconds, int poll, int detectionTimeoutMS, bool detectOnly, bool preserveHistory, int shortcutKeyWaitIdleMS, int shortcutKeyMaxWaitMS, int winShortcutKeyIntervalMS, string shortcutKeyMode, bool wmCloseOnly)
        {
            var logger = Program.Logger.Instance;

            var tracked = new Dictionary<string, TrackedInfo>();
            var groups = new Dictionary<int, DateTime>();
            int nextGroupId = 1;
            // Display timer: when a target toast is first discovered, set this timer
            // and when it elapses, a background worker will perform the idle-check -> send-shortcut flow.
            DateTime? displayDeadline = null;
            bool displayTimerActive = false;
            var displayTimerLock = new object();
            // Single lock to protect shared state: tracked, groups, nextGroupId,
            // displayTimerActive, displayDeadline. Keep critical sections small.
            var stateLock = new object();

            // UIA automation instances are reinitializable on timeout. Keep them in mutable variables
            UIA3Automation? automation = new UIA3Automation();
            ConditionFactory? cf = new ConditionFactory(new UIA3PropertyLibrary());
            FlaUI.Core.AutomationElements.AutomationElement? desktop = automation?.GetDesktop();
            var automationLock = new object();

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

            InitializeAutomation();

            // initialize cursor position
            try { NativeMethods.GetCursorPos(out Program._lastCursorPos); } catch { }

            // Local copy of config flags used inside the loop
            var localCfg = cfg ?? new Config();
            // Enforce timer-only behavior: disable legacy preserve-history fallback
            if (preserveHistory)
            {
                try { logger?.Info("preserveHistory flag overridden: running display-timer-only mode"); } catch { }
            }
            preserveHistory = false;
            bool _monitoringStarted = false;

            while (true)
            {
                try
                {
                    if (preserveHistory && !_monitoringStarted)
                    {
                        lock (stateLock)
                        {
                            if (tracked.Count > 0)
                            {
                                // preserve-history fallback disabled (timer-only mode)
                            }
                        }
                    }

                    lock (automationLock)
                    {
                        try { desktop = automation?.GetDesktop(); } catch { desktop = automation?.GetDesktop(); }
                    }

                    var searchStart = DateTime.UtcNow;
                    logger?.Info("Toast search: start");

                    var foundList = new List<FlaUI.Core.AutomationElements.AutomationElement>();

                    Task<List<FlaUI.Core.AutomationElements.AutomationElement>> searchTask = Task.Run(() =>
                    {
                        var localFound = new List<FlaUI.Core.AutomationElements.AutomationElement>();
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
                    });

                    if (searchTask.Wait(detectionTimeoutMS) && searchTask.Status == TaskStatus.RanToCompletion)
                    {
                        foundList = searchTask.Result ?? new System.Collections.Generic.List<FlaUI.Core.AutomationElements.AutomationElement>();
                    }
                    else
                    {
                        logger?.Warn($"CoreWindow search timed out after {detectionTimeoutMS}ms; skipping this scan to avoid long blocking. (elapsed={(DateTime.UtcNow - searchStart).TotalMilliseconds:0.0}ms)");
                        logger?.Debug($"CoreWindow search timed out after {detectionTimeoutMS}ms and was cancelled for this poll (durationMS={detectionTimeoutMS})");
                        foundList = new List<FlaUI.Core.AutomationElements.AutomationElement>();

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
                        });

                        bool reinitCompleted = reinitTask.Wait(detectionTimeoutMS);
                        reinitSw.Stop();
                        if (reinitCompleted && reinitTask.Result)
                        {
                            logger?.Info($"UIA reinitialized in {reinitSw.ElapsedMilliseconds}ms after search timeout");
                        }
                        else
                        {
                            logger?.Debug($"UIA reinitialization timed out after {detectionTimeoutMS}ms");
                            try { Thread.Sleep(detectionTimeoutMS); } catch { }
                        }
                    }

                    FlaUI.Core.AutomationElements.AutomationElement[] found = foundList.ToArray();
                    // Ensure we have a non-null ConditionFactory for downstream usage to avoid null dereferences
                    var cfSafe = cf ?? new ConditionFactory(new UIA3PropertyLibrary());
                    if (found == null || found.Length == 0)
                    {
                        // No toasts found by CoreWindow-based search; end scan
                        logger?.Info($"No toasts found by CoreWindow-based search; ending search for this scan. (elapsed={(DateTime.UtcNow - searchStart).TotalMilliseconds:0.0}ms)");
                        logger?.Info($"Toast search: end (duration={(DateTime.UtcNow - searchStart).TotalMilliseconds:0.0}ms) found=0");

                        // Immediately remove tracked entries that are no longer present
                        // so any display-timer worker won't send for cleared toasts.
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
                                    catch { }
                                }
                            }
                            logger?.Info("No toasts present: cleaned tracked/groups");
                        }
                        catch (Exception ex)
                        {
                            try { logger?.Error($"Error cleaning tracked on empty scan: {ex.Message}"); } catch { }
                        }

                        try { Thread.Sleep(TimeSpan.FromSeconds(poll)); } catch { }
                        continue;
                    }

                    var searchEnd = DateTime.UtcNow;
                    var searchMS = (searchEnd - searchStart).TotalMilliseconds;
                    logger?.Debug($"Scan found {found.Length} candidates durationMS={searchMS:0.0}");
                    logger?.Info($"Toast search: end (duration={searchMS:0.0}ms) found={found.Length}");

                    // Re-iterate through found for existing processing (we will process again below)
                    var postedHwnds = new HashSet<long>();
                    var actionCenterToggled = false;
                    foreach (var w in found)
                    {
                        string key = MakeKey(w);
                        bool isNewKey = false;
                        lock (stateLock) { isNewKey = !tracked.ContainsKey(key); }
                        if (isNewKey)
                        {
                            int assignedGroup = -1;
                            var now = DateTime.UtcNow;
                            // Determine group id with minimal locking
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
                                    catch { }
                                }
                                if (parts.Count > 0)
                                {
                                    contentSummary = string.Join(" || ", parts);
                                    try
                                    {
                                        var nameLower = SafeGetName(w).ToLowerInvariant();
                                        var filtered = parts.Where(p => !nameLower.Contains((p ?? string.Empty).ToLowerInvariant())).ToList();
                                        if (filtered.Count == 0)
                                        {
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
                            lock (stateLock)
                            {
                                tracked[key] = new TrackedInfo { FirstSeen = now, GroupId = assignedGroup, Method = methodStr, Pid = pidVal2, ShortName = cleanName };
                            }

                            // If no display timer is active, set one now and start a background
                            // worker that will wait until the deadline and then perform the
                            // idle-check -> send-shortcut flow. Do NOT reset if already active.
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
                                        logger?.Info($"Display timer set (deadline={displayDeadline:O}, displayLimitSeconds={minSeconds})");
                                        workerDeadline = displayDeadline.Value;
                                        shouldStartWorker = true;
                                    }
                                }

                                if (shouldStartWorker)
                                {
                                    Task.Run(async () =>
                                    {
                                        try
                                        {
                                            var waitMs = (int)Math.Max(0, (workerDeadline - DateTime.UtcNow).TotalMilliseconds);
                                            if (waitMs > 0) await Task.Delay(waitMs).ConfigureAwait(false);

                                            logger?.Info($"Display timer worker awakened (deadline={workerDeadline:O})");

                                            var monitoringStart = DateTime.UtcNow;

                                            try
                                            {
                                                if (NativeMethods.GetCursorPos(out var ipos))
                                                {
                                                    Program._lastCursorPos = ipos;
                                                    Program._lastMouseTick = (uint)Environment.TickCount;
                                                    if (Program.Logger.IsDebugEnabled) logger?.Debug($"DisplayTimerWorker: Mouse at {ipos.X},{ipos.Y}");
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
                                                        if (transition && (Program.IsKeyboardVirtualKey(vk) || vk == 0x01 || vk == 0x02 || vk == 0x04))
                                                        {
                                                            Program._lastKeyboardTick = (uint)Environment.TickCount;
                                                            if (Program.Logger.IsDebugEnabled) logger?.Debug($"DisplayTimerWorker: Detected vk={vk}");
                                                            break;
                                                        }
                                                    }
                                                    catch { }
                                                }
                                            }
                                            catch { }

                                            try
                                            {
                                                while (true)
                                                {
                                                    try { Thread.Sleep(200); } catch { }

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
                                                    catch { }

                                                    try
                                                    {
                                                        for (int vk = 0x01; vk <= 0xFE; vk++)
                                                        {
                                                            try
                                                            {
                                                                short s = NativeMethods.GetAsyncKeyState(vk);
                                                                bool transition = (s & 0x0001) != 0;
                                                                if (transition && (Program.IsKeyboardVirtualKey(vk) || vk == 0x01 || vk == 0x02 || vk == 0x04))
                                                                {
                                                                    Program._lastKeyboardTick = (uint)Environment.TickCount;
                                                                    if (Program.Logger.IsDebugEnabled) logger?.Debug($"DisplayTimerWorker: Detected keyboard activity during monitoring (vk={vk})");
                                                                    break;
                                                                }
                                                            }
                                                            catch { }
                                                        }
                                                    }
                                                    catch { }

                                                    try
                                                    {
                                                        var monitorElapsedMS = (int)(DateTime.UtcNow - monitoringStart).TotalMilliseconds;
                                                        if (shortcutKeyMaxWaitMS > 0 && monitorElapsedMS >= shortcutKeyMaxWaitMS)
                                                        {
                                                            logger?.Info($"DisplayTimerWorker: monitor timed out after {monitorElapsedMS}ms (max {shortcutKeyMaxWaitMS}ms); considering send");
                                                            bool shouldSendTimeout = false;
                                                            try { lock (stateLock) { shouldSendTimeout = tracked.Count > 0; } } catch { shouldSendTimeout = true; }
                                                            if (shouldSendTimeout)
                                                            {
                                                                if (string.Equals(shortcutKeyMode, "noticecenter", StringComparison.OrdinalIgnoreCase))
                                                                {
                                                                    ToggleShortcutWithDetection('N', IsNotificationCenterOpen, winShortcutKeyIntervalMS);
                                                                    logger?.Info("Notification Center toggled (display-timer: timeout)");
                                                                }
                                                                else
                                                                {
                                                                    ToggleShortcutWithDetection('A', IsActionCenterOpen, winShortcutKeyIntervalMS);
                                                                    logger?.Info("Action Center toggled (display-timer: timeout)");
                                                                }
                                                            }
                                                            else
                                                            {
                                                                logger?.Info("DisplayTimerWorker: tracked empty at timeout; skipping send");
                                                            }

                                                            break;
                                                        }

                                                        uint elapsedSinceLastInput = (uint)(Environment.TickCount - Math.Max(Program._lastKeyboardTick, Program._lastMouseTick));
                                                        if (elapsedSinceLastInput >= (uint)shortcutKeyWaitIdleMS)
                                                        {
                                                            bool shouldSendIdle = false;
                                                            try { lock (stateLock) { shouldSendIdle = tracked.Count > 0; } } catch { shouldSendIdle = true; }
                                                            if (shouldSendIdle)
                                                            {
                                                                if (string.Equals(shortcutKeyMode, "noticecenter", StringComparison.OrdinalIgnoreCase))
                                                                {
                                                                    ToggleShortcutWithDetection('N', IsNotificationCenterOpen, winShortcutKeyIntervalMS);
                                                                    logger?.Info("Notification Center toggled (display-timer)");
                                                                }
                                                                else
                                                                {
                                                                    ToggleShortcutWithDetection('A', IsActionCenterOpen, winShortcutKeyIntervalMS);
                                                                    logger?.Info("Action Center toggled (display-timer)");
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
                                    });
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
                            continue;
                        }

                        int groupId;
                        DateTime groupStart;
                        TrackedInfo stored;
                        lock (stateLock)
                        {
                            groupId = tracked[key].GroupId;
                            groupStart = groups.ContainsKey(groupId) ? groups[groupId] : tracked[key].FirstSeen;
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
                                logger?.Info($"key={key} | Details: {contentEx}");
                            }
                        }
                        catch { }

                        // Disable per-poll immediate close; handled by display timer worker instead
                        if (false && elapsed >= minSeconds)
                        {
                            var closeMsg = $"key={key} Attempting to close group={groupId} (elapsed {elapsed:0.0})";
                            logger?.Info(closeMsg);

                            if (detectOnly)
                            {
                                var skipMsg = $"key={key} Detect-only mode: not closing group={groupId}";
                                logger?.Info(skipMsg);
                                continue;
                            }

                            bool closed = false;
                            if (preserveHistory)
                            {
                                try
                                {
                                    uint lastSystemTick = 0;
                                    try
                                    {
                                        var li2 = new NativeMethods.LASTINPUTINFO();
                                        li2.cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf(typeof(NativeMethods.LASTINPUTINFO));
                                        if (NativeMethods.GetLastInputInfo(ref li2)) lastSystemTick = li2.dwTime;
                                    }
                                    catch { }

                                    uint lastKbMouseTick = Math.Max(Program._lastKeyboardTick, Program._lastMouseTick);

                                    bool treatAsActive = false;
                                    try
                                    {
                                        while (true)
                                        {
                                            uint curLastKbMouseTick = Math.Max(Program._lastKeyboardTick, Program._lastMouseTick);
                                            uint curLastSystemTick = 0;
                                            try
                                            {
                                                var li2 = new NativeMethods.LASTINPUTINFO();
                                                li2.cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf(typeof(NativeMethods.LASTINPUTINFO));
                                                if (NativeMethods.GetLastInputInfo(ref li2)) curLastSystemTick = li2.dwTime;
                                            }
                                            catch { }

                                            try
                                            {
                                                var dbgSys = curLastSystemTick;
                                                logger?.Debug($"key={key} DebugTicks: EnvTick={Environment.TickCount} lastKb={Program._lastKeyboardTick} lastMouse={Program._lastMouseTick} curLastKbMouseTick={curLastKbMouseTick} lastSystemInput={dbgSys}");
                                            }
                                            catch { }

                                            bool isActiveNow = false;
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
                                                isActiveNow = false;
                                            }

                                            if (!isActiveNow)
                                            {
                                                treatAsActive = false;
                                                break;
                                            }

                                            logger?.Debug($"key={key} User active: waiting up to {shortcutKeyWaitIdleMS}ms while polling for keyboard/mouse activity (preserve-history)");
                                            int waited = 0;
                                            int step = Math.Min(200, Math.Max(50, shortcutKeyWaitIdleMS / 10));
                                            bool innerIdleSatisfied = false;
                                            while (waited < shortcutKeyWaitIdleMS)
                                            {
                                                Thread.Sleep(step);
                                                waited += step;

                                                try
                                                {
                                                    if (NativeMethods.GetCursorPos(out var curPos))
                                                    {
                                                        if (curPos.X != Program._lastCursorPos.X || curPos.Y != Program._lastCursorPos.Y)
                                                        {
                                                            Program._lastCursorPos = curPos;
                                                            Program._lastMouseTick = (uint)Environment.TickCount;
                                                            logger?.Debug($"key={key} Detected mouse movement during wait; updating lastMouseTick");
                                                            break;
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
                                                            bool down = (s & 0x8000) != 0;
                                                            if (transition || (down && Program.IsKeyboardVirtualKey(vk)))
                                                            {
                                                                Program._lastKeyboardTick = (uint)Environment.TickCount;
                                                                logger?.Debug($"key={key} Detected keyboard activity (vk={vk}) during wait; updating lastKeyboardTick");
                                                                break;
                                                            }
                                                        }
                                                        catch { }
                                                    }
                                                }
                                                catch { }

                                                try
                                                {
                                                    uint elapsedSinceLastInput = (uint)(Environment.TickCount - Math.Max(Program._lastKeyboardTick, Program._lastMouseTick));
                                                    if (elapsedSinceLastInput >= (uint)shortcutKeyWaitIdleMS)
                                                    {
                                                        innerIdleSatisfied = true;
                                                        break;
                                                    }
                                                }
                                                catch { }
                                            }
                                            if (innerIdleSatisfied) isActiveNow = false;
                                        }
                                    }
                                    catch (ThreadInterruptedException) { }
                                    if (treatAsActive)
                                    {
                                        closed = false;
                                    }
                                    else
                                    {
                                        if (!actionCenterToggled)
                                        {
                                            var present = new List<(string key, string name)>();
                                            foreach (var fe in found)
                                            {
                                                try { var k = MakeKey(fe); var nm = SafeGetName(fe).Replace('\n',' ').Replace('\r',' ').Trim(); present.Add((k, nm)); } catch { }
                                            }
                                            var dedup = present.GroupBy(p => p.key).Select(g => g.First()).ToList();
                                            var summary = string.Join(" | ", dedup.Select(d => $"key={d.key} name=\"{d.name}\""));
                                            logger?.Info($"key={key} Opening Action Center to preserve history for {dedup.Count} toasts: {summary}");

                                            if (string.Equals(shortcutKeyMode, "noticecenter", StringComparison.OrdinalIgnoreCase))
                                            {
                                                ToggleShortcutWithDetection('N', IsNotificationCenterOpen, winShortcutKeyIntervalMS);
                                                logger?.Info($"key={key} Notification Center toggled (preserve-history)");
                                            }
                                            else
                                            {
                                                ToggleShortcutWithDetection('A', IsActionCenterOpen, winShortcutKeyIntervalMS);
                                                logger?.Info($"key={key} Action Center toggled (preserve-history)");
                                            }

                                            lock (stateLock)
                                            {
                                                foreach (var d in dedup)
                                                {
                                                    try
                                                    {
                                                        if (tracked.ContainsKey(d.key))
                                                        {
                                                            tracked.Remove(d.key);
                                                            var cbMsg = $"key={d.key} ClosedBy=PreserveHistory | name=\"{d.name}\"";
                                                            logger?.Info(cbMsg);
                                                        }
                                                    }
                                                    catch { }
                                                }
                                            }
                                            actionCenterToggled = true;
                                            closed = true;
                                        }
                                            else
                                        {
                                            logger?.Info($"key={key} Action Center already toggled this scan; assuming toast moved to history");
                                            lock (stateLock)
                                            {
                                                if (tracked.ContainsKey(key)) tracked.Remove(key);
                                            }
                                            closed = true;
                                        }
                                    }
                                }
                                catch (Exception ex)
                                {
                                    logger?.Error($"key={key} preserve-history failed: {ex.Message}");
                                }
                            }
                            else
                            {
                                string? closedBy = null;
                                try
                                {
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
                                                logger?.Info($"key={key} Attempted WindowPattern.Close");
                                            }
                                            catch (Exception ex)
                                            {
                                                logger?.Debug($"key={key} WindowPattern.Close threw: {ex.Message}");
                                            }
                                        }
                                    }
                                    catch { }

                                    if (!attempted)
                                    {
                                        logger?.Info($"key={key} WindowPattern not supported on element; skipping other fallbacks");
                                        try
                                        {
                                            IntPtr nativeHwnd = IntPtr.Zero;
                                            try { var nv = w.Properties.NativeWindowHandle.ValueOrDefault; if (nv != 0) nativeHwnd = new IntPtr(nv); } catch { }
                                            var className = w.ClassName ?? string.Empty;
                                            var aid = string.Empty;
                                            try { aid = w.Properties.AutomationId.ValueOrDefault ?? string.Empty; } catch { }
                                            var rid = SafeGetRuntimeIdString(w);
                                            var pid = SafeGetProcessId(w);
                                            var rect = w.BoundingRectangle;
                                            logger?.Info($"key={key} Diagnostics: class={className} aid={aid} nativeHandle=0x{nativeHwnd.ToInt64():X} pid={pid} rid={rid} rect={rect.Left}-{rect.Top}-{rect.Right}-{rect.Bottom}");

                                            int textCount = 0;
                                            try { var tnodes = w.FindAllDescendants(cfSafe.ByControlType(ControlType.Text)); textCount = tnodes?.Length ?? 0; } catch { }
                                            logger?.Info($"key={key} Diagnostics: textNodeCount={textCount}");

                                            bool hasCloseBtn = false;
                                            try { var btnCond = cfSafe.ByControlType(ControlType.Button).And(cfSafe.ByName("閉じる").Or(cfSafe.ByName("Close"))); var btn = w.FindFirstDescendant(btnCond); hasCloseBtn = btn != null; } catch { }
                                            logger?.Info($"key={key} Diagnostics: hasCloseButton={hasCloseBtn}");

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
                                                    logger?.Info($"key={key} Diagnostics: hostHwnd=0x{hostHwnd.ToInt64():X} hostClass={hostClass} hostTitle=\"{hostTitle}\"");
                                                }
                                                else
                                                {
                                                    logger?.Info($"key={key} Diagnostics: hostHwnd=0 (none found)");
                                                }
                                            }
                                            catch (Exception ex)
                                            {
                                                logger?.Debug($"key={key} Diagnostics: FindHostWindowHandle error: {ex.Message}");
                                            }
                                        }
                                        catch (Exception ex)
                                        {
                                            logger?.Debug($"key={key} Diagnostics logging failed: {ex.Message}");
                                        }
                                    }
                                }
                                catch (Exception ex)
                                {
                                    logger?.Error($"key={key} Error during WindowPattern attempt: {ex.Message}");
                                }

                                if (closed && !string.IsNullOrEmpty(closedBy))
                                {
                                    var cbMsg = $"key={key} ClosedBy={closedBy}";
                                    logger?.Info(cbMsg);
                                }
                            }

                            if (closed)
                            {
                                lock (stateLock)
                                {
                                    tracked.Remove(key);
                                    if (!tracked.Values.Any(t => t.GroupId == groupId)) { groups.Remove(groupId); }
                                }
                            }
                        }
                    }

                    var presentKeys = new HashSet<string>(found.Select(f => MakeKey(f)));
                    // Immediately remove tracked entries that are no longer present.
                    // Protect with stateLock to avoid races with the display-timer worker.
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

                Thread.Sleep(TimeSpan.FromSeconds(poll));
            }
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
            s = System.Text.RegularExpressions.Regex.Replace(s, "\\s+", " ").Trim();
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
                if (w == null) return Guid.NewGuid().ToString();
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

        static string SafeGetName(FlaUI.Core.AutomationElements.AutomationElement e)
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

        static int SafeGetProcessId(FlaUI.Core.AutomationElements.AutomationElement e)
        {
            if (e == null) return 0;
            try { return (int)(e.Properties.ProcessId.ValueOrDefault); } catch { return 0; }
        }

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

        static IntPtr FindHostWindowHandle(FlaUI.Core.AutomationElements.AutomationElement w)
        {
            try
            {
                var rect = w.BoundingRectangle;
                var cx = (int)((rect.Left + rect.Right) / 2);
                var cy = (int)((rect.Top + rect.Bottom) / 2);
                var hwnd = NativeMethods.WindowFromPoint(new System.Drawing.Point(cx, cy));
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

                NativeMethods.SendInput((uint)inputs.Length, inputs, System.Runtime.InteropServices.Marshal.SizeOf(typeof(NativeMethods.INPUT)));
                try { Program.Logger.Instance?.Info($"Sent Win+{char.ToUpperInvariant(keyChar)} #{i+1}/{sends}"); } catch { }
                Thread.Sleep(waitMS);
            }
        }

        static uint GetIdleMilliseconds()
        {
            var li = new NativeMethods.LASTINPUTINFO();
            li.cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf(typeof(NativeMethods.LASTINPUTINFO));
            if (!NativeMethods.GetLastInputInfo(ref li)) return 0;
            uint tick = (uint)Environment.TickCount;
            if (tick >= li.dwTime) return tick - li.dwTime;
            return (uint)((uint.MaxValue - li.dwTime) + tick);
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
