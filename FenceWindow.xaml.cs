using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using WF = System.Windows.Forms;

namespace Paddock
{
    public class FenceItem
    {
        public string Name { get; set; } = "";
        public string Path { get; set; } = "";
        public BitmapSource? Icon { get; set; }
        public double IconSize { get; set; } = 32;
        public double TileWidth => Math.Max(64, IconSize * 2.25);
    }

    public partial class FenceWindow : Window
    {
        const double HeaderHeight = 28;
        FenceConfig _cfg; // the active tab's config; a window with several tabs swaps this
        readonly Action _save;
        readonly Action<FenceWindow> _onDelete;
        readonly ObservableCollection<FenceItem> _items = new();
        readonly List<FileSystemWatcher> _watchers = new();
        readonly DispatcherTimer _refreshTimer;
        double _expandedHeight;

        // Edge docking: while docked the window is either a thin strip on the edge or fully expanded.
        const double StripSize = HeaderHeight, EnterDelayMs = 150;

        /// <summary>How long the mouse must stay outside an expanded docked pad before it collapses (tray setting).</summary>
        public static double LeaveDelayMs = 450;

        /// <summary>Every pad's config in the active profile, so the default pad can tell what the others claim.</summary>
        public static Func<IEnumerable<FenceConfig>> AllConfigs = () => Array.Empty<FenceConfig>();

        /// <summary>Raised when the default pad changes so every pad re-evaluates what it shows.</summary>
        public static Action? DefaultChanged;
        readonly DispatcherTimer _hoverTimer;
        bool _collapsed, _dragging;
        int _animGen;
        DateTime? _leaveSince, _enterSince;

        public FenceConfig Config => _cfg;

        public FenceWindow(FenceConfig cfg, Action save, Action<FenceWindow> onDelete)
        {
            InitializeComponent();
            _cfg = cfg;
            _save = () => { SyncGroup(); save(); };
            _onDelete = onDelete;

            Left = cfg.X; Top = cfg.Y; Width = cfg.Width;
            _expandedHeight = cfg.Height;
            Height = cfg.RolledUp ? HeaderHeight : cfg.Height;
            ApplyConfigState();
            ArrangeHeader();
            Items.ItemsSource = _items;
            ApplyRollUp();

            // Coalesce bursts of file-system events into one refresh.
            _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
            _refreshTimer.Tick += (_, _) => { _refreshTimer.Stop(); Refresh(); };

            SourceInitialized += (_, _) =>
            {
                var h = new WindowInteropHelper(this).Handle;
                Native.MakeToolWindow(h);
                Native.SitOnDesktop(h);
                FitToScreens(); // needs the window's DPI, so not in the constructor
            };
            _hoverTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
            _hoverTimer.Tick += (_, _) => HoverTick();
            _hoverTimer.Start();
            Activated += (_, _) => Dispatcher.BeginInvoke(() =>
            {
                if (!_peeking) Native.SitOnDesktop(new WindowInteropHelper(this).Handle);
            });
            _noteTimer.Tick += (_, _) => FlushNote();
            Body.IsVisibleChanged += (_, _) => SyncNoteVisibility();
            Closed += (_, _) => { FlushNote(); _hoverTimer.Stop(); foreach (var w in _watchers) w.Dispose(); };

            Watch();
            Refresh();
        }

        // ---- Tabs: pads sharing a TabGroup are one window; the header shows one label per tab ----

        /// <summary>All windows, so a pad dropped on another pad's header can merge into it as a tab.</summary>
        public static Func<IEnumerable<FenceWindow>> AllWindows = () => Array.Empty<FenceWindow>();

        /// <summary>A window whose tabs were all merged into another window: the app closes it (configs are kept).</summary>
        public static Action<FenceWindow>? Merged;

        /// <summary>A tab split out of a group needs its own window.</summary>
        public static Action<FenceConfig>? SplitOut;

        /// <summary>Remove one config from the layout (a deleted tab).</summary>
        public static Action<FenceConfig>? DeleteConfig;

        List<FenceConfig> GroupTabs() =>
            string.IsNullOrEmpty(_cfg.TabGroup)
                ? new List<FenceConfig> { _cfg }
                : AllConfigs().Where(c => c.TabGroup == _cfg.TabGroup).ToList();

        /// <summary>Tabs of one window share its position, size and dock state; keep the siblings in step with the active one.</summary>
        void SyncGroup()
        {
            if (string.IsNullOrEmpty(_cfg.TabGroup)) return;
            foreach (var c in GroupTabs().Where(c => c != _cfg))
            {
                c.X = _cfg.X; c.Y = _cfg.Y; c.Width = _cfg.Width; c.Height = _cfg.Height;
                c.Dock = _cfg.Dock; c.RolledUp = _cfg.RolledUp;
            }
        }

        /// <summary>Everything that depends on which config is active: title, look, menu ticks, tab labels.</summary>
        void ApplyConfigState()
        {
            TitleText.Text = _cfg.Title;
            ExcludeItem.IsChecked = _cfg.ExcludeFromQuickHide;
            bool note = _cfg.NotePath != null;
            RulesItem.Visibility = _cfg.PortalPath == null && !note ? Visibility.Visible : Visibility.Collapsed;
            DefaultItem.IsChecked = _cfg.IsDefault;
            DefaultItem.Visibility = _cfg.PortalPath == null && _cfg.VaultPath == null && !note ? Visibility.Visible : Visibility.Collapsed;
            NoteOpenItem.Visibility = note ? Visibility.Visible : Visibility.Collapsed;
            Items.Visibility = note ? Visibility.Collapsed : Visibility.Visible;
            SyncNoteVisibility();
            if (note) LoadNote(force: true);
            ApplyLook();
            BuildTabs();
        }

        // ---- Note pad: a text box editing one file in place, saved shortly after you stop typing ----
        readonly DispatcherTimer _noteTimer = new() { Interval = TimeSpan.FromMilliseconds(700) };
        bool _noteLoading, _noteDirty;

        /// <summary>The editor shows only for a note pad whose content area is showing (not rolled up / collapsed).</summary>
        void SyncNoteVisibility() =>
            NoteBox.Visibility = _cfg.NotePath != null && Body.Visibility == Visibility.Visible ? Visibility.Visible : Visibility.Collapsed;

        void LoadNote(bool force)
        {
            if (_cfg.NotePath == null) return;
            if (!force && (_noteDirty || NoteBox.IsKeyboardFocused)) return; // never overwrite what you are typing
            string text = "";
            try { if (File.Exists(_cfg.NotePath)) text = File.ReadAllText(_cfg.NotePath); } catch { return; }
            if (text == NoteBox.Text) { _noteDirty = false; return; } // our own save echoing back: leave the caret alone
            _noteLoading = true;
            NoteBox.Text = text;
            _noteLoading = false;
            _noteDirty = false;
        }

