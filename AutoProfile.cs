using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;

namespace Paddock
{
    /// <summary>One auto-switch rule: when <see cref="Condition"/> holds, the profile named <see cref="Profile"/> should be active.</summary>
    public class AutoProfileRule
    {
        public string Profile = "";
        public string Kind = "";   // app | time | day | monitors
        public string Value = "";

        public bool Matches(DateTime now, string? foregroundApp, int monitorCount)
        {
            var parts = Value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            switch (Kind)
            {
                case "app":
                    return foregroundApp != null && parts.Any(p =>
                        foregroundApp.Equals(p.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? p[..^4] : p, StringComparison.OrdinalIgnoreCase));
                case "day":
                    return parts.Any(p => p.Length >= 3 && now.DayOfWeek.ToString().StartsWith(p, StringComparison.OrdinalIgnoreCase));
                case "monitors":
                    return int.TryParse(Value.Trim(), out var n) && n == monitorCount;
                case "time":
                    // HH:mm-HH:mm; a range that wraps midnight (22:00-06:00) works.
                    var r = Value.Split('-');
                    if (r.Length != 2 || !TimeSpan.TryParse(r[0], out var a) || !TimeSpan.TryParse(r[1], out var b)) return false;
                    var t = now.TimeOfDay;
                    return a <= b ? t >= a && t < b : t >= a || t < b;
            }
            return false;
        }
    }

    /// <summary>
    /// Text syntax, one rule per ';' (first match wins):  Work = app:code,devenv ; Night = time:22:00-06:00 ;
    /// Weekend = day:sat,sun ; Docked = monitors:2
    /// </summary>
    public static class AutoProfileText
    {
        static readonly string[] Kinds = { "app", "time", "day", "monitors" };

        /// <summary>Parses the text; bad entries are reported in <paramref name="errors"/> and skipped.</summary>
        public static List<AutoProfileRule> Parse(string text, out List<string> errors)
        {
            errors = new();
            var list = new List<AutoProfileRule>();
            foreach (var raw in text.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var eq = raw.IndexOf('=');
                var colon = eq < 0 ? -1 : raw.IndexOf(':', eq);
                if (eq <= 0 || colon < 0) { errors.Add($"\"{raw}\": expected  Profile = kind:value"); continue; }
                var rule = new AutoProfileRule
                {
                    Profile = raw[..eq].Trim(),
                    Kind = raw[(eq + 1)..colon].Trim().ToLowerInvariant(),
                    Value = raw[(colon + 1)..].Trim()
                };
                if (!Kinds.Contains(rule.Kind)) { errors.Add($"\"{raw}\": kind must be app, time, day or monitors"); continue; }
                if (rule.Value.Length == 0) { errors.Add($"\"{raw}\": missing a value"); continue; }
                if (rule.Kind == "time" && !IsValidTime(rule.Value))
                { errors.Add($"\"{raw}\": time should look like 09:00-17:00"); continue; }
                list.Add(rule);
            }
            return list;
        }

        static bool IsValidTime(string v)
        {
            var r = v.Split('-');
            return r.Length == 2 && TimeSpan.TryParse(r[0], out _) && TimeSpan.TryParse(r[1], out _);
        }
    }

    /// <summary>The window the user is working in right now, by process name (no ".exe").</summary>
    public static class ForegroundApp
    {
        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);

        public static string? Name()
        {
            try
            {
                var h = GetForegroundWindow();
                if (h == IntPtr.Zero) return null;
                GetWindowThreadProcessId(h, out var pid);
                if (pid == 0 || pid == (uint)Environment.ProcessId) return null; // Paddock's own dialogs don't count
                using var p = Process.GetProcessById((int)pid);
                return p.ProcessName;
            }
            catch { return null; }
        }
    }
}
