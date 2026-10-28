using System;
using System.Linq;
using System.Windows;
using WF = System.Windows.Forms;

namespace Paddock
{
    public enum DockEdge { None, Left, Right, Top, Bottom }

    /// <summary>
    /// Multi-monitor helpers. WPF positions are device-independent units; WinForms Screen is in pixels,
    /// so every call takes the window's DPI scale.
    /// </summary>
    public static class Screens
    {
        public static Rect ToDips(System.Drawing.Rectangle r, double s) => new(r.X / s, r.Y / s, r.Width / s, r.Height / s);

        public static WF.Screen NearestTo(Rect dips, double s) =>
            WF.Screen.FromRectangle(new System.Drawing.Rectangle(
                (int)(dips.X * s), (int)(dips.Y * s),
                (int)Math.Max(1, dips.Width * s), (int)Math.Max(1, dips.Height * s)));

        /// <summary>Usable area of the screen (excludes the taskbar).</summary>
        public static Rect WorkArea(WF.Screen sc, double s) => ToDips(sc.WorkingArea, s);

        /// <summary>True when nothing is plugged onto that side of the screen (no neighbouring monitor).</summary>
        public static bool EdgeExposed(WF.Screen sc, DockEdge edge, Rect dips, double s)
        {
            var b = sc.Bounds;
            int cx = (int)((dips.X + dips.Width / 2) * s), cy = (int)((dips.Y + dips.Height / 2) * s);
            var probe = edge switch
            {
                DockEdge.Left => new System.Drawing.Point(b.Left - 2, Math.Clamp(cy, b.Top, b.Bottom - 1)),
                DockEdge.Right => new System.Drawing.Point(b.Right + 1, Math.Clamp(cy, b.Top, b.Bottom - 1)),
                DockEdge.Top => new System.Drawing.Point(Math.Clamp(cx, b.Left, b.Right - 1), b.Top - 2),
                _ => new System.Drawing.Point(Math.Clamp(cx, b.Left, b.Right - 1), b.Bottom + 1),
            };
            return !WF.Screen.AllScreens.Any(o => o.Bounds.Contains(probe));
        }

        /// <summary>Which exposed edge the rectangle is within <paramref name="threshold"/> of, if any (nearest wins).</summary>
        public static DockEdge DetectEdge(Rect r, Rect area, WF.Screen sc, double s)
        {
            double tx = Snapper.Tol(r.Width), ty = Snapper.Tol(r.Height);
            var cands = new (DockEdge e, double d, double t)[]
            {
                (DockEdge.Left, r.Left - area.Left, tx), (DockEdge.Right, area.Right - r.Right, tx),
                (DockEdge.Top, r.Top - area.Top, ty), (DockEdge.Bottom, area.Bottom - r.Bottom, ty),
            };
            foreach (var (e, d, _) in cands.Where(c => c.d <= c.t).OrderBy(c => Math.Abs(c.d)))
                if (EdgeExposed(sc, e, r, s)) return e;
            return DockEdge.None;
        }
    }
}
