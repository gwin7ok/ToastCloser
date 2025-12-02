using System;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace ToastCloser
{
    static class TrayBootstrap
    {
        // Hold the process-wide mutex so the OS-level named mutex isn't released.
        private static System.Threading.Mutex? _processMutex = null;
        [STAThread]
        static void Main()
        {
            // Ensure single-instance at process start (applies to the Tray bootstrap)
            try
            {
                bool createdNew = false;
                var mutexName = "Global\\ToastCloser_mutex";
                try { _processMutex = new System.Threading.Mutex(true, mutexName, out createdNew); } catch { }
                if (!createdNew)
                {
                    try { MessageBox.Show("ToastCloser is already running.", "ToastCloser", MessageBoxButtons.OK, MessageBoxIcon.Information); } catch { }
                    return;
                }
            }
            catch { }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            var cfg = Config.Load();

            // Logger initialization was moved to Program.Main for clarity.

            var ctx = new TrayApplicationContext(cfg);

            // Start background scanner. For debugging, support an env var
            // `TOASTCLOSER_INLINE` which causes `Program.Main` to be invoked
            // synchronously (inline) so any exceptions or early returns are
            // visible immediately instead of running in a background Task.
            try
            {
                var inline = false;
                try { inline = !string.IsNullOrEmpty(System.Environment.GetEnvironmentVariable("TOASTCLOSER_INLINE")); } catch { inline = false; }
                if (inline)
                {
                    try
                    {
                        Program.Main(new string[] { "--background-service" });
                    }
                    catch (Exception ex)
                    {
                        try { System.IO.File.WriteAllText(System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"toastcloser_inline_exception_{System.DateTime.Now:yyyyMMddHHmmss}.txt"), ex.ToString()); } catch { }
                    }
                }
                else
                {
                    Task.Run(() =>
                    {
                        try
                        {
                            Program.Main(new string[] { "--background-service" });
                        }
                        catch (Exception ex)
                        {
                            try { System.IO.File.WriteAllText(System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"toastcloser_background_exception_{System.DateTime.Now:yyyyMMddHHmmss}.txt"), ex.ToString()); } catch { }
                        }
                    });
                }
            }
            catch { }

            Application.Run(ctx);
        }
    }
}
