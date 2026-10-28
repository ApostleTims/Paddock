using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;

namespace Paddock
{
    /// <summary>Installed apps: Start menu shortcuts plus Store apps (from Get-StartApps).</summary>
    public static class StartApps
    {
        /// <param name="Shortcut">Existing .lnk, or null for apps with none (Store apps); then <paramref name="AppId"/> is set.</param>
        public record Entry(string Name, string? Shortcut, string? AppId);

        public static List<Entry> Load()
        {
            var list = new List<Entry>();
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var root in new[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.Programs),
                Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms),
            }.Where(Directory.Exists))
            {
                try
                {
                    foreach (var f in new DirectoryInfo(root).EnumerateFiles("*.lnk", SearchOption.AllDirectories))
                    {
                        var n = System.IO.Path.GetFileNameWithoutExtension(f.Name);
                        if (n.Contains("uninstall", StringComparison.OrdinalIgnoreCase) || !names.Add(n)) continue;
                        list.Add(new Entry(n, f.FullName, null));
                    }
                }
                catch { /* unreadable folder */ }
            }
            // Apps with no Start menu shortcut (mostly Store apps): the shell knows them by AppID.
            try
            {
                var psi = new ProcessStartInfo("powershell.exe",
                    "-NoProfile -NonInteractive -Command \"[Console]::OutputEncoding=[Text.Encoding]::UTF8; Get-StartApps | ForEach-Object { $_.Name + '|' + $_.AppID }\"")
                { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, StandardOutputEncoding = Encoding.UTF8 };
                using var p = Process.Start(psi)!;
                var text = p.StandardOutput.ReadToEnd();
                p.WaitForExit(15000);
                foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                {
                    var l = line.Trim();
                    int i = l.LastIndexOf('|');
                    if (i <= 0) continue;
                    var name = l[..i].Trim(); var id = l[(i + 1)..].Trim();
                    if (name.Length == 0 || id.Length == 0 || name.Contains("uninstall", StringComparison.OrdinalIgnoreCase) || !names.Add(name)) continue;
                    list.Add(new Entry(name, null, id));
                }
            }
            catch { /* PowerShell unavailable: Start menu shortcuts only */ }
            return list.OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }

        static readonly string Generated = System.IO.Path.Combine(ConfigStore.Dir, "apps");

        /// <summary>A real file to pin or copy: the app's own shortcut, or a small .lnk we make that launches it through the shell.</summary>
        public static string? Resolve(Entry e)
        {
            if (e.Shortcut != null) return e.Shortcut;
            try
            {
                Directory.CreateDirectory(Generated);
                var safe = string.Concat(e.Name.Select(c => System.IO.Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
                var path = System.IO.Path.Combine(Generated, safe + ".lnk");
                dynamic sh = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!)!;
                dynamic lnk = sh.CreateShortcut(path);
                lnk.TargetPath = "explorer.exe";
                lnk.Arguments = "shell:AppsFolder\\" + e.AppId;
                lnk.Save();
                return path;
            }
            catch { return null; }
        }
    }

    public static class AppPicker
    {
        /// <summary>Pick installed apps; returns the chosen entries (empty if cancelled).</summary>
        public static List<StartApps.Entry> Pick(List<StartApps.Entry> all, Window? owner)
        {
            var filter = new TextBox { Margin = new Thickness(0, 0, 0, 8) };
            var list = new ListBox { SelectionMode = SelectionMode.Extended, Height = 360, DisplayMemberPath = "Name" };
            var hint = new TextBlock { Text = "Ctrl/Shift-click to pick several.", FontSize = 11, Foreground = System.Windows.Media.Brushes.Gray, Margin = new Thickness(0, 6, 0, 0) };
            var ok = new Button { Content = "Add", IsDefault = true, Width = 70, Margin = new Thickness(0, 0, 6, 0) };
            var cancel = new Button { Content = "Cancel", IsCancel = true, Width = 70 };
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
            buttons.Children.Add(ok); buttons.Children.Add(cancel);
            var root = new StackPanel { Margin = new Thickness(14), Width = 340 };
            root.Children.Add(new TextBlock { Text = "Search installed apps", Margin = new Thickness(0, 0, 0, 4) });
            root.Children.Add(filter); root.Children.Add(list); root.Children.Add(hint); root.Children.Add(buttons);

            var chosen = new List<StartApps.Entry>();
            void Fill() => list.ItemsSource = all.Where(a => a.Name.Contains(filter.Text.Trim(), StringComparison.OrdinalIgnoreCase)).ToList();
            filter.TextChanged += (_, _) => Fill();
            Fill();

            var win = new Window
            {
                Title = "Add app to pad", Content = root, SizeToContent = SizeToContent.WidthAndHeight, ResizeMode = ResizeMode.NoResize,
                WindowStartupLocation = WindowStartupLocation.CenterScreen, ShowInTaskbar = false,
            };
            win.Loaded += (_, _) => filter.Focus();
            list.MouseDoubleClick += (_, _) => ok.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            ok.Click += (_, _) => { chosen = list.SelectedItems.Cast<StartApps.Entry>().ToList(); win.Close(); };
            win.ShowDialog();
            return chosen;
        }
    }
}
