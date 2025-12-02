using System;
using System.Linq;
using FlaUI.Core;
using FlaUI.Core.Definitions;
using FlaUI.Core.Conditions;
using FlaUI.UIA3;
using System.Text;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace TraverseToastTest
{
    class Program
    {
        static int Main(string[] args)
        {
            Console.WriteLine($"TraverseToastTest (Strategy2-only) starting at {DateTime.Now:yyyy/MM/dd HH:mm:ss}");
            try
            {
                using var automation = new UIA3Automation();
                var cf = new ConditionFactory(new UIA3PropertyLibrary());
                var desktop = automation.GetDesktop();

                Console.WriteLine("Strategy 2: Find CoreWindow with Name == '新しい通知' (fallback to any CoreWindow)");
                // Log search start
                var searchStart = DateTime.Now;
                var sw = System.Diagnostics.Stopwatch.StartNew();
                Console.WriteLine($"検索開始: {searchStart:yyyy/MM/dd HH:mm:ss.fff}");
                var coreByNameCond = cf.ByClassName("Windows.UI.Core.CoreWindow").And(cf.ByName("新しい通知"));
                var coreByName = desktop.FindFirstDescendant(coreByNameCond);
                FlaUI.Core.AutomationElements.AutomationElement coreElement = null;
                if (coreByName != null)
                {
                    Console.WriteLine("  -> Found CoreWindow with Name '新しい通知'");
                    coreElement = coreByName;
                }
                else
                {
                    var coreAny = desktop.FindFirstDescendant(cf.ByClassName("Windows.UI.Core.CoreWindow"));
                    if (coreAny != null)
                    {
                        Console.WriteLine("  -> Found CoreWindow via desktop.FindFirstDescendant (fallback)");
                        coreElement = coreAny;
                    }
                    else
                    {
                        Console.WriteLine("  -> No CoreWindow found via direct descendant search.");
                    }
                }

                if (coreElement != null)
                {
                    // Log the chain step-by-step from this core element
                    try
                    {
                        var coreName = SafeGet(coreElement.Properties.Name.ValueOrDefault, coreElement.Name);
                        Console.WriteLine($"    CoreWindow: Name=\"{coreName}\" Class=\"{coreElement.ClassName ?? string.Empty}\"");

                        var scroll = coreElement.FindFirstDescendant(cf.ByClassName("ScrollViewer"));
                        if (scroll != null)
                        {
                            Console.WriteLine($"      -> Found ScrollViewer Class=\"{scroll.ClassName ?? string.Empty}\"");

                            var toast = scroll.FindFirstDescendant(cf.ByClassName("FlexibleToastView"));
                            if (toast != null)
                            {
                                Console.WriteLine($"        -> Found FlexibleToastView Class=\"{toast.ClassName ?? string.Empty}\"");

                                // First try: find TextBlock with AutomationId == "Attribution"
                                var tbAttrCond = cf.ByClassName("TextBlock").And(cf.ByAutomationId("Attribution")).And(cf.ByControlType(ControlType.Text));
                                var tbAttr = toast.FindFirstDescendant(tbAttrCond);
                                if (tbAttr != null)
                                {
                                    var tbName = SafeGet(tbAttr.Properties.Name.ValueOrDefault, tbAttr.Name);
                                    Console.WriteLine($"          -> Found TextBlock (AutomationId=Attribution): Name=\"{tbName}\"");
                                }
                                else
                                {
                                    // Fallback: use previous logic (prefer 3rd TextBlock if present)
                                    var tbCond = cf.ByClassName("TextBlock").And(cf.ByControlType(ControlType.Text));
                                    var tbs = toast.FindAllDescendants(tbCond);
                                    if (tbs != null && tbs.Length > 0)
                                    {
                                        var pickIndex = tbs.Length >= 3 ? 2 : 0;
                                        var tb = tbs[pickIndex];
                                        var tbName = SafeGet(tb.Properties.Name.ValueOrDefault, tb.Name);
                                        Console.WriteLine($"          -> Found TextBlock (fallback index={pickIndex}, total={tbs.Length}): Name=\"{tbName}\"");
                                    }
                                    else
                                    {
                                        Console.WriteLine("          -> TextBlock not found under this toast");
                                    }
                                }
                            }
                            else
                            {
                                Console.WriteLine("        -> FlexibleToastView not found under ScrollViewer");
                            }
                        }
                        else
                        {
                            Console.WriteLine("      -> ScrollViewer not found under CoreWindow");
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"    Error while traversing from CoreWindow: {ex.Message}");
                    }
                }

                // Log search end and duration
                sw.Stop();
                var searchEnd = DateTime.Now;
                Console.WriteLine($"検索終了: {searchEnd:yyyy/MM/dd HH:mm:ss.fff}");
                Console.WriteLine($"所要時間: {sw.Elapsed.TotalMilliseconds:F1} ms");
                Console.WriteLine("Strategy2-only traversal finished.");
                return 0;
            }
            catch (Exception ex)
            {
                Console.WriteLine("Fatal error: " + ex);
                return 2;
            }
        }

        static string SafeGet(string? v1, string? v2)
        {
            if (!string.IsNullOrEmpty(v1)) return v1!;
            if (!string.IsNullOrEmpty(v2)) return v2!;
            return string.Empty;
        }

        // Helper: attempt to find the chain CoreWindow -> ScrollViewer -> FlexibleToastView -> TextBlock starting at given root
        // Returns tuple indicating whether toast and text were found and the text value when present
        static (bool toastFound, bool textFound, string textValue) FindToastChain(FlaUI.Core.AutomationElements.AutomationElement root, ConditionFactory cf)
        {
            try
            {
                // If root itself is a CoreWindow or ScrollViewer or FlexibleToastView, allow starting there
                var core = root;
                if (!string.Equals(core.ClassName, "Windows.UI.Core.CoreWindow", StringComparison.OrdinalIgnoreCase))
                {
                    var coreCandidate = root.FindFirstDescendant(cf.ByClassName("Windows.UI.Core.CoreWindow"));
                    if (coreCandidate != null) core = coreCandidate;
                }

                // Find ScrollViewer
                var scroll = core.FindFirstDescendant(cf.ByClassName("ScrollViewer"));
                if (scroll == null) return (false, false, string.Empty);

                // Find FlexibleToastView
                var toast = scroll.FindFirstDescendant(cf.ByClassName("FlexibleToastView"));
                if (toast == null) return (false, false, string.Empty);

                // Find TextBlock under toast
                var tbCond = cf.ByClassName("TextBlock").And(cf.ByControlType(ControlType.Text));
                var tb = toast.FindFirstDescendant(tbCond);
                if (tb != null)
                {
                    var tbName = SafeGet(tb.Properties.Name.ValueOrDefault, tb.Name);
                    return (true, true, tbName);
                }
                return (true, false, string.Empty);
            }
            catch (Exception ex) { try { Console.Error.WriteLine("TraverseToastTest: FindToastChain failed: " + ex.ToString()); } catch { } return (false, false, string.Empty); }
        }

        // Strategy 4: Use native EnumWindows to quickly enumerate top-level HWNDs, filter by title/class, then convert to AutomationElement
        static (bool toastFound, bool textFound, string textValue, double durationMs) TryEnumWindowsStrategy(UIA3Automation automation, ConditionFactory cf)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var handles = new List<IntPtr>();
            NativeEnumMethods.EnumWindows((h, l) => { handles.Add(h); return true; }, IntPtr.Zero);
            bool anyFound = false; string foundText = string.Empty; bool textFound = false;
            foreach (var h in handles)
            {
                try
                {
                    // skip invisible windows
                    if (!NativeEnumMethods.IsWindowVisible(h)) continue;
                    var tb = new StringBuilder(512);
                    NativeEnumMethods.GetWindowText(h, tb, tb.Capacity);
                    var title = tb.ToString();
                    var cb = new StringBuilder(256);
                    NativeEnumMethods.GetClassName(h, cb, cb.Capacity);
                    var cls = cb.ToString();

                    // candidate heuristics: class contains CoreWindow OR title contains 'デスクトップ' or '新しい通知'
                    if (cls.IndexOf("CoreWindow", StringComparison.OrdinalIgnoreCase) >= 0 || title.IndexOf("デスクトップ", StringComparison.OrdinalIgnoreCase) >= 0 || title.IndexOf("新しい通知", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        // convert to AutomationElement
                        FlaUI.Core.AutomationElements.AutomationElement elem = null;
                        try
                        {
                            elem = automation.FromHandle(h);
                        }
                        catch (Exception ex) { try { Console.Error.WriteLine("TraverseToastTest: automation.FromHandle failed: " + ex.ToString()); } catch { } }
                        if (elem == null) continue;

                        var res = FindToastChain(elem, cf);
                        if (res.toastFound)
                        {
                            anyFound = true; textFound = res.textFound; foundText = res.textValue; break;
                        }
                    }
                }
                catch (Exception ex) { try { Console.Error.WriteLine("TraverseToastTest: TryEnumWindowsStrategy inner loop failed: " + ex.ToString()); } catch { } }
            }
            sw.Stop();
            return (anyFound, textFound, foundText, sw.Elapsed.TotalMilliseconds);
        }

        static class NativeEnumMethods
        {
            public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
            [DllImport("user32.dll")]
            public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
            [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
            public static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);
            [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
            public static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);
            [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
        }
    }
}
