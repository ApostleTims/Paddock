using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WF = System.Windows.Forms;

namespace Paddock
{
    /// <summary>One row in the palette: a pad item (opens the file) or a command (runs an action).</summary>
    public class PaletteEntry
    {
        public string Title { get; set; } = "";
        public string Subtitle { get; set; } = "";
        public BitmapSource? Icon { get; set; }
        public string? Path { get; set; }       // set for pad items
        public FenceConfig? Pad { get; set; }   // which pad it came from (vault pads open in Obsidian)
        public Action? Run { get; set; }        // set for commands
    }

    /// <summary>Fuzzy matching: every query character must appear in order; runs, word starts and early hits score higher.</summary>
    public static class Fuzzy
    {
        /// <summary>Returns a score (higher is better) or null when the text doesn't match.</summary>
        public static int? Score(string query, string text)
        {
            if (query.Length == 0) return 0;
            int score = 0, qi = 0, last = -2;
            for (int i = 0; i < text.Length && qi < query.Length; i++)
            {
                if (char.ToLowerInvariant(text[i]) != char.ToLowerInvariant(query[qi])) continue;
                score += 10;
                if (i == last + 1) score += 15;                                                       // consecutive run
                if (i == 0 || !char.IsLetterOrDigit(text[i - 1])) score += 12;                        // start of a word
                else if (char.IsUpper(text[i]) && char.IsLower(text[i - 1])) score += 8;              // camelCase hump
                if (qi == 0) score -= Math.Min(i, 20);                                                // prefer early starts
                last = i; qi++;
            }
            return qi == query.Length ? score - text.Length / 8 : null;                                // shorter names edge out longer
        }
    }

    /// <summary>Hotkey-driven finder: type to fuzzy-search every item on every pad plus a few commands; Enter runs it.</summary>
    public class PaletteWindow : Window
    {
        /// <summary>Everything searchable right now. Set by the app so the palette doesn't know about windows.</summary>
        public static Func<IEnumerable<PaletteEntry>> Source = () => Array.Empty<PaletteEntry>();
        public static Action<PaletteEntry> Open = _ => { };
        public static Action<PaletteEntry> Reveal = _ => { };

        static PaletteWindow? _current;

        /// <summary>Show the palette, or close it if it's already up (so the hotkey toggles).</summary>
        public static void Toggle()
        {
            if (_current != null) { _current.Close(); return; }
            _current = new PaletteWindow();
            _current.Show();
        }

        readonly TextBox _box = new();
        readonly ListBox _list = new();
        readonly List<PaletteEntry> _all;

