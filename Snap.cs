using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;

namespace Paddock
{
    /// <summary>
    /// Snap-on for fences: while moving or resizing, edges pull onto neighbouring fences and onto the screen edges
    /// (work area) once they come within 7% of the fence's own size. An optional grid is also available.
    /// All values are DIPs. Holding Ctrl while dragging bypasses snapping.
    /// </summary>
    public static class Snapper
    {
        public static bool Grid = false, Fences = true;
        public static double GridSize = 20;

        /// <summary>How close counts as "near": 7% of the fence's size along that axis, never less than 6 px.</summary>
        public static double Tol(double size) => Math.Max(6, 0.07 * size);

        /// <summary>Supplies the rectangles of the other (undocked, visible) fences; set by the app.</summary>
        public static Func<FenceWindow, IEnumerable<Rect>> Others = _ => Array.Empty<Rect>();

        /// <summary>Nearest candidate within the tolerance, or the value untouched. Fence/screen edges beat grid lines on ties.</summary>
        static double Pull(double v, IEnumerable<double> lines, double tol)
        {
            double best = v, bestD = tol + 1;
            if (Fences)
                foreach (var c in lines)
                {
                    var d = Math.Abs(c - v);
                    if (d <= tol && d < bestD) { best = c; bestD = d; }
                }
            if (Grid && GridSize >= 4)
            {
                var c = Math.Round(v / GridSize) * GridSize;
                var d = Math.Abs(c - v);
                if (d <= tol && d < bestD) { best = c; bestD = d; }
            }
            return best;
        }

        static bool Overlaps(double a0, double a1, double b0, double b1, double slack) =>
            a0 < b1 + slack && b0 < a1 + slack;

        /// <summary>Snap a rectangle being moved (size stays the same). <paramref name="area"/> is the nearest screen's work area.</summary>
        public static Point Move(FenceWindow self, Rect r, Rect area)
        {
            double tx = Tol(r.Width), ty = Tol(r.Height);
            var xs = new List<double>();
            var ys = new List<double>();
            if (Fences)
            {
                xs.Add(area.Left); xs.Add(area.Right - r.Width);
                ys.Add(area.Top); ys.Add(area.Bottom - r.Height);
                // Only neighbours alongside us on the other axis count, so a far-away fence doesn't attract.
                foreach (var o in Others(self))
                {
                    if (Overlaps(r.Top, r.Bottom, o.Top, o.Bottom, ty))
                    { xs.Add(o.Left); xs.Add(o.Right); xs.Add(o.Left - r.Width); xs.Add(o.Right - r.Width); }
                    if (Overlaps(r.Left, r.Right, o.Left, o.Right, tx))
                    { ys.Add(o.Top); ys.Add(o.Bottom); ys.Add(o.Top - r.Height); ys.Add(o.Bottom - r.Height); }
                }
            }
            return new Point(Pull(r.X, xs, tx), Pull(r.Y, ys, ty));
        }

        /// <summary>Snap the right and bottom edges of a rectangle being resized (left/top stay put).</summary>
        public static Size Resize(FenceWindow self, Rect r, Rect area)
        {
            double tx = Tol(r.Width), ty = Tol(r.Height);
            var xs = new List<double>();
            var ys = new List<double>();
            if (Fences)
            {
                xs.Add(area.Right); ys.Add(area.Bottom);
                foreach (var o in Others(self))
                {
                    if (Overlaps(r.Top, r.Bottom, o.Top, o.Bottom, ty)) { xs.Add(o.Left); xs.Add(o.Right); }
                    if (Overlaps(r.Left, r.Right, o.Left, o.Right, tx)) { ys.Add(o.Top); ys.Add(o.Bottom); }
                }
            }
            return new Size(Pull(r.Right, xs, tx) - r.Left, Pull(r.Bottom, ys, ty) - r.Top);
        }
    }
}
