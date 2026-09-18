using System;
using System.Runtime.InteropServices;
using System.Text;

namespace ToastCloser
{
    /// <summary>
    /// 最前面ウィンドウにおいて、ATOK / TSF / IMM32 による
    /// IME未確定文字列（入力・変換中）が存在するか判定するクラス
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
                // 判定1: ATOK / TSF の変換中・候補ウィンドウの検出（最も確実）
                // -------------------------------------------------------------
                if (IsAtokOrTsfWindowActive(targetThreadId, targetPid))
                {
                    return true;
                }

                // -------------------------------------------------------------
                // 判定2: 従来の IMM32 API による直接問い合わせ（Win32クラシックアプリ向け）
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
                // エラー時は安全側に倒して握りつぶす
            }

            return false;
        }

        /// <summary>
        /// ATOK または TSF の未確定文字列表示・候補選択用ウィンドウが可視化されているか検査
        /// </summary>
        private static bool IsAtokOrTsfWindowActive(uint targetThreadId, uint targetPid)
        {
            bool composingFound = false;

            // 1. まず対象入力スレッドが持つウィンドウを列挙
            EnumThreadWindows(targetThreadId, (hWnd, lParam) =>
            {
                if (CheckWindowForIme(hWnd))
                {
                    composingFound = true;
                    return false; // 列挙中止
                }
                return true;
            }, IntPtr.Zero);

            if (composingFound) return true;

            // 2. スレッド外に常駐プロセスとして浮いている ATOK 専用ウィンドウ（Comp/Candidate等）を検査
            EnumWindows((hWnd, lParam) =>
            {
                if (!IsWindowVisible(hWnd)) return true;

                var sb = new StringBuilder(256);
                int len = GetClassName(hWnd, sb, sb.Capacity);
                if (len > 0)
                {
                    string cls = sb.ToString();

                    // ATOKの変換・候補・入力中ウィンドウ
                    // 例: ATOK34CompWnd, ATOK...Cand, ATOK... など
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

                    // Windows TSF 標準のコンポジションウィンドウ (Chromium, VSCode 等)
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

            // ATOK 関連クラス
            if (cls.IndexOf("ATOK", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }

            // TSF / Modern IME 関連クラス
            if (cls.IndexOf("MSCTFIME", StringComparison.OrdinalIgnoreCase) >= 0 ||
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