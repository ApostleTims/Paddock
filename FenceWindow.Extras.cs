using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace Paddock
{
    // Newer per-pad features: manual order, open all, hotkey, export, chameleon, wallpaper colour,
    // live pad types (downloads, clipboard, todo, links, project, saved search) and the file report.
    public partial class FenceWindow
    {
        /// <summary>Set by the app: (re)registers every pad hotkey and returns the configs whose key was already taken.</summary>
        public static Func<List<FenceConfig>>? RehookHotkeys;

        readonly DispatcherTimer _searchTimer = new() { Interval = TimeSpan.FromSeconds(60) };
        readonly DispatcherTimer _peekTimer = new() { Interval = TimeSpan.FromSeconds(10) };
        CancellationTokenSource? _searchCts;
        int _searchGen;
        bool _hotkeyRevealed;

        /// <summary>Icons can be dragged into a manual order only on ordinary pads, and not while a search filter is showing.</summary>
        bool Reorderable => _cfg.PadType == null && _cfg.VaultPath == null && _cfg.NotePath == null && SearchBox.Visibility != Visibility.Visible;

        void InitExtras()
        {
            Root.MouseEnter += (_, _) => FadeIcons(true);
            Root.MouseLeave += (_, _) => FadeIcons(false);
            Action clip = () => { if (_cfg.PadType == "clipboard") Refresh(); };
            ClipboardHistory.Changed += clip;
            _searchTimer.Tick += (_, _) => { if (_cfg.PadType == "search" && IsVisible) Refresh(); };
            _searchTimer.Start();
            _peekTimer.Tick += (_, _) =>
            {
                _peekTimer.Stop();
                SetPeek(false);
                if (_hotkeyRevealed) { _hotkeyRevealed = false; Hide(); } // it was hidden by quick-hide before the hotkey
            };
            Loaded += (_, _) => ApplyWallpaper();
            Closed += (_, _) => { ClipboardHistory.Changed -= clip; _searchTimer.Stop(); _peekTimer.Stop(); _searchCts?.Cancel(); };
        }

        /// <summary>Menu visibility and ticks that depend on the pad type. Called from ApplyConfigState after the basic rules.</summary>
        void ApplyExtrasState()
        {
            var t = _cfg.PadType;
            bool note = _cfg.NotePath != null;
            if (t != null)
            {
                RulesItem.Visibility = t == "search" ? Visibility.Visible : Visibility.Collapsed;
                ActionSetItem.Visibility = ActionRunItem.Visibility = ActionAutoItem.Visibility = Visibility.Collapsed;
                DefaultItem.Visibility = Visibility.Collapsed;
                AddAppItem.Visibility = t == "project" ? Visibility.Visible : Visibility.Collapsed;
            }
            var archiveVis = t == null && !note ? Visibility.Visible : Visibility.Collapsed;
            ArchiveSetItem.Visibility = ArchiveNowItem.Visibility = archiveVis;
            LinksItem.Visibility = t is "links" or "project" ? Visibility.Visible : Visibility.Collapsed;
            OpenAllItem.Visibility = note || t is "clipboard" or "todo" ? Visibility.Collapsed : Visibility.Visible;
            ReportItem.Visibility = note || t is "clipboard" or "todo" or "links" ? Visibility.Collapsed : Visibility.Visible;
            OpenAllButton.Visibility = t == "project" ? Visibility.Visible : Visibility.Collapsed;
            ChameleonItem.IsChecked = _cfg.Chameleon;
            WallpaperItem.IsChecked = _cfg.WallpaperColor;
            FadeIcons(Root.IsMouseOver);
            if (IsLoaded) ApplyWallpaper();
        }

        // ---- Chameleon: icons fade to a ghost until the mouse is over the pad ----
        void FadeIcons(bool hover)
        {
            if (!_cfg.Chameleon)
            {
                Items.BeginAnimation(OpacityProperty, null);
                Items.Opacity = 1;
                return;
            }
            double to = hover || Header.ContextMenu is { IsOpen: true } ? 1 : 0.12;
            Items.BeginAnimation(OpacityProperty, new DoubleAnimation(to, TimeSpan.FromMilliseconds(180)));
        }

        void Chameleon_Click(object s, RoutedEventArgs e)
        {
            _cfg.Chameleon = ChameleonItem.IsChecked;
            FadeIcons(Root.IsMouseOver);
            _save();
        }

        // ---- Wallpaper colour: tint the pad from the picture behind it ----
        bool ApplyWallpaper()
        {
            if (!_cfg.WallpaperColor || !IsLoaded) return false;
            double s = Scale;
            var scr = Screens.NearestTo(new Rect(Left, Top, Width, Height), s);
            var px = new System.Drawing.Rectangle((int)(Left * s), (int)(Top * s), Math.Max(1, (int)(Width * s)), Math.Max(1, (int)(Height * s)));
            var avg = Wallpaper.Behind(scr, px);
            if (avg == null) return false;
            _cfg.Color = Wallpaper.PadColor(avg.Value);
            _cfg.Color2 = null;
            ApplyLook(smooth: true);
            return true;
        }

        void Wallpaper_Click(object s, RoutedEventArgs e)
        {
            _cfg.WallpaperColor = WallpaperItem.IsChecked;
            if (_cfg.WallpaperColor && !ApplyWallpaper())
            {
                _cfg.WallpaperColor = false; WallpaperItem.IsChecked = false;
                MessageBox.Show("Couldn't read the wallpaper (a slideshow or unusual setting?).", "Paddock");
            }
            _save();
        }

        // ---- Manual icon order ----
        void ReorderDrop(DragEventArgs e, string dragged)
        {
            if (!Reorderable) return;
            var from = _items.FirstOrDefault(i => i.Path.Equals(dragged, StringComparison.OrdinalIgnoreCase));
            if (from == null) return;
            FenceItem? target = null;
            bool after = false;
            var hit = VisualTreeHelper.HitTest(Items, e.GetPosition(Items))?.VisualHit;
            if (hit != null && Items.ContainerFromElement(hit) is FrameworkElement box)
            {
                target = Items.ItemContainerGenerator.ItemFromContainer(box) as FenceItem;
                after = e.GetPosition(box).X > box.ActualWidth / 2;
            }
            if (target == from) return;
            var list = _items.Select(i => i.Path).Where(p => !p.Equals(dragged, StringComparison.OrdinalIgnoreCase)).ToList();
            int at = list.Count; // empty space = the end
            if (target != null)
            {
                at = list.FindIndex(p => p.Equals(target.Path, StringComparison.OrdinalIgnoreCase));
                if (at < 0) at = list.Count; else if (after) at++;
            }
            list.Insert(at, dragged);
            _cfg.ItemOrder = list; // saved as a full order, so nothing reflows by itself afterwards
            Refresh();
            _save();
        }

        // ---- Open all ----
        static void OpenUrl(string url)
        {
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); }
            catch (Exception ex) { MessageBox.Show(ex.Message, "Paddock"); }
        }

        void OpenAll_Click(object s, RoutedEventArgs e) => OpenAllItems();

        void OpenAllButton_MouseLeftButtonDown(object s, MouseButtonEventArgs e)
        {
            e.Handled = true; // don't start a window drag
            OpenAllItems();
        }

        void OpenAllItems()
        {
            if (_cfg.PadType == "project")
            {
                // A project's launch set: its pinned apps/files, its links, and the folder itself (not every file in it).
                foreach (var p in _cfg.Pinned.ToList()) if (File.Exists(p) || Directory.Exists(p)) OpenItem(_cfg, p);
                foreach (var l in LiveItems.ParseLinks(_cfg.Links)) OpenUrl(l.Url);
                if (_cfg.PadSpec != null && Directory.Exists(_cfg.PadSpec)) OpenItem(_cfg, _cfg.PadSpec);
                return;
            }
            var list = _items.ToList();
            if (list.Count == 0) return;
            if (list.Count > 5 && MessageBox.Show($"Open all {list.Count} items?", "Paddock", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
            foreach (var i in list) { if (i.OnOpen != null) i.OnOpen(); else OpenItem(_cfg, i.Path); }
        }

        // ---- Per-pad hotkey ----
        void Hotkey_Click(object s, RoutedEventArgs e)
        {
            var text = InputDialog.Ask("Pad hotkey",
                "Global hotkey that raises this pad for 10 seconds, e.g.  Ctrl+Alt+F1\nNeeds Ctrl, Alt, Shift or Win plus a key (A-Z, 0-9, F1-F12, Space). Empty = none.",
                _cfg.Hotkey ?? "", this);
            if (text == null) return;
            text = text.Trim();
            if (text.Length == 0) _cfg.Hotkey = null;
            else if (!HotkeyText.Parse(text, out _, out _)) { MessageBox.Show("Couldn't read that. Example: Ctrl+Alt+F1", "Paddock"); return; }
            else _cfg.Hotkey = text;
            var failed = RehookHotkeys?.Invoke();
            if (_cfg.Hotkey != null && failed != null && failed.Contains(_cfg))
            {
                MessageBox.Show($"{_cfg.Hotkey} is already used by another program (or another pad). Pick a different one.", "Paddock");
                _cfg.Hotkey = null;
                RehookHotkeys?.Invoke();
            }
            _save();
        }

        public bool Owns(FenceConfig c) => GroupTabs().Contains(c);

        /// <summary>The pad's hotkey was pressed: bring this window up (even if quick-hidden) on that tab for 10 seconds.</summary>
        public void RaiseFor(FenceConfig c)
        {
            if (!IsVisible) { Show(); _hotkeyRevealed = true; }
            if (c != _cfg) SwitchTab(c);
            if (_cfg.Dock != DockEdge.None && _collapsed) EnterDock(_cfg.Dock, collapsed: false, animate: true);
            SetPeek(true);
            _peekTimer.Stop(); _peekTimer.Start();
        }

        // ---- Export / import one pad ----
        static readonly JsonSerializerOptions PadJson = new() { WriteIndented = true };

        void Export_Click(object s, RoutedEventArgs e)
        {
            var safe = string.Concat(_cfg.Title.Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch));
            var dlg = new Microsoft.Win32.SaveFileDialog { FileName = safe, DefaultExt = ".paddockpad", Filter = "Paddock pad (*.paddockpad)|*.paddockpad" };
            if (dlg.ShowDialog(this) != true) return;
            try
            {
                File.WriteAllText(dlg.FileName, JsonSerializer.Serialize(_cfg, PadJson));
                MessageBox.Show("Exported. Paths inside the pad (folders, pinned files) are saved as-is, so on another PC they only work if the same paths exist.", "Paddock");
            }
            catch (Exception ex) { MessageBox.Show(ex.Message, "Paddock"); }
        }

        /// <summary>Read a .paddockpad file as a fresh pad (new id, no tab group, no hotkey, nudged so it doesn't cover the original).</summary>
        public static FenceConfig? ReadPadFile(string path)
        {
            try
            {
                var c = JsonSerializer.Deserialize<FenceConfig>(File.ReadAllText(path), PadJson);
                if (c == null) return null;
                c.Id = Guid.NewGuid().ToString("N");
                c.TabGroup = null; c.TabActive = false; c.IsDefault = false; c.Hotkey = null;
                c.X += 30; c.Y += 30;
                return c;
            }
            catch { return null; }
        }

        // ---- Duplicates and stale files ----
        async void Report_Click(object s, RoutedEventArgs e)
        {
            var paths = _items.Where(i => i.OnOpen == null && File.Exists(i.Path)).Select(i => i.Path).ToList();
            if (paths.Count == 0) { MessageBox.Show("This pad has no files to check right now.", "Paddock"); return; }
            int days = _cfg.ArchiveAfterDays > 0 ? _cfg.ArchiveAfterDays : 90;
            var prev = Mouse.OverrideCursor;
            Mouse.OverrideCursor = Cursors.Wait;
            (List<PadReport.Group>, List<FileInfo>, bool) r;
            try { r = await Task.Run(() => PadReport.Analyze(paths, days)); }
            finally { Mouse.OverrideCursor = prev; }
            PadReport.Show(this, _cfg.Title, days, r, files => { var n = Archiver.Move(_cfg.Title, files); if (n > 0) Refresh(); return n; });
        }

        // ---- Links ----
        void Links_Click(object s, RoutedEventArgs e)
        {
            var text = InputDialog.Ask("Edit links",
                "Links as  Title|url; Title|url   (a bare URL works too). You can also drop a link onto a links pad.",
                _cfg.Links, this);
            if (text == null) return;
            _cfg.Links = text.Trim();
            _save();
            Refresh();
        }

        void AddLinksFromText(string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            var add = text.Split('\n').Select(l => l.Trim())
                          .Where(l => l.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || l.StartsWith("https://", StringComparison.OrdinalIgnoreCase) || l.StartsWith("www.", StringComparison.OrdinalIgnoreCase))
                          .ToList();
            if (add.Count == 0) return;
            _cfg.Links = string.Join("; ", new[] { _cfg.Links }.Concat(add).Where(x => x.Length > 0));
            _save();
            Refresh();
        }

        // ---- Live pad contents ----
        FenceItem ItemFor(FileSystemInfo f) => new()
        {
            Name = System.IO.Path.GetFileNameWithoutExtension(f.Name) is { Length: > 0 } n && f is FileInfo ? n : f.Name,
            Path = f.FullName, Icon = Icons.For(f.FullName), IconSize = _cfg.IconSize,
        };

        FenceItem GlyphItem(string name, string glyph, Color color, string? tip, Action? open) => new()
        {
            Name = name, Icon = Glyphs.Make(glyph, color), IconSize = _cfg.IconSize, TipText = tip ?? name, OnOpen = open,
        };

        static bool Junk(FileSystemInfo f) =>
            f.Name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase) || (f.Attributes & FileAttributes.Hidden) != 0;

        void RefreshSpecial()
        {
            var list = new List<FenceItem>();
            var type = _cfg.PadType;
            var accent = Color.FromRgb(0x8E, 0xC5, 0xFF);
            switch (type)
            {
                case "downloads":
                    foreach (var dir in SourceFolders().Where(Directory.Exists))
                        try
                        {
                            list.AddRange(new DirectoryInfo(dir).EnumerateFileSystemInfos()
                                .Where(f => !Junk(f) && !new[] { ".crdownload", ".tmp", ".part", ".download" }.Contains(f.Extension.ToLowerInvariant()))
                                .OrderByDescending(f => f.LastWriteTimeUtc).Take(30).Select(ItemFor));
                        }
                        catch { }
                    break;

                case "clipboard":
                    ClipboardHistory.Start();
                    foreach (var t in ClipboardHistory.Items.ToList())
                    {
                        var first = t.Split('\n')[0].Trim();
                        var name = first.Length == 0 ? "(blank)" : first.Length > 26 ? first[..26] + "…" : first;
                        var text = t;
                        list.Add(GlyphItem(name, "", accent, (text.Length > 400 ? text[..400] + "…" : text) + "\n\nDouble-click to copy again",
                            () => { try { ClipboardHistory.Ignore(text); Clipboard.SetText(text); } catch { } }));
                    }
                    if (list.Count == 0) list.Add(GlyphItem("Copy some text…", "", accent, "Text you copy while Paddock runs appears here (memory only, never saved)", null));
                    break;

                case "todo":
                    var note = _cfg.PadSpec;
                    if (note == null || !File.Exists(note)) { list.Add(GlyphItem("Todo note not found", "", Colors.Orange, note, null)); break; }
                    try
                    {
                        foreach (var todo in LiveItems.ParseTodos(File.ReadAllText(note)).Where(x => !x.Done).Take(30))
                        {
                            var txt = todo.Text;
                            list.Add(GlyphItem(txt, "", Colors.White, txt + "\n\nDouble-click to tick it off in the note", () =>
                            {
                                // Find the line again by its text: the note may have changed since this list was drawn.
                                var cur = LiveItems.ParseTodos(File.ReadAllText(note)).FirstOrDefault(x => !x.Done && x.Text == txt);
                                if (cur != null) LiveItems.ToggleTodo(note, cur.Index);
                                Refresh();
                            }));
                        }
                        if (list.Count == 0) list.Add(GlyphItem("All done!", "", Colors.LightGreen, "No open - [ ] tasks in the note", null));
                    }
                    catch { }
                    break;

                case "links":
                    AddLinkItems(list, accent);
                    if (list.Count == 0) list.Add(GlyphItem("Add links…", "", accent, "Right-click > Edit links, or drop a URL here", () => Links_Click(this, new RoutedEventArgs())));
                    break;

                case "project":
                    foreach (var p in _cfg.Pinned.Where(p => File.Exists(p) || Directory.Exists(p)))
                        list.Add(ItemFor(File.Exists(p) ? new FileInfo(p) : new DirectoryInfo(p)));
                    AddLinkItems(list, accent);
                    if (_cfg.PadSpec != null && Directory.Exists(_cfg.PadSpec))
                        try
                        {
                            var pinned = new HashSet<string>(_cfg.Pinned, StringComparer.OrdinalIgnoreCase);
                            list.AddRange(new DirectoryInfo(_cfg.PadSpec).EnumerateFileSystemInfos()
                                .Where(f => !Junk(f) && !f.Name.StartsWith('.') && !pinned.Contains(f.FullName))
                                .OrderByDescending(f => f.LastWriteTimeUtc).Take(40).Select(ItemFor));
                        }
                        catch { }
                    break;

                case "search":
                    SearchRefresh();
                    return;
            }
            ShowItems(list);
        }

        void AddLinkItems(List<FenceItem> list, Color accent)
        {
            foreach (var (title, url) in LiveItems.ParseLinks(_cfg.Links))
            {
                var u = url;
                list.Add(GlyphItem(title, "", accent, u, () => OpenUrl(u)));
            }
        }

        void ShowItems(IEnumerable<FenceItem> items)
        {
            var query = SearchBox.Visibility == Visibility.Visible ? SearchBox.Text.Trim() : "";
            _items.Clear();
            foreach (var i in items.Where(i => query.Length == 0 || i.Name.Contains(query, StringComparison.OrdinalIgnoreCase)))
                _items.Add(i);
        }

        /// <summary>Saved search: walks the folder on a background thread; results replace the list when done.</summary>
        async void SearchRefresh()
        {
            var root = _cfg.PadSpec;
            var rules = _cfg.Rules.ToList();
            if (root == null || rules.Count == 0 || !Directory.Exists(root))
            {
                ShowItems(new[] { GlyphItem("Set up the search…", "", Color.FromRgb(0x8E, 0xC5, 0xFF), "Right-click > Edit rules, e.g.  expr: modified < 1d", null) });
                return;
            }
            _searchCts?.Cancel();
            var cts = _searchCts = new CancellationTokenSource();
            int gen = ++_searchGen;
            if (_items.Count == 0) _items.Add(GlyphItem("Searching…", "", Color.FromRgb(0x8E, 0xC5, 0xFF), root, null));
            List<FileSystemInfo> hits;
            try { hits = await Task.Run(() => LiveItems.Search(root, rules, cts.Token), cts.Token); }
            catch { return; }
            if (gen != _searchGen || cts.IsCancellationRequested) return;
            ShowItems(hits.Select(ItemFor));
        }
    }
}