        /// <summary>Write pending edits to disk now.</summary>
        void FlushNote()
        {
            _noteTimer.Stop();
            if (!_noteDirty || _cfg.NotePath == null) return;
            try
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_cfg.NotePath)!);
                File.WriteAllText(_cfg.NotePath, NoteBox.Text);
                _noteDirty = false;
            }
            catch (Exception ex) { MessageBox.Show($"Could not save the note: {ex.Message}", "Paddock"); }
        }

        void NoteBox_TextChanged(object s, TextChangedEventArgs e)
        {
            if (_noteLoading) return;
            _noteDirty = true;
            _noteTimer.Stop(); _noteTimer.Start();
        }

        void NoteBox_LostFocus(object s, KeyboardFocusChangedEventArgs e) => FlushNote();

        void NoteOpen_Click(object s, RoutedEventArgs e)
        {
            FlushNote();
            if (_cfg.NotePath != null && File.Exists(_cfg.NotePath))
                try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(_cfg.NotePath) { UseShellExecute = true }); }
                catch (Exception ex) { MessageBox.Show(ex.Message, "Paddock"); }
        }

        /// <summary>Quick capture: add a timestamped bullet to the end of this note.</summary>
        public void AppendNote(string text)
        {
            if (_cfg.NotePath == null) return;
            FlushNote();
            try
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_cfg.NotePath)!);
                var existing = File.Exists(_cfg.NotePath) ? File.ReadAllText(_cfg.NotePath) : "";
                var sep = existing.Length == 0 || existing.EndsWith('\n') ? "" : "\n";
                File.AppendAllText(_cfg.NotePath, $"{sep}- {DateTime.Now:yyyy-MM-dd HH:mm} {text}\n");
            }
            catch (Exception ex) { MessageBox.Show($"Could not save the note: {ex.Message}", "Paddock"); return; }
            LoadNote(force: true);
        }

        void BuildTabs()
        {
            var tabs = GroupTabs();
            SplitItem.Visibility = tabs.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
            TabsPanel.Children.Clear();
            bool searching = SearchBox.Visibility == Visibility.Visible;
            if (tabs.Count < 2)
            {
                TabsPanel.Visibility = Visibility.Collapsed;
                TitleText.Visibility = searching ? Visibility.Collapsed : Visibility.Visible;
                return;
            }
            TitleText.Visibility = Visibility.Collapsed;
            TabsPanel.Visibility = searching ? Visibility.Collapsed : Visibility.Visible;
            foreach (var t in tabs)
            {
                bool active = t == _cfg;
                var label = new TextBlock
                {
                    Text = t.Title, Tag = t, Padding = new Thickness(8, 3, 8, 3), Margin = new Thickness(0, 0, 2, 0),
                    FontWeight = active ? FontWeights.SemiBold : FontWeights.Normal,
                    Foreground = new SolidColorBrush(active ? Colors.White : Color.FromArgb(0xA0, 255, 255, 255)),
                    Background = new SolidColorBrush(active ? Color.FromArgb(0x50, 255, 255, 255) : Color.FromArgb(0x18, 255, 255, 255)),
                    MaxWidth = 140, TextTrimming = TextTrimming.CharacterEllipsis, Cursor = Cursors.Hand,
                };
                // Switch on press, then let the event reach the header so the same press can still drag the pad.
                label.MouseLeftButtonDown += (_, _) => SwitchTab(t);
                label.MouseRightButtonDown += (_, _) => SwitchTab(t); // so the menu acts on the tab you right-clicked
                TabsPanel.Children.Add(label);
            }
        }

        void SwitchTab(FenceConfig next)
        {
            if (next == _cfg) return;
            SyncGroup();
            _cfg.TabActive = false;
            BecomeActive(next);
            _save();
        }

        /// <summary>Make next the showing tab: swap the config, re-watch its folders and redraw.</summary>
        void BecomeActive(FenceConfig next)
        {
            FlushNote(); // pending edits belong to the tab we're leaving
            _cfg = next;
            _cfg.TabActive = true;
            foreach (var w in _watchers) w.Dispose();
            _watchers.Clear();
            Watch();
            ApplyConfigState();
            Refresh();
        }

        /// <summary>Add a pad as a tab of this window and show it.</summary>
        void AddTab(FenceConfig c)
        {
            _cfg.TabGroup ??= Guid.NewGuid().ToString("N");
            c.TabGroup = _cfg.TabGroup;
            c.X = _cfg.X; c.Y = _cfg.Y; c.Width = _cfg.Width; c.Height = _cfg.Height; c.Dock = _cfg.Dock; c.RolledUp = _cfg.RolledUp;
            _cfg.TabActive = false;
            BecomeActive(c);
        }

        Rect HeaderRect() => new(Left, Top, Width, HeaderHeight);

        /// <summary>Released with the cursor on another undocked pad's header: join it as tab(s) and close this window.</summary>
        bool TryMergeIntoTarget()
        {
            double s = Scale;
            var mp = WF.Control.MousePosition;
            var cur = new Point(mp.X / s, mp.Y / s);
            var target = AllWindows().FirstOrDefault(w => w != this && w.IsVisible && w._cfg.Dock == DockEdge.None && w.HeaderRect().Contains(cur));
            if (target == null) return false;
            var mine = GroupTabs();
            var showing = _cfg;
            foreach (var c in mine) target.AddTab(c);
            target.SwitchTab(showing);
            target._save();
            Merged?.Invoke(this);
            return true;
        }

        void Split_Click(object s, RoutedEventArgs e)
        {
            var tabs = GroupTabs();
            if (tabs.Count < 2) return;
            SyncGroup();
            var leaving = _cfg;
            var next = tabs.First(t => t != leaving);
            leaving.TabGroup = null; leaving.TabActive = false;
            leaving.Dock = DockEdge.None; leaving.RolledUp = false;
            leaving.X = Left + 36; leaving.Y = Top + 36;
            if (tabs.Count == 2) next.TabGroup = null; // a group of one is just a pad again
            BecomeActive(next);
            _save();
            SplitOut?.Invoke(leaving);
        }

        IEnumerable<string> SourceFolders()
        {
            if (_cfg.NotePath != null) yield break; // a note pad edits one file; Watch() handles that file itself
            if (_cfg.PortalPath != null) { yield return _cfg.PortalPath; yield break; }
            if (_cfg.VaultPath != null) { yield return _cfg.VaultPath; yield break; }
            yield return Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            yield return Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory);
        }

        void Watch()
        {
            if (_cfg.NotePath != null)
            {
                // Pick up outside edits (Obsidian, an editor, another tool) to the note's file.
                var dir = System.IO.Path.GetDirectoryName(_cfg.NotePath);
                if (dir != null && Directory.Exists(dir))
                {
                    var nw = new FileSystemWatcher(dir, System.IO.Path.GetFileName(_cfg.NotePath)) { EnableRaisingEvents = true };
                    FileSystemEventHandler nh = (_, _) => Dispatcher.BeginInvoke(() => { _refreshTimer.Stop(); _refreshTimer.Start(); });
                    nw.Changed += nh; nw.Created += nh;
                    _watchers.Add(nw);
                }
                return;
            }
            foreach (var dir in SourceFolders().Where(Directory.Exists))
            {
                var w = new FileSystemWatcher(dir) { EnableRaisingEvents = true };
                if (_cfg.VaultPath != null) { w.IncludeSubdirectories = true; w.Filter = "*.md"; }
                FileSystemEventHandler h = (_, _) => Dispatcher.BeginInvoke(() => { _refreshTimer.Stop(); _refreshTimer.Start(); });
                w.Created += h; w.Deleted += h; w.Changed += h;
                w.Renamed += (_, _) => Dispatcher.BeginInvoke(() => { _refreshTimer.Stop(); _refreshTimer.Start(); });
                _watchers.Add(w);
            }
        }

        /// <summary>All .md notes under the vault, skipping dot-folders such as .obsidian.</summary>
        static IEnumerable<FileSystemInfo> VaultNotes(DirectoryInfo root)
        {
            var stack = new Stack<DirectoryInfo>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                var d = stack.Pop();
                IEnumerable<FileSystemInfo> entries;
                try { entries = d.EnumerateFileSystemInfos().ToList(); } catch { continue; }
                foreach (var e in entries)
                {
                    if (e.Name.StartsWith('.')) continue;
                    if (e is DirectoryInfo sub) stack.Push(sub);
                    else if (e.Extension.Equals(".md", StringComparison.OrdinalIgnoreCase)) yield return e;
                }
            }
        }

        public void Refresh()
        {
            if (_cfg.NotePath != null) { LoadNote(force: false); return; }
            var found = new List<FileSystemInfo>();
            // The default pad also takes whatever no other desktop pad's rules claim.
            var others = _cfg.IsDefault && _cfg.PortalPath == null && _cfg.VaultPath == null
                ? AllConfigs().Where(c => c != _cfg && c.PortalPath == null && c.VaultPath == null && c.NotePath == null).ToList()
                : null;
            bool desktopPad = _cfg.PortalPath == null && _cfg.VaultPath == null;
            // A file dragged onto a desktop pad is pinned there: it shows in that pad only, whatever the rules say.
            var pinnedElsewhere = desktopPad
                ? new HashSet<string>(AllConfigs().Where(c => c != _cfg).SelectMany(c => c.Pinned), StringComparer.OrdinalIgnoreCase)
                : new HashSet<string>();
            var pinnedHere = new HashSet<string>(desktopPad ? _cfg.Pinned : new List<string>(), StringComparer.OrdinalIgnoreCase);
            foreach (var dir in SourceFolders().Where(Directory.Exists))
            {
                try
                {
                    var di = new DirectoryInfo(dir);
                    foreach (var f in _cfg.VaultPath != null ? VaultNotes(di) : di.EnumerateFileSystemInfos())
                    {
                        if (f.Name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)) continue;
                        if ((f.Attributes & FileAttributes.Hidden) != 0) continue;
                        if (pinnedHere.Contains(f.FullName)) { found.Add(f); continue; }
                        if (pinnedElsewhere.Contains(f.FullName)) continue;
                        // Portal: show everything. Desktop fence: only what the rules match.
                        if (_cfg.PortalPath != null || (_cfg.VaultPath != null && _cfg.Rules.Count == 0) || _cfg.Rules.Any(r => r.Matches(f))
                            || (others != null && !others.Any(c => c.Rules.Any(r => r.Matches(f)))))
                            found.Add(f);
                    }
                }
                catch { /* unreadable folder: skip */ }
            }
            // Pins that point outside the desktop folders (e.g. dragged out of a portal) are added by reference.
            foreach (var p in pinnedHere)
            {
                if (found.Any(f => f.FullName.Equals(p, StringComparison.OrdinalIgnoreCase))) continue;
                if (File.Exists(p)) found.Add(new FileInfo(p));
                else if (Directory.Exists(p)) found.Add(new DirectoryInfo(p));
            }

            var query = SearchBox.Visibility == Visibility.Visible ? SearchBox.Text.Trim() : "";
            _items.Clear();
            foreach (var f in found.Where(f => query.Length == 0 || f.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
                                   .OrderBy(f => _cfg.VaultPath != null ? 0 : 1)
                                   .ThenBy(f => _cfg.VaultPath != null ? -f.LastWriteTimeUtc.Ticks : 0)
                                   .ThenBy(f => f.Name, StringComparer.OrdinalIgnoreCase))
                _items.Add(new FenceItem { Name = System.IO.Path.GetFileNameWithoutExtension(f.Name) is { Length: > 0 } n && f is FileInfo ? n : f.Name, Path = f.FullName, Icon = Icons.For(f.FullName), IconSize = _cfg.IconSize });
        }

        /// <summary>Paint the pad from its style fields. <paramref name="smooth"/> fades solid colour changes over TransitionMs.</summary>
        void ApplyLook(bool smooth = false)
        {
            byte alpha = (byte)(Math.Clamp(_cfg.Opacity, 0.1, 1) * 255);
            var c1 = ColorMath.Parse(_cfg.Color, Color.FromRgb(0x20, 0x20, 0x30));
            var a1 = Color.FromArgb(alpha, c1.R, c1.G, c1.B);

            Root.CornerRadius = new CornerRadius(Math.Max(0, _cfg.CornerRadius));
            Root.BorderThickness = new Thickness(Math.Max(0, _cfg.BorderWidth));
            Root.BorderBrush = BorderBrushFor();

            Root.Background?.BeginAnimation(Brush.OpacityProperty, null);
            if (_cfg.Color2 == null)
            {
                if (smooth && _cfg.TransitionMs > 0 && Root.Background is SolidColorBrush cur && !cur.IsFrozen)
                    cur.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(a1, TimeSpan.FromMilliseconds(_cfg.TransitionMs)));
                else
                    Root.Background = new SolidColorBrush(a1);
            }
            else
            {
                var c2 = ColorMath.Parse(_cfg.Color2, c1);
                double rad = _cfg.GradientAngle * Math.PI / 180;
                var g = new LinearGradientBrush
                {
                    StartPoint = new Point(0.5 - Math.Cos(rad) / 2, 0.5 - Math.Sin(rad) / 2),
                    EndPoint = new Point(0.5 + Math.Cos(rad) / 2, 0.5 + Math.Sin(rad) / 2),
                };
                g.GradientStops.Add(new GradientStop(a1, 0));
                g.GradientStops.Add(new GradientStop(Color.FromArgb(alpha, c2.R, c2.G, c2.B), 1));
                if (_cfg.Anim == "Drift")
                {
                    var rot = new RotateTransform(0, 0.5, 0.5);
                    g.RelativeTransform = rot;
                    rot.BeginAnimation(RotateTransform.AngleProperty,
                        new DoubleAnimation(0, 360, TimeSpan.FromSeconds(Math.Max(2, _cfg.AnimSeconds))) { RepeatBehavior = RepeatBehavior.Forever });
                }
                Root.Background = g;
            }

            if (_cfg.Anim == "Breathe")
                Root.Background.BeginAnimation(Brush.OpacityProperty,
                    new DoubleAnimation(1, 0.55, TimeSpan.FromSeconds(Math.Max(2, _cfg.AnimSeconds) / 2))
                    { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut } });

            double inner = Math.Max(0, _cfg.CornerRadius - 1);
            Header.CornerRadius = HeaderCorners(inner);
        }

        Brush BorderBrushFor() => _cfg.BorderColor == null
            ? new SolidColorBrush(Color.FromArgb(0x40, 255, 255, 255))
            : new SolidColorBrush(ColorMath.Parse(_cfg.BorderColor, Colors.White));

        int _headerSide;
        CornerRadius HeaderCorners(double r) => _headerSide switch
        {
            0 => new CornerRadius(r, r, 0, 0), 1 => new CornerRadius(0, 0, r, r),
            2 => new CornerRadius(r, 0, 0, r), _ => new CornerRadius(0, r, r, 0),
        };

        /// <summary>Peek: float above app windows, or drop back to the desktop layer.</summary>
        public void SetPeek(bool on)
        {
            _peeking = on;
            UpdateLayer();
        }

        bool _peeking;

        /// <summary>Fences always sit on the desktop layer, docked or not; only a peek raises them (temporarily).</summary>
        void UpdateLayer()
        {
            ArrangeHeader();
            Topmost = _peeking;
            if (!_peeking) Native.SitOnDesktop(new WindowInteropHelper(this).Handle);
        }

        void Style_Click(object s, RoutedEventArgs e)
        {
            if (StyleDialog.Edit(_cfg, smooth => ApplyLook(smooth), new Rect(Left, Top, ActualWidth, ActualHeight)))
                _save();
        }

        void IconSize_Click(object s, RoutedEventArgs e)
        {
            _cfg.IconSize = int.Parse((string)((MenuItem)s).Tag);
            Refresh(); _save();
        }

        void Rules_Click(object s, RoutedEventArgs e)
        {
            var text = InputDialog.Ask("Edit rules",
                "Rules (any match wins), e.g.  ext:pdf,docx; name:invoice; regex:^IMG; days:3\n" +
                "Or a script: expr: ext in (png,jpg) and size > 5mb and not name ~ draft\n" +
                "Fields: name ext path size modified created dir file hidden   Ops: = != < > <= >= ~ ~~ in   and/or/not ( )",
                RuleText.Format(_cfg.Rules), this);
            if (text == null) return;
            var rules = RuleText.Parse(text);
            foreach (var r in rules.Where(r => r.Kind == RuleKind.Expression))
            {
                var err = ExprRule.Validate(r.Value);
                if (err != null) { MessageBox.Show($"Rule \"{r.Value}\": {err}", "Paddock"); return; }
            }
            _cfg.Rules = rules;
            Refresh(); _save();
        }

        // ---- Header search: filters this fence's items as you type; Esc or leaving the box (when empty) closes it ----
        void OpenSearch()
        {
            if (_cfg.RolledUp) ToggleRollUp();
            SearchBox.Visibility = Visibility.Visible;
            BuildTabs();
            Activate();
            SearchBox.Focus();
            Keyboard.Focus(SearchBox);
        }

        void CloseSearch()
        {
            SearchBox.Text = "";
            SearchBox.Visibility = Visibility.Collapsed;
            BuildTabs();
            Refresh();
        }

        void SearchButton_MouseLeftButtonDown(object s, MouseButtonEventArgs e)
        {
            e.Handled = true; // don't start a window drag
            if (SearchBox.Visibility == Visibility.Visible) CloseSearch(); else OpenSearch();
        }

        void SearchBox_TextChanged(object s, TextChangedEventArgs e) { if (IsLoaded) Refresh(); }

        void SearchBox_PreviewKeyDown(object s, KeyEventArgs e)
        {
            if (e.Key == Key.Escape) { CloseSearch(); e.Handled = true; }
        }

        void SearchBox_LostFocus(object s, KeyboardFocusChangedEventArgs e)
        {
            if (SearchBox.Text.Length == 0 && SearchBox.Visibility == Visibility.Visible) CloseSearch();
        }

        void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.OriginalSource is DependencyObject d && SearchBox.IsAncestorOf(d)) return;
            if (e.ClickCount == 2) { ToggleRollUp(); return; }
            _dragging = true;
            try { DragMove(); } catch { /* button released early */ }
            finally { _dragging = false; }
            AfterMove();
        }

        // ---- Keeping fences on screen, and docking them to screen edges ----
        double Scale => VisualTreeHelper.GetDpi(this).DpiScaleX;

        static Rect Inflate(Rect r, double d) => new(r.X - d, r.Y - d, r.Width + 2 * d, r.Height + 2 * d);

        /// <summary>Expanded rectangle of a docked fence, hugging its edge and kept inside the work area.</summary>
        Rect DockedRect(Rect area, bool store)
        {
            double w = Math.Min(_cfg.Width, area.Width), h = Math.Min(_cfg.Height, area.Height);
            double x = Math.Clamp(_cfg.X, area.Left, area.Right - w), y = Math.Clamp(_cfg.Y, area.Top, area.Bottom - h);
            switch (_cfg.Dock)
            {
                case DockEdge.Left: x = area.Left; break;
                case DockEdge.Right: x = area.Right - w; break;
                case DockEdge.Top: y = area.Top; break;
                case DockEdge.Bottom: y = area.Bottom - h; break;
            }
            var r = new Rect(x, y, w, h);
            if (store) { _cfg.X = x; _cfg.Y = y; _cfg.Width = w; _cfg.Height = h; _expandedHeight = h; }
            return r;
        }

        Rect StripRect(Rect expanded) => _cfg.Dock switch
        {
            DockEdge.Left => new Rect(expanded.Left, expanded.Top, StripSize, expanded.Height),
            DockEdge.Right => new Rect(expanded.Right - StripSize, expanded.Top, StripSize, expanded.Height),
            DockEdge.Top => new Rect(expanded.Left, expanded.Top, expanded.Width, StripSize),
            _ => new Rect(expanded.Left, expanded.Bottom - StripSize, expanded.Width, StripSize),
        };

        Rect DockArea() => Screens.WorkArea(Screens.NearestTo(new Rect(_cfg.X, _cfg.Y, _cfg.Width, _cfg.Height), Scale), Scale);

        /// <summary>The header stays visible even when the pad is collapsed to its edge; only the contents hide.</summary>
        void ShowContent(bool on)
        {
            var v = on ? Visibility.Visible : Visibility.Collapsed;
            Body.Visibility = v; Grip.Visibility = v;
            SyncNoteVisibility();
        }

        /// <summary>
        /// Puts the header on the side opposite the docked edge (docked Left: header on the right, and so on),
        /// rotating it to read along a vertical side. Undocked pads keep it on top.
        /// </summary>
        void ArrangeHeader()
        {
            // 0 top, 1 bottom, 2 left, 3 right
            int side = _cfg.Dock switch { DockEdge.Left => 3, DockEdge.Right => 2, DockEdge.Top => 1, _ => 0 };
            var hdr = new GridLength(HeaderHeight);
            var rest = new GridLength(1, GridUnitType.Star);
            Layout.RowDefinitions.Clear(); Layout.ColumnDefinitions.Clear();
            int hRow = 0, hCol = 0, bRow = 0, bCol = 0;
            switch (side)
            {
                case 0: Layout.RowDefinitions.Add(new RowDefinition { Height = hdr }); Layout.RowDefinitions.Add(new RowDefinition { Height = rest }); bRow = 1; break;
                case 1: Layout.RowDefinitions.Add(new RowDefinition { Height = rest }); Layout.RowDefinitions.Add(new RowDefinition { Height = hdr }); hRow = 1; break;
                case 2: Layout.ColumnDefinitions.Add(new ColumnDefinition { Width = hdr }); Layout.ColumnDefinitions.Add(new ColumnDefinition { Width = rest }); bCol = 1; break;
                default: Layout.ColumnDefinitions.Add(new ColumnDefinition { Width = rest }); Layout.ColumnDefinitions.Add(new ColumnDefinition { Width = hdr }); hCol = 1; break;
            }
            Grid.SetRow(Header, hRow); Grid.SetColumn(Header, hCol);
            Grid.SetRow(Body, bRow); Grid.SetColumn(Body, bCol);
            Grid.SetRow(Grip, 0); Grid.SetColumn(Grip, 0); // the handles ring the whole pad, header included
            Grid.SetRow(NoteBox, bRow); Grid.SetColumn(NoteBox, bCol);
            _headerSide = side;
            Header.CornerRadius = HeaderCorners(Math.Max(0, _cfg.CornerRadius - 1));
            HeaderInner.LayoutTransform = side switch
            {
                2 => new RotateTransform(-90), 3 => new RotateTransform(90), _ => Transform.Identity,
            };
        }

        void MoveTo(Rect to, bool animate, Action? done = null)
        {
            int gen = ++_animGen;
            foreach (var p in new[] { LeftProperty, TopProperty, WidthProperty, HeightProperty }) BeginAnimation(p, null);
            var from = new Rect(Left, Top, Width, Height);
            Left = to.Left; Top = to.Top; Width = to.Width; Height = to.Height;
            if (!animate || !IsLoaded || _cfg.TransitionMs <= 0) { done?.Invoke(); return; }

            void Anim(DependencyProperty p, double f, double t, bool last)
            {
                if (Math.Abs(f - t) < 0.5) return;
                var a = new DoubleAnimation(f, t, TimeSpan.FromMilliseconds(_cfg.TransitionMs)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
                if (last)
                    a.Completed += (_, _) =>
                    {
                        if (gen != _animGen) return;
                        foreach (var q in new[] { LeftProperty, TopProperty, WidthProperty, HeightProperty }) BeginAnimation(q, null);
                        done?.Invoke();
                    };
                BeginAnimation(p, a);
            }
            // Completed handler rides on whichever property actually changes most.
            var deltas = new[] { Math.Abs(from.Left - to.Left), Math.Abs(from.Top - to.Top), Math.Abs(from.Width - to.Width), Math.Abs(from.Height - to.Height) };
            int lastIdx = Array.IndexOf(deltas, deltas.Max());
            if (deltas[lastIdx] < 0.5) { done?.Invoke(); return; }
            Anim(LeftProperty, from.Left, to.Left, lastIdx == 0);
            Anim(TopProperty, from.Top, to.Top, lastIdx == 1);
            Anim(WidthProperty, from.Width, to.Width, lastIdx == 2);
            Anim(HeightProperty, from.Height, to.Height, lastIdx == 3);
        }

        void EnterDock(DockEdge edge, bool collapsed, bool animate)
        {
            _cfg.Dock = edge;
            UpdateLayer();
            if (_cfg.RolledUp) { _cfg.RolledUp = false; }
            _collapsed = collapsed;
            _leaveSince = _enterSince = null;
            var expanded = DockedRect(DockArea(), store: true);
            if (collapsed)
                MoveTo(StripRect(expanded), animate, () => { if (_collapsed) ShowContent(false); });
            else
            {
                ShowContent(true);
                MoveTo(expanded, animate);
            }
        }

        public void FitToScreens()
        {
            if (!IsInitialized) return;
            if (_cfg.Dock != DockEdge.None) { EnterDock(_cfg.Dock, collapsed: true, animate: false); return; }

            double s = Scale;
            var area = Screens.WorkArea(Screens.NearestTo(new Rect(Left, Top, Width, Height), s), s);
            double w = Math.Min(Width, area.Width);
            _expandedHeight = Math.Min(_expandedHeight, area.Height);
            double h = _cfg.RolledUp ? HeaderHeight : _expandedHeight;
            Width = w; Height = h;
            Left = Math.Clamp(Left, area.Left, area.Right - w);
            Top = Math.Clamp(Top, area.Top, area.Bottom - h);
            CaptureBounds();
        }

        /// <summary>After a header drag: dock if dropped near an exposed screen edge, else undock; always stay on screen.</summary>
        void AfterMove()
        {
            if (TryMergeIntoTarget()) return; // dropped on another pad's header: now a tab there
            double s = Scale;
            var rect = new Rect(Left, Top, Width, Height);
            var scr = Screens.NearestTo(rect, s);
            // Moving is free while you drag; on release the pad settles onto a nearby pad or screen edge (within 7%).
            var snapped = Snapper.Move(this, rect, Screens.WorkArea(scr, s));
            Left = snapped.X; Top = snapped.Y;
            rect = new Rect(Left, Top, Width, Height);
            var edge = Screens.DetectEdge(rect, Screens.WorkArea(scr, s), scr, s);
            if (edge != DockEdge.None)
            {
                _cfg.X = Left; _cfg.Y = Top; _cfg.Width = Width;
                _cfg.Height = _cfg.RolledUp ? _expandedHeight : Height;
                EnterDock(edge, collapsed: false, animate: true); // cursor is still over it, so it stays open until you leave
            }
            else
            {
                _cfg.Dock = DockEdge.None;
                _collapsed = false;
                UpdateLayer();
                FitToScreens();
            }
            _save();
        }

        void Dock_Click(object s, RoutedEventArgs e)
        {
            var edge = Enum.Parse<DockEdge>((string)((MenuItem)s).Tag);
            if (edge == DockEdge.None)
            {
                _cfg.Dock = DockEdge.None; _collapsed = false;
                UpdateLayer();
                ShowContent(true); ApplyRollUp(); FitToScreens();
            }
            else
            {
                if (_cfg.Dock == DockEdge.None) CaptureBounds();
                EnterDock(edge, collapsed: false, animate: true);
            }
            _save();
        }

        bool Busy() =>
            _dragging || Header.ContextMenu is { IsOpen: true } || OwnedWindows.Count > 0 || IsMouseCaptured ||
            (SearchBox.Visibility == Visibility.Visible && (SearchBox.Text.Length > 0 || SearchBox.IsKeyboardFocused));

        void HoverTick()
        {
            if (_cfg.Dock == DockEdge.None || !IsVisible) return;
            double s = Scale;
            var mp = WF.Control.MousePosition;
            var cur = new Point(mp.X / s, mp.Y / s);
            var expanded = DockedRect(DockArea(), store: false);

            if (_collapsed)
            {
                _leaveSince = null;
                if (Inflate(StripRect(expanded), 2).Contains(cur))
                {
                    _enterSince ??= DateTime.UtcNow;
                    if ((DateTime.UtcNow - _enterSince.Value).TotalMilliseconds >= EnterDelayMs) EnterDock(_cfg.Dock, collapsed: false, animate: true);
                }
                else _enterSince = null;
            }
            else
            {
                _enterSince = null;
                if (Inflate(expanded, 10).Contains(cur) || Busy()) { _leaveSince = null; return; }
                _leaveSince ??= DateTime.UtcNow;
                if ((DateTime.UtcNow - _leaveSince.Value).TotalMilliseconds >= LeaveDelayMs) EnterDock(_cfg.Dock, collapsed: true, animate: true);
            }
        }

        void Item_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            // Remember where the press began; Item_MouseMove turns a long enough drag into a pad-to-pad drag.
            _dragOrigin = e.GetPosition(this);
            _dragItem = (sender as FrameworkElement)?.DataContext as FenceItem;
            if (e.ClickCount != 2 || sender is not FrameworkElement { DataContext: FenceItem item }) return;
            OpenItem(_cfg, item.Path);
        }

        /// <summary>Open a pad item: vault notes go to Obsidian, everything else to its default app.</summary>
        public static void OpenItem(FenceConfig cfg, string path)
        {
            try
            {
                var target = path;
                if (cfg.VaultPath != null)
                {
                    // Open the note in Obsidian rather than whatever owns .md.
                    var vault = System.IO.Path.GetFileName(cfg.VaultPath.TrimEnd('\\', '/'));
                    var rel = System.IO.Path.GetRelativePath(cfg.VaultPath, path).Replace('\\', '/');
                    target = $"obsidian://open?vault={Uri.EscapeDataString(vault)}&file={Uri.EscapeDataString(rel)}";
                }
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(target) { UseShellExecute = true });
            }
            catch (Exception ex) { MessageBox.Show(ex.Message, "Paddock"); }
        }

        /// <summary>What this pad is showing right now (the active tab), for the command palette.</summary>
        public IEnumerable<PaletteEntry> PaletteItems() =>
            _items.Select(i => new PaletteEntry { Title = i.Name, Subtitle = _cfg.Title + "  -  " + i.Path, Icon = i.Icon, Path = i.Path, Pad = _cfg });

        // Resizing from any side or corner. The mouse is read in screen space (not the thumb's own deltas),
        // because dragging a left/top edge moves the window and the thumb with it.
        Rect _edgeStart;
        System.Drawing.Point _edgeMouse;

        static string EdgeOf(object sender) => (string)((FrameworkElement)sender).Tag;

        void Edge_DragStarted(object sender, DragStartedEventArgs e)
        {
            _edgeStart = new Rect(Left, Top, Width, Height);
            _edgeMouse = WF.Control.MousePosition;
        }

        void Edge_DragDelta(object sender, DragDeltaEventArgs e)
        {
            // Resizing is free while you drag; snapping happens when you let go (Edge_DragCompleted).
            var edge = EdgeOf(sender);
            double s = Scale, minW = 120, minH = HeaderHeight + 40;
            var mp = WF.Control.MousePosition;
            double dx = (mp.X - _edgeMouse.X) / s, dy = (mp.Y - _edgeMouse.Y) / s;
            double l = _edgeStart.Left, t = _edgeStart.Top, w = _edgeStart.Width, h = _edgeStart.Height;
            if (edge.Contains('R')) w = Math.Max(minW, w + dx);
            if (edge.Contains('B')) h = Math.Max(minH, h + dy);
            if (edge.Contains('L')) { var nw = Math.Max(minW, w - dx); l += w - nw; w = nw; }
            if (edge.Contains('T')) { var nh = Math.Max(minH, h - dy); t += h - nh; h = nh; }
            Left = l; Top = t; Width = w; Height = h;
        }

        void Edge_DragCompleted(object sender, DragCompletedEventArgs e)
        {
            var edge = EdgeOf(sender);
            // Snapping only moves the right/bottom edge, so skip it when a left/top edge was dragged.
            if (!edge.Contains('L') && !edge.Contains('T'))
            {
                double s = Scale;
                var r = new Rect(Left, Top, Width, Height);
                var sz = Snapper.Resize(this, r, Screens.WorkArea(Screens.NearestTo(r, s), s));
                Width = Math.Max(120, sz.Width); Height = Math.Max(HeaderHeight + 40, sz.Height);
            }
            _expandedHeight = Height;
            CaptureBounds();
            _save();
        }

        void CaptureBounds()
        {
            _cfg.X = Left; _cfg.Y = Top; _cfg.Width = Width;
            _cfg.Height = _cfg.RolledUp ? _expandedHeight : Height;
        }

        void ToggleRollUp()
        {
            if (_cfg.Dock != DockEdge.None) return; // docked fences collapse to the edge instead
            if (!_cfg.RolledUp) _expandedHeight = Height;
            _cfg.RolledUp = !_cfg.RolledUp;
            ApplyRollUp();
            CaptureBounds();
            _save();
        }

        void ApplyRollUp()
        {
            Body.Visibility = _cfg.RolledUp ? Visibility.Collapsed : Visibility.Visible;
            Grip.Visibility = Body.Visibility;
            SyncNoteVisibility();
            Height = _cfg.RolledUp ? HeaderHeight : _expandedHeight;
        }

        // ---- Auto-archive: files only, never folders; moved (not deleted) and journaled for undo ----
        public List<FileInfo> StaleFiles(int days)
        {
            var list = new List<FileInfo>();
            if (days <= 0 || _cfg.VaultPath != null) return list;
            foreach (var dir in SourceFolders().Where(Directory.Exists))
            {
                try
                {
                    foreach (var f in new DirectoryInfo(dir).EnumerateFiles())
                    {
                        if (f.Name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)) continue;
                        if ((f.Attributes & FileAttributes.Hidden) != 0) continue;
                        if (_cfg.PortalPath == null && !_cfg.Rules.Any(r => r.Matches(f))) continue;
                        if ((DateTime.Now - f.LastWriteTime).TotalDays >= days) list.Add(f);
                    }
                }
                catch { }
            }
            return list;
        }

        /// <summary>Run the archive for this fence; returns how many files moved.</summary>
        public int RunArchive()
        {
            var n = Archiver.Move(_cfg.Title, StaleFiles(_cfg.ArchiveAfterDays));
            if (n > 0) Refresh();
            return n;
        }

        void ArchiveSet_Click(object s, RoutedEventArgs e)
        {
            if (_cfg.VaultPath != null) { MessageBox.Show("Vault pads only show notes; nothing is archived from them.", "Paddock"); return; }
            var text = InputDialog.Ask("Auto-archive",
                "Move files in this pad untouched for N days to Documents\\Paddock Archive.\nEnter days (0 = off). Checked at startup and every 6 hours. Undo from the tray.",
                _cfg.ArchiveAfterDays > 0 ? _cfg.ArchiveAfterDays.ToString() : "30", this);
            if (text == null) return;
            if (!int.TryParse(text.Trim(), out var days) || days < 0) { MessageBox.Show("Enter a whole number of days.", "Paddock"); return; }
            _cfg.ArchiveAfterDays = days;
            _save();
            if (days == 0) return;
            var count = StaleFiles(days).Count;
            if (count > 0 && MessageBox.Show($"{count} file(s) here already qualify. Archive them now?\n(If you say No they move at the next check.)",
                    "Paddock", MessageBoxButton.YesNo) == MessageBoxResult.Yes)
                RunArchive();
        }

        void ArchiveNow_Click(object s, RoutedEventArgs e)
        {
            if (_cfg.ArchiveAfterDays <= 0) { MessageBox.Show("Turn on Auto-archive for this pad first (sets the number of days).", "Paddock"); return; }
            var count = StaleFiles(_cfg.ArchiveAfterDays).Count;
            if (count == 0) { MessageBox.Show("Nothing is old enough yet.", "Paddock"); return; }
            if (MessageBox.Show($"Move {count} file(s) to the archive?", "Paddock", MessageBoxButton.YesNo) == MessageBoxResult.Yes)
                RunArchive();
        }

        void RollUp_Click(object s, RoutedEventArgs e) => ToggleRollUp();

        void Exclude_Click(object s, RoutedEventArgs e)
        {
            _cfg.ExcludeFromQuickHide = ExcludeItem.IsChecked;
            _save();
        }

        void Default_Click(object s, RoutedEventArgs e)
        {
            _cfg.IsDefault = DefaultItem.IsChecked;
            if (_cfg.IsDefault)
                foreach (var c in AllConfigs().Where(c => c != _cfg)) c.IsDefault = false; // only one default pad
            _save();
            DefaultChanged?.Invoke();
        }

        /// <summary>Re-sync the menu tick (another pad may have taken over as default).</summary>
        public void SyncDefaultTick() => DefaultItem.IsChecked = _cfg.IsDefault;

        void Rename_Click(object s, RoutedEventArgs e)
        {
            var name = InputDialog.Ask("Rename pad", "Name:", _cfg.Title, this);
            if (string.IsNullOrWhiteSpace(name)) return;
            _cfg.Title = name.Trim();
            TitleText.Text = _cfg.Title;
            BuildTabs();
            _save();
        }

        void Delete_Click(object s, RoutedEventArgs e)
        {
            if (MessageBox.Show($"Delete pad \"{_cfg.Title}\"? Your files are not touched.", "Paddock",
                    MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
            var tabs = GroupTabs();
            if (tabs.Count < 2) { _onDelete(this); return; }
            // Only this tab goes; the window stays with the others.
            var gone = _cfg;
            var next = tabs.First(t => t != gone);
            DeleteConfig?.Invoke(gone);
            if (tabs.Count == 2) next.TabGroup = null;
            BecomeActive(next);
            _save();
        }

        // ---- Drag icons between pads ----

        /// <summary>The pad an in-app icon drag started from (null for drops from Explorer).</summary>
        static FenceWindow? DragSource;
        Point _dragOrigin;
        FenceItem? _dragItem;

        /// <summary>Moves made by dropping onto a portal pad, newest last, for "Undo last pad drop".</summary>
        static readonly List<List<(string From, string To)>> DropLog = new();

        void Item_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
        {
            if (e.LeftButton != MouseButtonState.Pressed || _dragItem == null || _cfg.VaultPath != null) return;
            var d = e.GetPosition(this) - _dragOrigin;
            if (Math.Abs(d.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(d.Y) < SystemParameters.MinimumVerticalDragDistance) return;
            var item = _dragItem; _dragItem = null;
            if (!File.Exists(item.Path) && !Directory.Exists(item.Path)) return;
            DragSource = this;
            try
            {
                var data = new DataObject(DataFormats.FileDrop, new[] { item.Path });
                DragDrop.DoDragDrop((DependencyObject)sender, data, DragDropEffects.Move | DragDropEffects.Copy);
            }
            finally { DragSource = null; }
        }

        bool CanAccept(System.Windows.DragEventArgs e) =>
            _cfg.VaultPath == null && _cfg.NotePath == null && DragSource != this && e.Data.GetDataPresent(DataFormats.FileDrop);

        void Pad_DragEnter(object s, System.Windows.DragEventArgs e)
        {
            if (!CanAccept(e)) { e.Effects = DragDropEffects.None; e.Handled = true; return; }
            Root.BorderBrush = Brushes.White;
            e.Effects = DragDropEffects.Copy; e.Handled = true;
        }

        void Pad_DragOver(object s, System.Windows.DragEventArgs e)
        {
            e.Effects = !CanAccept(e) ? DragDropEffects.None
                : (DragSource != null && _cfg.PortalPath != null && (e.KeyStates & System.Windows.DragDropKeyStates.ControlKey) == 0)
                    ? DragDropEffects.Move : DragDropEffects.Copy;
            e.Handled = true;
        }

        void Pad_DragLeave(object s, System.Windows.DragEventArgs e) => Root.BorderBrush = BorderBrushFor();

        void Pad_Drop(object s, System.Windows.DragEventArgs e)
        {
            Root.BorderBrush = BorderBrushFor();
            e.Handled = true;
            if (!CanAccept(e) || e.Data.GetData(DataFormats.FileDrop) is not string[] paths) return;
            bool copy = DragSource == null || (e.KeyStates & System.Windows.DragDropKeyStates.ControlKey) != 0;

            if (_cfg.PortalPath != null)
            {
                // A portal is a real folder, so dropping really moves (or copies) the file into it.
                var moved = new List<(string From, string To)>();
                foreach (var p in paths)
                {
                    try { var to = PlaceInto(p, _cfg.PortalPath, copy); if (!copy && to != null) moved.Add((p, to)); }
                    catch (Exception ex) { MessageBox.Show($"Could not {(copy ? "copy" : "move")} \"{System.IO.Path.GetFileName(p)}\": {ex.Message}", "Paddock"); }
                }
                if (moved.Count > 0) DropLog.Add(moved);
            }
            else
            {
                // A desktop pad is a view, so dropping only pins a reference; the file stays where it is.
                foreach (var p in paths)
                {
                    foreach (var c in AllConfigs()) c.Pinned.RemoveAll(x => x.Equals(p, StringComparison.OrdinalIgnoreCase));
                    _cfg.Pinned.Add(p);
                }
            }
            _save();
            Refresh();
            DefaultChanged?.Invoke(); // every pad re-evaluates what it shows
        }

        /// <summary>Move or copy into a folder, picking "name (2)" if taken. Returns the new path.</summary>
        static string? PlaceInto(string src, string folder, bool copy)
        {
            bool isDir = Directory.Exists(src);
            var name = System.IO.Path.GetFileName(src.TrimEnd('\\', '/'));
            var full = System.IO.Path.GetFullPath(src).TrimEnd('\\', '/');
            var destFolder = System.IO.Path.GetFullPath(folder).TrimEnd('\\', '/');
            if (string.Equals(System.IO.Path.GetDirectoryName(full), destFolder, StringComparison.OrdinalIgnoreCase)) return null; // already there
            if (isDir && (destFolder + "\\").StartsWith(full + "\\", StringComparison.OrdinalIgnoreCase)) throw new IOException("a folder can't go inside itself");

            string Unique()
            {
                var stem = isDir ? name : System.IO.Path.GetFileNameWithoutExtension(name);
                var ext = isDir ? "" : System.IO.Path.GetExtension(name);
                var dest = System.IO.Path.Combine(destFolder, name);
                for (int i = 2; File.Exists(dest) || Directory.Exists(dest); i++)
                    dest = System.IO.Path.Combine(destFolder, $"{stem} ({i}){ext}");
                return dest;
            }
            var target = Unique();
            if (!isDir)
            {
                if (copy) File.Copy(src, target); else File.Move(src, target);
            }
            else if (copy) CopyDir(src, target);
            else
            {
                try { Directory.Move(src, target); }
                catch (IOException) { CopyDir(src, target); Directory.Delete(src, true); } // across volumes
            }
            return target;
        }

        static void CopyDir(string from, string to)
        {
            Directory.CreateDirectory(to);
            foreach (var f in Directory.GetFiles(from)) File.Copy(f, System.IO.Path.Combine(to, System.IO.Path.GetFileName(f)));
            foreach (var d in Directory.GetDirectories(from)) CopyDir(d, System.IO.Path.Combine(to, System.IO.Path.GetFileName(d)));
        }

        /// <summary>Put the files from the last portal drop back where they came from. Returns a message for the user.</summary>
        public static string UndoLastDrop()
        {
            if (DropLog.Count == 0) return "Nothing to undo.";
            var last = DropLog[^1]; DropLog.RemoveAt(DropLog.Count - 1);
            int ok = 0, skipped = 0;
            foreach (var (from, to) in last)
            {
                try
                {
                    if (File.Exists(from) || Directory.Exists(from)) { skipped++; continue; } // something is already at the old spot
                    if (Directory.Exists(to)) Directory.Move(to, from);
                    else if (File.Exists(to)) File.Move(to, from);
                    else { skipped++; continue; }
                    ok++;
                }
                catch { skipped++; }
            }
            DefaultChanged?.Invoke();
            return skipped == 0 ? $"Moved {ok} item(s) back." : $"Moved {ok} back; {skipped} could not be restored (moved again, or the old spot is taken).";
        }

        void Unpin_Click(object s, RoutedEventArgs e)
        {
            // The menu belongs to the item's StackPanel (its placement target), whose DataContext is the item.
            var target = ((s as MenuItem)?.Parent as ContextMenu)?.PlacementTarget as FrameworkElement;
            var item = target?.DataContext as FenceItem ?? (s as FrameworkElement)?.DataContext as FenceItem;
            if (item == null) return;
            if (_cfg.Pinned.RemoveAll(x => x.Equals(item.Path, StringComparison.OrdinalIgnoreCase)) == 0)
            {
                MessageBox.Show("This item isn't pinned here; it shows because of the rules.", "Paddock");
                return;
            }
            _save();
            Refresh();
            DefaultChanged?.Invoke();
        }
    }

    /// <summary>Tiny code-only prompt so the MVP needs no extra XAML.</summary>
    public static class InputDialog
    {
        public static string? Ask(string title, string label, string initial, Window? owner)
        {
            var box = new TextBox { Text = initial, Margin = new Thickness(0, 6, 0, 10), MinWidth = 260 };
            var ok = new Button { Content = "OK", IsDefault = true, Width = 70, Margin = new Thickness(0, 0, 6, 0) };
            var cancel = new Button { Content = "Cancel", IsCancel = true, Width = 70 };
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            buttons.Children.Add(ok); buttons.Children.Add(cancel);
            var panel = new StackPanel { Margin = new Thickness(14) };
            panel.Children.Add(new TextBlock { Text = label });
            panel.Children.Add(box); panel.Children.Add(buttons);
            var win = new Window
            {
                Title = title, Content = panel, SizeToContent = SizeToContent.WidthAndHeight,
                ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterScreen,
                Owner = owner, ShowInTaskbar = false
            };
            ok.Click += (_, _) => win.DialogResult = true;
            win.Loaded += (_, _) => { box.Focus(); box.SelectAll(); };
            return win.ShowDialog() == true ? box.Text : null;
        }
    }
}
