using System;
using System.Collections.Generic;
using System.IO;
using System.Globalization;

namespace ToastCloser
{
    public class Config
    {
        public double DisplayLimitSeconds { get; set; } = 10.0;
        public double PollIntervalSeconds { get; set; } = 1.0;
        public bool DetectOnly { get; set; } = false;
        public bool PreserveHistory { get; set; } = true;
        public string ShortcutKeyMode { get; set; } = "noticecenter";
        public int ShortcutKeyWaitIdleMS { get; set; } = 2000;
        public int ShortcutKeyMaxWaitSeconds { get; set; } = 15;
        public int DetectionTimeoutMS { get; set; } = 2000;
        public int WinShortcutKeyIntervalMS { get; set; } = 300;
        public bool VerboseLog { get; set; } = false;
        // Maximum number of archived log files to keep. Older files beyond this count are deleted on startup.
        // Set to 0 to disable pruning.
        public int LogArchiveLimit { get; set; } = 100;

        public static string ConfigFileName => Path.Combine(AppContext.BaseDirectory, "ToastCloser.ini");

        public void Save()
        {
            try
            {
                var lines = new List<string>();
                lines.Add("[General]");
                lines.Add($"DisplayLimitSeconds={DisplayLimitSeconds.ToString(CultureInfo.InvariantCulture)}");
                lines.Add($"PollIntervalSeconds={PollIntervalSeconds.ToString(CultureInfo.InvariantCulture)}");
                lines.Add($"DetectOnly={DetectOnly}");
                lines.Add($"PreserveHistory={PreserveHistory}");
                lines.Add($"ShortcutKeyMode={ShortcutKeyMode}");
                lines.Add($"ShortcutKeyWaitIdleMS={ShortcutKeyWaitIdleMS}");
                lines.Add($"ShortcutKeyMaxWaitSeconds={ShortcutKeyMaxWaitSeconds}");
                lines.Add($"DetectionTimeoutMS={DetectionTimeoutMS}");
                lines.Add($"WinShortcutKeyIntervalMS={WinShortcutKeyIntervalMS}");
                lines.Add($"VerboseLog={VerboseLog}");
                lines.Add($"LogArchiveLimit={LogArchiveLimit}");
                File.WriteAllLines(ConfigFileName, lines);
            }
            catch { }
        }

        public static Config Load()
        {
            var cfg = new Config();
            try
            {
                if (!File.Exists(ConfigFileName)) return cfg;
                var lines = File.ReadAllLines(ConfigFileName);
                foreach (var raw in lines)
                {
                    var line = raw.Trim();
                    if (string.IsNullOrEmpty(line) || line.StartsWith("#") || line.StartsWith(";") || line.StartsWith("[")) continue;
                    var idx = line.IndexOf('=');
                    if (idx <= 0) continue;
                    var key = line.Substring(0, idx).Trim();
                    var val = line.Substring(idx + 1).Trim();
                    switch (key)
                    {
                        case "DisplayLimitSeconds": double.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out var v1); cfg.DisplayLimitSeconds = v1; break;
                        case "PollIntervalSeconds": double.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out var v2); cfg.PollIntervalSeconds = v2; break;
                        case "DetectOnly": bool.TryParse(val, out var b1); cfg.DetectOnly = b1; break;
                        case "PreserveHistory": bool.TryParse(val, out var b2); cfg.PreserveHistory = b2; break;
                        case "ShortcutKeyMode": cfg.ShortcutKeyMode = val; break;
                        case "ShortcutKeyWaitIdleMS": int.TryParse(val, out var i1); cfg.ShortcutKeyWaitIdleMS = i1; break;
                        case "ShortcutKeyMaxWaitSeconds": int.TryParse(val, out var i2); cfg.ShortcutKeyMaxWaitSeconds = i2; break;
                        case "DetectionTimeoutMS": int.TryParse(val, out var i3); cfg.DetectionTimeoutMS = i3; break;
                        case "WinShortcutKeyIntervalMS": int.TryParse(val, out var i4); cfg.WinShortcutKeyIntervalMS = i4; break;
                        case "VerboseLog": bool.TryParse(val, out var b3); cfg.VerboseLog = b3; break;
                        case "LogArchiveLimit": int.TryParse(val, out var la); cfg.LogArchiveLimit = la; break;
                    }
                }
            }
            catch { }
            return cfg;
        }
    }
}
