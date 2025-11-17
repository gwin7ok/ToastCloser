using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using FlaUI.Core;
using FlaUI.Core.Definitions;
using FlaUI.Core.Conditions;
using FlaUI.Core.AutomationElements;
using System.Text.RegularExpressions;
using FlaUI.UIA3;

namespace ToastCloser
{
    class Program
    {
        static void Main(string[] args)
        {
            double minSeconds = 10.0;
            double maxSeconds = 30.0;
            double poll = 1.0;
            bool detectOnly = false;

            // Parse positional args first (min, max, poll) but also allow a named flag --detect-only or --no-auto-close
            var argList = args?.ToList() ?? new List<string>();
            if (argList.Contains("--detect-only") || argList.Contains("--no-auto-close"))
            {
                detectOnly = true;
                // remove flag so positional parsing below is simpler
                argList = argList.Where(a => a != "--detect-only" && a != "--no-auto-close").ToList();
            }

            if (argList.Count >= 1) double.TryParse(argList[0], out minSeconds);
            if (argList.Count >= 2) double.TryParse(argList[1], out maxSeconds);
            if (argList.Count >= 3) double.TryParse(argList[2], out poll);

            LogConsole($"ToastCloser starting (min={minSeconds} max={maxSeconds} poll={poll} detectOnly={detectOnly})");

            var tracked = new Dictionary<string, TrackedInfo>();
            var groups = new Dictionary<int, DateTime>();
            int nextGroupId = 1;

            // setup log file in same folder as executable
            var exeFolder = AppContext.BaseDirectory;
            var logPath = System.IO.Path.Combine(exeFolder, "auto_closer.log");
            var logger = new SimpleLogger(logPath);

            using var automation = new UIA3Automation();
            var cf = new ConditionFactory(new UIA3PropertyLibrary());

            while (true)
            {
                try
                {
                    var desktop = automation.GetDesktop();

                    // Primary search: class only (AutomationId is not required)
                    var cond = cf.ByClassName("FlexibleToastView");
                    var found = desktop.FindAllDescendants(cond);
                    bool usedFallback = false;

                    // Fallback: strict pattern detection for YouTube toast
                    if (found == null || found.Length == 0)
                    {
                        LogConsole("No toasts found by class; performing fallback strict-scan for YouTube toasts...");
                        // Scan windows and detect elements that satisfy all independent conditions:
                        // AutomationId == "PriorityToastView" AND ClassName == "FlexibleToastView"
                        // AND contains a descendant with ClassName "TextBlock" and Name contains "www.youtube.com"
                        var all = desktop.FindAllDescendants(cf.ByControlType(ControlType.Window));
                        var list = new List<FlaUI.Core.AutomationElements.AutomationElement>();
                        foreach (var w in all)
                        {
                            try
                            {
                                // do not require AutomationId; only require ClassName
                                var cname = w.ClassName ?? string.Empty;
                                if (!string.Equals(cname, "FlexibleToastView", StringComparison.OrdinalIgnoreCase))
                                    continue;

                                // find a TextBlock descendant whose Name contains "www.youtube.com"
                                var textBlockCond = cf.ByClassName("TextBlock").And(cf.ByControlType(ControlType.Text));
                                var tb = w.FindFirstDescendant(textBlockCond);
                                if (tb != null && !string.IsNullOrEmpty(tb.Name) && tb.Name.IndexOf("www.youtube.com", StringComparison.OrdinalIgnoreCase) >= 0)
                                {
                                    list.Add(w);
                                }
                            }
                            catch { }
                        }
                        found = list.ToArray();
                        usedFallback = true;
                        LogConsole($"Fallback (strict) found {found.Length} candidates");
                    }

                    logger.Debug($"Scan found {found.Length} candidates (usedFallback={usedFallback})");
                    for (int _i = 0; _i < found.Length; _i++)
                    {
                        var w = found[_i];
                        try
                        {
                            // compute key early so logs can be prefixed with it
                            string keyCandidate = MakeKey(w);
                            var n = w.Name ?? string.Empty;
                            var cn = w.ClassName ?? string.Empty;
                            var aidx = w.Properties.AutomationId.ValueOrDefault ?? string.Empty;
                            var pid = w.Properties.ProcessId.ValueOrDefault;
                            var rect = w.BoundingRectangle;
                            // attempt to read RuntimeId (may be array)
                            string runtimeIdStr = string.Empty;
                            try
                            {
                                var rid = w.Properties.RuntimeId.ValueOrDefault;
                                if (rid != null)
                                {
                                    if (rid is System.Collections.IEnumerable ie)
                                    {
                                        var parts = new System.Collections.Generic.List<string>();
                                        foreach (var x in ie) parts.Add(x?.ToString());
                                        runtimeIdStr = string.Join("_", parts);
                                    }
                                    else runtimeIdStr = rid.ToString();
                                }
                            }
                            catch { }

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
                                        var tname = tn.Name ?? string.Empty;
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
                                        var nameLower = (w.Name ?? string.Empty).ToLowerInvariant();
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

                            var pidVal2 = w.Properties.ProcessId.ValueOrDefault;
                            var safeName2 = (w.Name ?? string.Empty).Replace('\n', ' ').Replace('\r', ' ').Trim();
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
                                    var tname = tn.Name ?? string.Empty;
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

                            bool closed = TryInvokeCloseButton(w, cf);
                            if (!closed && elapsed >= maxSeconds)
                            {
                                try
                                {
                                    var hwnd = w.Properties.NativeWindowHandle.ValueOrDefault;
                                    if (hwnd != 0)
                                    {
                                        NativeMethods.PostMessage(new IntPtr((long)hwnd), NativeMethods.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
                                        var pm = $"key={key} Posted WM_CLOSE to hwnd 0x{hwnd:X}";
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
                            foreach (var x in ie) parts.Add(x?.ToString());
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

        // Console output helper that prefixes the human-friendly timestamp
        private static void LogConsole(string m)
        {
            Console.WriteLine($"{DateTime.Now:yyyy/MM/dd HH:mm:ss} {m}");
        }

        class TrackedInfo
        {
            public DateTime FirstSeen { get; set; }
            public int GroupId { get; set; }
            public string Method { get; set; }
            public int Pid { get; set; }
            public string ShortName { get; set; }
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
        [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
        public static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);
    }
}
