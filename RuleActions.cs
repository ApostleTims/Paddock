using System;
using System.IO;
using System.Linq;

namespace Paddock
{
    public static class RuleActions
    {
        /// <summary>Fill {yyyy} {MM} {ext} {pad} from the file's modified date, extension and the pad's title.</summary>
        public static string Expand(string template, FileInfo f, string pad)
        {
            string Safe(string s) => string.Concat(s.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).Trim();
            var ext = f.Extension.TrimStart('.').ToLowerInvariant();
            return template
                .Replace("{yyyy}", f.LastWriteTime.ToString("yyyy"))
                .Replace("{MM}", f.LastWriteTime.ToString("MM"))
                .Replace("{ext}", ext.Length == 0 ? "no-extension" : Safe(ext))
                .Replace("{pad}", Safe(pad));
        }

        /// <summary>Parse "move: D:\Sorted\{ext}" / "copy: ..."; empty or "off" turns it off. Returns null on a bad format.</summary>
        public static (string Mode, string? Target)? Parse(string text)
        {
            text = text.Trim();
            if (text.Length == 0 || text.Equals("off", StringComparison.OrdinalIgnoreCase)) return ("Off", null);
            int i = text.IndexOf(':');
            if (i < 0) return null;
            var mode = text[..i].Trim().ToLowerInvariant();
            var target = text[(i + 1)..].Trim().Trim('"');
            if (target.Length == 0 || !Path.IsPathRooted(target)) return null;
            return mode switch { "move" => ("Move", target), "copy" => ("Copy", target), _ => null };
        }

        public static string Format(FenceConfig c) =>
            c.ActionMode == "Off" || c.ActionTarget == null ? "" : $"{c.ActionMode.ToLowerInvariant()}: {c.ActionTarget}";
    }
}
