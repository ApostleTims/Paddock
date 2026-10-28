using System;
using System.Collections.Concurrent;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace Paddock
{
    public static class Icons
    {
        static readonly ConcurrentDictionary<string, BitmapSource?> Cache = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Shell icon for a file or folder, cached by path. Null if the shell gives none.</summary>
        public static BitmapSource? For(string path)
        {
            return Cache.GetOrAdd(path, p =>
            {
                try
                {
                    // Shortcuts (and the launchers we make for Store apps) get the shell's own tile image:
                    // sharp, and the real app logo rather than explorer's.
                    if (p.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
                    {
                        var side = p + ".appid";
                        var name = File.Exists(side) ? "shell:AppsFolder\\" + File.ReadAllText(side).Trim() : p;
                        var shell = ShellImage(name, 128);
                        if (shell != null) return shell;
                    }
                    using var icon = Icon.ExtractAssociatedIcon(p);
                    if (icon == null) return null;
                    var src = Imaging.CreateBitmapSourceFromHIcon(
                        icon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                    src.Freeze();
                    return src;
                }
                catch { return null; }
            });
        }

        [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IShellItemImageFactory { [PreserveSig] int GetImage(SIZE size, int flags, out IntPtr hbitmap); }
        [StructLayout(LayoutKind.Sequential)] struct SIZE { public int cx, cy; }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
        static extern void SHCreateItemFromParsingName(string path, IntPtr bc, [In] ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory item);
        [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr o);

        static BitmapSource? ShellImage(string parsingName, int px)
        {
            try
            {
                var iid = typeof(IShellItemImageFactory).GUID;
                SHCreateItemFromParsingName(parsingName, IntPtr.Zero, ref iid, out var f);
                // flags: 0x20 = ICONONLY (skip thumbnails), 0x0 = BIGGERSIZEOK off
                if (f.GetImage(new SIZE { cx = px, cy = px }, 0x20, out var hbmp) != 0 || hbmp == IntPtr.Zero) return null;
                try
                {
                    var src = Imaging.CreateBitmapSourceFromHBitmap(hbmp, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                    // HBITMAP from the shell is premultiplied 32-bit ARGB; convert so transparency is kept.
                    var conv = new FormatConvertedBitmap(src, System.Windows.Media.PixelFormats.Pbgra32, null, 0);
                    conv.Freeze();
                    return conv;
                }
                finally { DeleteObject(hbmp); }
            }
            catch { return null; }
        }
    }
}
