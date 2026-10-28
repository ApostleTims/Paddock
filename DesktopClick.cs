using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Automation;

namespace Paddock
{
    /// <summary>
    /// Detects a double-click on empty desktop (a low-level mouse hook) and raises <see cref="DoubleClicked"/>.
    /// A click on a desktop icon is ignored: UI Automation tells an icon (ListItem) from the empty list behind it.
    /// </summary>
    public static class DesktopClick
    {
        public static bool Enabled = true;
        public static Action? DoubleClicked;

        const int WH_MOUSE_LL = 14, WM_LBUTTONDOWN = 0x0201;

        [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }
        [StructLayout(LayoutKind.Sequential)] struct MSLLHOOK { public POINT Pt; public uint Data, Flags, Time; public IntPtr Extra; }

        delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)] static extern IntPtr SetWindowsHookEx(int id, HookProc cb, IntPtr mod, uint thread);
        [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(IntPtr hook);
        [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")] static extern IntPtr WindowFromPoint(POINT pt);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr hwnd, StringBuilder sb, int max);
        [DllImport("user32.dll")] static extern uint GetDoubleClickTime();
        [DllImport("user32.dll")] static extern int GetSystemMetrics(int index);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr GetModuleHandle(string? name);

        static IntPtr _hook;
        static HookProc? _proc; // kept alive: the hook calls back into this delegate
        static uint _lastTime;
        static POINT _lastPt;
        static bool _haveLast;

        public static void Start()
        {
            if (_hook != IntPtr.Zero) return;
            _proc = Hook;
            _hook = SetWindowsHookEx(WH_MOUSE_LL, _proc, GetModuleHandle(null), 0);
        }

        public static void Stop()
        {
            if (_hook != IntPtr.Zero) UnhookWindowsHookEx(_hook);
            _hook = IntPtr.Zero;
        }

        static IntPtr Hook(int code, IntPtr wParam, IntPtr lParam)
        {
            // Keep this fast: the system waits on it for every mouse event.
            if (code >= 0 && Enabled && wParam.ToInt32() == WM_LBUTTONDOWN)
            {
                var m = Marshal.PtrToStructure<MSLLHOOK>(lParam);
                int dx = GetSystemMetrics(36) / 2, dy = GetSystemMetrics(37) / 2; // SM_CXDOUBLECLK / SM_CYDOUBLECLK
                bool dbl = _haveLast && m.Time - _lastTime <= GetDoubleClickTime()
                           && Math.Abs(m.Pt.X - _lastPt.X) <= dx && Math.Abs(m.Pt.Y - _lastPt.Y) <= dy;
                if (dbl)
                {
                    _haveLast = false;
                    var pt = m.Pt;
                    Task.Run(() => { if (IsEmptyDesktop(pt)) System.Windows.Application.Current?.Dispatcher.BeginInvoke(() => DoubleClicked?.Invoke()); });
                }
                else { _haveLast = true; _lastTime = m.Time; _lastPt = m.Pt; }
            }
            return CallNextHookEx(_hook, code, wParam, lParam);
        }

        static bool IsEmptyDesktop(POINT pt)
        {
            try
            {
                var h = WindowFromPoint(pt);
                if (h == IntPtr.Zero) return false;
                var sb = new StringBuilder(64);
                GetClassName(h, sb, sb.Capacity);
                if (sb.ToString() is not ("SysListView32" or "SHELLDLL_DefView" or "Progman" or "WorkerW")) return false;
                var el = AutomationElement.FromPoint(new System.Windows.Point(pt.X, pt.Y));
                return el == null || el.Current.ControlType != ControlType.ListItem;
            }
            catch { return false; }
        }
    }
}