        PaletteWindow()
        {
            _all = Source().ToList();
            WindowStyle = WindowStyle.None; AllowsTransparency = true; Background = Brushes.Transparent;
            ShowInTaskbar = false; ResizeMode = ResizeMode.NoResize; Topmost = true; // a short-lived popup, unlike the pads
            Width = 560; SizeToContent = SizeToContent.Height;

            _box.FontSize = 18; _box.Padding = new Thickness(10, 8, 10, 8); _box.BorderThickness = new Thickness(0);
            _box.Background = new SolidColorBrush(Color.FromRgb(0x1c, 0x1c, 0x28)); _box.Foreground = Brushes.White; _box.CaretBrush = Brushes.White;

            _list.Background = new SolidColorBrush(Color.FromRgb(0x1c, 0x1c, 0x28)); _list.BorderThickness = new Thickness(0);
            _list.MaxHeight = 360; _list.Focusable = false;
            _list.ItemContainerStyle = ItemStyle();
            _list.ItemTemplate = RowTemplate();
            _list.MouseDoubleClick += (_, _) => Run(false);

            var hint = new TextBlock
            {
                Text = "Enter open   Shift+Enter show in folder   Esc close   (type > for commands only)",
                Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x99)), FontSize = 11, Margin = new Thickness(10, 4, 10, 6),
            };
            var stack = new StackPanel();
            stack.Children.Add(_box); stack.Children.Add(_list); stack.Children.Add(hint);
            Content = new Border
            {
                Child = stack, CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1),
                BorderBrush = new SolidColorBrush(Color.FromArgb(0x60, 255, 255, 255)),
                Background = new SolidColorBrush(Color.FromRgb(0x1c, 0x1c, 0x28)),
            };

            // Centre on the screen the cursor is on, a third of the way down.
            var mp = WF.Control.MousePosition;
            var scr = WF.Screen.FromPoint(mp).WorkingArea;
            double s = 1;
            Loaded += (_, _) =>
            {
                s = VisualTreeHelper.GetDpi(this).DpiScaleX;
                Left = scr.Left / s + (scr.Width / s - Width) / 2;
                Top = scr.Top / s + scr.Height / s / 4;
                Activate(); _box.Focus(); Keyboard.Focus(_box);
            };
            _box.TextChanged += (_, _) => Refilter();
            PreviewKeyDown += OnKey;
            Deactivated += (_, _) => { if (IsVisible) Close(); };
            Closed += (_, _) => _current = null;
            Refilter();
        }

        void Refilter()
        {
            var q = _box.Text.Trim();
            bool cmdOnly = q.StartsWith('>');
            if (cmdOnly) q = q[1..].Trim();
            var scored = new List<(PaletteEntry E, int S)>();
            foreach (var e in _all)
            {
                if (cmdOnly && e.Run == null) continue;
                // Match the name first; fall back to pad name + name so "docs inv" finds invoice.pdf on the Docs pad.
                var sc = Fuzzy.Score(q, e.Title) ?? (Fuzzy.Score(q, e.Subtitle + " " + e.Title) is int x ? x - 25 : (int?)null);
                if (sc != null) scored.Add((e, sc.Value));
            }
            var shown = (q.Length == 0
                ? scored.OrderBy(t => t.E.Run == null ? 1 : 0).ThenBy(t => t.E.Title, StringComparer.OrdinalIgnoreCase) // empty: commands on top
                : scored.OrderByDescending(t => t.S).ThenBy(t => t.E.Title, StringComparer.OrdinalIgnoreCase))
                .Take(50).Select(t => t.E).ToList();
            _list.ItemsSource = shown;
            if (shown.Count > 0) _list.SelectedIndex = 0;
        }

        void OnKey(object s, KeyEventArgs e)
        {
            switch (e.Key)
            {
                case Key.Escape: Close(); e.Handled = true; break;
                case Key.Down: Move(+1); e.Handled = true; break;
                case Key.Up: Move(-1); e.Handled = true; break;
                case Key.Enter: Run((Keyboard.Modifiers & ModifierKeys.Shift) != 0); e.Handled = true; break;
            }
        }

        void Move(int d)
        {
            if (_list.Items.Count == 0) return;
            _list.SelectedIndex = Math.Clamp(_list.SelectedIndex + d, 0, _list.Items.Count - 1);
            _list.ScrollIntoView(_list.SelectedItem);
        }

        void Run(bool reveal)
        {
            if (_list.SelectedItem is not PaletteEntry e) return;
            Close();
            if (e.Run != null) e.Run();
            else if (reveal) Reveal(e);
            else Open(e);
        }

        static Style ItemStyle()
        {
            var st = new Style(typeof(ListBoxItem));
            st.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(10, 4, 10, 4)));
            st.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));
            var sel = new Trigger { Property = ListBoxItem.IsSelectedProperty, Value = true };
            sel.Setters.Add(new Setter(Control.BackgroundProperty, new SolidColorBrush(Color.FromArgb(0x50, 0x80, 0x90, 0xff))));
            st.Triggers.Add(sel);
            return st;
        }

        static DataTemplate RowTemplate()
        {
            var tpl = new DataTemplate();
            var row = new FrameworkElementFactory(typeof(StackPanel));
            row.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal);
            var img = new FrameworkElementFactory(typeof(Image));
            img.SetBinding(Image.SourceProperty, new System.Windows.Data.Binding(nameof(PaletteEntry.Icon)));
            img.SetValue(FrameworkElement.WidthProperty, 22.0); img.SetValue(FrameworkElement.HeightProperty, 22.0);
            img.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 0, 10, 0));
            var col = new FrameworkElementFactory(typeof(StackPanel));
            var title = new FrameworkElementFactory(typeof(TextBlock));
            title.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(PaletteEntry.Title)));
            title.SetValue(TextBlock.ForegroundProperty, Brushes.White); title.SetValue(TextBlock.FontSizeProperty, 14.0);
            var sub = new FrameworkElementFactory(typeof(TextBlock));
            sub.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(PaletteEntry.Subtitle)));
            sub.SetValue(TextBlock.ForegroundProperty, new SolidColorBrush(Color.FromRgb(0x99, 0x99, 0xaa))); sub.SetValue(TextBlock.FontSizeProperty, 11.0);
            sub.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
            col.AppendChild(title); col.AppendChild(sub);
            row.AppendChild(img); row.AppendChild(col);
            tpl.VisualTree = row;
            return tpl;
        }
    }
}
