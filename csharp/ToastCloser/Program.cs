using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
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
        private static uint _lastKeyboardTick = 0;
        private static uint _lastMouseTick = 0;
        private static System.Drawing.Point _lastCursorPos = new System.Drawing.Point(0,0);

        static void Main(string[] args)
        {
            double minSeconds = 10.0;
            double maxSeconds = 30.0;
            double poll = 1.0;
            bool detectOnly = false;
            bool preserveHistory = false;
            bool wmCloseOnly = false;
            bool skipFallback = false;
            int preserveHistoryIdleMs = 2000; // default: require 2s idle

            // Parse positional args first (min, max, poll) but also allow a named flag --detect-only or --no-auto-close
            var argList = args?.ToList() ?? new List<string>();
            if (argList.Contains("--detect-only") || argList.Contains("--no-auto-close"))
            {
                detectOnly = true;
                // remove flag so positional parsing below is simpler
                argList = argList.Where(a => a != "--detect-only" && a != "--no-auto-close").ToList();
            }
            if (argList.Contains("--preserve-history"))
            {
                preserveHistory = true;
                argList = argList.Where(a => a != "--preserve-history").ToList();
            }
            if (argList.Contains("--wm-close-only"))
            {
                wmCloseOnly = true;
                argList = argList.Where(a => a != "--wm-close-only").ToList();
            }
            if (argList.Contains("--skip-fallback"))
            {
                skipFallback = true;
                argList = argList.Where(a => a != "--skip-fallback").ToList();
            }
            // allow optional idle-ms override: --preserve-history-idle=ms
            var idleArg = argList.FirstOrDefault(a => a.StartsWith("--preserve-history-idle="));
            if (!string.IsNullOrEmpty(idleArg))
            {
                var part = idleArg.Split('=');
                if (part.Length == 2 && int.TryParse(part[1], out var v)) preserveHistoryIdleMs = Math.Max(0, v);
                argList = argList.Where(a => !a.StartsWith("--preserve-history-idle=")).ToList();
            }

            if (argList.Count >= 1) double.TryParse(argList[0], out minSeconds);
            if (argList.Count >= 2) double.TryParse(argList[1], out maxSeconds);
            if (argList.Count >= 3) double.TryParse(argList[2], out poll);

            LogConsole($"ToastCloser starting (min={minSeconds} max={maxSeconds} poll={poll} detectOnly={detectOnly} preserveHistory={preserveHistory} wmCloseOnly={wmCloseOnly} skipFallback={skipFallback})");

            var tracked = new Dictionary<string, TrackedInfo>();
            var groups = new Dictionary<int, DateTime>();
            int nextGroupId = 1;

            // setup log file in same folder as executable
            var exeFolder = AppContext.BaseDirectory;
            var logPath = System.IO.Path.Combine(exeFolder, "auto_closer.log");
            var logger = new SimpleLogger(logPath);

            using var automation = new UIA3Automation();
            var cf = new ConditionFactory(new UIA3PropertyLibrary());

            // initialize cursor position
            try { NativeMethods.GetCursorPos(out _lastCursorPos); } catch { }

            while (true)
            {
                try
                {
                    // Update keyboard/mouse last-activity ticks
                    try
                    {
                        // mouse
                        if (NativeMethods.GetCursorPos(out var curPos))
                        {
                            if (curPos.X != _lastCursorPos.X || curPos.Y != _lastCursorPos.Y)
                            {
                                _lastCursorPos = curPos;
                                _lastMouseTick = (uint)Environment.TickCount;
                                LogConsole($"Mouse moved to {_lastCursorPos.X},{_lastCursorPos.Y}");
                            }
                        }
                    }
                    catch { }

                    try
                    {
                        // keyboard: if any key currently down, update keyboard tick
                        for (int vk = 0x01; vk <= 0xFE; vk++)
                        {
                            try
                            {
                                short s = NativeMethods.GetAsyncKeyState(vk);
                                bool down = (s & 0x8000) != 0;
                                if (down)
                                {
                                    _lastKeyboardTick = (uint)Environment.TickCount;
                                    break;
                                }
                            }
                            catch { }
                        }
                    }
                    catch { }

                    var desktop = automation.GetDesktop();

                    // Log search start time for diagnostics
                    var searchStart = DateTime.UtcNow;
                    LogConsole("Toast search: start");

                    // Primary search: prefer CoreWindow -> ScrollViewer -> FlexibleToastView chain
                    // and only select toasts whose Attribution TextBlock contains 'youtube' (or 'www.youtube.com').
                    var foundList = new List<FlaUI.Core.AutomationElements.AutomationElement>();
                    bool usedFallback = false;

                    try
                    {
                        // try CoreWindow by name '新しい通知' first
                        var coreByNameCond = cf.ByClassName("Windows.UI.Core.CoreWindow").And(cf.ByName("新しい通知"));
                        var coreElement = desktop.FindFirstDescendant(coreByNameCond);
                        if (coreElement == null)
                        {
                            // fallback to any CoreWindow
                            coreElement = desktop.FindFirstDescendant(cf.ByClassName("Windows.UI.Core.CoreWindow"));
                        }

                        if (coreElement != null)
                        {
                            var scroll = coreElement.FindFirstDescendant(cf.ByClassName("ScrollViewer"));
                            if (scroll != null)
                            {
                                var toasts = scroll.FindAllDescendants(cf.ByClassName("FlexibleToastView"));
                                if (toasts != null && toasts.Length > 0)
                                {
                                    foreach (var t in toasts)
                                    {
                                        try
                                        {
                                            var tbAttrCond = cf.ByClassName("TextBlock").And(cf.ByAutomationId("Attribution")).And(cf.ByControlType(ControlType.Text));
                                            var tbAttr = t.FindFirstDescendant(tbAttrCond);
                                            if (tbAttr != null)
                                            {
                                                var attr = SafeGetName(tbAttr);
                                                if (!string.IsNullOrEmpty(attr) && attr.IndexOf("youtube", StringComparison.OrdinalIgnoreCase) >= 0)
                                                {
                                                    foundList.Add(t);
                                                }
                                            }
                                        }
                                        catch { }
                                    }
                                }
                            }
                        }
                    }
                    catch { }

                    // If CoreWindow-based search yielded nothing, fall back to previous class-only search or strict fallback below
                    FlaUI.Core.AutomationElements.AutomationElement[] found = foundList.ToArray();
                    if (found.Length == 0)
                    {
                        // try the previous simple class search first
                        var cond = cf.ByClassName("FlexibleToastView");
                        var byClass = desktop.FindAllDescendants(cond);
                        if (byClass != null && byClass.Length > 0)
                        {
                            // filter by Attribution containing youtube
                            var temp = new List<FlaUI.Core.AutomationElements.AutomationElement>();
                            foreach (var w in byClass)
                            {
                                try
                                {
                                    var tbAttrCond = cf.ByClassName("TextBlock").And(cf.ByAutomationId("Attribution")).And(cf.ByControlType(ControlType.Text));
                                    var tbAttr = w.FindFirstDescendant(tbAttrCond);
                                    if (tbAttr != null)
                                    {
                                        var attr = SafeGetName(tbAttr);
                                        if (!string.IsNullOrEmpty(attr) && attr.IndexOf("youtube", StringComparison.OrdinalIgnoreCase) >= 0)
                                        {
                                            temp.Add(w);
                                        }
                                    }
                                }
                                catch { }
                            }
                            found = temp.ToArray();
                        }
                        else
                        {
                            found = new FlaUI.Core.AutomationElements.AutomationElement[0];
                        }
                    }

                    // Skip heavy fallback full-tree scans; rely only on CoreWindow-based discovery and quick class search above.
                    if (found == null || found.Length == 0)
                    {
                        LogConsole("No toasts found by CoreWindow-based search; skipping heavy fallback to avoid long scans.");
                        found = new FlaUI.Core.AutomationElements.AutomationElement[0];
                        usedFallback = false;
                    }

                    var searchEnd = DateTime.UtcNow;
                    var searchMs = (searchEnd - searchStart).TotalMilliseconds;
                    LogConsole($"Toast search: end (duration={searchMs:0.0}ms) found={found.Length} usedFallback={usedFallback}");
                    logger.Debug($"Scan found {found.Length} candidates (usedFallback={usedFallback}) durationMs={searchMs:0.0}");
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

                            logger.Debug($"key={keyCandidate} Candidate[{_i}]: name={n} class={cn} aid={aidx} pid={pid} rid={runtimeIdStr} rect={rect.Left}-{rect.Top}-{rect.Right}-{rect.Bottom}");
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
                            var methodStr = usedFallback ? "fallback" : "priority";
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
                            // Console: show the detailed line (for debugging)
                            LogConsole(msg);
                            // File: write a concise, user-friendly Japanese message (avoid duplicating content)
                            var infoMsg = $"新しい通知があります。key={key} | Found | group={assignedGroup} | method={methodStr} | pid={pidVal2} | name=\"{cleanName}\"";
                            logger.Info(infoMsg);
                            continue;
                        }

                        var groupId = tracked[key].GroupId;
                        var groupStart = groups.ContainsKey(groupId) ? groups[groupId] : tracked[key].FirstSeen;
                        var elapsed = (DateTime.UtcNow - groupStart).TotalSeconds;
                        var msgElapsed = $"key={key} | group={groupId} | elapsed={elapsed:0.0}s";
                        LogConsole(msgElapsed);
                        logger.Debug(msgElapsed);

                        // File: log a concise message indicating the notification is still present
                        try
                        {
                            var stored = tracked[key];
                            var methodStored = stored.Method ?? (usedFallback ? "fallback" : "priority");
                            var pidStored = stored.Pid;
                            var nameStored = stored.ShortName ?? string.Empty;
                            var stillMsg = $"閉じられていない通知があります　key={key} | Found | group={groupId} | method={methodStored} | pid={pidStored} | name=\"{nameStored}\" (elapsed {elapsed:0.0})";
                            logger.Info(stillMsg);
                            // Also print to console so the user can see detection each scan
                            LogConsole(stillMsg);
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

                                    // If we have a real keyboard/mouse timestamp, measure delta between system LastInput and that timestamp.
                                    bool treatAsActive = false;
                                    if (lastKbMouseTick != 0 && lastSystemTick != 0)
                                    {
                                        uint delta;
                                        if (lastSystemTick >= lastKbMouseTick) delta = lastSystemTick - lastKbMouseTick;
                                        else delta = (uint)((uint.MaxValue - lastKbMouseTick) + lastSystemTick + 1);
                                        if (delta <= (uint)preserveHistoryIdleMs)
                                        {
                                            treatAsActive = true;
                                            LogConsole($"key={key} Skipping preserve-history because recent keyboard/mouse activity detected (delta={delta}ms <= {preserveHistoryIdleMs}ms)");
                                            logger.Debug($"key={key} Skipping preserve-history because recent keyboard/mouse activity detected (delta={delta}ms <= {preserveHistoryIdleMs}ms)");
                                        }
                                    }
                                    else
                                    {
                                        // Fallback: no keyboard/mouse timestamp available — use system idle as before
                                        var sysIdle = GetIdleMilliseconds();
                                        if (sysIdle < (uint)preserveHistoryIdleMs)
                                        {
                                            treatAsActive = true;
                                            LogConsole($"key={key} Skipping preserve-history because user active (system idle={sysIdle}ms < {preserveHistoryIdleMs}ms)");
                                            logger.Debug($"key={key} Skipping preserve-history because user active (system idle={sysIdle}ms < {preserveHistoryIdleMs}ms)");
                                        }
                                    }

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

                                            ToggleActionCenterViaWinA();
                                            LogConsole($"key={key} Action Center toggled (preserve-history)");
                                            logger.Info($"key={key} Action Center toggled (preserve-history)");

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
                                // NEW: Try WM_CLOSE first to see if closing via WM_CLOSE preserves history.
                                string? closedBy = null;
                                try
                                {
                                    // Determine host HWND: prefer element's NativeWindowHandle, else try to find host CoreWindow two levels up
                                    IntPtr hostHwnd = IntPtr.Zero;
                                    var native = w.Properties.NativeWindowHandle.ValueOrDefault;
                                    if (native != 0) hostHwnd = new IntPtr(native);
                                    else hostHwnd = FindHostWindowHandle(w);

                                    if (hostHwnd != IntPtr.Zero)
                                    {
                                        long hval = hostHwnd.ToInt64();
                                        if (!postedHwnds.Contains(hval))
                                        {
                                            NativeMethods.PostMessage(hostHwnd, NativeMethods.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
                                            string hostClass = string.Empty;
                                            try
                                            {
                                                var csb = new System.Text.StringBuilder(256);
                                                var clenHost = NativeMethods.GetClassName(hostHwnd, csb, csb.Capacity);
                                                if (clenHost > 0) hostClass = csb.ToString();
                                            }
                                            catch { }
                                            var pm = $"key={key} Posted WM_CLOSE to hwnd 0x{hval:X} class={hostClass} (attempt before Invoke to prefer history retention)";
                                            LogConsole(pm);
                                            logger.Info(pm);
                                            postedHwnds.Add(hval);
                                            // give the window a short moment to process WM_CLOSE
                                            Thread.Sleep(250);
                                            try
                                            {
                                                var className = new System.Text.StringBuilder(256);
                                                var clen = NativeMethods.GetClassName(hostHwnd, className, className.Capacity);
                                                if (clen == 0)
                                                {
                                                    closed = true; // window gone
                                                    closedBy = "WM_CLOSE";
                                                }
                                            }
                                            catch { }
                                        }
                                        else
                                        {
                                            // already posted WM_CLOSE for this host; assume it will close children
                                            closed = true;
                                            closedBy = "WM_CLOSE(alreadyPosted)";
                                        }
                                    }
                                    else
                                    {
                                        // no host hwnd available; fall back to invoking the close button unless wmCloseOnly is set
                                        if (!wmCloseOnly && TryInvokeCloseButton(w, cf))
                                        {
                                            closed = true;
                                            closedBy = "Invoke";
                                        }
                                    }
                                }
                                catch (Exception ex)
                                {
                                    logger.Error($"key={key} Error during WM_CLOSE-first attempt: {ex.Message}");
                                }

                                if (!closed)
                                {
                                    // If WM_CLOSE did not close it (or couldn't be sent), try the UIA Invoke close button
                                    if (!wmCloseOnly && TryInvokeCloseButton(w, cf))
                                    {
                                        closed = true;
                                        closedBy = "Invoke";
                                    }
                                }

                                if (closed && !string.IsNullOrEmpty(closedBy))
                                {
                                    var cbMsg = $"key={key} ClosedBy={closedBy}";
                                    LogConsole(cbMsg);
                                    logger.Info(cbMsg);
                                }
                            }
                            if (!closed && elapsed >= maxSeconds)
                            {
                                try
                                {
                                    var hwnd = w.Properties.NativeWindowHandle.ValueOrDefault;
                                    if (hwnd != 0)
                                    {
                                        var target = new IntPtr((long)hwnd);
                                        NativeMethods.PostMessage(target, NativeMethods.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
                                        string targetClass = string.Empty;
                                        try
                                        {
                                            var tsb = new System.Text.StringBuilder(256);
                                            var tclen = NativeMethods.GetClassName(target, tsb, tsb.Capacity);
                                            if (tclen > 0) targetClass = tsb.ToString();
                                        }
                                        catch { }
                                        var pm = $"key={key} Posted WM_CLOSE to hwnd 0x{hwnd:X} class={targetClass}";
                                        LogConsole(pm);
                                        logger.Info(pm);
                                        closed = true;
                                    }
                                }
                                catch (Exception ex)
                                {
                                    var em = $"Failed to post WM_CLOSE: {ex.Message}";
                                    LogConsole(em);
                                    logger.Error(em);
                                }
                            }

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
            Console.WriteLine($"{DateTime.Now:yyyy/MM/dd HH:mm:ss} {m}");
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
            var open = false;
            NativeMethods.EnumWindows((h, l) => {
                try
                {
                    if (!NativeMethods.IsWindowVisible(h)) return true;
                    // check class name
                    var className = new System.Text.StringBuilder(256);
                    var clen = NativeMethods.GetClassName(h, className, className.Capacity);
                    if (clen > 0)
                    {
                        var cls = className.ToString();
                        if (string.Equals(cls, "ControlCenterWindow", StringComparison.OrdinalIgnoreCase))
                        {
                            open = true;
                            return false;
                        }
                    }
                    return true;
                }
                catch { return true; }
            }, IntPtr.Zero);
            return open;
        }

        private static void ToggleActionCenterViaWinA(int waitMs = 700)
        {
            // Determine how many toggles to send based on whether Action Center already open
            bool alreadyOpen = IsActionCenterOpen();
            int sends = alreadyOpen ? 3 : 2; // if already open: close->open->close; if closed: open->close
            for (int i = 0; i < sends; i++)
            {
                var inputs = new NativeMethods.INPUT[4];
                // LWIN down
                inputs[0].type = NativeMethods.INPUT_KEYBOARD;
                inputs[0].U.ki.wVk = (ushort)NativeMethods.VK_LWIN;
                // 'A' down
                inputs[1].type = NativeMethods.INPUT_KEYBOARD;
                inputs[1].U.ki.wVk = (ushort)NativeMethods.VK_A;
                // 'A' up
                inputs[2].type = NativeMethods.INPUT_KEYBOARD;
                inputs[2].U.ki.wVk = (ushort)NativeMethods.VK_A;
                inputs[2].U.ki.dwFlags = NativeMethods.KEYEVENTF_KEYUP;
                // LWIN up
                inputs[3].type = NativeMethods.INPUT_KEYBOARD;
                inputs[3].U.ki.wVk = (ushort)NativeMethods.VK_LWIN;
                inputs[3].U.ki.dwFlags = NativeMethods.KEYEVENTF_KEYUP;

                NativeMethods.SendInput((uint)inputs.Length, inputs, System.Runtime.InteropServices.Marshal.SizeOf(typeof(NativeMethods.INPUT)));
                Thread.Sleep(waitMs);
            }
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

        class SimpleLogger : IDisposable
        {
            private readonly object _lock = new object();
            private readonly System.IO.StreamWriter _writer;
            public SimpleLogger(string path)
            {
                _writer = new System.IO.StreamWriter(path, append: true) { AutoFlush = true };
                Info($"===== log start: {DateTime.Now:yyyy/MM/dd HH:mm:ss} =====");
            }
            public void Info(string m) => Write("INFO", m);
            public void Debug(string m) => Write("DEBUG", m);
            public void Error(string m) => Write("ERROR", m);
            private void Write(string level, string m)
            {
                lock (_lock)
                {
                    _writer.WriteLine($"{DateTime.Now:yyyy/MM/dd HH:mm:ss} [{level}] {m}");
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

        public const uint GA_PARENT = 1;
    }

    
}
