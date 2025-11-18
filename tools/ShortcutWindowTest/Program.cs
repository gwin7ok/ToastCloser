using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Conditions;
using FlaUI.UIA3;

class Program
{
    // Note: window detection uses UIA (FlaUI) via desktop.FindFirstChild(cond)

    // SendInput for key simulation
    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, [In] INPUT[] pInputs, int cbSize);

    private const ushort VK_LWIN = 0x5B;
    private const uint KEYEVENTF_KEYUP = 0x0002;

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public InputUnion U;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

        static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        Console.WriteLine("ShortcutWindowTest start");

        if (args.Length == 0)
        {
            Console.WriteLine("Usage: ShortcutWindowTest --mode=1|2 [--visible-only]");
            return 1;
        }

        int mode = 1;
        bool visibleOnly = false;
            int intervalSeconds = 2;
        foreach (var a in args)
        {
            if (a.StartsWith("--mode="))
            {
                var v = a.Substring("--mode=".Length);
                if (!int.TryParse(v, out mode)) mode = 1;
            }
            if (a == "--visible-only") visibleOnly = true;
                if (a.StartsWith("--interval="))
                {
                    var v = a.Substring("--interval=".Length);
                    if (!int.TryParse(v, out intervalSeconds)) intervalSeconds = 2;
                    if (intervalSeconds <= 0) intervalSeconds = 2;
                }
        }

        // Define combos
        // combo1: Win+A => opens ControlCenterWindow, Name="クイック設定"
        // combo2: Win+N => opens Windows.UI.Core.CoreWindow, Name="通知センター"

        if (mode == 1)
        {
            Console.WriteLine("Mode 1: shortcut=Win+A  target Class=ControlCenterWindow Name=クイック設定");
                RunModeLoop("ControlCenterWindow", "クイック設定", 'A', visibleOnly, intervalSeconds);
        }
        else
        {
            Console.WriteLine("Mode 2: shortcut=Win+N  target Class=Windows.UI.Core.CoreWindow Name=通知センター");
                RunModeLoop("Windows.UI.Core.CoreWindow", "通知センター", 'N', visibleOnly, intervalSeconds);
        }

        return 0;
    }

    private static void RunModeLoop(string className, string name, char keyChar, bool visibleOnly, int intervalSeconds)
    {
        Console.WriteLine($"Starting loop: check every {intervalSeconds} seconds. Press Ctrl+C to stop.");
        while (true)
        {
            Console.WriteLine($"[{DateTime.Now:yyyy/MM/dd HH:mm:ss}] Checking existence: class='{className}' name='{name}' visibleOnly={visibleOnly}");
            // Use UIA FindFirstChild to detect the desktop direct child matching class+name
            var res = FindTopLevelWindowByClassAndName_UIA(className, name, visibleOnly);
            var exists = res.found;
            var info = res.info;
            Console.WriteLine($"[{DateTime.Now:yyyy/MM/dd HH:mm:ss}] Found={exists} info='{info}'");
            Thread.Sleep(intervalSeconds * 1000);
        }
    }

    // Use FlaUI UIA3 to find a desktop direct child matching class and name
    private static (bool found, string info) FindTopLevelWindowByClassAndName_UIA(string className, string targetName, bool requireVisible)
    {
        try
        {
            using var automation = new UIA3Automation();
            var cf = new ConditionFactory(new UIA3PropertyLibrary());
            var desktop = automation.GetDesktop();
            var cond = cf.ByClassName(className).And(cf.ByName(targetName));
            var el = desktop.FindFirstChild(cond);
            if (el != null)
            {
                long hwnd = 0;
                try { hwnd = Convert.ToInt64(el.Properties.NativeWindowHandle.ValueOrDefault); } catch { }
                var nm = string.Empty; try { nm = el.Properties.Name.ValueOrDefault ?? string.Empty; } catch { }
                var cls = string.Empty; try { cls = el.ClassName ?? string.Empty; } catch { }
                var info = $"hwnd=0x{hwnd:X} class='{cls}' name='{nm}'";
                return (true, info);
            }
            return (false, string.Empty);
        }
        catch (Exception ex)
        {
            return (false, "UIA error: " + ex.Message);
        }
    }

    // native top-level enumeration removed; detection uses UIA only

    private static void SendWinPlusChar(char c, int times, int delayMs)
    {
        ushort vk = (ushort)char.ToUpperInvariant(c);
        for (int i = 0; i < times; i++)
        {
            // Press LWIN down
            var inputs = new INPUT[4];
            inputs[0].type = 1; // INPUT_KEYBOARD
            inputs[0].U.ki.wVk = VK_LWIN;
            inputs[0].U.ki.dwFlags = 0;

            // key down
            inputs[1].type = 1;
            inputs[1].U.ki.wVk = vk;
            inputs[1].U.ki.dwFlags = 0;

            // key up
            inputs[2].type = 1;
            inputs[2].U.ki.wVk = vk;
            inputs[2].U.ki.dwFlags = KEYEVENTF_KEYUP;

            // LWIN up
            inputs[3].type = 1;
            inputs[3].U.ki.wVk = VK_LWIN;
            inputs[3].U.ki.dwFlags = KEYEVENTF_KEYUP;

            var sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf(typeof(INPUT)));
            if (sent != inputs.Length)
            {
                Console.WriteLine($"SendInput sent {sent} of {inputs.Length} (GetLastError: {Marshal.GetLastWin32Error()})");
            }
            System.Threading.Thread.Sleep(delayMs);
        }
    }
}
