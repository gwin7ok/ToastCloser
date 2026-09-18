using System;
using System.Runtime.InteropServices;
using System.Text;

namespace ToastCloser
{
    /// <summary>
    /// 最前面ウィンドウにおいて、主要な日本語 IME (ATOK / Microsoft IME / Google日本語入力) による
    /// 未確定文字列（入力・変換中）が存在するか判定するクラス
    /// </summary>
    public static class ExternalImeDetector
    {
        private const int GCS_COMPSTR = 0x0008;

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();

        [DllImport("user32.dll")]
        private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

        [DllImport("user32.dll")]
        private static extern IntPtr GetFocus();

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

        public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
        [DllImport("user32.dll")]
        private static extern bool EnumThreadWindows(uint dwThreadId, EnumWindowsProc lpfn, IntPtr lParam);
        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("imm32.dll")]
        private static extern IntPtr ImmGetContext(IntPtr hWnd);

        [DllImport("imm32.dll")]
        private static extern bool ImmReleaseContext(IntPtr hWnd, IntPtr hIMC);

        [DllImport("imm32.dll", CharSet = CharSet.Auto)]
        private static extern int ImmGetCompositionString(IntPtr hIMC, int dwIndex, byte[]? lpBuf, int dwBufLen);

        /// <summary>
        /// 現在最前面のウィンドウで IME 未確定文字（入力・変換中）が存在するか判定
        /// </summary>
        public static bool IsForegroundWindowComposing()
        {
            try
            {
                IntPtr fgWnd = GetForegroundWindow();
                if (fgWnd == IntPtr.Zero) return false;

                uint targetThreadId = GetWindowThreadProcessId(fgWnd, out uint targetPid);
                if (targetThreadId == 0) return false;

                // -------------------------------------------------------------
                // 判定1: 各種 IME (ATOK / MS-IME / Google日本語入力) の変換中UIウィンドウ検出
                // -------------------------------------------------------------
                if (IsAnyImeWindowActive(targetThreadId))
                {
                    return true;
                }

                // -------------------------------------------------------------
                // 判定2: 従来の IMM32 API による直接問い合わせ（全IME共通・クラシックWin32対応）
                // -------------------------------------------------------------
                uint currentThreadId = GetCurrentThreadId();
                IntPtr focusWnd = fgWnd;
                bool attached = false;

                if (targetThreadId != currentThreadId)
                {
                    attached = AttachThreadInput(currentThreadId, targetThreadId, true);
                    if (attached)
                    {
                        IntPtr focused = GetFocus();
                        if (focused != IntPtr.Zero) focusWnd = focused;
                    }
                }

                try
                {
                    if (CheckImm32Composition(focusWnd)) return true;
                    if (focusWnd != fgWnd && CheckImm32Composition(fgWnd)) return true;
                }
                finally
                {
                    if (attached)
                    {
                        AttachThreadInput(currentThreadId, targetThreadId, false);
                    }
                }
            }
            catch
            {
                // エラー時は安全側に倒す
            }

            return false;
        }

        /// <summary>
        /// ATOK, Microsoft IME, Google日本語入力などの未確定・候補ウィンドウが可視状態か検査
        /// </summary>
        private static bool IsAnyImeWindowActive(uint targetThreadId)
        {
            bool composingFound = false;

            // 1. 対象入力スレッドが持つウィンドウを列挙
            EnumThreadWindows(targetThreadId, (hWnd, lParam) =>
            {
                if (CheckWindowForIme(hWnd))
                {
                    composingFound = true;
                    return false;
                }
                return true;
            }, IntPtr.Zero);

            if (composingFound) return true;

            // 2. スレッド外に常駐プロセスとして浮いている各 IME 専用UIウィンドウを検査
            EnumWindows((hWnd, lParam) =>
            {
                if (!IsWindowVisible(hWnd)) return true;

                var sb = new StringBuilder(256);
                int len = GetClassName(hWnd, sb, sb.Capacity);
                if (len > 0)
                {
                    string cls = sb.ToString();

                    // ① ATOK
                    if (cls.IndexOf("ATOK", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        if (cls.IndexOf("Comp", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            cls.IndexOf("Cand", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            cls.IndexOf("Guide", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            composingFound = true;
                            return false;
                        }
                    }

                    // ② Google 日本語入力
                    if (cls.IndexOf("GoogleJapaneseInput", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        composingFound = true;
                        return false;
                    }

                    // ③ Microsoft IME (モダン / クラシック)
                    if (cls.IndexOf("MSIME_", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        cls.IndexOf("Microsoft.IME", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        composingFound = true;
                        return false;
                    }

                    // ④ Windows TSF 標準コンポジション枠 (VSCode, Chrome, Edge 等)
                    if (string.Equals(cls, "MSCTFIME Composition", StringComparison.OrdinalIgnoreCase))
                    {
                        composingFound = true;
                        return false;
                    }
                }
                return true;
            }, IntPtr.Zero);

            return composingFound;
        }

        private static bool CheckWindowForIme(IntPtr hWnd)
        {
            if (!IsWindowVisible(hWnd)) return false;

            var sb = new StringBuilder(256);
            int len = GetClassName(hWnd, sb, sb.Capacity);
            if (len <= 0) return false;

            string cls = sb.ToString();

            // ATOK, Google日本語入力, MS-IME, TSF 関連クラス名
            if (cls.IndexOf("ATOK", StringComparison.OrdinalIgnoreCase) >= 0 ||
                cls.IndexOf("GoogleJapaneseInput", StringComparison.OrdinalIgnoreCase) >= 0 ||
                cls.IndexOf("MSIME_", StringComparison.OrdinalIgnoreCase) >= 0 ||
                cls.IndexOf("Microsoft.IME", StringComparison.OrdinalIgnoreCase) >= 0 ||
                cls.IndexOf("MSCTFIME", StringComparison.OrdinalIgnoreCase) >= 0 ||
                cls.IndexOf("CiceroUIWndFrame", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }

            return false;
        }

        private static bool CheckImm32Composition(IntPtr hWnd)
        {
            if (hWnd == IntPtr.Zero) return false;
            IntPtr hIMC = ImmGetContext(hWnd);
            if (hIMC == IntPtr.Zero) return false;

            try
            {
                int len = ImmGetCompositionString(hIMC, GCS_COMPSTR, null, 0);
                return len > 0;
            }
            catch
            {
                return false;
            }
            finally
            {
                ImmReleaseContext(hWnd, hIMC);
            }
        }
    }
}