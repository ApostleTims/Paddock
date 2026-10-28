using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Paddock
{
    /// <summary>
    /// Tiny expression language for rules, e.g.
    ///   ext in (pdf,docx) and size > 5mb and not name ~ draft
    ///   modified &lt; 7d and (name ~~ ^IMG or ext = png)
    /// Fields: name, ext, path (text); size (bytes, kb/mb/gb), modified, created (age, d/h/w); dir, file, hidden (flags).
    /// Operators: = != &lt; &gt; &lt;= &gt;= ~ (contains) ~~ (regex) in (...); combine with and / or / not / parentheses.
    /// </summary>
    /// <summary>Frontmatter (tags, status) and body of a markdown note, cached per file version.</summary>
    public static class VaultMeta
    {
        public record Info(string[] Tags, string Status);
        static readonly Dictionary<string, (long ticks, Info info)> Cache = new();
        static readonly Info Empty = new(Array.Empty<string>(), "");

        public static Info Of(FileSystemInfo f)
        {
            if (f is not FileInfo fi || !fi.Extension.Equals(".md", StringComparison.OrdinalIgnoreCase)) return Empty;
            var ticks = fi.LastWriteTimeUtc.Ticks;
            lock (Cache)
                if (Cache.TryGetValue(fi.FullName, out var c) && c.ticks == ticks) return c.info;
            Info info;
            try { info = Parse(File.ReadLines(fi.FullName).Take(60).ToList()); } catch { info = Empty; }
            lock (Cache) Cache[fi.FullName] = (ticks, info);
            return info;
        }

        public static string Body(FileSystemInfo f)
        {
            try { return f is FileInfo { Length: < 2_000_000 } fi ? File.ReadAllText(fi.FullName) : ""; } catch { return ""; }
        }

        static Info Parse(List<string> lines)
        {
            if (lines.Count == 0 || lines[0].Trim() != "---") return Empty;
            var tags = new List<string>(); string status = ""; bool inTags = false;
            foreach (var raw in lines.Skip(1))
            {
                var l = raw.TrimEnd();
                if (l.Trim() == "---") break;
                if (inTags && l.TrimStart().StartsWith("- ")) { tags.Add(l.TrimStart()[2..].Trim().Trim('"', '\'', '#')); continue; }
                inTags = false;
                var i = l.IndexOf(':');
                if (i < 0) continue;
                var key = l[..i].Trim().ToLowerInvariant(); var val = l[(i + 1)..].Trim();
                if (key == "status") status = val.Trim('"', '\'');
                else if (key is "tags" or "tag")
                {
                    if (val.Length == 0) inTags = true;
                    else tags.AddRange(val.Trim('[', ']').Split(',', ' ').Select(t => t.Trim().Trim('"', '\'', '#')).Where(t => t.Length > 0));
                }
            }
            return new Info(tags.ToArray(), status);
        }
    }

    public static class ExprRule
    {
        public class ParseError : Exception { public ParseError(string m) : base(m) { } }

        static readonly Dictionary<string, Func<FileSystemInfo, bool>> Cache = new();

        /// <summary>Compile once and cache; throws <see cref="ParseError"/> on bad syntax.</summary>
        public static Func<FileSystemInfo, bool> Compile(string text)
        {
            lock (Cache)
            {
                if (Cache.TryGetValue(text, out var f)) return f;
                var p = new Parser(Tokenize(text));
                var node = p.ParseAll();
                return Cache[text] = node;
            }
        }

        /// <summary>Null when valid, otherwise a short message.</summary>
        public static string? Validate(string text)
        {
            try { Compile(text); return null; } catch (ParseError e) { return e.Message; }
        }

        static List<string> Tokenize(string s)
        {
            var t = new List<string>();
            int i = 0;
            while (i < s.Length)
            {
                char c = s[i];
                if (char.IsWhiteSpace(c)) { i++; continue; }
                if (c == '(' || c == ')' || c == ',') { t.Add(c.ToString()); i++; continue; }
                if (c == '"' || c == '\'')
                {
                    int j = s.IndexOf(c, i + 1);
                    if (j < 0) throw new ParseError("Unclosed quote");
                    t.Add("\u0001" + s[(i + 1)..j]); // marker: quoted literal
                    i = j + 1; continue;
                }
                if ("<>=!~".IndexOf(c) >= 0)
                {
                    int j = i + 1;
                    while (j < s.Length && "<>=!~".IndexOf(s[j]) >= 0) j++;
                    t.Add(s[i..j]); i = j; continue;
                }
                {
                    int j = i;
                    while (j < s.Length && !char.IsWhiteSpace(s[j]) && "(),<>=!~\"'".IndexOf(s[j]) < 0) j++;
                    t.Add(s[i..j]); i = j;
                }
            }
            return t;
        }

        class Parser
        {
            readonly List<string> _t; int _p;
            public Parser(List<string> t) { _t = t; }
            string? Peek => _p < _t.Count ? _t[_p] : null;
            bool IsKw(string k) => Peek != null && !Peek.StartsWith('\u0001') && Peek.Equals(k, StringComparison.OrdinalIgnoreCase);

            public Func<FileSystemInfo, bool> ParseAll()
            {
                if (_t.Count == 0) throw new ParseError("Empty expression");
                var n = Or();
                if (Peek != null) throw new ParseError($"Unexpected \"{Peek.TrimStart('\u0001')}\"");
                return n;
            }

            Func<FileSystemInfo, bool> Or()
            {
                var l = And();
                while (IsKw("or")) { _p++; var a = l; var r = And(); l = f => a(f) || r(f); }
                return l;
            }

            Func<FileSystemInfo, bool> And()
            {
                var l = Not();
                while (IsKw("and")) { _p++; var a = l; var r = Not(); l = f => a(f) && r(f); }
                return l;
            }

            Func<FileSystemInfo, bool> Not()
            {
                if (IsKw("not")) { _p++; var n = Not(); return f => !n(f); }
                return Primary();
            }

            Func<FileSystemInfo, bool> Primary()
            {
                if (Peek == "(")
                {
                    _p++; var n = Or();
                    if (Peek != ")") throw new ParseError("Missing )");
                    _p++; return n;
                }
                return Cmp();
            }

            string Next(string what)
            {
                if (Peek == null) throw new ParseError($"Expected {what}");
                return _t[_p++];
            }

            Func<FileSystemInfo, bool> Cmp()
            {
                var field = Next("a field").ToLowerInvariant();
                switch (field)
                {
                    case "dir": return f => f is DirectoryInfo;
                    case "file": return f => f is FileInfo;
                    case "hidden": return f => (f.Attributes & FileAttributes.Hidden) != 0;
                }

                bool numeric = field is "size" or "modified" or "created";
                if (!numeric && field is not ("name" or "ext" or "path" or "folder" or "tag" or "status" or "text"))
                    throw new ParseError($"Unknown field \"{field}\"");

                if (IsKw("in"))
                {
                    _p++;
                    if (Peek != "(") throw new ParseError("Expected ( after in");
                    _p++;
                    var vals = new List<string>();
                    while (true)
                    {
                        vals.Add(Next("a value").TrimStart('\u0001'));
                        if (Peek == ",") { _p++; continue; }
                        if (Peek == ")") { _p++; break; }
                        throw new ParseError("Expected , or ) in list");
                    }
                    if (numeric) throw new ParseError("'in' works on name, ext and path");
                    var set = vals.Select(v => field == "ext" ? v.TrimStart('.') : v).ToList();
                    return f => Texts(f, field).Any(t => set.Any(v => t.Equals(v, StringComparison.OrdinalIgnoreCase)));
                }

                var op = Next("an operator");
                var raw = Next("a value");
                var val = raw.TrimStart('\u0001');

                if (numeric)
                {
                    double target = field == "size" ? ParseSize(val) : ParseDays(val);
                    Func<double, bool> test = op switch
                    {
                        "=" or "==" => x => Math.Abs(x - target) < 1e-9,
                        "!=" => x => Math.Abs(x - target) >= 1e-9,
                        "<" => x => x < target,
                        ">" => x => x > target,
                        "<=" => x => x <= target,
                        ">=" => x => x >= target,
                        _ => throw new ParseError($"Operator {op} doesn't work on {field}")
                    };
                    return f => test(Number(f, field));
                }

                if (field == "ext") val = val.TrimStart('.');
                switch (op)
                {
                    case "=" or "==": return f => Texts(f, field).Any(t => t.Equals(val, StringComparison.OrdinalIgnoreCase));
                    case "!=": return f => !Texts(f, field).Any(t => t.Equals(val, StringComparison.OrdinalIgnoreCase));
                    case "~": return f => Texts(f, field).Any(t => t.Contains(val, StringComparison.OrdinalIgnoreCase));
                    case "~~":
                        Regex rx;
                        try { rx = new Regex(val, RegexOptions.IgnoreCase); }
                        catch (ArgumentException e) { throw new ParseError("Bad regex: " + e.Message); }
                        return f => Texts(f, field).Any(t => rx.IsMatch(t));
                    default: throw new ParseError($"Operator {op} doesn't work on {field}");
                }
            }

            /// <summary>Text fields can hold several values (tag); a comparison holds if any value satisfies it.</summary>
            static IEnumerable<string> Texts(FileSystemInfo f, string field)
            {
                switch (field)
                {
                    case "name": return new[] { f.Name };
                    case "ext": return new[] { f.Extension.TrimStart('.') };
                    case "folder": return new[] { Path.GetDirectoryName(f.FullName) ?? "" };
                    case "tag": return VaultMeta.Of(f).Tags;
                    case "status": return new[] { VaultMeta.Of(f).Status };
                    case "text": return new[] { VaultMeta.Body(f) };
                    default: return new[] { f.FullName };
                }
            }

            static double Number(FileSystemInfo f, string field) => field switch
            {
                "size" => f is FileInfo fi ? fi.Length : 0,
                "modified" => (DateTime.Now - f.LastWriteTime).TotalDays,
                _ => (DateTime.Now - f.CreationTime).TotalDays,
            };

            static (double n, string unit) Split(string v)
            {
                int i = 0;
                while (i < v.Length && (char.IsDigit(v[i]) || v[i] == '.')) i++;
                if (i == 0 || !double.TryParse(v[..i], System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out var n))
                    throw new ParseError($"\"{v}\" is not a number");
                return (n, v[i..].Trim().ToLowerInvariant());
            }

            static double ParseSize(string v)
            {
                var (n, u) = Split(v);
                return u switch
                {
                    "" or "b" => n, "kb" or "k" => n * 1024, "mb" or "m" => n * 1024 * 1024,
                    "gb" or "g" => n * 1024 * 1024 * 1024,
                    _ => throw new ParseError($"Unknown size unit \"{u}\" (use b, kb, mb, gb)")
                };
            }

            static double ParseDays(string v)
            {
                var (n, u) = Split(v);
                return u switch
                {
                    "" or "d" => n, "h" => n / 24, "w" => n * 7,
                    _ => throw new ParseError($"Unknown time unit \"{u}\" (use h, d, w)")
                };
            }
        }
    }
}
