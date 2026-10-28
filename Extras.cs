using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using WF = System.Windows.Forms;

namespace Paddock
{
    /// <summary>"Ctrl+Alt+F1" text to the modifier/virtual-key pair RegisterHotKey wants.</summary>
    public static class HotkeyText
    {
        public static bool Parse(string? text, out uint mods, out uint vk)
        {
            mods = 0; vk = 0;
            if (string.IsNullOrWhiteSpace(text)) return false;
            foreach (var raw in text.Split('+'))
            {
                var p = raw.Trim();
                switch (p.ToLowerInvariant())
                {
                    case "ctrl": case "control": mods |= 0x2; continue;
                    case "alt": mods |= 0x1; continue;
                    case "shift": mods |= 0x4; continue;
                    case "win": mods |= 0x8; continue;
                    case "space": vk = 0x20; continue;
                }
                if (p.Length == 1 && char.IsAsciiLetterOrDigit(p[0])) vk = char.ToUpperInvariant(p[0]);
                else if (p.Length is 2 or 3 && (p[0] == 'F' || p[0] == 'f') && int.TryParse(p[1..], out var n) && n is >= 1 and <= 24) vk = (uint)(0x70 + n - 1);
                else return false;
            }
            return vk != 0 && (mods & 0xF) != 0;
        }
    }

    /// <summary>Text copied while Paddock runs, newest first. Memory only: never written to disk.</summary>
    public static class ClipboardHistory
    {
        public static readonly List<string> Items = new();
        public static event Action? Changed;
        static DispatcherTimer? _timer;
        static string? _last;

        public static void Start()
        {
            if (_timer != null) return;
            _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(900) };
            _timer.Tick += (_, _) => Poll();
            _timer.Start();
        }

        /// <summary>Set by Paddock itself when it copies an entry back, so that copy doesn't reorder the list.</summary>
        public static void Ignore(string text) => _last = text;

