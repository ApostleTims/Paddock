using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Paddock
{
    /// <summary>
    /// Moves stale files out of a fence into Documents\Paddock Archive\&lt;fence&gt;\yyyy-MM, never deletes,
    /// and journals every move so the last batch can be undone.
    /// </summary>
    public static class Archiver
    {
        /// <param name="Op">"move" (archive or rule-action move) or "copy" (rule-action copy; undo removes the copy).</param>
        public record Entry(string Batch, DateTime When, string From, string To, string Op = "move");

        public static readonly string Root =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Paddock Archive");
        static readonly string LogPath = Path.Combine(ConfigStore.Dir, "archive-log.json");
        static readonly JsonSerializerOptions Opts = new() { WriteIndented = true };

        static List<Entry> ReadLog()
        {
            try { if (File.Exists(LogPath)) return JsonSerializer.Deserialize<List<Entry>>(File.ReadAllText(LogPath), Opts) ?? new(); }
            catch { }
            return new();
        }

        static void WriteLog(List<Entry> log)
        {
            Directory.CreateDirectory(ConfigStore.Dir);
            var tmp = LogPath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(log, Opts));
            File.Move(tmp, LogPath, overwrite: true);
        }

        static string Safe(string s) =>
            string.Concat(s.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).Trim();

        static string Unique(string path)
        {
            if (!File.Exists(path) && !Directory.Exists(path)) return path;
            var dir = Path.GetDirectoryName(path)!;
            var name = Path.GetFileNameWithoutExtension(path);
            var ext = Path.GetExtension(path);
            for (int i = 2; ; i++)
            {
                var p = Path.Combine(dir, $"{name} ({i}){ext}");
                if (!File.Exists(p)) return p;
            }
        }

        /// <summary>Move the files; returns how many actually moved. Locked or vanished files are skipped.</summary>
        public static int Move(string fenceTitle, IEnumerable<FileInfo> files)
        {
            var batch = Guid.NewGuid().ToString("N");
            var log = ReadLog();
            int moved = 0;
            foreach (var f in files)
            {
                try
                {
                    var destDir = Path.Combine(Root, Safe(fenceTitle), f.LastWriteTime.ToString("yyyy-MM"));
                    Directory.CreateDirectory(destDir);
                    var dest = Unique(Path.Combine(destDir, f.Name));
                    File.Move(f.FullName, dest);
                    log.Add(new Entry(batch, DateTime.Now, f.FullName, dest));
                    moved++;
                }
                catch { /* in use, permission, or gone: leave it where it is */ }
            }
            if (moved > 0) WriteLog(log);
            return moved;
        }

        /// <summary>Rule actions: move or copy each file into <paramref name="destDir"/>(file), journaled like an archive batch.</summary>
        public static int Transfer(IEnumerable<FileInfo> files, Func<FileInfo, string> destDir, bool copy)
        {
            var batch = Guid.NewGuid().ToString("N");
            var log = ReadLog();
            int done = 0;
            foreach (var f in files)
            {
                try
                {
                    var dir = destDir(f);
                    if (string.Equals(Path.GetFullPath(dir).TrimEnd('\\'), f.DirectoryName!.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) continue;
                    Directory.CreateDirectory(dir);
                    var dest = Unique(Path.Combine(dir, f.Name));
                    if (copy) File.Copy(f.FullName, dest); else File.Move(f.FullName, dest);
                    log.Add(new Entry(batch, DateTime.Now, f.FullName, dest, copy ? "copy" : "move"));
                    done++;
                }
                catch { /* in use, permission, or gone: leave it */ }
            }
            if (done > 0) WriteLog(log);
            return done;
        }

        /// <summary>Rule action "rename": rename each file in place (never overwriting), journaled so Undo puts the old name back.</summary>
        public static int Rename(IEnumerable<FileInfo> files, Func<FileInfo, string?> newName)
        {
            var batch = Guid.NewGuid().ToString("N");
            var log = ReadLog();
            int done = 0;
            foreach (var f in files)
            {
                try
                {
                    var name = newName(f);
                    if (name == null || name.Equals(f.Name, StringComparison.Ordinal)) continue;
                    var dest = Unique(Path.Combine(f.DirectoryName!, name));
                    File.Move(f.FullName, dest);
                    log.Add(new Entry(batch, DateTime.Now, f.FullName, dest, "move"));
                    done++;
                }
                catch { /* in use, permission, or gone: leave it */ }
            }
            if (done > 0) WriteLog(log);
            return done;
        }

        /// <summary>Put the most recent batch back. Returns (restored, skipped).</summary>
        public static (int restored, int skipped) UndoLast()
        {
            var log = ReadLog();
            if (log.Count == 0) return (0, 0);
            var batch = log[^1].Batch;
            int ok = 0, skipped = 0;
            foreach (var e in log.Where(e => e.Batch == batch).Reverse().ToList())
            {
                try
                {
                    if (!File.Exists(e.To)) { skipped++; log.Remove(e); continue; }
                    if (e.Op == "copy")
                    {
                        // Only remove the copy if the original is still there and the copy is unchanged.
                        if (File.Exists(e.From) && new FileInfo(e.From).Length == new FileInfo(e.To).Length) { File.Delete(e.To); ok++; log.Remove(e); }
                        else { skipped++; }
                        continue;
                    }
                    Directory.CreateDirectory(Path.GetDirectoryName(e.From)!);
                    File.Move(e.To, Unique(e.From));
                    ok++; log.Remove(e);
                }
                catch { skipped++; }
            }
            WriteLog(log);
            return (ok, skipped);
        }
    }
}
