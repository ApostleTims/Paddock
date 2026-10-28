using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace Paddock
{
    public static class RuleActions
    {
        static string Safe(string s) => string.Concat(s.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).Trim();

        /// <summary>Fill {yyyy} {MM} {ext} {pad} from the file's modified date, extension and the pad's title.</summary>
        public static string Expand(string template, FileInfo f, string pad)
        {
            var ext = f.Extension.TrimStart('.').ToLowerInvariant();
            return template
                .Replace("{yyyy}", f.LastWriteTime.ToString("yyyy"))
                .Replace("{MM}", f.LastWriteTime.ToString("MM"))
                .Replace("{ext}", ext.Length == 0 ? "no-extension" : Safe(ext))
                .Replace("{pad}", Safe(pad));
        }

        /// <summary>
        /// The new file name for a "rename:" action, or null when the file already fits the template (so running it
        /// twice, or on automatic, never stacks prefixes). {name} is the name without its extension; the extension is kept.
        /// </summary>
        public static string? ExpandName(string template, FileInfo f, string pad)
        {
            var stem = Path.GetFileNameWithoutExtension(f.Name);
            var already = "^" + Regex.Escape(Expand(template, f, pad)).Replace(Regex.Escape("{name}"), "(.*)") + "$";
            if (Regex.IsMatch(stem, already, RegexOptions.IgnoreCase | RegexOptions.Singleline)) return null;
            var name = Safe(Expand(template, f, pad).Replace("{name}", stem));
            return name.Length == 0 ? null : name + f.Extension;
        }

        /// <summary>Parse "move: D:\Sorted\{ext}" / "copy: ..." / "rename: {yyyy}-{MM}-{name}"; empty or "off" turns it off. Returns null on a bad format.</summary>
        public static (string Mode, string? Target)? Parse(string text)
        {
            text = text.Trim();
            if (text.Length == 0 || text.Equals("off", StringComparison.OrdinalIgnoreCase)) return ("Off", null);
            int i = text.IndexOf(':');
            if (i < 0) return null;
            var mode = text[..i].Trim().ToLowerInvariant();
            var target = text[(i + 1)..].Trim().Trim('"');
            if (mode == "rename")
            {
                // A template, not a path; it must keep {name} or every file would collapse onto one name.
                if (!target.Contains("{name}") || target.IndexOfAny(new[] { (char)92, '/', ':' }) >= 0) return null;
                return ("Rename", target);
            }
            if (target.Length == 0 || !Path.IsPathRooted(target)) return null;
            return mode switch { "move" => ("Move", target), "copy" => ("Copy", target), _ => null };
        }

        public static string Format(FenceConfig c) =>
            c.ActionMode == "Off" || c.ActionTarget == null ? "" : $"{c.ActionMode.ToLowerInvariant()}: {c.ActionTarget}";
    }
}
