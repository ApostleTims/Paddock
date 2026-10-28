using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows;
using System.Windows.Interop;

namespace Paddock
{
    public partial class App : Application, IPaddockHost
    {
        LayoutFile _layout = new();
        readonly List<FenceWindow> _windows = new();
        System.Windows.Forms.NotifyIcon? _tray;
        bool _hidden;
        bool _peek;
        System.Windows.Forms.ToolStripMenuItem? _hideIconsItem;
        System.Windows.Forms.ToolStripMenuItem? _profilesMenu;
        HwndSource? _hotkeySource;
        const int HotkeyPeek = 1, HotkeyNext = 2, HotkeyPrev = 3, HotkeyPalette = 4, HotkeyJumpBase = 10;

        /// <summary>Everything the command palette can search: items on all pads, then app commands.</summary>
        IEnumerable<PaletteEntry> PaletteSource()
        {
            foreach (var w in _windows) foreach (var e in w.PaletteItems()) yield return e;
            PaletteEntry Cmd(string title, string sub, Action run) => new() { Title = title, Subtitle = "Command  -  " + sub, Run = run };
            yield return Cmd("Quick-hide / show pads", "same as double-clicking the desktop", ToggleQuickHide);
            yield return Cmd("Peek pads", "Ctrl+Alt+Space", TogglePeek);
            yield return Cmd("New desktop pad", "tray menu", NewDesktopFence);
            yield return Cmd("New folder portal", "tray menu", NewPortal);
            yield return Cmd("New note pad", "tray menu", NewNotePad);
            yield return Cmd("Quick capture to note pad", "appends a timestamped line", QuickCapture);
            yield return Cmd("Next profile", "Ctrl+Alt+Right", () => CycleProfile(+1));
            yield return Cmd("Previous profile", "Ctrl+Alt+Left", () => CycleProfile(-1));
            foreach (var name in _layout.ProfileNames().Where(n => !n.Equals(_layout.ActiveProfile, StringComparison.OrdinalIgnoreCase)))
                yield return Cmd("Switch profile: " + name, "profile", () => SwitchProfile(name));
        }

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            _layout = ConfigStore.Load();
            FenceWindow.LeaveDelayMs = _layout.CollapseDelayMs;
            FenceWindow.AllConfigs = () => _layout.Fences;
            FenceWindow.DefaultChanged = () => { foreach (var w in _windows) { w.SyncDefaultTick(); w.Refresh(); } };
            FenceWindow.AllWindows = () => _windows;
            PaletteWindow.Source = PaletteSource;
            PaletteWindow.Open = e => { if (e.Pad != null && e.Path != null) FenceWindow.OpenItem(e.Pad, e.Path); };
            PaletteWindow.Reveal = e =>
            {
                if (e.Path == null) return;
                try { System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{e.Path}\""); } catch { }
            };
            FenceWindow.Merged = w => { _windows.Remove(w); w.Close(); Save(); }; // its pads live on as tabs of another window
            FenceWindow.SplitOut = Open;
            FenceWindow.DeleteConfig = c => { _layout.Fences.Remove(c); Save(); };
            AutoSnapshot("auto-startup", minGapMinutes: 30);
            OpenAll();

            // Double-click empty desktop = quick-hide / show.
            DesktopClick.Enabled = _layout.DoubleClickHide;
            DesktopClick.DoubleClicked = ToggleQuickHide;
            DesktopClick.Start();
            BuildTray();
            StartAutoProfileTimer();
            PluginLoader.LoadAll(this);
            Save(); // writes the seeded layout on first run

            // The real desktop icons must never stay hidden after Paddock dies.
            Native.SetDesktopIconsVisible(!_layout.HideDesktopIcons);
            DispatcherUnhandledException += (_, _) => Native.SetDesktopIconsVisible(true);
            AppDomain.CurrentDomain.ProcessExit += (_, _) => Native.SetDesktopIconsVisible(true);