        static void Poll()
        {
            try
            {
                if (!Clipboard.ContainsText()) return;
                var t = Clipboard.GetText();
                if (string.IsNullOrWhiteSpace(t) || t == _last) return;
                _last = t;
                if (t.Length > 2000) t = t[..2000];
                Items.RemoveAll(x => x == t);
                Items.Insert(0, t);
                if (Items.Count > 25) Items.RemoveRange(25, Items.Count - 25);
                Changed?.Invoke();
            }
            catch { /* clipboard is locked by another app right now: try next tick */ }
        }
    }

    /// <summary>Simple font-glyph icons for items that aren't files (links, todos, clipboard entries).</summary>
    public static class Glyphs
    {
        static readonly Dictionary<string, BitmapSource> Cache = new();

        public static BitmapSource Make(string glyph, Color color)
        {
            var key = glyph + color;
            if (Cache.TryGetValue(key, out var hit)) return hit;
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen())
            {
                var ft = new FormattedText(glyph, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                    new Typeface("Segoe MDL2 Assets"), 44, new SolidColorBrush(color), 1.0);
                dc.DrawText(ft, new Point((64 - ft.Width) / 2, (64 - ft.Height) / 2));
            }
            var rtb = new RenderTargetBitmap(64, 64, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(dv);
            rtb.Freeze();
            return Cache[key] = rtb;
        }
    }

    /// <summary>Parsers and scanners behind the live pad types.</summary>
    public static class LiveItems
    {
        static readonly Regex TodoRx = new(@"^(\s*[-*+]\s*\[)([ xX])(\]\s*)(.*)$", RegexOptions.Compiled);

        public record TodoLine(int Index, bool Done, string Text);

        public static List<TodoLine> ParseTodos(string text)
        {
            var list = new List<TodoLine>();
            var lines = text.Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                var m = TodoRx.Match(lines[i].TrimEnd('\r'));
                if (m.Success) list.Add(new TodoLine(i, m.Groups[2].Value != " ", m.Groups[4].Value.Trim()));
            }
            return list;
        }

        /// <summary>Flip the checkbox on one line of the note in place; every other byte of the file stays as it was.</summary>
        public static bool ToggleTodo(string path, int lineIndex)
        {
            try
            {
                var lines = File.ReadAllText(path).Split('\n');
                if (lineIndex < 0 || lineIndex >= lines.Length) return false;
                var m = TodoRx.Match(lines[lineIndex].TrimEnd('\r'));
                if (!m.Success) return false;
                var mark = m.Groups[2].Value == " " ? "x" : " ";
                lines[lineIndex] = m.Groups[1].Value + mark + lines[lineIndex][(m.Groups[1].Length + 1)..];
                File.WriteAllText(path, string.Join("\n", lines));
                return true;
            }
            catch { return false; }
        }

        public static List<(string Title, string Url)> ParseLinks(string text)
        {
            var list = new List<(string, string)>();
            foreach (var raw in (text ?? "").Split(';', '\n'))
            {
                var part = raw.Trim();
                if (part.Length == 0) continue;
                string title = "", url = part;
                int bar = part.IndexOf('|');
                if (bar >= 0) { title = part[..bar].Trim(); url = part[(bar + 1)..].Trim(); }
                if (url.Length == 0) continue;
                if (!url.Contains("://")) url = "https://" + url;
                if (!Uri.TryCreate(url, UriKind.Absolute, out var u)) continue;
                list.Add((title.Length > 0 ? title : u.Host, url));
            }
            return list;
        }

        static readonly HashSet<string> SkipDirs = new(StringComparer.OrdinalIgnoreCase)
            { "node_modules", "$RECYCLE.BIN", "System Volume Information", "AppData", "Windows", "Program Files", "Program Files (x86)", "ProgramData" };

        /// <summary>
        /// Walk <paramref name="root"/> (up to 6 levels, 60,000 entries) and return up to 100 entries any rule matches,
        /// newest first. Dot-folders, hidden entries and system folders are skipped. Meant for a background thread.
        /// </summary>
        public static List<FileSystemInfo> Search(string root, IList<RuleDef> rules, CancellationToken ct)
        {
            var preds = new List<Func<FileSystemInfo, bool>>();
            foreach (var r in rules)
            {
                if (r.Kind == RuleKind.Expression) { try { preds.Add(ExprRule.Compile(r.Value)); } catch { } }
                else { var rr = r; preds.Add(f => rr.Matches(f)); }
            }
            var hits = new List<FileSystemInfo>();
            if (preds.Count == 0 || !Directory.Exists(root)) return hits;
            var stack = new Stack<(DirectoryInfo Dir, int Depth)>();
            stack.Push((new DirectoryInfo(root), 0));
            int visited = 0;
            while (stack.Count > 0 && visited < 60000 && !ct.IsCancellationRequested)
            {
                var (d, depth) = stack.Pop();
                IEnumerable<FileSystemInfo> entries;
                try { entries = d.EnumerateFileSystemInfos().ToList(); } catch { continue; }
                foreach (var e in entries)
                {
                    visited++;
                    if (e.Name.StartsWith('.') || (e.Attributes & FileAttributes.Hidden) != 0 || e.Name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)) continue;
                    if (e is DirectoryInfo sub)
                    {
                        if ((e.Attributes & FileAttributes.ReparsePoint) == 0 && depth < 6 && !SkipDirs.Contains(e.Name)) stack.Push((sub, depth + 1));
                    }
                    bool match = false;
                    foreach (var p in preds) { try { if (p(e)) { match = true; break; } } catch { } }
                    if (match) hits.Add(e);
                }
            }
            return hits.OrderByDescending(f => f.LastWriteTimeUtc).Take(100).ToList();
        }
    }

    /// <summary>Average colour of the wallpaper behind a pad, to tint the pad to match.</summary>
    public static class Wallpaper
    {
        static string? FilePath()
        {
            try
            {
                using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Control Panel\Desktop");
                var p = k?.GetValue("WallPaper") as string;
                if (!string.IsNullOrEmpty(p) && File.Exists(p)) return p;
            }
            catch { }
            var t = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @"Microsoft\Windows\Themes\TranscodedWallpaper");
            return File.Exists(t) ? t : null;
        }

        static Color? SolidBackground()
        {
            try
            {
                using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Control Panel\Colors");
                var parts = (k?.GetValue("Background") as string)?.Split(' ');
                if (parts is { Length: 3 }) return Color.FromRgb(byte.Parse(parts[0]), byte.Parse(parts[1]), byte.Parse(parts[2]));
            }
            catch { }
            return null;
        }

        /// <summary>
        /// Average colour under <paramref name="padPx"/> (device pixels, desktop coordinates) on <paramref name="scr"/>.
        /// Assumes the wallpaper is set to "Fill" (Windows' default); with Fit/Stretch/Tile the sample is approximate.
        /// </summary>
        public static Color? Behind(WF.Screen scr, System.Drawing.Rectangle padPx)
        {
            var path = FilePath();
            if (path == null) return SolidBackground();
            try
            {
                var bi = new BitmapImage();
                bi.BeginInit();
                bi.UriSource = new Uri(path);
                bi.DecodePixelWidth = 640;
                bi.CacheOption = BitmapCacheOption.OnLoad;
                bi.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
                bi.EndInit();
                var src = new FormatConvertedBitmap(bi, PixelFormats.Bgra32, null, 0);
                int w = src.PixelWidth, h = src.PixelHeight, stride = w * 4;
                var px = new byte[stride * h];
                src.CopyPixels(px, stride, 0);

                double sw = scr.Bounds.Width, sh = scr.Bounds.Height;
                double scale = Math.Max(sw / w, sh / h);
                double ox = (sw - w * scale) / 2, oy = (sh - h * scale) / 2;
                double x0 = Math.Clamp((padPx.Left - scr.Bounds.Left - ox) / scale, 0, w - 1);
                double x1 = Math.Clamp((padPx.Right - scr.Bounds.Left - ox) / scale, x0 + 1, w);
                double y0 = Math.Clamp((padPx.Top - scr.Bounds.Top - oy) / scale, 0, h - 1);
                double y1 = Math.Clamp((padPx.Bottom - scr.Bounds.Top - oy) / scale, y0 + 1, h);
                long r = 0, g = 0, b = 0, n = 0;
                for (int y = (int)y0; y < (int)Math.Ceiling(y1) && y < h; y++)
                    for (int x = (int)x0; x < (int)Math.Ceiling(x1) && x < w; x++)
                    {
                        int i = y * stride + x * 4;
                        b += px[i]; g += px[i + 1]; r += px[i + 2]; n++;
                    }
                return n == 0 ? null : Color.FromRgb((byte)(r / n), (byte)(g / n), (byte)(b / n));
            }
            catch { return SolidBackground(); }
        }

        /// <summary>A pad colour from the wallpaper average: same hue, dark enough for white text to stay readable.</summary>
        public static string PadColor(Color avg)
        {
            double lum = 0.299 * avg.R + 0.587 * avg.G + 0.114 * avg.B;
            double target = 52;
            double k = lum > target ? target / lum : 1;
            byte f(byte c) => (byte)Math.Clamp(c * k, 0, 255);
            return $"#{f(avg.R):X2}{f(avg.G):X2}{f(avg.B):X2}";
        }
    }

    /// <summary>Looks for a newer GitHub release. Tells you; never installs anything by itself.</summary>
    public static class UpdateChecker
    {
        const string Repo = "ApostleTims/Paddock";

        public record Result(bool Ok, bool Newer, string Current, string? Tag, string? Url, string? Error);

        public static string CurrentVersion()
        {
            var v = System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(0, 0, 0);
            return $"{v.Major}.{v.Minor}.{Math.Max(v.Build, 0)}";
        }

        public static async Task<Result> CheckAsync()
        {
            var cur = CurrentVersion();
            string? tag = null, url = null, err = null;
            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(12) };
                http.DefaultRequestHeaders.UserAgent.ParseAdd("Paddock-update-check");
                var resp = await http.GetAsync($"https://api.github.com/repos/{Repo}/releases/latest");
                if (resp.IsSuccessStatusCode)
                {
                    using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
                    tag = doc.RootElement.GetProperty("tag_name").GetString();
                    url = doc.RootElement.GetProperty("html_url").GetString();
                }
                else err = $"GitHub answered {(int)resp.StatusCode} (the repository is private, so the public API can't see it).";
            }
            catch (Exception ex) { err = ex.Message; }

            if (tag == null)
            {
                // Private repo: fall back to the GitHub CLI, which is signed in as you.
                try
                {
                    var psi = new ProcessStartInfo("gh", $"api repos/{Repo}/releases/latest --jq \".tag_name + ' ' + .html_url\"")
                    { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                    using var p = Process.Start(psi)!;
                    var outText = await p.StandardOutput.ReadToEndAsync();
                    await p.WaitForExitAsync(new CancellationTokenSource(15000).Token);
                    var parts = outText.Trim().Split(' ', 2);
                    if (p.ExitCode == 0 && parts.Length == 2) { tag = parts[0]; url = parts[1]; err = null; }
                    else err += "  GitHub CLI fallback failed (is `gh auth login` done?).";
                }
                catch { err += "  GitHub CLI (gh) not available either."; }
            }
            if (tag == null) return new Result(false, false, cur, null, null, err);
            bool newer = Version.TryParse(tag.TrimStart('v', 'V'), out var latest) && Version.TryParse(cur, out var mine) && latest > mine;
            return new Result(true, newer, cur, tag, url, null);
        }
    }

    /// <summary>Duplicate and stale file report for one pad.</summary>
    public static class PadReport
    {
        public record Group(long Size, List<FileInfo> Files);

        /// <summary>Blocking (hashes files); call from a background thread. Hashes at most 400 same-size candidates.</summary>
        public static (List<Group> Dupes, List<FileInfo> Stale, bool Capped) Analyze(IEnumerable<string> paths, int staleDays)
        {
            var files = paths.Where(File.Exists).Select(p => new FileInfo(p)).ToList();
            var stale = files.Where(f => (DateTime.Now - f.LastWriteTime).TotalDays >= staleDays).OrderBy(f => f.LastWriteTime).ToList();
            var dupes = new List<Group>();
            bool capped = false;
            int hashed = 0;
            foreach (var bySize in files.Where(f => f.Length > 0).GroupBy(f => f.Length).Where(g => g.Count() > 1))
            {
                var byHash = new Dictionary<string, List<FileInfo>>();
                foreach (var f in bySize)
                {
                    if (hashed >= 400) { capped = true; break; }
                    hashed++;
                    try
                    {
                        using var s = File.OpenRead(f.FullName);
                        var h = Convert.ToHexString(SHA256.HashData(s));
                        if (!byHash.TryGetValue(h, out var l)) byHash[h] = l = new();
                        l.Add(f);
                    }
                    catch { /* locked or vanished: not comparable */ }
                }
                dupes.AddRange(byHash.Values.Where(l => l.Count > 1).Select(l => new Group(bySize.Key, l)));
            }
            return (dupes.OrderByDescending(g => g.Size * (g.Files.Count - 1)).ToList(), stale, capped);
        }

        static string Size(long b) => b >= 1L << 30 ? $"{b / (double)(1L << 30):0.0} GB" : b >= 1L << 20 ? $"{b / (double)(1L << 20):0.0} MB" : $"{Math.Max(1, b / 1024)} KB";

        public static void Show(Window? owner, string pad, int staleDays, (List<Group> Dupes, List<FileInfo> Stale, bool Capped) r, Func<List<FileInfo>, int> archive)
        {
            var sb = new StringBuilder();
            long wasted = r.Dupes.Sum(g => g.Size * (g.Files.Count - 1));
            sb.AppendLine($"DUPLICATES: {r.Dupes.Count} group(s), {Size(wasted)} taken by extra copies");
            foreach (var g in r.Dupes)
            {
                sb.AppendLine($"  {Size(g.Size)} x {g.Files.Count}");
                foreach (var f in g.Files) sb.AppendLine("      " + f.FullName);
            }
            if (r.Capped) sb.AppendLine("  (only the first 400 candidates were compared)");
            sb.AppendLine();
            sb.AppendLine($"STALE: {r.Stale.Count} file(s) not changed in {staleDays}+ days, {Size(r.Stale.Sum(f => f.Length))}");
            foreach (var f in r.Stale.Take(200)) sb.AppendLine($"  {f.LastWriteTime:yyyy-MM-dd}  {f.FullName}");
            if (r.Stale.Count > 200) sb.AppendLine($"  ...and {r.Stale.Count - 200} more");
            sb.AppendLine();
            sb.AppendLine("Duplicates are only reported; Paddock never deletes them. \"Archive stale\" moves files (undoable from the tray).");

            var box = new TextBox
            {
                Text = sb.ToString(), IsReadOnly = true, FontFamily = new FontFamily("Consolas"), FontSize = 12,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                Width = 640, Height = 380,
            };
            var arch = new Button { Content = "Archive stale files", Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(0, 0, 6, 0), IsEnabled = r.Stale.Count > 0 };
            var copy = new Button { Content = "Copy report", Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(0, 0, 6, 0) };
            var close = new Button { Content = "Close", Padding = new Thickness(10, 3, 10, 3), IsCancel = true };
            var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
            row.Children.Add(arch); row.Children.Add(copy); row.Children.Add(close);
            var panel = new StackPanel { Margin = new Thickness(14) };
            panel.Children.Add(box); panel.Children.Add(row);
            var win = new Window
            {
                Title = $"Paddock - report for \"{pad}\"", Content = panel, SizeToContent = SizeToContent.WidthAndHeight,
                ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterScreen, ShowInTaskbar = false,
            };
            if (owner != null) win.Owner = owner;
            copy.Click += (_, _) => { try { Clipboard.SetText(box.Text); } catch { } };
            arch.Click += (_, _) =>
            {
                if (MessageBox.Show($"Move {r.Stale.Count} stale file(s) to the archive?", "Paddock", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
                int n = archive(r.Stale);
                MessageBox.Show($"Archived {n} file(s). Tray > Undo last archive puts them back.", "Paddock");
                arch.IsEnabled = false;
            };
            win.ShowDialog();
        }
    }
}
