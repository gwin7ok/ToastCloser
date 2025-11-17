using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Collections.Generic;
using SharpDX.DirectInput;

class Program
{
    [StructLayout(LayoutKind.Sequential)]
    struct LASTINPUTINFO {
        public uint cbSize;
        public uint dwTime;
    }

    [DllImport("user32.dll")]
    static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);

    [DllImport("user32.dll")]
    static extern short GetAsyncKeyState(int vKey);

    [DllImport("user32.dll")]
    static extern bool GetCursorPos(out System.Drawing.Point pt);

    [DllImport("xinput1_4.dll", EntryPoint = "XInputGetState")]
    private static extern uint XInputGetState(uint dwUserIndex, out XINPUT_STATE pState);

    [StructLayout(LayoutKind.Sequential)]
    private struct XINPUT_STATE
    {
        public uint dwPacketNumber;
        public XINPUT_GAMEPAD Gamepad;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct XINPUT_GAMEPAD
    {
        public ushort wButtons;
        public byte bLeftTrigger;
        public byte bRightTrigger;
        public short sThumbLX;
        public short sThumbLY;
        public short sThumbRX;
        public short sThumbRY;
    }

    static void Main(string[] args)
    {
        Console.WriteLine("CheckIdle extended — prints GetLastInputInfo plus per-source activity. Ctrl+C to stop.");

        uint lastLi = 0;
        var lastXInputTimes = new DateTime[4];
        var lastXInputTicks = new uint[4];
        var lastXInputStates = new XINPUT_STATE[4];
        var lastDirectTimes = new Dictionary<Guid, DateTime>();
        var lastDirectTicks = new Dictionary<Guid, uint>();
        var lastDirectStates = new Dictionary<Guid, DirectState>();
        var lastCursor = new System.Drawing.Point(0,0);
        var lastMouseTime = DateTime.MinValue;
        var lastMouseTick = 0u;
        var lastKeyboardTime = DateTime.MinValue;
        var lastKeyboardTick = 0u;
        var directInitialized = new HashSet<Guid>();
        // track previous keyboard state per VK to detect edge (down) events only
        var prevKeyDown = new bool[256];

        // initialize cursor
        GetCursorPos(out lastCursor);

        // Setup DirectInput and enumerate attached game controllers
        DirectInput di = null;
        List<DeviceInstance> diDevices = new List<DeviceInstance>();
        try
        {
            di = new DirectInput();
            var devices = di.GetDevices(DeviceClass.GameControl, DeviceEnumerationFlags.AttachedOnly);
            foreach (var dev in devices) diDevices.Add(dev);
        }
        catch { di = null; }

        // prime XInput states
        for (uint i = 0; i < 4; i++)
        {
            try { XInputGetState(i, out lastXInputStates[i]); } catch { }
        }

        int counter = 0;
        while (true)
        {
            try
            {
                // poll XInput (fast)
                for (uint i = 0; i < 4; i++)
                {
                    try
                    {
                        var res = XInputGetState(i, out var st);
                        if (res == 0)
                        {
                            bool changed = st.dwPacketNumber != lastXInputStates[i].dwPacketNumber || st.Gamepad.wButtons != lastXInputStates[i].Gamepad.wButtons || st.Gamepad.bLeftTrigger != lastXInputStates[i].Gamepad.bLeftTrigger || st.Gamepad.bRightTrigger != lastXInputStates[i].Gamepad.bRightTrigger || st.Gamepad.sThumbLX != lastXInputStates[i].Gamepad.sThumbLX || st.Gamepad.sThumbLY != lastXInputStates[i].Gamepad.sThumbLY || st.Gamepad.sThumbRX != lastXInputStates[i].Gamepad.sThumbRX || st.Gamepad.sThumbRY != lastXInputStates[i].Gamepad.sThumbRY;
                            if (changed)
                            {
                                lastXInputStates[i] = st;
                                lastXInputTimes[i] = DateTime.UtcNow;
                                lastXInputTicks[i] = (uint)Environment.TickCount;
                                Console.WriteLine($"{DateTime.Now:HH:mm:ss} XInput[{i}] activity detected");
                            }
                        }
                    }
                    catch { }
                }

                // poll DirectInput devices (infrequent)
                if (di != null && diDevices.Count > 0 && (counter % 5) == 0)
                {
                    foreach (var dev in diDevices)
                    {
                        try
                        {
                            using (var joystick = new Joystick(di, dev.InstanceGuid))
                            {
                                joystick.Properties.BufferSize = 0;
                                joystick.Acquire();
                                var state = joystick.GetCurrentState();
                                if (state != null)
                                {
                                    // Build current snapshot
                                    var ds = new DirectState
                                    {
                                        X = state.X,
                                        Y = state.Y,
                                        RotationX = state.RotationX,
                                        RotationY = state.RotationY,
                                        Buttons = state.Buttons == null ? Array.Empty<bool>() : state.Buttons
                                    };

                                            // If we've never initialized this device's baseline, store it but don't log (suppress first-seen)
                                            if (!directInitialized.Contains(dev.InstanceGuid))
                                            {
                                                lastDirectStates[dev.InstanceGuid] = ds;
                                                directInitialized.Add(dev.InstanceGuid);
                                            }
                                            else
                                            {
                                                bool changed = true;
                                                if (lastDirectStates.TryGetValue(dev.InstanceGuid, out var prev))
                                                {
                                                    changed = !AreDirectStatesEqual(prev, ds, 1000);
                                                }

                                                if (changed)
                                                {
                                                    lastDirectStates[dev.InstanceGuid] = ds;
                                                    lastDirectTimes[dev.InstanceGuid] = DateTime.UtcNow;
                                                    lastDirectTicks[dev.InstanceGuid] = (uint)Environment.TickCount;
                                                    Console.WriteLine($"{DateTime.Now:HH:mm:ss} DirectInput:{dev.InstanceName} activity detected ({dev.InstanceGuid})");
                                                }
                                            }
                                }
                            }
                        }
                        catch { }
                    }
                }

                // poll mouse movement
                if (GetCursorPos(out var cur))
                {
                    if (cur.X != lastCursor.X || cur.Y != lastCursor.Y)
                    {
                        lastCursor = cur;
                        lastMouseTime = DateTime.UtcNow; // treat as user input
                        lastMouseTick = (uint)Environment.TickCount;
                        Console.WriteLine($"{DateTime.Now:HH:mm:ss} Mouse moved to {cur.X},{cur.Y}");
                    }
                }

                // poll keyboard: detect key-down EDGE events to avoid continuous reporting
                bool anyKeyDownEdge = false;
                for (int vk = 0x01; vk <= 0xFE; vk++)
                {
                    try
                    {
                        short s = GetAsyncKeyState(vk);
                        bool down = (s & 0x8000) != 0;
                        if (down && !prevKeyDown[vk])
                        {
                            anyKeyDownEdge = true;
                        }
                        prevKeyDown[vk] = down;
                    }
                    catch { }
                }
                if (anyKeyDownEdge)
                {
                    lastKeyboardTime = DateTime.UtcNow;
                    lastKeyboardTick = (uint)Environment.TickCount;
                    Console.WriteLine($"{DateTime.Now:HH:mm:ss} Keyboard activity detected (edge)");
                }

                // check GetLastInputInfo every loop but print summary every ~1s (counter)
                var li = new LASTINPUTINFO();
                li.cbSize = (uint)Marshal.SizeOf(typeof(LASTINPUTINFO));
                bool ok = GetLastInputInfo(ref li);
                uint tick = (uint)Environment.TickCount;
                uint idle = 0;
                if (ok)
                {
                    if (tick >= li.dwTime) idle = tick - li.dwTime;
                    else idle = (uint)((uint.MaxValue - li.dwTime) + tick);
                }

                if ((counter % 5) == 0)
                {
                    Console.WriteLine($"{DateTime.Now:HH:mm:ss} [Summary] Tick={tick} LastInput.dwTime={li.dwTime} idle={idle}ms");

                    // If GetLastInputInfo changed since last loop, print likely source
                    if (li.dwTime != lastLi)
                    {
                        // Build candidate ticks
                        var candidates = new List<(string name, uint tick)>();
                        if (lastKeyboardTick != 0) candidates.Add(("Keyboard", lastKeyboardTick));
                        if (lastMouseTick != 0) candidates.Add(("Mouse", lastMouseTick));
                        for (int i = 0; i < 4; i++) if (lastXInputTicks[i] != 0) candidates.Add(($"XInput[{i}]", lastXInputTicks[i]));
                        foreach (var kv in lastDirectTicks) candidates.Add(($"Direct:{kv.Key}", kv.Value));

                        if (candidates.Count > 0)
                        {
                            (string name, uint tick) best = ("(unknown)", 0);
                            uint bestDelta = uint.MaxValue;
                            foreach (var c in candidates)
                            {
                                uint delta;
                                if (li.dwTime >= c.tick) delta = li.dwTime - c.tick;
                                else delta = (uint.MaxValue - c.tick) + li.dwTime + 1;
                                if (delta < bestDelta)
                                {
                                    bestDelta = delta;
                                    best = c;
                                }
                            }
                            Console.WriteLine($"  LastInput changed -> likely source: {best.name} (delta={bestDelta}ms)");
                        }
                        else
                        {
                            Console.WriteLine("  LastInput changed -> no per-source timestamps available to correlate");
                        }
                    }

                    // print nearest source times (human friendly)
                    var recentSources = new List<string>();
                    if (lastKeyboardTime != DateTime.MinValue) recentSources.Add($"Keyboard={lastKeyboardTime:HH:mm:ss.fff}");
                    if (lastMouseTime != DateTime.MinValue) recentSources.Add($"MousePos={lastCursor.X},{lastCursor.Y}");
                    for (int i = 0; i < 4; i++) if (lastXInputTimes[i] != DateTime.MinValue) recentSources.Add($"XInput[{i}]={lastXInputTimes[i]:HH:mm:ss.fff}");
                    foreach (var kv in lastDirectTimes) recentSources.Add($"Direct:{kv.Key}={kv.Value:HH:mm:ss.fff}");
                    Console.WriteLine("  RecentSources: " + string.Join(" | ", recentSources));
                }

                // remember last GetLastInputInfo time
                lastLi = li.dwTime;

                counter++;
            }
            catch (Exception ex)
            {
                Console.WriteLine("Exception: " + ex.Message);
            }
            Thread.Sleep(200);
        }
    }

    // Snapshot for DirectInput device state
    struct DirectState
    {
        public int X;
        public int Y;
        public int RotationX;
        public int RotationY;
        public bool[] Buttons;
    }

    static bool AreDirectStatesEqual(DirectState a, DirectState b, int axisTolerance)
    {
        if (Math.Abs(a.X - b.X) > axisTolerance) return false;
        if (Math.Abs(a.Y - b.Y) > axisTolerance) return false;
        if (Math.Abs(a.RotationX - b.RotationX) > axisTolerance) return false;
        if (Math.Abs(a.RotationY - b.RotationY) > axisTolerance) return false;
        if (a.Buttons.Length != b.Buttons.Length) return false;
        for (int i = 0; i < a.Buttons.Length; i++) if (a.Buttons[i] != b.Buttons[i]) return false;
        return true;
    }
}
