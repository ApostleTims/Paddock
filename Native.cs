using System;
using System.Runtime.InteropServices;
using System.Text;

namespace Paddock
{
    /// <summary>Win32 helpers that keep a fence above the desktop but below every app window.</summary>
    public static class Native
    {
        const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOACTIVATE = 0x10;
        const int GWL_EXSTYLE = -20;
        const int WS_EX_TOOLWINDOW = 0x80;

        delegate bool EnumProc(IntPtr hwnd, IntPtr lParam);

        [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc cb, IntPtr lParam);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindow(string cls, string? title);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string cls, string? title);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr hwnd, StringBuilder sb, int max);
        [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);

        /// <summary>The top-level window that hosts the desktop icon view (Progman, or a WorkerW on some builds).</summary>
        public static IntPtr FindDesktopHost()
        {
            IntPtr host = IntPtr.Zero;
            EnumWindows((hwnd, _) =>
            {
                var sb = new StringBuilder(64);
                GetClassName(hwnd, sb, sb.Capacity);
                var cls = sb.ToString();
                if ((cls == "WorkerW" || cls == "Progman") &&
                    FindWindowEx(hwnd, IntPtr.Zero, "SHELLDLL_DefView", null) != IntPtr.Zero)
                {
                    host = hwnd;
                    return false;
                }
                return true;
            }, IntPtr.Zero);
            return host != IntPtr.Zero ? host : FindWindow("Progman", null);
        }

        /// <summary>Place hwnd directly above the desktop host in z-order.</summary>
        public static void SitOnDesktop(IntPtr hwnd)
        {
            var host = FindDesktopHost();
            if (host == IntPtr.Zero) return;
            SetWindowPos(hwnd, host, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        }

        [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr hwnd, int cmd);
        [DllImport("user32.dll")] public static extern bool RegisterHotKey(IntPtr hwnd, int id, uint mods, uint vk);
        [DllImport("user32.dll")] public static extern bool UnregisterHotKey(IntPtr hwnd, int id);

        /// <summary>Show or hide the real desktop icon list. Always restore on exit.</summary>
        public static void SetDesktopIconsVisible(bool visible)
        {
            var host = FindDesktopHost();
            if (host == IntPtr.Zero) return;
            var view = FindWindowEx(host, IntPtr.Zero, "SHELLDLL_DefView", null);
            if (view == IntPtr.Zero) return;
            var list = FindWindowEx(view, IntPtr.Zero, "SysListView32", null);
            if (list != IntPtr.Zero) ShowWindow(list, visible ? 5 : 0); // SW_SHOW : SW_HIDE
        }

        /// <summary>Hide from Alt+Tab.</summary>
        public static void MakeToolWindow(IntPtr hwnd)
        {
            var ex = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
            SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(ex | WS_EX_TOOLWINDOW));
        }
    }
}