            // Peek: Ctrl+Alt+Space raises every fence above app windows; press again to drop them back.
            _hotkeySource = new HwndSource(new HwndSourceParameters("PaddockHotkey") { ParentWindow = new IntPtr(-3) });
            _hotkeySource.AddHook(HotkeyHook);
            const uint mods = 0x1 | 0x2 | 0x4000; // Alt|Ctrl|NoRepeat
            Native.RegisterHotKey(_hotkeySource.Handle, HotkeyPeek, mods, 0x20); // Space
            Native.RegisterHotKey(_hotkeySource.Handle, HotkeyPalette, mods, 0x4B); // K: command palette

            // Profiles: Ctrl+Alt+Right / Left cycle, Ctrl+Alt+1..9 jump to the Nth profile (alphabetical).
            Native.RegisterHotKey(_hotkeySource.Handle, HotkeyNext, mods, 0x27);
            Native.RegisterHotKey(_hotkeySource.Handle, HotkeyPrev, mods, 0x25);
            for (int i = 0; i < 9; i++)
                Native.RegisterHotKey(_hotkeySource.Handle, HotkeyJumpBase + i, mods, (uint)(0x31 + i));
        }

        IntPtr HotkeyHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg != 0x0312) return IntPtr.Zero;
            int id = wParam.ToInt32();
            handled = true;
            if (id == HotkeyPeek) TogglePeek();
            else if (id == HotkeyPalette) PaletteWindow.Toggle();
            else if (id == HotkeyNext) CycleProfile(+1);
            else if (id == HotkeyPrev) CycleProfile(-1);
            else if (id >= HotkeyJumpBase && id < HotkeyJumpBase + 9)
            {
                var names = _layout.ProfileNames();
                if (id - HotkeyJumpBase < names.Count) SwitchProfile(names[id - HotkeyJumpBase]);
            }
            else handled = false;
            return IntPtr.Zero;
        }

        void TogglePeek()
        {
            _peek = !_peek;
            foreach (var w in _windows)
            {
                if (_peek && !w.IsVisible) w.Show();
                w.SetPeek(_peek);
            }
            if (!_peek && _hidden) foreach (var w in _windows.Where(w => !w.Config.ExcludeFromQuickHide)) w.Hide();
        }

        protected override void OnExit(ExitEventArgs e)
        {
            Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
            if (_hotkeySource != null)
            {
                foreach (var id in new[] { HotkeyPeek, HotkeyNext, HotkeyPrev, HotkeyPalette }.Concat(Enumerable.Range(HotkeyJumpBase, 9)))
                    Native.UnregisterHotKey(_hotkeySource.Handle, id);
                _hotkeySource.Dispose();
            }
            DesktopClick.Stop();
            Native.SetDesktopIconsVisible(true);
            _tray?.Dispose();
            base.OnExit(e);
        }

        /// <summary>One window per pad, except pads sharing a TabGroup: those are one window showing the active tab.</summary>
        void OpenAll()
        {
            foreach (var group in _layout.Fences.GroupBy(c => string.IsNullOrEmpty(c.TabGroup) ? c.Id : c.TabGroup))
            {
                var tabs = group.ToList();
                if (tabs.Count == 1) { tabs[0].TabGroup = null; tabs[0].TabActive = false; Open(tabs[0]); continue; }
                var active = tabs.FirstOrDefault(t => t.TabActive) ?? tabs[0];
                foreach (var t in tabs) t.TabActive = t == active;
                Open(active);
            }
        }

        void Open(FenceConfig cfg)
        {
            var w = new FenceWindow(cfg, Save, Remove);
            _windows.Add(w);
            if (!_hidden || cfg.ExcludeFromQuickHide) w.Show();
        }

        void Remove(FenceWindow w)
        {
            _layout.Fences.Remove(w.Config);
            _windows.Remove(w);
            w.Close();
            Save();
        }

        void Save() => ConfigStore.Save(_layout);

        /// <summary>The heart icon embedded in the exe, at the size the tray wants; stock icon if it can't be read.</summary>
        static System.Drawing.Icon LoadTrayIcon()
        {
            try
            {
                using var s = typeof(App).Assembly.GetManifestResourceStream("paddock.ico");
                if (s != null) return new System.Drawing.Icon(s, System.Windows.Forms.SystemInformation.SmallIconSize);
            }
            catch { }
            return SystemIcons.Application;
        }

        void BuildTray()
        {
            var menu = new System.Windows.Forms.ContextMenuStrip();
            _profilesMenu = new System.Windows.Forms.ToolStripMenuItem("Profiles");
            _profilesMenu.DropDownOpening += (_, _) => RebuildProfilesMenu();
            _profilesMenu.DropDownItems.Add("loading..."); // so the arrow shows; replaced on open
            menu.Items.Add(_profilesMenu);
            menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
            menu.Items.Add("Command palette (Ctrl+Alt+K)", null, (_, _) => PaletteWindow.Toggle());
            menu.Items.Add("New desktop pad", null, (_, _) => NewDesktopFence());
            menu.Items.Add("New folder portal...", null, (_, _) => NewPortal());
            menu.Items.Add("New vault pad...", null, (_, _) => NewVaultFence());
            menu.Items.Add("New note pad...", null, (_, _) => NewNotePad());
            menu.Items.Add("New note pad from file...", null, (_, _) => NewNotePadFromFile());
            menu.Items.Add("Quick capture to note pad...", null, (_, _) => QuickCapture());
            menu.Items.Add("Undo last archive / rule action", null, (_, _) => UndoArchive());
            menu.Items.Add("Undo last pad drop", null, (_, _) =>
                _tray?.ShowBalloonTip(2500, "Paddock", FenceWindow.UndoLastDrop(), System.Windows.Forms.ToolTipIcon.Info));
            menu.Items.Add("Open archive folder", null, (_, _) =>
            {
                System.IO.Directory.CreateDirectory(Archiver.Root);
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Archiver.Root) { UseShellExecute = true });
            });
            StartArchiveTimer();
            Microsoft.Win32.SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
            menu.Items.Add("Quick-hide / show", null, (_, _) => ToggleQuickHide());
            _hideIconsItem = new System.Windows.Forms.ToolStripMenuItem("Hide real desktop icons")
            {
                CheckOnClick = true,
                Checked = _layout.HideDesktopIcons
            };
            _hideIconsItem.Click += (_, _) =>
            {
                _layout.HideDesktopIcons = _hideIconsItem.Checked;
                Native.SetDesktopIconsVisible(!_layout.HideDesktopIcons);
                Save();
            };
            menu.Items.Add(_hideIconsItem);

            // Snap-on: statics read by FenceWindow while dragging/resizing.
            Snapper.Grid = _layout.GridSnap; Snapper.Fences = _layout.SnapToFences;
            Snapper.GridSize = _layout.GridSize;
            Snapper.Others = self => _windows
                .Where(w => w != self && w.IsVisible && w.Config.Dock == DockEdge.None)
                .Select(w => new System.Windows.Rect(w.Left, w.Top, w.Width, w.Height));
            var snapGrid = new System.Windows.Forms.ToolStripMenuItem("Snap to grid") { CheckOnClick = true, Checked = _layout.GridSnap };
            snapGrid.Click += (_, _) => { _layout.GridSnap = Snapper.Grid = snapGrid.Checked; Save(); };
            var snapFences = new System.Windows.Forms.ToolStripMenuItem("Snap to pads and screen edges") { CheckOnClick = true, Checked = _layout.SnapToFences };
            snapFences.Click += (_, _) => { _layout.SnapToFences = Snapper.Fences = snapFences.Checked; Save(); };
            menu.Items.Add(snapGrid);
            menu.Items.Add(snapFences);
            var dblHide = new System.Windows.Forms.ToolStripMenuItem("Double-click desktop to hide pads") { CheckOnClick = true, Checked = _layout.DoubleClickHide };
            dblHide.Click += (_, _) => { _layout.DoubleClickHide = DesktopClick.Enabled = dblHide.Checked; Save(); };
            menu.Items.Add(dblHide);

            // Collapse delay: how long a docked pad waits before folding back to its edge.
            var delayMenu = new System.Windows.Forms.ToolStripMenuItem("Collapse delay");
            foreach (var (label, ms) in new[] { ("Instant (0.1 s)", 100.0), ("Quick (0.45 s)", 450.0), ("Relaxed (1 s)", 1000.0), ("Slow (2 s)", 2000.0), ("Very slow (4 s)", 4000.0) })
            {
                var it = new System.Windows.Forms.ToolStripMenuItem(label) { Checked = Math.Abs(_layout.CollapseDelayMs - ms) < 1 };
                it.Click += (_, _) =>
                {
                    _layout.CollapseDelayMs = FenceWindow.LeaveDelayMs = ms;
                    foreach (System.Windows.Forms.ToolStripMenuItem o in delayMenu.DropDownItems) o.Checked = o == it;
                    Save();
                };
                delayMenu.DropDownItems.Add(it);
            }
            menu.Items.Add(delayMenu);

            var snapMenu = new System.Windows.Forms.ToolStripMenuItem("Layout snapshots");
            snapMenu.DropDownOpening += (_, _) => RebuildSnapshotMenu(snapMenu);
            snapMenu.DropDownItems.Add("loading..."); // so the arrow shows; replaced on open
            menu.Items.Add(snapMenu);

            menu.Items.Add("Open config folder", null, (_, _) =>
                System.Diagnostics.Process.Start("explorer.exe", ConfigStore.Dir));
            menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
            menu.Items.Add("Exit Paddock", null, (_, _) => Shutdown());

            _tray = new System.Windows.Forms.NotifyIcon
            {
                Icon = LoadTrayIcon(),
                Text = "Paddock",
                Visible = true,
                ContextMenuStrip = menu
            };
            _tray.DoubleClick += (_, _) => ToggleQuickHide();
        }

        void NewDesktopFence()
        {
            var title = InputDialog.Ask("New pad", "Name:", "New pad", null);
            if (string.IsNullOrWhiteSpace(title)) return;
            var ext = InputDialog.Ask("Rule", "File extensions to collect (e.g. zip,rar,7z):", "", null);
            var rules = string.IsNullOrWhiteSpace(ext)
                ? Array.Empty<RuleDef>()
                : new[] { new RuleDef { Kind = RuleKind.Extension, Value = ext } };
            AddDesktopFence(title.Trim(), rules);
        }

        void NewPortal()
        {
            using var dlg = new System.Windows.Forms.FolderBrowserDialog { Description = "Folder to show in a pad" };
            if (dlg.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
            AddPortal(System.IO.Path.GetFileName(dlg.SelectedPath.TrimEnd('\\')) is { Length: > 0 } n ? n : dlg.SelectedPath,
                dlg.SelectedPath);
        }

        // ---- Monitors plugged/unplugged, resolution or taskbar changes: pull every fence back on screen ----
        System.Windows.Threading.DispatcherTimer? _displayDebounce;

        void OnDisplaySettingsChanged(object? sender, EventArgs e)
        {
            // Raised on a background thread, often several times in a row.
            Dispatcher.BeginInvoke(() =>
            {
                // Before the refit moves anything, keep the layout as the user arranged it (restorable from the tray).
                if (_displayDebounce is not { IsEnabled: true }) AutoSnapshot("auto-before-display-change", minGapMinutes: 0);
                _displayDebounce ??= new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
                _displayDebounce.Tick -= RefitAll;
                _displayDebounce.Tick += RefitAll;
                _displayDebounce.Stop();
                _displayDebounce.Start();
            });
        }

        void RefitAll(object? sender, EventArgs e)
        {
            _displayDebounce?.Stop();
            foreach (var w in _windows) w.FitToScreens();
            Save();
        }

        // ---- Layout snapshots ----
        void AutoSnapshot(string label, int minGapMinutes)
        {
            try
            {
                var newest = ConfigStore.Snapshots().FirstOrDefault();
                if (minGapMinutes > 0 && newest.Path != null &&
                    (DateTime.Now - System.IO.File.GetLastWriteTime(newest.Path)).TotalMinutes < minGapMinutes) return;
                ConfigStore.Snapshot(_layout, label);
            }
            catch { /* a failed snapshot must never stop Paddock */ }
        }

        void RebuildSnapshotMenu(System.Windows.Forms.ToolStripMenuItem menu)
        {
            var items = menu.DropDownItems;
            items.Clear();
            items.Add("Save snapshot now...", null, (_, _) =>
            {
                var name = InputDialog.Ask("Save snapshot", "Name (optional):", "", null);
                if (name == null) return;
                ConfigStore.Snapshot(_layout, string.IsNullOrWhiteSpace(name) ? "manual" : name.Trim());
                _tray?.ShowBalloonTip(900, "Paddock", "Snapshot saved", System.Windows.Forms.ToolTipIcon.None);
            });
            items.Add(new System.Windows.Forms.ToolStripSeparator());
            var snaps = ConfigStore.Snapshots().Take(15).ToList();
            if (snaps.Count == 0) items.Add("(none yet)").Enabled = false;
            foreach (var (path, label) in snaps)
                items.Add(label, null, (_, _) => RestoreSnapshot(path));
            items.Add(new System.Windows.Forms.ToolStripSeparator());
            items.Add("Open snapshots folder", null, (_, _) =>
            {
                System.IO.Directory.CreateDirectory(ConfigStore.SnapDir);
                System.Diagnostics.Process.Start("explorer.exe", ConfigStore.SnapDir);
            });
        }

        /// <summary>Restore a snapshot. The current layout is snapshotted first, so a restore can itself be undone.</summary>
        void RestoreSnapshot(string path)
        {
            var snap = ConfigStore.LoadSnapshot(path);
            if (snap == null) { MessageBox.Show("That snapshot could not be read.", "Paddock"); return; }
            ConfigStore.Snapshot(_layout, "auto-before-restore");

            // App-wide settings stay as they are now; only pads, profiles and the active profile come from the snapshot.
            snap.HideDesktopIcons = _layout.HideDesktopIcons; snap.GridSnap = _layout.GridSnap;
            snap.SnapToFences = _layout.SnapToFences; snap.GridSize = _layout.GridSize;
            snap.CollapseDelayMs = _layout.CollapseDelayMs; snap.DoubleClickHide = _layout.DoubleClickHide;
            _layout = snap;

            foreach (var w in _windows.ToList()) w.Close();
            _windows.Clear();
            _peek = false;
            OpenAll();
            Save();
            _tray?.ShowBalloonTip(1200, "Paddock", "Snapshot restored. Tray > Layout snapshots > \"before restore\" undoes it.", System.Windows.Forms.ToolTipIcon.None);
        }

        // ---- Auto-archive: first check 20 s after start, then every 6 h ----
        System.Windows.Threading.DispatcherTimer? _archiveTimer;

        void StartArchiveTimer()
        {
            _archiveTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(20) };
            _archiveTimer.Tick += (_, _) =>
            {
                _archiveTimer!.Interval = TimeSpan.FromHours(6);
                int moved = 0;
                foreach (var w in _windows.ToList())
                    if (w.Config.ArchiveAfterDays > 0) moved += w.RunArchive();
                if (moved > 0)
                    _tray?.ShowBalloonTip(2500, "Paddock", $"Archived {moved} old file(s). Tray > Undo last archive to put them back.",
                        System.Windows.Forms.ToolTipIcon.None);
            };
            _archiveTimer.Start();

            // Automatic rule actions: every 5 minutes, only files untouched for 60 s (so half-downloaded files are left alone).
            var actionTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMinutes(5) };
            actionTimer.Tick += (_, _) =>
            {
                int n = 0;
                foreach (var w in _windows.ToList())
                    if (w.Config.ActionAuto) n += w.RunAction(60);
                if (n > 0)
                    _tray?.ShowBalloonTip(2500, "Paddock", $"Rule action handled {n} file(s). Tray > Undo last archive / rule action puts them back.",
                        System.Windows.Forms.ToolTipIcon.None);
            };
            actionTimer.Start();
            _actionTimer = actionTimer;
        }

        System.Windows.Threading.DispatcherTimer? _actionTimer;

        void UndoArchive()
        {
            var (ok, skipped) = Archiver.UndoLast();
            if (ok + skipped == 0) { MessageBox.Show("Nothing to undo.", "Paddock"); return; }
            foreach (var w in _windows) w.Refresh();
            MessageBox.Show($"Restored {ok} file(s)." + (skipped > 0 ? $" {skipped} could not be restored (missing or in use)." : ""), "Paddock");
        }

        // ---- Profiles ----
        public string ActiveProfile => _layout.ActiveProfile;

        public void SwitchProfile(string name)
        {
            if (name.Equals(_layout.ActiveProfile, StringComparison.OrdinalIgnoreCase)) return;
            var key = _layout.Profiles.Keys.FirstOrDefault(k => k.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (key == null) return;

            // Park the current fences under their profile name and take the target's out of storage.
            _layout.Profiles[_layout.ActiveProfile] = _layout.Fences;
            _layout.Fences = _layout.Profiles[key];
            _layout.Profiles.Remove(key);
            _layout.ActiveProfile = key;

            foreach (var w in _windows.ToList()) w.Close();
            _windows.Clear();
            _peek = false;
            OpenAll();
            Save();
            _tray?.ShowBalloonTip(900, "Paddock", $"Profile: {key}", System.Windows.Forms.ToolTipIcon.None);
        }

        void CycleProfile(int step)
        {
            var names = _layout.ProfileNames();
            if (names.Count < 2) return;
            int i = names.FindIndex(n => n.Equals(_layout.ActiveProfile, StringComparison.OrdinalIgnoreCase));
            SwitchProfile(names[(i + step + names.Count) % names.Count]);
        }

        void RebuildProfilesMenu()
        {
            var items = _profilesMenu!.DropDownItems;
            items.Clear();
            var names = _layout.ProfileNames();
            for (int i = 0; i < names.Count; i++)
            {
                var name = names[i];
                var label = i < 9 ? $"{name}   (Ctrl+Alt+{i + 1})" : name;
                var item = new System.Windows.Forms.ToolStripMenuItem(label)
                    { Checked = name.Equals(_layout.ActiveProfile, StringComparison.OrdinalIgnoreCase) };
                item.Click += (_, _) => SwitchProfile(name);
                items.Add(item);
            }
            items.Add(new System.Windows.Forms.ToolStripSeparator());
            items.Add("New profile (copy of current)...", null, (_, _) => NewProfile(copy: true));
            items.Add("New empty profile...", null, (_, _) => NewProfile(copy: false));
            items.Add("Auto-switch rules...", null, (_, _) => EditAutoProfiles());
            items.Add("Rename current...", null, (_, _) => RenameProfile());
            items.Add("Delete current...", null, (_, _) => DeleteProfile());
        }

        // ---- Auto-switch: a rule's profile becomes active when its condition starts to hold ----
        System.Windows.Threading.DispatcherTimer? _autoTimer;
        string? _lastAutoMatch;

        void StartAutoProfileTimer()
        {
            _autoTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            _autoTimer.Tick += (_, _) => AutoProfileTick();
            _autoTimer.Start();
        }

        /// <summary>
        /// Edge-triggered: acts only when the matching rule changes, so a manual switch sticks until the
        /// situation changes (another app comes forward, the time window starts, a monitor is plugged in).
        /// </summary>
        void AutoProfileTick()
        {
            if (string.IsNullOrWhiteSpace(_layout.AutoProfiles)) { _lastAutoMatch = null; return; }
            var rules = AutoProfileText.Parse(_layout.AutoProfiles, out _);
            var app = ForegroundApp.Name();
            var screens = System.Windows.Forms.Screen.AllScreens.Length;
            var hit = rules.FirstOrDefault(r => r.Matches(DateTime.Now, app, screens));
            // Focusing something no rule mentions (or Paddock itself) leaves things as they are.
            var key = hit == null ? null : $"{hit.Profile}|{hit.Kind}:{hit.Value}";
            if (key == _lastAutoMatch || hit == null) { if (hit == null && app != null) _lastAutoMatch = null; return; }
            _lastAutoMatch = key;
            if (NameTaken(hit.Profile)) SwitchProfile(hit.Profile);
        }

        void EditAutoProfiles()
        {
            var text = InputDialog.Ask("Auto-switch profiles",
                "One rule per ';', first match wins:  Profile = kind:value\n" +
                "app:code,devenv   |   time:09:00-17:00   |   day:sat,sun   |   monitors:2\n" +
                "Example:  Work = app:code ; Night = time:22:00-06:00 ; Docked = monitors:2\n" +
                "Leave empty to switch only by hotkey/menu.",
                _layout.AutoProfiles, null);
            if (text == null) return;
            var rules = AutoProfileText.Parse(text, out var errors);
            errors.AddRange(rules.Where(r => !NameTaken(r.Profile)).Select(r => $"No profile named \"{r.Profile}\""));
            if (errors.Count > 0) { MessageBox.Show(string.Join("\n", errors), "Paddock"); return; }
            _layout.AutoProfiles = text.Trim();
            _lastAutoMatch = null;
            Save();
        }

        bool NameTaken(string name) =>
            _layout.ProfileNames().Any(n => n.Equals(name, StringComparison.OrdinalIgnoreCase));

        void NewProfile(bool copy)
        {
            var name = InputDialog.Ask("New profile", "Profile name (e.g. a project):", "", null)?.Trim();
            if (string.IsNullOrEmpty(name)) return;
            if (NameTaken(name)) { MessageBox.Show($"A profile named \"{name}\" already exists.", "Paddock"); return; }

            List<FenceConfig> fences = new();
            if (copy)
            {
                // Deep copy through JSON, with fresh ids, so the two profiles never share fence objects.
                fences = System.Text.Json.JsonSerializer.Deserialize<List<FenceConfig>>(
                    System.Text.Json.JsonSerializer.Serialize(_layout.Fences)) ?? new();
                foreach (var f in fences) f.Id = Guid.NewGuid().ToString("N");
            }
            _layout.Profiles[name] = fences;
            SwitchProfile(name);
        }

        void RenameProfile()
        {
            var name = InputDialog.Ask("Rename profile", "New name:", _layout.ActiveProfile, null)?.Trim();
            if (string.IsNullOrEmpty(name) || name == _layout.ActiveProfile) return;
            if (NameTaken(name)) { MessageBox.Show($"A profile named \"{name}\" already exists.", "Paddock"); return; }
            _layout.ActiveProfile = name;
            Save();
        }

        void DeleteProfile()
        {
            if (_layout.Profiles.Count == 0) { MessageBox.Show("This is your only profile.", "Paddock"); return; }
            if (MessageBox.Show($"Delete profile \"{_layout.ActiveProfile}\" and its pads? Your files are not touched.",
                    "Paddock", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
            var old = _layout.ActiveProfile;
            SwitchProfile(_layout.ProfileNames().First(n => n != old));
            _layout.Profiles.Remove(old);
            Save();
        }

        // ---- IPaddockHost ----
        public FenceConfig AddDesktopFence(string title, params RuleDef[] rules)
        {
            var cfg = new FenceConfig { Title = title, Rules = rules.ToList(), X = 120, Y = 120 };
            _layout.Fences.Add(cfg);
            Open(cfg);
            Save();
            return cfg;
        }

        public FenceConfig AddVaultFence(string title, string vaultFolder, string expr)
        {
            var rules = string.IsNullOrWhiteSpace(expr)
                ? new List<RuleDef>()
                : new List<RuleDef> { new RuleDef { Kind = RuleKind.Expression, Value = expr.Trim() } };
            var cfg = new FenceConfig { Title = title, VaultPath = vaultFolder, Rules = rules, X = 160, Y = 160 };
            _layout.Fences.Add(cfg);
            Open(cfg);
            Save();
            return cfg;
        }

        void NewVaultFence()
        {
            const string defaultVault = @"C:\Users\colli\Wanda-Brain";
            using var dlg = new System.Windows.Forms.FolderBrowserDialog
            {
                Description = "Notes folder (Obsidian vault) for this pad",
                SelectedPath = System.IO.Directory.Exists(defaultVault) ? defaultVault : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
            };
            if (dlg.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;

            var title = InputDialog.Ask("New vault pad", "Name:", "Notes today", null);
            if (string.IsNullOrWhiteSpace(title)) return;
            var expr = InputDialog.Ask("Which notes?",
                "Expression, e.g.  modified < 1d   |   tag = paddock   |   status = current and folder ~ Projects\nFields: name path folder tag status text modified created",
                "modified < 1d", null);
            if (expr == null) return;
            if (expr.Trim().Length > 0 && ExprRule.Validate(expr) is { } err)
            { MessageBox.Show(err, "Paddock"); return; }
            AddVaultFence(title.Trim(), dlg.SelectedPath, expr);
        }

        public FenceConfig AddNotePad(string title, string file)
        {
            var cfg = new FenceConfig { Title = title, NotePath = file, Width = 300, Height = 260, X = 180, Y = 180 };
            _layout.Fences.Add(cfg);
            Open(cfg);
            Save();
            return cfg;
        }

        /// <summary>Creates an empty note under %APPDATA%\Paddock\notes and opens it in a pad.</summary>
        void NewNotePad()
        {
            var title = InputDialog.Ask("New note pad", "Name:", "Scratch", null);
            if (string.IsNullOrWhiteSpace(title)) return;
            var safe = string.Concat(title.Trim().Select(ch => System.IO.Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch));
            var dir = System.IO.Path.Combine(ConfigStore.Dir, "notes");
            System.IO.Directory.CreateDirectory(dir);
            var file = System.IO.Path.Combine(dir, safe + ".md");
            if (!System.IO.File.Exists(file)) System.IO.File.WriteAllText(file, "");
            AddNotePad(title.Trim(), file);
        }

        /// <summary>Opens an existing .md/.txt (default start: the vault) in a pad that edits it in place.</summary>
        void NewNotePadFromFile()
        {
            const string defaultVault = @"C:\Users\colli\Wanda-Brain";
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Filter = "Notes (*.md;*.txt)|*.md;*.txt|All files|*.*",
                InitialDirectory = System.IO.Directory.Exists(defaultVault) ? defaultVault : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
            };
            if (dlg.ShowDialog() != true) return;
            AddNotePad(System.IO.Path.GetFileNameWithoutExtension(dlg.FileName), dlg.FileName);
        }

        /// <summary>Appends one timestamped line to the first note pad (the active tab of each window).</summary>
        void QuickCapture()
        {
            var pad = _windows.FirstOrDefault(w => w.Config.NotePath != null);
            if (pad == null)
            { MessageBox.Show("No note pad is open. Create one from the tray first.", "Paddock"); return; }
            var text = InputDialog.Ask("Quick capture", "Added to \"" + pad.Config.Title + "\":", "", null);
            if (string.IsNullOrWhiteSpace(text)) return;
            pad.AppendNote(text.Trim());
        }

        public FenceConfig AddPortal(string title, string folder)
        {
            var cfg = new FenceConfig { Title = title, PortalPath = folder, X = 140, Y = 140 };
            _layout.Fences.Add(cfg);
            Open(cfg);
            Save();
            return cfg;
        }

        public void ToggleQuickHide()
        {
            _hidden = !_hidden;
            foreach (var w in _windows)
            {
                if (_hidden && !w.Config.ExcludeFromQuickHide) w.Hide();
                else w.Show();
            }
        }

        public void RefreshAll()
        {
            foreach (var w in _windows) w.Refresh();
        }
    }
}
