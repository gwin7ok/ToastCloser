using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Reflection;
using System.Linq;
using System.Drawing;
using FlaUI.Core;
using FlaUI.Core.Conditions;
using FlaUI.UIA3;
using FlaUI.Core.AutomationElements;
// UIA via COM will be used (dynamic); do not rely on System.Windows.Automation assembly reference

class Program
{
    private const uint OBJID_WINDOW = 0x00000000;
    private const uint OBJID_CLIENT = 0xFFFFFFFCu;

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(System.Drawing.Point p);

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr hwnd, uint gaFlags);

    private const uint GA_PARENT = 1;

    [DllImport("oleacc.dll")]
    private static extern int AccessibleObjectFromWindow(IntPtr hwnd, uint dwId, ref Guid riid, out IntPtr ppvObject);

    // IID_IAccessible
    private static readonly Guid IID_IAccessible = new Guid("618736E0-3C3D-11CF-810C-00AA00389B71");

    [ComImport, Guid("618736E0-3C3D-11CF-810C-00AA00389B71"), InterfaceType(ComInterfaceType.InterfaceIsIDispatch)]
    private interface IAccessible
    {
        // Only declare the members we need
        [return: MarshalAs(UnmanagedType.BStr)]
        string get_accName(object varChild);
    }

    static void Main(string[] args)
    {
        string targetName = "新しい通知";
        Console.OutputEncoding = Encoding.UTF8;

        Console.WriteLine($"Overall search start: target='{targetName}'");
        // Per user instruction: use only FlaUI(UIA) minimal detection.
        // Run multiple trials and report timing statistics.
        int trials = 10;
        int delayMs = 200;
        if (args != null && args.Length >= 1) int.TryParse(args[0], out trials);
        if (args != null && args.Length >= 2) int.TryParse(args[1], out delayMs);

        Console.WriteLine($"Running FlaUI minimal detection trials: count={trials} delayMs={delayMs}");
        var times = new List<long>();
        int foundCount = 0;

        // Prepare for UIA with the ability to reinitialize on timeout.
        UIA3Automation? automation = null;
        ConditionFactory? cf = null;
        FlaUI.Core.AutomationElements.AutomationElement? desktop = null;

        void EnsureAutomation()
        {
            if (automation == null)
            {
                automation = new UIA3Automation();
                cf = new ConditionFactory(new UIA3PropertyLibrary());
                desktop = automation.GetDesktop();
            }
        }

        // Maximum number of reinitialization attempts after timeout (initial attempt + 1 retry)
        const int maxReinitRetries = 1;

        for (int i = 0; i < trials; i++)
        {
            try
            {
                EnsureAutomation();

                var trialOverallSw = System.Diagnostics.Stopwatch.StartNew();

                // Instrument: condition build
                var condSw = System.Diagnostics.Stopwatch.StartNew();
                var cond = cf!.ByClassName("Windows.UI.Core.CoreWindow").And(cf.ByName(targetName));
                condSw.Stop();

                // Attempt Find with timeout and optional reinit+retry
                FlaUI.Core.AutomationElements.AutomationElement? el = null;
                Exception? findEx = null;
                long findCallMs = 0;
                int attempt = 0;
                bool foundAttempt = false;

                while (attempt <= maxReinitRetries)
                {
                    attempt++;
                    var findSw = System.Diagnostics.Stopwatch.StartNew();
                    try
                    {
                        // Run Find on threadpool and wait with timeout
                        var localDesktop = desktop!; // capture
                        var task = System.Threading.Tasks.Task.Run(() => localDesktop.FindFirstDescendant(cond));
                        bool completed = task.Wait(TimeSpan.FromSeconds(3));
                        findSw.Stop();
                        findCallMs = findSw.ElapsedMilliseconds;
                        if (completed)
                        {
                            try { el = task.Result; } catch (AggregateException ae) { findEx = ae.Flatten().InnerException ?? ae; }
                            foundAttempt = el != null;
                            break; // either found or not found but completed quickly
                        }
                        else
                        {
                            // Timeout: log, dispose and reinit then retry
                            Console.WriteLine($"  Find call timed out after 3000ms. Reinitializing UIA (attempt {attempt}/{maxReinitRetries + 1})");
                            try { automation?.Dispose(); } catch { }
                            automation = null; cf = null; desktop = null;
                            System.Threading.Thread.Sleep(200);
                            EnsureAutomation();
                            // update desktop reference for next attempt
                        }
                    }
                    catch (Exception ex)
                    {
                        findSw.Stop();
                        findCallMs = findSw.ElapsedMilliseconds;
                        findEx = ex;
                        break;
                    }
                }

                long propSwMs = 0;
                long hwndVal = 0;
                if (el != null)
                {
                    var propSw = System.Diagnostics.Stopwatch.StartNew();
                    try { hwndVal = Convert.ToInt64(el.Properties.NativeWindowHandle.ValueOrDefault); } catch { }
                    propSw.Stop();
                    propSwMs = propSw.ElapsedMilliseconds;
                    Console.WriteLine($"FlaUI: Found element Name='{el.Properties.Name.ValueOrDefault}' Class='{el.ClassName}' NativeWindowHandle=0x{hwndVal:X}");
                }

                trialOverallSw.Stop();
                times.Add(trialOverallSw.ElapsedMilliseconds);
                if (el != null) foundCount++;

                Console.WriteLine($"Trial {i+1}/{trials}: found={el != null} overall={trialOverallSw.ElapsedMilliseconds}ms condBuild={condSw.ElapsedMilliseconds}ms findCall={findCallMs}ms propRead={propSwMs}ms");
                if (findEx != null) Console.WriteLine($"  Find exception: {findEx.GetType().Name}: {findEx.Message}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Trial {i+1} exception: {ex.GetType().Name}: {ex.Message}");
                times.Add(-1);
            }
            System.Threading.Thread.Sleep(delayMs);
        }

        // Dispose automation if still alive
        try { automation?.Dispose(); } catch { }

        var validTimes = times.Where(t => t >= 0).ToList();
        if (validTimes.Count > 0)
        {
            long min = validTimes.Min();
            long max = validTimes.Max();
            double avg = validTimes.Average();
            long median = validTimes.OrderBy(t => t).ElementAt(validTimes.Count / 2);
            Console.WriteLine("\n--- Summary ---");
            Console.WriteLine($"Trials: {trials}  FoundCount: {foundCount}");
            Console.WriteLine($"Min: {min} ms  Max: {max} ms  Avg: {avg:0.0} ms  Median: {median} ms");
        }
        else
        {
            Console.WriteLine("No valid timing samples collected.");
        }

        // End: do not run other detection methods.
        return;

        // Fast top-level GetWindowText-based detection (checks only desktop direct children)
        // Per user request, ignore visibility and check all top-level windows.
        var topLevel = FindTopLevelWindowByText(targetName, "Windows.UI.Core.CoreWindow", requireVisible: false);
        Console.WriteLine($"Top-level GetWindowText search: found={topLevel.found} elapsed={topLevel.elapsed} ms");
        if (topLevel.found)
        {
            Console.WriteLine($"  {topLevel.info}");
            // since user requested this method specifically, end here
            return;
        }
        // If not found, poll the top-level windows for up to 30s with 100ms interval
        Console.WriteLine("\nTop-level polling: duration=30s interval=100ms — start");
        var swPoll = System.Diagnostics.Stopwatch.StartNew();
        bool polled = PollTopLevelForTarget(targetName, "Windows.UI.Core.CoreWindow", 30000, 100);
        swPoll.Stop();
        Console.WriteLine($"Top-level polling result: found={polled} totalElapsed={swPoll.ElapsedMilliseconds} ms");
        if (polled) return;

        var methods = new List<(string name, Func<IntPtr, string?> matcher)>
        {
            ("ClassNameExact", hWnd => {
                var sb = new StringBuilder(256);
                GetClassName(hWnd, sb, sb.Capacity);
                var cls = sb.ToString();
                if (string.Equals(cls, "Windows.UI.Core.CoreWindow", StringComparison.OrdinalIgnoreCase))
                {
                    GetWindowThreadProcessId(hWnd, out uint pid);
                    return $"hwnd=0x{hWnd.ToInt64():X} pid={pid} class='{cls}'";
                }
                return null;
            }),
            ("WindowTextExact", hWnd => {
                var sb = new StringBuilder(512);
                GetWindowText(hWnd, sb, sb.Capacity);
                var txt = sb.ToString();
                if (string.Equals(txt, targetName, StringComparison.Ordinal))
                    return $"hwnd=0x{hWnd.ToInt64():X} text='{txt}'";
                return null;
            }),
            ("WindowTextContains", hWnd => {
                var sb = new StringBuilder(512);
                GetWindowText(hWnd, sb, sb.Capacity);
                var txt = sb.ToString();
                if (!string.IsNullOrEmpty(txt) && txt.Contains(targetName, StringComparison.Ordinal))
                    return $"hwnd=0x{hWnd.ToInt64():X} text='{txt}'";
                return null;
            }),
            ("AccName_OBJID_WINDOW", hWnd => GetAccessibleNameMatch(hWnd, OBJID_WINDOW, targetName)),
            ("AccName_OBJID_CLIENT", hWnd => GetAccessibleNameMatch(hWnd, OBJID_CLIENT, targetName)),
            ("ChildWindows_AccName", hWnd => GetChildAccessibleMatch(hWnd, targetName))
        };

        // Extend with additional search strategies (contains, case-insensitive, deep child search, PID-based)
        methods.Add(("ClassNameContains", hWnd => {
            var sb = new StringBuilder(256);
            GetClassName(hWnd, sb, sb.Capacity);
            var cls = sb.ToString();
            if (!string.IsNullOrEmpty(cls) && cls.IndexOf("CoreWindow", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                GetWindowThreadProcessId(hWnd, out uint pid);
                return $"hwnd=0x{hWnd.ToInt64():X} pid={pid} class='{cls}'";
            }
            return null;
        }));

        methods.Add(("WindowTextContains_CaseInsensitive", hWnd => {
            var sb = new StringBuilder(512);
            GetWindowText(hWnd, sb, sb.Capacity);
            var txt = sb.ToString();
            if (!string.IsNullOrEmpty(txt) && txt.IndexOf(targetName, StringComparison.OrdinalIgnoreCase) >= 0)
                return $"hwnd=0x{hWnd.ToInt64():X} text='{txt}'";
            return null;
        }));

        methods.Add(("AccNameContains_OBJID_WINDOW", hWnd => GetAccessibleNameMatchContains(hWnd, OBJID_WINDOW, targetName)));
        methods.Add(("AccNameContains_OBJID_CLIENT", hWnd => GetAccessibleNameMatchContains(hWnd, OBJID_CLIENT, targetName)));
        methods.Add(("ByProcessId_30232", hWnd => {
            GetWindowThreadProcessId(hWnd, out uint pid);
            if (pid == 30232)
            {
                var sb = new StringBuilder(256);
                GetClassName(hWnd, sb, sb.Capacity);
                var cls = sb.ToString();
                var txtSb = new StringBuilder(512);
                GetWindowText(hWnd, txtSb, txtSb.Capacity);
                var txt = txtSb.ToString();
                return $"hwnd=0x{hWnd.ToInt64():X} pid={pid} class='{cls}' text='{txt}'";
            }
            return null;
        }));

        var overallSw = System.Diagnostics.Stopwatch.StartNew();

        foreach (var (name, matcher) in methods)
        {
            Console.WriteLine($"\n--- Method '{name}' start: {DateTime.Now:O}");
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var matches = new List<string>();
            EnumWindows((hWnd, lParam) => {
                // consider top-level windows
                try
                {
                    var info = matcher(hWnd);
                    if (info != null)
                        matches.Add(info);
                }
                catch { }
                return true;
            }, IntPtr.Zero);
            sw.Stop();
            Console.WriteLine($"Method '{name}' end: {DateTime.Now:O} elapsed={sw.ElapsedMilliseconds} ms matches={matches.Count}");
            foreach (var m in matches)
                Console.WriteLine($"  {m}");
        }

        // Run UI Automation based search (COM-based)
        // FindWithUIA_COM searches by Name/ControlType/ClassName and compares NativeWindowHandle
        FindWithUIA_COM(targetName, "Windows.UI.Core.CoreWindow", 0x109A6);

        // Poll for short period to catch transient toast windows
        Console.WriteLine("\nStarting polling search for transient window (10s, 200ms interval)...");
        bool found = PollForTarget(targetName, "Windows.UI.Core.CoreWindow", 0x109A6, 30232, 10000, 200);
        Console.WriteLine($"Polling result: found={found}");

        // FlaUI-based search removed to simplify build in this environment.

        overallSw.Stop();
        Console.WriteLine($"\nOverall search end: {DateTime.Now:O} totalElapsed={overallSw.ElapsedMilliseconds} ms");
        // Also check the specific native handle if it exists in this session
        CheckSpecificNativeHandle(0x109A6, targetName);
    }

    private static void CheckSpecificNativeHandle(int nativeHandle, string targetName)
    {
        Console.WriteLine($"\n--- Specific HWND check: 0x{nativeHandle:X}");
        IntPtr h = new IntPtr(nativeHandle);
        if (!IsWindow(h))
        {
            Console.WriteLine($"HWND 0x{nativeHandle:X} is not a valid window in this session.");
            return;
        }
        var sb = new StringBuilder(256);
        GetClassName(h, sb, sb.Capacity);
        var cls = sb.ToString();
        var txtSb = new StringBuilder(512);
        GetWindowText(h, txtSb, txtSb.Capacity);
        var txt = txtSb.ToString();
        GetWindowThreadProcessId(h, out uint pid);
        Console.WriteLine($"Specific HWND info: hwnd=0x{h.ToInt64():X} pid={pid} class='{cls}' text='{txt}'");
        var accWindow = GetAccessibleNameMatch(h, OBJID_WINDOW, targetName);
        var accClient = GetAccessibleNameMatch(h, OBJID_CLIENT, targetName);
        Console.WriteLine($"Accessible OBJID_WINDOW match: {accWindow ?? "(none)"}");
        Console.WriteLine($"Accessible OBJID_CLIENT match: {accClient ?? "(none)"}");
    }

    private static void FindWithManagedUIA_Reflection(string name, string className, int nativeHandleToConfirm)
    {
        Console.WriteLine($"\n--- Managed UIA (reflection) start: {DateTime.Now:O}");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            // Try to load the UIAutomationClient assembly and use AutomationElement via reflection
            var asmName = "UIAutomationClient";
            var asm = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == asmName);
            if (asm == null)
            {
                try { asm = Assembly.Load(asmName); } catch { asm = null; }
            }
            if (asm == null)
            {
                Console.WriteLine("Managed UIA assembly 'UIAutomationClient' not available in this process.");
                sw.Stop();
                Console.WriteLine($"Managed UIA (reflection) end: elapsed={sw.ElapsedMilliseconds} ms");
                return;
            }

            var aeType = asm.GetType("System.Windows.Automation.AutomationElement");
            var rootProp = aeType.GetProperty("RootElement", BindingFlags.Public | BindingFlags.Static);
            var root = rootProp.GetValue(null);
            var findAllMethod = aeType.GetMethods().FirstOrDefault(m => m.Name == "FindAll" && m.GetParameters().Length == 2);
            // Build a PropertyCondition via types in same assembly
            var conditionsAsm = asm;
            var propConditionType = conditionsAsm.GetType("System.Windows.Automation.PropertyCondition");
            var automationType = conditionsAsm.GetType("System.Windows.Automation.AutomationProperty");
            // Fallback: use NameProperty by reflection value 30005 and ClassNameProperty 30012 and ControlType.Window id 50032
            var conditionFactoryType = conditionsAsm.GetType("System.Windows.Automation.Condition");

            // We'll build simple name condition using System.Windows.Automation.AutomationElement.NameProperty static field
            var namePropField = aeType.GetField("NameProperty", BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy);
            var classPropField = aeType.GetField("ClassNameProperty", BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy);
            var controlTypePropField = aeType.GetField("ControlTypeProperty", BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy);
            if (namePropField == null || classPropField == null || controlTypePropField == null)
            {
                Console.WriteLine("Unable to find AutomationElement property fields via reflection.");
                sw.Stop();
                Console.WriteLine($"Managed UIA (reflection) end: elapsed={sw.ElapsedMilliseconds} ms");
                return;
            }

            var nameProp = namePropField.GetValue(null);
            var classProp = classPropField.GetValue(null);
            var controlTypeProp = controlTypePropField.GetValue(null);

            // Create PropertyCondition objects by invoking constructor
            var propCondCtor = propConditionType?.GetConstructor(new Type[] { automationType, typeof(object) });
            object nameCond = null; object classCond = null; object controlCond = null;
            try { nameCond = propCondCtor.Invoke(new object[] { nameProp, name }); } catch { }
            try { classCond = propCondCtor.Invoke(new object[] { classProp, className }); } catch { }
            try { controlCond = propCondCtor.Invoke(new object[] { controlTypeProp, 50032 }); } catch { }

            // Combine conditions if AndCondition type exists
            var andType = conditionsAsm.GetType("System.Windows.Automation.AndCondition");
            object andCond = null;
            if (andType != null && nameCond != null && classCond != null && controlCond != null)
            {
                var andCtor = andType.GetConstructor(new Type[] { typeof(object[])});
                try { andCond = andCtor.Invoke(new object[] { new object[] { nameCond, classCond, controlCond } }); } catch { andCond = null; }
            }

            object searchCond = andCond ?? (object)nameCond ?? (object)classCond;
            if (searchCond == null)
            {
                Console.WriteLine("Failed to construct search condition for managed UIA.");
                sw.Stop();
                Console.WriteLine($"Managed UIA (reflection) end: elapsed={sw.ElapsedMilliseconds} ms");
                return;
            }

            // TreeScope.Subtree enum value is 4
            var treeScopeType = conditionsAsm.GetType("System.Windows.Automation.TreeScope");
            var subtreeVal = Enum.ToObject(treeScopeType, 4);
            var found = findAllMethod.Invoke(root, new object[] { subtreeVal, searchCond });
            // Try to get Count/Length
            int count = 0;
            try { count = (int)found.GetType().GetProperty("Count").GetValue(found); } catch { try { count = (int)found.GetType().GetProperty("Length").GetValue(found); } catch { } }
            Console.WriteLine($"Managed UIA (reflection) found count={count}");
            for (int i = 0; i < count; i++)
            {
                object item = null;
                try { item = found.GetType().GetMethod("get_Item").Invoke(found, new object[] { i }); } catch { try { item = found.GetType().GetMethod("Get").Invoke(found, new object[] { i }); } catch { } }
                if (item == null) continue;
                // Get Current.Name and Current.ClassName and NativeWindowHandle
                string foundName = ""; string foundClass = ""; int hwnd = 0;
                try { foundName = (string)item.GetType().GetProperty("Current").GetValue(item).GetType().GetProperty("Name").GetValue(item.GetType().GetProperty("Current").GetValue(item))?.ToString() ?? ""; } catch { }
                try { foundClass = (string)item.GetType().GetProperty("Current").GetValue(item).GetType().GetProperty("ClassName").GetValue(item.GetType().GetProperty("Current").GetValue(item))?.ToString() ?? ""; } catch { }
                try { hwnd = (int)item.GetType().GetProperty("Current").GetValue(item).GetType().GetProperty("NativeWindowHandle").GetValue(item.GetType().GetProperty("Current").GetValue(item)); } catch { }
                Console.WriteLine($"  ManagedUIA match idx={i} Name='{foundName}' Class='{foundClass}' NativeWindowHandle=0x{hwnd:X}");
                if (hwnd == nativeHandleToConfirm)
                    Console.WriteLine($"  -> NativeWindowHandle matches expected 0x{nativeHandleToConfirm:X}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine("Managed UIA (reflection) failed: " + ex.Message);
        }
        sw.Stop();
        Console.WriteLine($"Managed UIA (reflection) end: {DateTime.Now:O} elapsed={sw.ElapsedMilliseconds} ms");
    }

    // UIA via COM (dynamic) to avoid referencing UIAutomationClient assembly
    private static void FindWithUIA_COM(string name, string className, int nativeHandleToConfirm)
    {
        Console.WriteLine($"\n--- UIA (COM) Method start: {DateTime.Now:O}");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            // CLSID of CUIAutomation
            var clsid = new Guid("FF48DBA4-60EF-4201-AA87-54103EEF594E");
            Type? uiaType = Type.GetTypeFromProgID("CUIAutomation");
            if (uiaType == null) uiaType = Type.GetTypeFromProgID("UIAutomationClient.CUIAutomation");
            if (uiaType == null) uiaType = Type.GetTypeFromCLSID(clsid);
            dynamic uia = Activator.CreateInstance(uiaType);

            // ensure CreatePropertyCondition is available
            try
            {
                const int UIA_NamePropertyId = 30005;
                const int UIA_ClassNamePropertyId = 30012;
                const int UIA_ControlTypePropertyId = 30003;
                const int UIA_NativeWindowHandlePropertyId = 30020;
                const int UIA_WindowControlTypeId = 50032;
                const int TreeScope_Subtree = 4;

                dynamic nameCond = uia.CreatePropertyCondition(UIA_NamePropertyId, name);
                dynamic classCond = uia.CreatePropertyCondition(UIA_ClassNamePropertyId, className);
                dynamic controlCond = uia.CreatePropertyCondition(UIA_ControlTypePropertyId, UIA_WindowControlTypeId);
                dynamic andCond = uia.CreateAndCondition(new object[] { nameCond, classCond, controlCond });

                dynamic root = uia.GetRootElement();
                dynamic found = root.FindAll(TreeScope_Subtree, andCond);

                int count = 0;
                try { count = (int)found.Length; } catch { try { count = (int)found.GetLength(); } catch { try { count = (int)found.Count; } catch { } } }
                Console.WriteLine($"UIA (COM) found count={count}");
                for (int i = 0; i < count; i++)
                {
                    dynamic el = null;
                    try { el = found.GetElement(i); } catch { try { el = found[i]; } catch { } }
                    if (el == null) continue;
                    object hwndObj = null;
                    try { hwndObj = el.GetCurrentPropertyValue(UIA_NativeWindowHandlePropertyId); } catch { }
                    int hwnd = 0;
                    try { hwnd = Convert.ToInt32(hwndObj); } catch { }
                    object nameObj = null; object classObj = null;
                    try { nameObj = el.GetCurrentPropertyValue(UIA_NamePropertyId); } catch { }
                    try { classObj = el.GetCurrentPropertyValue(UIA_ClassNamePropertyId); } catch { }
                    string foundName = nameObj?.ToString() ?? "";
                    string foundClass = classObj?.ToString() ?? "";
                    Console.WriteLine($"  UIA(COM) match idx={i} Name='{foundName}' Class='{foundClass}' NativeWindowHandle=0x{hwnd:X}");
                    if (hwnd == nativeHandleToConfirm)
                        Console.WriteLine($"  -> NativeWindowHandle matches expected 0x{nativeHandleToConfirm:X}");
                }
            }
            catch (Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
            {
                Console.WriteLine("UIA COM dynamic invocation failed due to binder issues.");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine("UIA (COM) search failed: " + ex.Message);
        }
        sw.Stop();
        Console.WriteLine($"UIA (COM) Method end: {DateTime.Now:O} elapsed={sw.ElapsedMilliseconds} ms");
    }

    // Fast search: enumerate top-level (desktop direct children) windows only and match GetWindowText
    private static (bool found, string info, long elapsed) FindTopLevelWindowByText(string targetName, string className = null, bool requireVisible = true)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        string? foundInfo = null;
        EnumWindows((hWnd, lParam) => {
            try
            {
                if (requireVisible && !IsWindowVisible(hWnd)) return true;
                var clsSb = new StringBuilder(256);
                GetClassName(hWnd, clsSb, clsSb.Capacity);
                var cls = clsSb.ToString();
                if (className != null && !string.Equals(cls, className, StringComparison.OrdinalIgnoreCase))
                    return true;
                var txtSb = new StringBuilder(512);
                GetWindowText(hWnd, txtSb, txtSb.Capacity);
                var txt = txtSb.ToString();
                if (!string.IsNullOrEmpty(txt) && (string.Equals(txt, targetName, StringComparison.Ordinal) || txt.IndexOf(targetName, StringComparison.OrdinalIgnoreCase) >= 0))
                {
                    GetWindowThreadProcessId(hWnd, out uint pid);
                    foundInfo = $"hwnd=0x{hWnd.ToInt64():X} pid={pid} class='{cls}' text='{txt}'";
                    return false; // stop enumeration early
                }
            }
            catch { }
            return true;
        }, IntPtr.Zero);
        sw.Stop();
        return (foundInfo != null, foundInfo ?? string.Empty, sw.ElapsedMilliseconds);
    }

    private static string? GetAccessibleNameMatch(IntPtr hWnd, uint objId, string target)
    {
        try
        {
            Guid iid = IID_IAccessible;
            IntPtr pAcc;
            int hr = AccessibleObjectFromWindow(hWnd, objId, ref iid, out pAcc);
            if (hr == 0 && pAcc != IntPtr.Zero)
            {
                try
                {
                    var acc = (IAccessible)Marshal.GetObjectForIUnknown(pAcc);
                    // Try the object itself first (varChild = 0)
                    try
                    {
                        string? name0 = null;
                        try { name0 = acc.get_accName(0); } catch { }
                        if (!string.IsNullOrEmpty(name0) && string.Equals(name0, target, StringComparison.Ordinal))
                            return $"hwnd=0x{hWnd.ToInt64():X} accName='{name0}' objId=0x{objId:X} varChild=0";
                    }
                    catch { }

                    // Some accessible objects expose children via child IDs. Try a range of child IDs.
                    for (int childId = 1; childId <= 50; childId++)
                    {
                        try
                        {
                            string? childName = null;
                            try { childName = acc.get_accName(childId); } catch { }
                            if (!string.IsNullOrEmpty(childName) && string.Equals(childName, target, StringComparison.Ordinal))
                                return $"hwnd=0x{hWnd.ToInt64():X} accName='{childName}' objId=0x{objId:X} varChild={childId}";
                        }
                        catch { }
                    }
                }
                finally { Marshal.Release(pAcc); }
            }
        }
        catch { }
        return null;
    }

    private static string? GetChildAccessibleMatch(IntPtr hWnd, string target)
    {
        var found = new List<string>();
        try
        {
            // check the window itself first (client)
            var self = GetAccessibleNameMatch(hWnd, OBJID_CLIENT, target);
            if (self != null) return self;

            // enumerate child windows
            EnumChildWindows(hWnd, (child, lparam) => {
                var m = GetAccessibleNameMatch(child, OBJID_CLIENT, target);
                if (m != null) found.Add(m);
                return true;
            }, IntPtr.Zero);

            if (found.Count > 0) return found[0];
        }
        catch { }
        return null;
    }

    private static string? GetAccessibleNameMatchContains(IntPtr hWnd, uint objId, string target)
    {
        try
        {
            Guid iid = IID_IAccessible;
            IntPtr pAcc;
            int hr = AccessibleObjectFromWindow(hWnd, objId, ref iid, out pAcc);
            if (hr == 0 && pAcc != IntPtr.Zero)
            {
                try
                {
                    var acc = (IAccessible)Marshal.GetObjectForIUnknown(pAcc);
                    try
                    {
                        string? name0 = null;
                        try { name0 = acc.get_accName(0); } catch { }
                        if (!string.IsNullOrEmpty(name0) && name0.IndexOf(target, StringComparison.OrdinalIgnoreCase) >= 0)
                            return $"hwnd=0x{hWnd.ToInt64():X} accName='{name0}' objId=0x{objId:X} varChild=0";
                    }
                    catch { }

                    for (int childId = 1; childId <= 50; childId++)
                    {
                        try
                        {
                            string? childName = null;
                            try { childName = acc.get_accName(childId); } catch { }
                            if (!string.IsNullOrEmpty(childName) && childName.IndexOf(target, StringComparison.OrdinalIgnoreCase) >= 0)
                                return $"hwnd=0x{hWnd.ToInt64():X} accName='{childName}' objId=0x{objId:X} varChild={childId}";
                        }
                        catch { }
                    }
                }
                finally { Marshal.Release(pAcc); }
            }
        }
        catch { }
        return null;
    }

    // Deep recursive child search removed due to high cost.

    [DllImport("user32.dll")]
    private static extern bool EnumChildWindows(IntPtr hWndParent, EnumWindowsProc lpEnumFunc, IntPtr lParam);

    private static bool PollForTarget(string targetName, string className, int expectedNativeHandle, uint expectedPid, int durationMs, int intervalMs)
{
    var sw = System.Diagnostics.Stopwatch.StartNew();
    while (sw.ElapsedMilliseconds < durationMs)
    {
        bool matched = false;
        EnumWindows((hWnd, lParam) =>
        {
            var sb = new StringBuilder(256);
            GetClassName(hWnd, sb, sb.Capacity);
            var cls = sb.ToString();
            var txtSb = new StringBuilder(512);
            GetWindowText(hWnd, txtSb, txtSb.Capacity);
            var txt = txtSb.ToString();
            if (string.Equals(cls, className, StringComparison.OrdinalIgnoreCase)
                && (string.Equals(txt, targetName, StringComparison.Ordinal) || !string.IsNullOrEmpty(txt) && txt.Contains(targetName)))
            {
                GetWindowThreadProcessId(hWnd, out uint pid);
                Console.WriteLine($"Polling matched hwnd=0x{hWnd.ToInt64():X} pid={pid} text='{txt}' class='{cls}'");
                if ((int)hWnd.ToInt64() == expectedNativeHandle || pid == expectedPid)
                {
                    Console.WriteLine($"Confirmed match by native handle or pid: hwnd=0x{hWnd.ToInt64():X} pid={pid}");
                    matched = true;
                    return false; // stop enumeration
                }
            }
            return true;
        }, IntPtr.Zero);
        if (matched) return true;
        System.Threading.Thread.Sleep(intervalMs);
    }
    return false;
}
    
    private static bool PollTopLevelForTarget(string targetName, string className, int durationMs, int intervalMs)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < durationMs)
        {
            var res = FindTopLevelWindowByText(targetName, className, requireVisible: false);
            if (res.found)
            {
                Console.WriteLine($"Top-level polling matched: {res.info} elapsedCheck={res.elapsed} ms");
                return true;
            }
            System.Threading.Thread.Sleep(intervalMs);
        }
        return false;
    }

    // FlaUI helper removed to avoid dependency issues in this environment.

    // Try to find the host HWND for a toast by starting from a screen point and climbing ancestors.
    // This mirrors the approach used in ToastCloser.Program.FindHostWindowHandle.
    private static IntPtr FindHostWindowHandleFromPoint(int cx, int cy)
    {
        try
        {
            var hwnd = WindowFromPoint(new System.Drawing.Point(cx, cy));
            if (hwnd == IntPtr.Zero) return IntPtr.Zero;
            var cur = hwnd;
            for (int i = 0; i < 8; i++)
            {
                try
                {
                    var className = new StringBuilder(256);
                    var clen = GetClassName(cur, className, className.Capacity);
                    var cls = clen > 0 ? className.ToString() : string.Empty;
                    var titleSb = new StringBuilder(256);
                    GetWindowText(cur, titleSb, titleSb.Capacity);
                    var title = titleSb.ToString() ?? string.Empty;

                    if (string.Equals(cls, "Windows.UI.Core.CoreWindow", StringComparison.OrdinalIgnoreCase)
                        && title.IndexOf("新しい通知", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        return cur;
                    }
                }
                catch { }

                cur = GetAncestor(cur, GA_PARENT);
                if (cur == IntPtr.Zero) break;
            }
        }
        catch { }
        return IntPtr.Zero;
    }

    // Mirror of ToastCloser.IsCoreNotificationWindowPresentNative: fast native EnumWindows check for CoreWindow title
    private static bool IsCoreNotificationWindowPresentNative()
    {
        bool found = false;
        try
        {
            EnumWindows((h, l) => {
                try
                {
                    if (!IsWindowVisible(h)) return true;
                    var className = new StringBuilder(256);
                    var clen = GetClassName(h, className, className.Capacity);
                    if (clen > 0)
                    {
                        var cls = className.ToString();
                        if (string.Equals(cls, "Windows.UI.Core.CoreWindow", StringComparison.OrdinalIgnoreCase))
                        {
                            var titleSb = new StringBuilder(256);
                            GetWindowText(h, titleSb, titleSb.Capacity);
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
                return true;
            }, IntPtr.Zero);
        }
        catch { }
        return found;
    }

    // Minimal FlaUI-based check: find a top-level CoreWindow with the given name.
    private static bool FindWithFlaUI_Minimal(string targetName, string className)
    {
        try
        {
            using var automation = new UIA3Automation();
            var cf = new ConditionFactory(new UIA3PropertyLibrary());
            var desktop = automation.GetDesktop();
            var cond = cf.ByClassName(className).And(cf.ByName(targetName));
            var el = desktop.FindFirstDescendant(cond);
            if (el != null)
            {
                long hwnd = Convert.ToInt64(el.Properties.NativeWindowHandle.ValueOrDefault);
                var name = el.Properties.Name.ValueOrDefault ?? string.Empty;
                var cls = el.ClassName ?? string.Empty;
                Console.WriteLine($"FlaUI: Found element Name='{name}' Class='{cls}' NativeWindowHandle=0x{hwnd:X}");
                return true;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine("FlaUI minimal check exception: " + ex.Message);
        }
        return false;
    }
}
