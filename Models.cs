using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Paddock
{
    public enum RuleKind { Extension, NameContains, Regex, ModifiedWithinDays, Expression }

    public class RuleDef
    {
        public RuleKind Kind { get; set; }
        public string Value { get; set; } = "";

        public bool Matches(FileSystemInfo f)
        {
            switch (Kind)
            {
                case RuleKind.Extension:
                    foreach (var e in Value.Split(',', ';', ' '))
                        if (e.Length > 0 && f.Extension.Equals(e.StartsWith('.') ? e : "." + e, StringComparison.OrdinalIgnoreCase))
                            return true;
                    return false;
                case RuleKind.NameContains:
                    return f.Name.Contains(Value, StringComparison.OrdinalIgnoreCase);
                case RuleKind.Regex:
                    try { return Regex.IsMatch(f.Name, Value, RegexOptions.IgnoreCase); } catch { return false; }
                case RuleKind.ModifiedWithinDays:
                    return int.TryParse(Value, out var d) && (DateTime.Now - f.LastWriteTime).TotalDays <= d;
                case RuleKind.Expression:
                    try { return ExprRule.Compile(Value)(f); } catch { return false; }
            }
            return false;
        }
    }

    public class FenceConfig
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string Title { get; set; } = "Pad";
        public double X { get; set; } = 100;
        public double Y { get; set; } = 100;
        public double Width { get; set; } = 280;
        public double Height { get; set; } = 220;
        public bool RolledUp { get; set; }
        public bool ExcludeFromQuickHide { get; set; }

        /// <summary>Default pad: also shows desktop files that no other pad's rules claim. At most one per profile.</summary>
        public bool IsDefault { get; set; }

        /// <summary>Pads sharing a TabGroup are tabs of one window (they share its position and size). Null = a normal pad.</summary>
        public string? TabGroup { get; set; }

        /// <summary>Which tab of its group is showing.</summary>
        public bool TabActive { get; set; }

        /// <summary>Desktop pads only: files dragged here by hand. A pinned file shows in this pad and no other pad's rules claim it.</summary>
        public List<string> Pinned { get; set; } = new();
        public string Color { get; set; } = "#202030";
        public double Opacity { get; set; } = 0.7;
        public int IconSize { get; set; } = 32;

        // ---- Style (edited in the "Style..." dialog; all optional so older layouts load unchanged) ----
        /// <summary>Second background colour (hex); null = solid <see cref="Color"/>.</summary>
        public string? Color2 { get; set; }
        /// <summary>Gradient direction in degrees: 0 = left to right, 90 = top to bottom.</summary>
        public double GradientAngle { get; set; } = 45;
        /// <summary>Border colour (hex); null = the default faint white.</summary>
        public string? BorderColor { get; set; }
        public double BorderWidth { get; set; } = 1;
        public double CornerRadius { get; set; } = 8;
        /// <summary>Milliseconds for colour fades and pad move/dock animations; 0 = instant.</summary>
        public int TransitionMs { get; set; } = 150;
        /// <summary>Ambient background animation: None, Breathe (slow fade) or Drift (rotating gradient).</summary>
        public string Anim { get; set; } = "None";
        /// <summary>Seconds per cycle of the ambient animation.</summary>
        public double AnimSeconds { get; set; } = 6;

        /// <summary>Folder path for a portal pad; null for a desktop-rules pad.</summary>
        public string? PortalPath { get; set; }

        /// <summary>Notes folder (an Obsidian vault) for a vault pad: its .md files, filtered by <see cref="Rules"/>.</summary>
        public string? VaultPath { get; set; }

        /// <summary>Text file (usually .md) a note pad edits in place; null for other pad kinds.</summary>
        public string? NotePath { get; set; }

        /// <summary>Move files untouched for this many days to the archive folder; 0 = off.</summary>
        public int ArchiveAfterDays { get; set; }

        /// <summary>Rule action for a desktop pad: "Off", "Move" or "Copy" files its rules match into <see cref="ActionTarget"/>.</summary>
        public string ActionMode { get; set; } = "Off";
        /// <summary>Destination folder; may use {yyyy} {MM} {ext} {pad}.</summary>
        public string? ActionTarget { get; set; }
        /// <summary>Also run on its own every 5 minutes (files untouched for at least a minute). Off by default.</summary>
        public bool ActionAuto { get; set; }

        /// <summary>Screen edge this pad auto-hides against (X/Y/Width/Height then describe its expanded size).</summary>
        public DockEdge Dock { get; set; } = DockEdge.None;

        /// <summary>Rules for a desktop pad (any match wins). Ignored for portals.</summary>
        public List<RuleDef> Rules { get; set; } = new();

        /// <summary>Manual icon order: paths in the order the user dragged them into. Unlisted items follow by name. Empty = automatic.</summary>
        public List<string> ItemOrder { get; set; } = new();

        /// <summary>Global hotkey text such as "Ctrl+Alt+F1" that raises this pad for a few seconds; null = none.</summary>
        public string? Hotkey { get; set; }

        /// <summary>Icons fade out until the mouse is over the pad.</summary>
        public bool Chameleon { get; set; }

        /// <summary>Pad background follows the colour of the wallpaper behind it.</summary>
        public bool WallpaperColor { get; set; }

        /// <summary>Live pad kind: "downloads", "clipboard", "todo", "links", "project", "search"; null = an ordinary pad.</summary>
        public string? PadType { get; set; }

        /// <summary>Folder for project/search pads, note file for todo pads.</summary>
        public string? PadSpec { get; set; }

        /// <summary>Links for links/project pads, as "Title|url; Title|url".</summary>
        public string Links { get; set; } = "";
    }

    public class LayoutFile
    {
        /// <summary>Pads of the active profile (key name kept as "Fences" so existing layout files still load).</summary>
        public List<FenceConfig> Fences { get; set; } = new();
        public bool HideDesktopIcons { get; set; }

        /// <summary>Snap-on: pull pads onto each other's edges and the screen edges (and optionally a grid) while moving/resizing.</summary>
        public bool GridSnap { get; set; }
        public bool SnapToFences { get; set; } = true;
        public double GridSize { get; set; } = 20;

        /// <summary>Milliseconds the mouse must stay off an expanded docked pad before it collapses.</summary>
        public double CollapseDelayMs { get; set; } = 450;

        /// <summary>Double-click empty desktop to quick-hide / show the pads.</summary>
        public bool DoubleClickHide { get; set; } = true;

        /// <summary>Name of the profile whose pads are in <see cref="Fences"/>.</summary>
        public string ActiveProfile { get; set; } = "Default";

        /// <summary>Auto-switch rules as text (see <see cref="AutoProfileText"/>); empty = never switch by itself.</summary>
        public string AutoProfiles { get; set; } = "";

        /// <summary>The other (inactive) profiles, by name. Older layout files simply have none.</summary>
        public Dictionary<string, List<FenceConfig>> Profiles { get; set; } = new();

        public List<string> ProfileNames() =>
            Profiles.Keys.Append(ActiveProfile).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>One-line rule syntax: ext:pdf,docx; name:invoice; regex:^IMG; days:3</summary>
    public static class RuleText
    {
        static readonly Dictionary<string, RuleKind> Keys = new(StringComparer.OrdinalIgnoreCase)
        {
            ["ext"] = RuleKind.Extension, ["name"] = RuleKind.NameContains,
            ["regex"] = RuleKind.Regex, ["days"] = RuleKind.ModifiedWithinDays,
            ["expr"] = RuleKind.Expression,
        };

        public static string Format(IEnumerable<RuleDef> rules) =>
            string.Join("; ", rules.Select(r => $"{Keys.First(k => k.Value == r.Kind).Key}:{r.Value}"));

        public static List<RuleDef> Parse(string text)
        {
            var list = new List<RuleDef>();
            foreach (var part in text.Split(';'))
            {
                var i = part.IndexOf(':');
                if (i < 0) continue;
                var key = part[..i].Trim();
                var val = part[(i + 1)..].Trim();
                if (val.Length > 0 && Keys.TryGetValue(key, out var kind))
                    list.Add(new RuleDef { Kind = kind, Value = val });
            }
            return list;
        }
    }

    public static class ConfigStore
    {
        public static readonly string Dir =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Paddock");
        static readonly string FilePath = Path.Combine(Dir, "layout.json");
        static readonly JsonSerializerOptions Opts = new() { WriteIndented = true };

        public static LayoutFile Load()
        {
            try
            {
                if (File.Exists(FilePath))
                    return JsonSerializer.Deserialize<LayoutFile>(File.ReadAllText(FilePath), Opts) ?? Seed();
            }
            catch { /* corrupt file: fall through to a fresh layout */ }
            return Seed();
        }

        public static void Save(LayoutFile layout)
        {
            Directory.CreateDirectory(Dir);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(layout, Opts));
            File.Move(tmp, FilePath, overwrite: true);
        }

        // ---- Snapshots: full copies of the layout (all profiles) that can be restored later ----
        public static readonly string SnapDir = Path.Combine(Dir, "snapshots");
        const int KeepAuto = 30;

        /// <summary>Write a snapshot. Auto snapshots (label starts with "auto-") are pruned to the newest 30; named ones are kept.</summary>
        public static string Snapshot(LayoutFile layout, string label)
        {
            Directory.CreateDirectory(SnapDir);
            var safe = new string(label.Select(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' ? ch : '_').ToArray());
            var path = Path.Combine(SnapDir, $"{DateTime.Now:yyyyMMdd-HHmmss-fff}__{safe}.json");
            File.WriteAllText(path, JsonSerializer.Serialize(layout, Opts));
            foreach (var old in Directory.GetFiles(SnapDir, "*__auto-*.json").OrderByDescending(p => p).Skip(KeepAuto))
                try { File.Delete(old); } catch { }
            return path;
        }

        /// <summary>Newest first: (path, display label).</summary>
        public static List<(string Path, string Label)> Snapshots()
        {
            if (!Directory.Exists(SnapDir)) return new();
            return Directory.GetFiles(SnapDir, "*.json").OrderByDescending(p => p).Select(p =>
            {
                var name = System.IO.Path.GetFileNameWithoutExtension(p);
                var parts = name.Split("__", 2);
                var stamp = DateTime.TryParseExact(parts[0], "yyyyMMdd-HHmmss-fff", null, System.Globalization.DateTimeStyles.None, out var t)
                    ? t.ToString("MMM d, HH:mm:ss") : parts[0];
                return (p, parts.Length > 1 ? $"{stamp}  {parts[1].Replace('_', ' ')}" : stamp);
            }).ToList();
        }

        public static LayoutFile? LoadSnapshot(string path)
        {
            try { return JsonSerializer.Deserialize<LayoutFile>(File.ReadAllText(path), Opts); } catch { return null; }
        }

        static LayoutFile Seed() => new()
        {
            Fences =
            {
                new FenceConfig
                {
                    Title = "Documents", X = 60, Y = 60,
                    Rules = { new RuleDef { Kind = RuleKind.Extension, Value = "pdf,docx,xlsx,pptx,txt,md" } }
                },
                new FenceConfig
                {
                    Title = "Images", X = 380, Y = 60,
                    Rules = { new RuleDef { Kind = RuleKind.Extension, Value = "png,jpg,jpeg,gif,webp" } }
                },
                new FenceConfig
                {
                    Title = "Recent", X = 700, Y = 60,
                    Rules = { new RuleDef { Kind = RuleKind.ModifiedWithinDays, Value = "3" } }
                },
            }
        };
    }
}
