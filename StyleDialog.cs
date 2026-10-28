using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace Paddock
{
    public static class ColorMath
    {
        public static (double H, double S, double V) ToHsv(Color c)
        {
            double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
            double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), d = max - min;
            double h = 0;
            if (d > 0)
            {
                if (max == r) h = 60 * (((g - b) / d) % 6);
                else if (max == g) h = 60 * ((b - r) / d + 2);
                else h = 60 * ((r - g) / d + 4);
                if (h < 0) h += 360;
            }
            return (h, max == 0 ? 0 : d / max, max);
        }

        public static Color FromHsv(double h, double s, double v)
        {
            h = ((h % 360) + 360) % 360;
            double c = v * s, x = c * (1 - Math.Abs((h / 60) % 2 - 1)), m = v - c;
            double r, g, b;
            switch ((int)(h / 60))
            {
                case 0: (r, g, b) = (c, x, 0); break;
                case 1: (r, g, b) = (x, c, 0); break;
                case 2: (r, g, b) = (0, c, x); break;
                case 3: (r, g, b) = (0, x, c); break;
                case 4: (r, g, b) = (x, 0, c); break;
                default: (r, g, b) = (c, 0, x); break;
            }
            byte To(double d) => (byte)Math.Round((d + m) * 255);
            return Color.FromRgb(To(r), To(g), To(b));
        }

        /// <summary>#RGB, #RRGGBB, with or without the '#'.</summary>
        public static bool TryParseHex(string text, out Color c)
        {
            c = default;
            var t = text.Trim().TrimStart('#');
            if (t.Length == 3) t = $"{t[0]}{t[0]}{t[1]}{t[1]}{t[2]}{t[2]}";
            if (t.Length != 6 || !int.TryParse(t, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var n)) return false;
            c = Color.FromRgb((byte)(n >> 16), (byte)(n >> 8), (byte)n);
            return true;
        }

        public static string Hex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

        public static Color Parse(string? s, Color fallback)
        {
            if (string.IsNullOrWhiteSpace(s)) return fallback;
            try { return ColorConverter.ConvertFromString(s) is Color c ? c : fallback; }
            catch { return fallback; }
        }

        public static Color Lighten(Color c, double amount) => Color.FromRgb(
            (byte)(c.R + (255 - c.R) * amount), (byte)(c.G + (255 - c.G) * amount), (byte)(c.B + (255 - c.B) * amount));
    }

    /// <summary>Saturation/value square, hue bar, hex and RGB boxes, and a swatch palette.</summary>
    public sealed class ColorPickerPanel : StackPanel
    {
        const double W = 224, SvH = 150, HueH = 16;

        static readonly string[] Presets =
        {
            "#202030", "#12315c", "#14452f", "#5c1a1a", "#3c1f5c", "#5c4310", "#000000", "#18181b",
            "#1e293b", "#334155", "#64748b", "#e2e8f0", "#ffffff", "#78350f", "#7f1d1d", "#14532d",
            "#0ea5e9", "#2563eb", "#7c3aed", "#c026d3", "#e11d48", "#f97316", "#eab308", "#22c55e",
        };

        /// <summary>Raised when the user changes the colour. The flag is true for discrete picks (swatch, typed value) that should fade.</summary>
        public event Action<Color, bool>? Changed;

        double _h, _s = 1, _v = 1;
        Color _cur = Colors.Red;
        bool _busy;

        readonly Canvas _sv = new() { Width = W, Height = SvH, Cursor = Cursors.Cross, ClipToBounds = true, Background = Brushes.Transparent };
        readonly Rectangle _svBase = new() { Width = W, Height = SvH };
        readonly Ellipse _svThumb = new() { Width = 12, Height = 12, Stroke = Brushes.White, StrokeThickness = 2, IsHitTestVisible = false };
        readonly Canvas _hueBar = new() { Width = W, Height = HueH, Cursor = Cursors.Hand, Background = Brushes.Transparent };
        readonly Rectangle _hueThumb = new() { Width = 3, Height = HueH + 4, Fill = Brushes.White, Stroke = Brushes.Black, StrokeThickness = 0.5, IsHitTestVisible = false };
        readonly TextBox _hex = new() { Width = 70 }, _r = new() { Width = 34 }, _g = new() { Width = 34 }, _b = new() { Width = 34 };
        readonly Border _preview = new() { Width = 24, Height = 22, BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(3), Margin = new Thickness(0, 0, 6, 0) };

        public ColorPickerPanel()
        {
            Width = W;

            _sv.Children.Add(_svBase);
            _sv.Children.Add(new Rectangle { Width = W, Height = SvH, Fill = new LinearGradientBrush(Colors.White, Color.FromArgb(0, 255, 255, 255), 0) });
            _sv.Children.Add(new Rectangle { Width = W, Height = SvH, Fill = new LinearGradientBrush(Color.FromArgb(0, 0, 0, 0), Colors.Black, 90) });
            _sv.Children.Add(_svThumb);
            Hook(_sv, e =>
            {
                var p = e.GetPosition(_sv);
                _s = Clamp(p.X / W); _v = 1 - Clamp(p.Y / SvH);
                _cur = ColorMath.FromHsv(_h, _s, _v);
                Update(true, null, false);
            });

            var rainbow = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0) };
            string[] hues = { "#FF0000", "#FFFF00", "#00FF00", "#00FFFF", "#0000FF", "#FF00FF", "#FF0000" };
            for (int i = 0; i < hues.Length; i++)
                rainbow.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString(hues[i]), i / 6.0));
            _hueBar.Children.Add(new Rectangle { Width = W, Height = HueH, Fill = rainbow, RadiusX = 3, RadiusY = 3 });
            _hueBar.Children.Add(_hueThumb);
            Hook(_hueBar, e =>
            {
                _h = Clamp(e.GetPosition(_hueBar).X / W) * 360;
                _cur = ColorMath.FromHsv(_h, _s, _v);
                Update(true, null, false);
            });

            var inputs = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 6) };
            inputs.Children.Add(_preview);
            inputs.Children.Add(_hex);
            foreach (var (label, box) in new[] { ("R", _r), ("G", _g), ("B", _b) })
            {
                inputs.Children.Add(new TextBlock { Text = label, Margin = new Thickness(6, 0, 2, 0), VerticalAlignment = VerticalAlignment.Center, FontSize = 11 });
                inputs.Children.Add(box);
            }
            _hex.TextChanged += (_, _) =>
            {
                if (_busy || !ColorMath.TryParseHex(_hex.Text, out var c)) return;
                Set(c); Update(true, "hex", true);
            };
            foreach (var tb in new[] { _r, _g, _b })
                tb.TextChanged += (_, _) =>
                {
                    if (_busy) return;
                    if (byte.TryParse(_r.Text, out var r) && byte.TryParse(_g.Text, out var g) && byte.TryParse(_b.Text, out var b))
                    { Set(Color.FromRgb(r, g, b)); Update(true, "rgb", true); }
                };

            var swatches = new WrapPanel { Width = W + 4, Margin = new Thickness(-2, 0, 0, 0) };
            foreach (var hex in Presets)
            {
                ColorMath.TryParseHex(hex, out var c);
                var sw = new Border
                {
                    Width = 20, Height = 20, Margin = new Thickness(2), CornerRadius = new CornerRadius(3), Cursor = Cursors.Hand,
                    Background = new SolidColorBrush(c), BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1), ToolTip = hex
                };
                sw.MouseLeftButtonDown += (_, _) => { Set(c); Update(true, null, true); };
                swatches.Children.Add(sw);
            }

            _sv.Margin = new Thickness(0, 0, 0, 6);
            Children.Add(_sv);
            Children.Add(_hueBar);
            Children.Add(inputs);
            Children.Add(swatches);
            Update(false, null, false);
        }

        /// <summary>Show a colour without raising <see cref="Changed"/>.</summary>
        public void SetColor(Color c) { Set(c); Update(false, null, false); }

        void Set(Color c)
        {
            var (h, s, v) = ColorMath.ToHsv(c);
            if (s > 0 && v > 0) _h = h; // keep the hue when the colour is grey or black, so the bar doesn't jump to red
            _s = s; _v = v; _cur = c;
        }

        /// <param name="source">"hex" or "rgb" when typing in those boxes, so the box being typed in isn't rewritten under the cursor.</param>
        void Update(bool raise, string? source, bool smooth)
        {
            _svBase.Fill = new SolidColorBrush(ColorMath.FromHsv(_h, 1, 1));
            Canvas.SetLeft(_svThumb, _s * W - 6); Canvas.SetTop(_svThumb, (1 - _v) * SvH - 6);
            Canvas.SetLeft(_hueThumb, _h / 360 * W - 1.5); Canvas.SetTop(_hueThumb, -2);
            _preview.Background = new SolidColorBrush(_cur);
            _busy = true;
            if (source != "hex") _hex.Text = ColorMath.Hex(_cur);
            if (source != "rgb") { _r.Text = _cur.R.ToString(); _g.Text = _cur.G.ToString(); _b.Text = _cur.B.ToString(); }
            _busy = false;
            if (raise) Changed?.Invoke(_cur, smooth);
        }

        static double Clamp(double x) => Math.Min(1, Math.Max(0, x));

        static void Hook(UIElement el, Action<MouseEventArgs> pick)
        {
            el.MouseLeftButtonDown += (_, e) => { el.CaptureMouse(); pick(e); };
            el.MouseMove += (_, e) => { if (el.IsMouseCaptured) pick(e); };
            el.MouseLeftButtonUp += (_, _) => el.ReleaseMouseCapture();
        }
    }

    /// <summary>The "Style..." dialog: live preview on the pad while you edit, OK keeps it, Cancel puts everything back.</summary>
    public static class StyleDialog
    {
        sealed class Look
        {
            public string Color = "#202030";
            public string? Color2, BorderColor, FontName;
            public double GradientAngle = 45, BorderWidth = 1, CornerRadius = 8, Opacity = 0.7, AnimSeconds = 6;
            public int TransitionMs = 150;
            public string Anim = "None";

            public static Look From(FenceConfig c) => new()
            {
                Color = c.Color, Color2 = c.Color2, BorderColor = c.BorderColor, GradientAngle = c.GradientAngle,
                BorderWidth = c.BorderWidth, CornerRadius = c.CornerRadius, Opacity = c.Opacity,
                AnimSeconds = c.AnimSeconds, TransitionMs = c.TransitionMs, Anim = c.Anim, FontName = c.FontName,
            };

            public void ApplyTo(FenceConfig c)
            {
                c.Color = Color; c.Color2 = Color2; c.BorderColor = BorderColor; c.GradientAngle = GradientAngle;
                c.BorderWidth = BorderWidth; c.CornerRadius = CornerRadius; c.Opacity = Opacity;
                c.AnimSeconds = AnimSeconds; c.TransitionMs = TransitionMs; c.Anim = Anim; c.FontName = FontName;
            }
        }

        /// <param name="preview">Re-applies the config to the live pad; the flag asks for a fade.</param>
        /// <param name="pad">The pad's bounds, so the dialog can sit beside it instead of covering it.</param>
        /// <returns>True when the user pressed OK.</returns>
        public static bool Edit(FenceConfig cfg, Action<bool> preview, Rect pad)
        {
            var orig = Look.From(cfg);
            int target = 0; // 0 background, 1 gradient end, 2 border
            var fallback = Color.FromRgb(0x20, 0x20, 0x30);
            var picker = new ColorPickerPanel();
            var root = new StackPanel { Margin = new Thickness(14), Width = 252 };
            var sliders = new System.Collections.Generic.List<Action>(); // re-reads the config into each control (for Reset)

            Color Current() => target switch
            {
                1 => ColorMath.Parse(cfg.Color2, ColorMath.Lighten(ColorMath.Parse(cfg.Color, fallback), 0.35)),
                2 => ColorMath.Parse(cfg.BorderColor, Colors.White),
                _ => ColorMath.Parse(cfg.Color, fallback),
            };

            TextBlock Heading(string t) => new() { Text = t, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 12, 0, 4) };

            // ---- Colours ----
            root.Children.Add(new TextBlock { Text = "Colour", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 4) });
            var targets = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
            var radios = new System.Collections.Generic.List<RadioButton>();
            var gradient = new CheckBox { Content = "Use a gradient", Margin = new Thickness(0, 8, 0, 0), IsChecked = cfg.Color2 != null };
            string[] names = { "Background", "Gradient end", "Border" };
            for (int i = 0; i < names.Length; i++)
            {
                int idx = i;
                var rb = new RadioButton { Content = names[i], GroupName = "target", IsChecked = i == 0, Margin = new Thickness(0, 0, 10, 0) };
                rb.Checked += (_, _) =>
                {
                    target = idx;
                    if (idx == 1) gradient.IsChecked = true;
                    picker.SetColor(Current());
                };
                radios.Add(rb);
                targets.Children.Add(rb);
            }
            root.Children.Add(targets);
            picker.SetColor(Current());
            picker.Changed += (c, smooth) =>
            {
                var hex = ColorMath.Hex(c);
                if (target == 0) cfg.Color = hex;
                else if (target == 1) { cfg.Color2 = hex; if (gradient.IsChecked != true) gradient.IsChecked = true; }
                else cfg.BorderColor = hex;
                preview(smooth);
            };
            root.Children.Add(picker);

            gradient.Checked += (_, _) =>
            {
                cfg.Color2 ??= ColorMath.Hex(ColorMath.Lighten(ColorMath.Parse(cfg.Color, fallback), 0.35));
                preview(true);
            };
            gradient.Unchecked += (_, _) => { cfg.Color2 = null; preview(true); };
            root.Children.Add(gradient);

            Slider AddSlider(string label, double min, double max, double step, string fmt, Func<double> get, Action<double> set)
            {
                var row = new Grid { Margin = new Thickness(0, 2, 0, 0) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(104) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) });
                var name = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, FontSize = 12 };
                var val = new TextBlock { HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, FontSize = 12 };
                var sl = new Slider
                {
                    Minimum = min, Maximum = max, TickFrequency = step, IsSnapToTickEnabled = true,
                    VerticalAlignment = VerticalAlignment.Center, Value = get()
                };
                val.Text = sl.Value.ToString(fmt, CultureInfo.InvariantCulture);
                sl.ValueChanged += (_, e) =>
                {
                    val.Text = e.NewValue.ToString(fmt, CultureInfo.InvariantCulture);
                    set(e.NewValue);
                    preview(false);
                };
                Grid.SetColumn(sl, 1); Grid.SetColumn(val, 2);
                row.Children.Add(name); row.Children.Add(sl); row.Children.Add(val);
                root.Children.Add(row);
                sliders.Add(() => sl.Value = get());
                return sl;
            }

            // ---- Shape ----
            root.Children.Add(Heading("Shape"));
            AddSlider("Opacity %", 10, 100, 1, "0", () => cfg.Opacity * 100, v => cfg.Opacity = v / 100);
            AddSlider("Corner radius", 0, 28, 1, "0", () => cfg.CornerRadius, v => cfg.CornerRadius = v);
            AddSlider("Border width", 0, 6, 1, "0", () => cfg.BorderWidth, v => cfg.BorderWidth = v);
            AddSlider("Gradient angle", 0, 360, 5, "0", () => cfg.GradientAngle, v => cfg.GradientAngle = v);

            // ---- Font ----
            root.Children.Add(Heading("Font"));
            var fontRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
            fontRow.Children.Add(new TextBlock { Text = "Icon labels", Width = 104, VerticalAlignment = VerticalAlignment.Center, FontSize = 12 });
            var font = new ComboBox { Width = 140 };
            font.Items.Add("(default)");
            var fontNames = new System.Collections.Generic.List<string>();
            foreach (var f in Fonts.SystemFontFamilies) fontNames.Add(f.Source);
            fontNames.Sort(StringComparer.OrdinalIgnoreCase);
            foreach (var n in fontNames) font.Items.Add(n);
            void SyncFont() => font.SelectedItem = cfg.FontName != null && font.Items.Contains(cfg.FontName) ? cfg.FontName : "(default)";
            SyncFont();
            font.SelectionChanged += (_, _) =>
            {
                cfg.FontName = font.SelectedItem is string n && n != "(default)" ? n : null;
                preview(false);
            };
            fontRow.Children.Add(font);
            root.Children.Add(fontRow);
            sliders.Add(SyncFont);

            // ---- Motion ----
            root.Children.Add(Heading("Motion"));
            AddSlider("Transition ms", 0, 1000, 10, "0", () => cfg.TransitionMs, v => cfg.TransitionMs = (int)v);
            var animRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
            animRow.Children.Add(new TextBlock { Text = "Animation", Width = 104, VerticalAlignment = VerticalAlignment.Center, FontSize = 12 });
            var anim = new ComboBox { Width = 120 };
            foreach (var a in new[] { "None", "Breathe", "Drift" }) anim.Items.Add(a);
            anim.SelectedItem = anim.Items.Contains(cfg.Anim) ? cfg.Anim : "None";
            anim.SelectionChanged += (_, _) => { cfg.Anim = (string)anim.SelectedItem; preview(false); };
            animRow.Children.Add(anim);
            root.Children.Add(animRow);
            AddSlider("Seconds / cycle", 2, 30, 1, "0", () => cfg.AnimSeconds, v => cfg.AnimSeconds = v);
            root.Children.Add(new TextBlock
            {
                Text = "Breathe: the background slowly fades in and out. Drift: the gradient slowly rotates.\nTransition sets how long colour changes and pad moves take.",
                FontSize = 11, Foreground = Brushes.Gray, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0)
            });

            // ---- Buttons ----
            var reset = new Button { Content = "Reset", Width = 64, Margin = new Thickness(0, 0, 70, 0) };
            var ok = new Button { Content = "OK", IsDefault = true, Width = 64, Margin = new Thickness(0, 0, 6, 0) };
            var cancel = new Button { Content = "Cancel", IsCancel = true, Width = 64 };
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
            buttons.Children.Add(reset); buttons.Children.Add(ok); buttons.Children.Add(cancel);
            root.Children.Add(buttons);

            reset.Click += (_, _) =>
            {
                Look.From(new FenceConfig()).ApplyTo(cfg);
                foreach (var s in sliders) s();
                gradient.IsChecked = false;
                anim.SelectedItem = cfg.Anim;
                radios[0].IsChecked = true;
                picker.SetColor(Current());
                preview(true);
            };

            var wa = SystemParameters.WorkArea;
            var win = new Window
            {
                Title = "Pad style", Content = new ScrollViewer { Content = root, VerticalScrollBarVisibility = ScrollBarVisibility.Auto },
                SizeToContent = SizeToContent.WidthAndHeight, MaxHeight = Math.Max(300, wa.Height - 20),
                ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual,
            };
            win.Loaded += (_, _) =>
            {
                double w = win.ActualWidth, h = win.ActualHeight;
                double left = pad.Right + 12;
                if (left + w > wa.Right) left = pad.Left - w - 12;
                if (left < wa.Left) left = Math.Max(wa.Left, wa.Right - w - 12);
                win.Left = left;
                win.Top = Math.Clamp(pad.Top, wa.Top, Math.Max(wa.Top, wa.Bottom - h));
            };

            bool accepted = false;
            ok.Click += (_, _) => { accepted = true; win.Close(); };
            win.ShowDialog();
            if (!accepted) { orig.ApplyTo(cfg); preview(true); }
            return accepted;
        }
    }
}
