using System;
using System.Collections.Concurrent;
using System.Drawing;
using System.IO;
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
    }
}
