using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using FlaUI.Core;
using FlaUI.Core.Definitions;
using FlaUI.Core.Conditions;
using FlaUI.Core.AutomationElements;
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

            if (args.Length >= 1) double.TryParse(args[0], out minSeconds);
            if (args.Length >= 2) double.TryParse(args[1], out maxSeconds);
            if (args.Length >= 3) double.TryParse(args[2], out poll);

            Console.WriteLine($"ToastCloser starting (min={minSeconds} max={maxSeconds} poll={poll})");

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

                    // Primary search: class + automation id
                    var cond = cf.ByClassName("FlexibleToastView").And(cf.ByAutomationId("PriorityToastView"));
                    var found = desktop.FindAllDescendants(cond);

                    // Fallback: broad scan by name/class/automation id heuristics
                    if (found == null || found.Length == 0)
                    {
                        Console.WriteLine("No toasts found by class; performing fallback scan...");
                        // Broad scan: consider top-level/window elements to reduce noise
                        var all = desktop.FindAllDescendants(cf.ByControlType(ControlType.Window));
                        var list = new List<FlaUI.Core.AutomationElements.AutomationElement>();
                        foreach (var w in all)
                        {
                            try
                            {
                                var name = w.Name ?? string.Empty;
                                var cname = w.ClassName ?? string.Empty;
                                var aid = w.Properties.AutomationId.ValueOrDefault ?? string.Empty;
                                var text = (name + " " + cname + " " + aid).ToLowerInvariant();
                                if (text.Contains("google chrome") || text.Contains("youtube") || text.Contains("通知") || text.Contains("toast") || text.Contains("flexibletoast") || text.Contains("prioritytoast"))
                                {
                                    list.Add(w);
                                }
                            }
                            catch { }
                        }
                        found = list.ToArray();
                        Console.WriteLine($"Fallback found {found.Length} candidates");
                    }

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
                            tracked[key] = new TrackedInfo { FirstSeen = now, GroupId = assignedGroup };
                            var msg = $"Found new toast: key={key} group={assignedGroup} name={w.Name} pid={w.Properties.ProcessId.ValueOrDefault}";
                            Console.WriteLine(msg);
                            logger.Info(msg);
                            continue;
                        }

                        var groupId = tracked[key].GroupId;
                        var groupStart = groups.ContainsKey(groupId) ? groups[groupId] : tracked[key].FirstSeen;
                        var elapsed = (DateTime.UtcNow - groupStart).TotalSeconds;
                        var msgElapsed = $"Toast {key} group={groupId} elapsed={elapsed:0.0}s (since group start)";
                        Console.WriteLine(msgElapsed);
                        logger.Debug(msgElapsed);

                        if (elapsed >= minSeconds)
                        {
                            var closeMsg = $"Attempting to close toast {key} group={groupId} (elapsed {elapsed:0.0})";
                            Console.WriteLine(closeMsg);
                            logger.Info(closeMsg);
                            bool closed = TryInvokeCloseButton(w, cf);
                            if (!closed && elapsed >= maxSeconds)
                            {
                                try
                                {
                                        var hwnd = w.Properties.NativeWindowHandle.ValueOrDefault;
                                    if (hwnd != 0)
                                    {
                                        NativeMethods.PostMessage(new IntPtr((long)hwnd), NativeMethods.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
                                        var pm = $"Posted WM_CLOSE to hwnd 0x{hwnd:X} for key={key}";
                                        Console.WriteLine(pm);
                                        logger.Info(pm);
                                        closed = true;
                                    }
                                }
                                catch (Exception ex)
                                {
                                    var em = $"Failed to post WM_CLOSE: {ex.Message}";
                                    Console.WriteLine(em);
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
                    Console.WriteLine("Exception during scan: " + ex);
                }

                Thread.Sleep(TimeSpan.FromSeconds(poll));
            }
        }

        static string MakeKey(FlaUI.Core.AutomationElements.AutomationElement w)
        {
            try
            {
                var rect = w.BoundingRectangle;
                var pid = w.Properties.ProcessId.ValueOrDefault;
                return $"{pid}:{rect.Left}-{rect.Top}-{rect.Right}-{rect.Bottom}";
            }
            catch { return Guid.NewGuid().ToString(); }
        }

        class TrackedInfo
        {
            public DateTime FirstSeen { get; set; }
            public int GroupId { get; set; }
        }

        class SimpleLogger : IDisposable
        {
            private readonly object _lock = new object();
            private readonly System.IO.StreamWriter _writer;
            public SimpleLogger(string path)
            {
                _writer = new System.IO.StreamWriter(path, append: true) { AutoFlush = true };
                Info($"===== log start: {DateTime.Now:O} =====");
            }
            public void Info(string m) => Write("INFO", m);
            public void Debug(string m) => Write("DEBUG", m);
            public void Error(string m) => Write("ERROR", m);
            private void Write(string level, string m)
            {
                lock (_lock)
                {
                    _writer.WriteLine($"{DateTime.Now:O} [{level}] {m}");
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
                        Console.WriteLine("Invoked close button via FlaUI");
                        return true;
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("Error in TryInvokeCloseButton: " + ex.Message);
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
