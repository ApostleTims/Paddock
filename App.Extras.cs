using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;

namespace Paddock
{
    // Pad hotkeys, live pads, pad import and the update check.
    public partial class App
    {
        const int HotkeyPadBase = 100;
        readonly Dictionary<int, FenceConfig> _padHotkeys = new();
        string? _updateUrl;

        /// <summary>Unregisters the old pad hotkeys and registers every pad's current one. Returns the pads whose key was refused (taken by something else).</summary>
        List<FenceConfig> RegisterPadHotkeys()
        {
            var failed = new List<FenceConfig>();
            if (_hotkeySource == null) return failed;
            foreach (var id in _padHotkeys.Keys) Native.UnregisterHotKey(_hotkeySource.Handle, id);
            _padHotkeys.Clear();
            int n = 0;
            foreach (var cfg in _layout.Fences.Where(c => !string.IsNullOrWhiteSpace(c.Hotkey)))
            {
                if (!HotkeyText.Parse(cfg.Hotkey, out var mods, out var vk)) { failed.Add(cfg); continue; }
                int id = HotkeyPadBase + n++;
                if (Native.RegisterHotKey(_hotkeySource.Handle, id, mods | 0x4000, vk)) _padHotkeys[id] = cfg;
                else failed.Add(cfg);
            }
            return failed;
        }

        void RaisePad(FenceConfig cfg)
        {
            var w = _windows.FirstOrDefault(x => x.Owns(cfg));
            w?.RaiseFor(cfg);
        }

        // ---- Live pads ----
        FenceConfig AddLivePad(string title, string type, string? spec = null, string? rules = null, int w = 280, int h = 240)
        {
            var cfg = new FenceConfig { Title = title, PadType = type, PadSpec = spec, Width = w, Height = h, X = 200, Y = 200 };
            if (rules != null) cfg.Rules = RuleText.Parse(rules);
            _layout.Fences.Add(cfg);
            Open(cfg);
            Save();
            return cfg;
        }

        void NewTodoPad()
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Filter = "Notes (*.md;*.txt)|*.md;*.txt|All files|*.*",
                Title = "Note containing '- [ ]' tasks"
            };
            if (dlg.ShowDialog() != true) return;
            AddLivePad("Todo", "todo", dlg.FileName);
        }

        void NewProjectPad()
        {
            using var dlg = new System.Windows.Forms.FolderBrowserDialog { Description = "Project folder" };
            if (dlg.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
            var name = System.IO.Path.GetFileName(dlg.SelectedPath.TrimEnd('\\'));
            AddLivePad(string.IsNullOrEmpty(name) ? "Project" : name, "project", dlg.SelectedPath, w: 320, h: 260);
        }

        void NewSearchPad()
        {
            using var dlg = new System.Windows.Forms.FolderBrowserDialog { Description = "Folder to search (subfolders included, up to 6 levels deep)" };
            if (dlg.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
            var expr = InputDialog.Ask("Saved search",
                "Expression, e.g.  modified < 1d   |   size > 100mb   |   ext = pdf and modified < 7d\nFields: name ext size modified created folder",
                "modified < 1d", null);
            if (string.IsNullOrWhiteSpace(expr)) return;
            if (ExprRule.Validate(expr) is { } err) { MessageBox.Show(err, "Paddock"); return; }
            var title = InputDialog.Ask("Saved search", "Pad name:", "Search: " + expr.Trim(), null);
            if (string.IsNullOrWhiteSpace(title)) return;
            AddLivePad(title.Trim(), "search", dlg.SelectedPath, "expr:" + expr.Trim(), 320, 260);
        }

        // ---- Import ----
        void ImportPad()
        {
            var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "Paddock pad (*.paddockpad)|*.paddockpad|All files|*.*" };
            if (dlg.ShowDialog() != true) return;
            var cfg = FenceWindow.ReadPadFile(dlg.FileName);
            if (cfg == null) { MessageBox.Show("That file isn't a Paddock pad.", "Paddock"); return; }
            _layout.Fences.Add(cfg);
            Open(cfg);
            Save();
            RegisterPadHotkeys();
        }

        // ---- Update check ----
        void StartUpdateChecks()
        {
            _ = CheckForUpdate(manual: false);
            var t = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromHours(24) };
            t.Tick += (_, _) => _ = CheckForUpdate(manual: false);
            t.Start();
        }

        async System.Threading.Tasks.Task CheckForUpdate(bool manual)
        {
            var r = await UpdateChecker.CheckAsync();
            if (r.Ok && r.Newer)
            {
                _updateUrl = r.Url;
                _tray?.ShowBalloonTip(6000, "Paddock update", $"{r.Tag} is available (you have {r.Current}). Click to open the release page.", System.Windows.Forms.ToolTipIcon.Info);
            }
            else if (manual)
            {
                var msg = !r.Ok ? "Couldn't check: " + r.Error : $"You're up to date ({r.Current}).";
                _tray?.ShowBalloonTip(3000, "Paddock", msg, System.Windows.Forms.ToolTipIcon.Info);
            }
        }

        void OnBalloonClicked()
        {
            if (_updateUrl == null) return;
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(_updateUrl) { UseShellExecute = true }); } catch { }
        }

        void AddLiveTrayItems(System.Windows.Forms.ContextMenuStrip menu)
        {
            var live = new System.Windows.Forms.ToolStripMenuItem("New live pad");
            live.DropDownItems.Add("Recent downloads", null, (_, _) => AddLivePad("Downloads", "downloads"));
            live.DropDownItems.Add("Clipboard history", null, (_, _) => AddLivePad("Clipboard", "clipboard"));
            live.DropDownItems.Add("Todo list from a note...", null, (_, _) => NewTodoPad());
            live.DropDownItems.Add("Links pad", null, (_, _) => AddLivePad("Links", "links"));
            live.DropDownItems.Add("Project pad...", null, (_, _) => NewProjectPad());
            live.DropDownItems.Add("Saved search...", null, (_, _) => NewSearchPad());
            menu.Items.Add(live);
            menu.Items.Add("Import pad...", null, (_, _) => ImportPad());
            menu.Items.Add("Check for updates...", null, (_, _) => _ = CheckForUpdate(manual: true));
            menu.Items.Add("Lock / unlock all pads", null, (_, _) => ToggleLockAll());
            menu.Items.Add("Health check...", null, (_, _) => HealthCheck());
            var startup = new System.Windows.Forms.ToolStripMenuItem("Start with Windows") { Checked = StartsWithWindows() };
            startup.Click += (_, _) => { SetStartWithWindows(!StartsWithWindows()); startup.Checked = StartsWithWindows(); };
            menu.Items.Add(startup);
        }

        // ---- Start with Windows: a per-user Run key (no admin, easy to undo from Task Manager > Startup) ----
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

        static bool StartsWithWindows()
        {
            try { using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey); return k?.GetValue("Paddock") != null; }
            catch { return false; }
        }

        static void SetStartWithWindows(bool on)
        {
            try
            {
                using var k = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(RunKey);
                if (on) k.SetValue("Paddock", "\"" + Environment.ProcessPath + "\"");
                else k.DeleteValue("Paddock", false);
            }
            catch (Exception ex) { System.Windows.MessageBox.Show("Could not change startup: " + ex.Message, "Paddock"); }
        }

        void ToggleLockAll()
        {
            bool lockAll = _windows.Any(w => !w.Config.Locked);
            foreach (var w in _windows) w.SetLocked(lockAll);
            Save();
        }

        static bool Missing(string p) => !p.Contains("://") && !System.IO.File.Exists(p) && !System.IO.Directory.Exists(p);

        /// <summary>Looks for pads whose folder/note is gone and shortcuts that no longer exist; offers to drop the dead shortcuts.</summary>
        void HealthCheck()
        {
            var problems = new List<string>();
            int dead = 0;
            foreach (var c in _layout.Fences)
            {
                var title = string.IsNullOrWhiteSpace(c.Title) ? "(untitled)" : c.Title;
                foreach (var (kind, p) in new[] { ("folder", c.PortalPath), ("vault", c.VaultPath), ("note", c.NotePath),
                             ("source", c.PadType is "project" or "search" or "todo" ? c.PadSpec : null) })
                    if (!string.IsNullOrEmpty(p) && Missing(p)) problems.Add($"{title}: {kind} missing  ({p})");
                var gone = c.Pinned.Where(Missing).ToList();
                dead += gone.Count;
                if (gone.Count > 0) problems.Add($"{title}: {gone.Count} missing shortcut(s), e.g. {System.IO.Path.GetFileName(gone[0])}");
            }
            if (problems.Count == 0) { System.Windows.MessageBox.Show("All pads look healthy: every folder, note and shortcut exists.", "Paddock - health check"); return; }
            var text = string.Join("\n", problems.Take(15)) + (problems.Count > 15 ? $"\n...and {problems.Count - 15} more" : "");
            if (dead == 0) { System.Windows.MessageBox.Show(text + "\n\nFix these by re-pointing the pad or deleting it.", "Paddock - health check"); return; }
            if (System.Windows.MessageBox.Show(text + $"\n\nRemove the {dead} missing shortcut(s) from their pads? (Folders/notes are left alone.)",
                    "Paddock - health check", System.Windows.MessageBoxButton.YesNo) != System.Windows.MessageBoxResult.Yes) return;
            foreach (var c in _layout.Fences) c.Pinned.RemoveAll(p => Missing(p));
            Save();
            foreach (var w in _windows) w.Refresh();
        }
    }
}
