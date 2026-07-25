using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using MetadataExtractor;
using MetadataExtractor.Formats.Exif;
using Vanara.PInvoke;
using Vanara.Windows.Shell;
using Directory = System.IO.Directory;

// Spike C — import engine (drive/card path, the ship-first source).
// Proves the parts that are the same for a card OR a camera: read EXIF tokens, resolve a user
// pattern, copy with checksum verify + dedupe. WPD camera traversal/copy needs a physical device
// and is deferred (see FINDINGS). Default run imports testdata/ as a mock source, twice, to show
// copy-verify then dedupe-skip.

class Program
{
    const string DefaultPattern = @"{yyyy}\{yyyy}-{MM}-{dd}\{model}\{yyyy}{MM}{dd}_{HH}{mm}{ss}.{ext}";

    [STAThread]   // MTP shell items want an STA apartment
    static int Main(string[] args)
    {
        if (args is ["selfcheck", ..]) { SelfCheck(); return 0; }

        var opt = ParseArgs(args);
        var dest0 = opt.GetValueOrDefault("dest") ?? Path.Combine(Path.GetTempPath(), "rawie_sc_import");
        var pattern0 = opt.GetValueOrDefault("pattern") ?? DefaultPattern;

        if (args.Contains("--list")) { DumpComputer(); return 0; }
        if (args.Contains("--scan")) return ScanCamera(opt.GetValueOrDefault("camera"));
        if (args.Contains("--grab")) return Grab(opt.GetValueOrDefault("grab"), opt.GetValueOrDefault("camera"));

        if (args.Contains("--camera"))   // MTP camera (no drive letter, lives under This PC)
            return CameraImport(opt.GetValueOrDefault("camera"),
                                int.TryParse(opt.GetValueOrDefault("max"), out var mx) ? mx : 10, dest0, pattern0);

        var source = opt.GetValueOrDefault("source") ?? FindCorpus();
        if (source is null) { Console.WriteLine("No source. Pass --source <dir>, --camera, or add testdata/."); return 1; }

        ListDrives();

        var files = Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories)
            .Where(IsMedia).OrderBy(f => f).ToList();
        Console.WriteLine($"\nSource: {source}  ({files.Count} media files)\nDest:   {dest0}\nPattern: {pattern0}\n");
        if (files.Count == 0) return 0;

        Pass("PASS 1 (copy + verify)", files, dest0, pattern0);
        Pass("PASS 2 (re-run → dedupe)", files, dest0, pattern0);
        return 0;
    }

    static void Pass(string title, List<string> files, string dest, string pattern)
    {
        Console.WriteLine($"=== {title} ===");
        long bytes = 0; double secs = 0; int copied = 0, dup = 0, fail = 0;
        foreach (var f in files)
        {
            var r = Import(f, dest, pattern);
            var tag = r.Status;
            Console.WriteLine($"  {Path.GetFileName(f),-16} {r.When:yyyy-MM-dd HH:mm:ss}  {r.Model,-16} -> {r.RelDest,-46} [{tag}]");
            if (r.Status == "copied-verified") { copied++; bytes += r.Bytes; secs += r.Secs; }
            else if (r.Status == "dup-skip") dup++;
            else fail++;
        }
        var mbps = secs > 0 ? bytes / 1048576.0 / secs : 0;
        Console.WriteLine($"  -> copied {copied}, dup-skipped {dup}, failed {fail}"
                        + (copied > 0 ? $", {mbps:F0} MB/s" : "") + "\n");
    }

    // --- camera (MTP) import via the shell namespace (no drive letter) ---
    static int CameraImport(string? nameFilter, int max, string dest, string pattern)
    {
        var stage = Path.Combine(Path.GetTempPath(), "rawie_sc_cam_stage");
        Directory.CreateDirectory(stage);

        var devices = FindPortableDevices(nameFilter);
        Console.WriteLine($"Portable devices under This PC{(nameFilter is null ? "" : $" matching '{nameFilter}'")}:");
        foreach (var d in devices) Console.WriteLine($"  {d.Name}");
        if (devices.Count == 0) { Console.WriteLine("  (none — camera on, unlocked, in MTP/PTP mode?)"); return 1; }
        Console.WriteLine($"\nDest:   {dest}\nPattern: {pattern}\n");

        foreach (var dev in devices)
        {
            Console.WriteLine($"=== {dev.Name} ===");
            var media = new List<ShellItem>();
            Collect(dev, media, 0, max);
            Console.WriteLine($"  {media.Count} media item(s) (capped at {max})");

            long bytes = 0; double camSecs = 0; int ok = 0, dup = 0, fail = 0;
            foreach (var item in media)
            {
                string temp; long b; double s;
                try { (temp, b, s) = StreamToTemp(item, stage); }
                catch (Exception e) { Console.WriteLine($"  {item.Name,-20} STREAM-FAIL: {Short(e)}"); fail++; continue; }

                var r = Import(temp, dest, pattern);   // reuse engine: EXIF tokens, pattern, verify, dedupe
                try { File.Delete(temp); } catch { }

                bytes += b; camSecs += s;
                Console.WriteLine($"  {item.Name,-20} {r.When:yyyy-MM-dd HH:mm:ss} {r.Model,-14} -> {r.RelDest,-42} [{r.Status}]  {b / 1048576.0 / Math.Max(s, 1e-6):F0} MB/s");
                if (r.Status == "copied-verified") ok++; else if (r.Status == "dup-skip") dup++; else fail++;
            }
            var mbps = camSecs > 0 ? bytes / 1048576.0 / camSecs : 0;
            Console.WriteLine($"  -> imported {ok}, dedup {dup}, failed {fail}, camera transfer {mbps:F0} MB/s\n");
        }
        return 0;
    }

    static bool IsVideo(string name) => new[] { ".mp4", ".mov", ".m4v", ".avi", ".mts" }
        .Contains(Path.GetExtension(name).ToLowerInvariant());

    static int ScanCamera(string? nameFilter)
    {
        foreach (var dev in FindPortableDevices(nameFilter))
        {
            Console.WriteLine($"=== {dev.Name} ===");
            var media = new List<ShellItem>();
            Collect(dev, media, 0, 3000);
            foreach (var m in media) Console.WriteLine($"  {(IsVideo(m.Name) ? "VIDEO" : "photo")}  {m.Name}");
            Console.WriteLine($"  total {media.Count}, video {media.Count(x => IsVideo(x.Name))}");
        }
        return 0;
    }

    // Pull the first camera item whose name contains <substr> into testdata/ (for the other spikes).
    static int Grab(string? substr, string? nameFilter)
    {
        var td = FindCorpus() ?? Path.Combine(Path.GetTempPath(), "rawie_grab");
        Directory.CreateDirectory(td);
        foreach (var dev in FindPortableDevices(nameFilter))
        {
            var media = new List<ShellItem>();
            Collect(dev, media, 0, 3000);
            var hit = media.FirstOrDefault(m => substr is null || m.Name.Contains(substr, StringComparison.OrdinalIgnoreCase));
            if (hit is null) continue;
            var (temp, b, s) = StreamToTemp(hit, td);
            Console.WriteLine($"grabbed {hit.Name} -> {temp}  ({b / 1048576.0:F1} MB, {b / 1048576.0 / Math.Max(s, 1e-6):F0} MB/s)");
            return 0;
        }
        Console.WriteLine("no matching item found"); return 1;
    }

    static void DumpComputer()
    {
        var pc = new ShellFolder(Shell32.KNOWNFOLDERID.FOLDERID_ComputerFolder);
        var filter = FolderItemFilter.Folders | FolderItemFilter.NonFolders | FolderItemFilter.Storage | FolderItemFilter.IncludeHidden;
        Console.WriteLine("This PC children:");
        foreach (var c in pc.EnumerateChildren(filter, HWND.NULL))
        {
            string fsp; try { fsp = c.FileSystemPath ?? "<null>"; } catch (Exception e) { fsp = "<throws: " + Short(e) + ">"; }
            Console.WriteLine($"  name='{c.Name}'  folder={c.IsFolder}  fsPath={fsp}");
            Console.WriteLine($"      parsing='{c.ParsingName}'");
        }
    }

    static List<ShellItem> FindPortableDevices(string? nameFilter)
    {
        var list = new List<ShellItem>();
        var pc = new ShellFolder(Shell32.KNOWNFOLDERID.FOLDERID_ComputerFolder);
        foreach (var child in pc.EnumerateChildren(FolderItemFilter.Folders | FolderItemFilter.Storage, HWND.NULL))
        {
            bool isDrive; try { isDrive = !string.IsNullOrEmpty(child.FileSystemPath); } catch { isDrive = false; }
            if (isDrive) continue;   // drives have a filesystem path; MTP devices don't
            if (nameFilter is not null && !child.Name.Contains(nameFilter, StringComparison.OrdinalIgnoreCase)) continue;
            list.Add(child);
        }
        return list;
    }

    // ponytail: leaks intermediate folder COM wrappers — fine for a short-lived spike, not production.
    static void Collect(ShellItem item, List<ShellItem> media, int depth, int max)
    {
        if (media.Count >= max || depth > 12) return;
        if (!item.IsFolder) { if (IsMedia(item.Name)) media.Add(item); return; }
        ShellFolder folder; try { folder = new ShellFolder(item); } catch { return; }
        foreach (var child in folder.EnumerateChildren(FolderItemFilter.Folders | FolderItemFilter.NonFolders, HWND.NULL))
        {
            Collect(child, media, depth + 1, max);
            if (media.Count >= max) break;
        }
    }

    static (string temp, long bytes, double secs) StreamToTemp(ShellItem item, string stage)
    {
        var temp = Path.Combine(stage, item.Name);
        // MTP serves one transfer resource at a time; the prior stream's RCW only frees on GC.
        // ponytail: GC + retry on ERROR_BUSY (0x800700AA) — the pragmatic MTP fix, not elegant.
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                var sw = Stopwatch.StartNew();
                using (var src = item.GetStream(STGM.STGM_READ))
                using (var dst = File.Create(temp))
                    src.CopyTo(dst, 1 << 20);
                sw.Stop();
                return (temp, new FileInfo(temp).Length, sw.Elapsed.TotalSeconds);
            }
            catch (Exception e) when (attempt < 6 && (uint)e.HResult == 0x800700AA)
            {
                GC.Collect(); GC.WaitForPendingFinalizers();
                Thread.Sleep(200 * attempt);
            }
        }
    }

    static string Short(Exception e) => (e.InnerException?.Message ?? e.Message).Split('\n')[0].Trim();

    // --- import: tokens -> pattern -> verified copy with dedupe ---
    static ImportResult Import(string src, string destRoot, string pattern)
    {
        var t = ReadTokens(src);
        var rel = ResolvePattern(pattern, t).Replace('/', '\\');
        var dest = Path.Combine(destRoot, rel);
        var srcHash = Sha256(src);

        for (var seq = 0; File.Exists(dest); seq++)
        {
            if (Sha256(dest) == srcHash) return new(src, dest, destRoot, "dup-skip", t, 0, 0);
            dest = Path.Combine(destRoot, AppendSeq(rel, seq + 1));   // burst-shot collision, different content
        }

        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        var sw = Stopwatch.StartNew();
        File.Copy(src, dest);
        sw.Stop();
        var ok = Sha256(dest) == srcHash;
        return new(src, dest, destRoot, ok ? "copied-verified" : "VERIFY-FAIL", t, new FileInfo(src).Length, sw.Elapsed.TotalSeconds);
    }

    static Tokens ReadTokens(string path)
    {
        var when = File.GetLastWriteTime(path);   // fallback if no EXIF date
        string make = "", model = "";
        try
        {
            var dirs = ImageMetadataReader.ReadMetadata(path);
            var sub = dirs.OfType<ExifSubIfdDirectory>().FirstOrDefault();
            var ifd0 = dirs.OfType<ExifIfd0Directory>().FirstOrDefault();
            if (sub is not null && sub.TryGetDateTime(ExifDirectoryBase.TagDateTimeOriginal, out var dto)) when = dto;
            else if (ifd0 is not null && ifd0.TryGetDateTime(ExifDirectoryBase.TagDateTime, out var dt)) when = dt;
            make = ifd0?.GetDescription(ExifDirectoryBase.TagMake) ?? "";
            model = ifd0?.GetDescription(ExifDirectoryBase.TagModel) ?? "";
        }
        catch { /* unreadable metadata -> fallbacks */ }
        return new Tokens(when, make.Trim(), model.Trim(),
                          Path.GetExtension(path).TrimStart('.').ToLowerInvariant(),
                          Path.GetFileNameWithoutExtension(path));
    }

    static string ResolvePattern(string pattern, Tokens t) =>
        Regex.Replace(pattern, @"\{(\w+)\}", m => t.Resolve(m.Groups[1].Value));

    static void ListDrives()
    {
        Console.WriteLine("Drives:");
        foreach (var d in DriveInfo.GetDrives().Where(d => d.IsReady))
            Console.WriteLine($"  {d.Name,-4} {d.DriveType,-9} {d.DriveFormat}"
                            + (d.DriveType == DriveType.Removable ? "  <- removable (card reader)" : ""));
        // NB: MTP cameras are NOT drives — they need WPD/shell enumeration (deferred, see FINDINGS).
    }

    static string Sha256(string path)
    {
        using var s = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(s));
    }

    static string AppendSeq(string rel, int n)
    {
        var dir = Path.GetDirectoryName(rel) ?? "";
        var name = Path.GetFileNameWithoutExtension(rel);
        var ext = Path.GetExtension(rel);
        return Path.Combine(dir, $"{name}_{n:D3}{ext}");
    }

    static bool IsMedia(string f) => Exts.Contains(Path.GetExtension(f).ToLowerInvariant());
    static readonly HashSet<string> Exts = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".heic", ".heif", ".tif", ".tiff", ".png",
        ".cr2", ".cr3", ".nef", ".nrw", ".arw", ".raf", ".orf", ".rw2", ".pef", ".dng", ".x3f",
        ".mp4", ".mov", ".m4v", ".avi", ".mts"
    };

    static Dictionary<string, string> ParseArgs(string[] a)
    {
        var d = new Dictionary<string, string>();
        for (var i = 0; i < a.Length - 1; i++)
            if (a[i].StartsWith("--") && !a[i + 1].StartsWith("--")) d[a[i][2..]] = a[i + 1];
        return d;
    }

    static string? FindCorpus()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var td = Path.Combine(dir.FullName, "testdata");
            if (Directory.Exists(td)) return td;
        }
        return null;
    }

    static void SelfCheck()
    {
        var t = new Tokens(new DateTime(2016, 5, 16, 12, 34, 56), "FUJIFILM", "X-T10/2", "raf", "DSCF5221");
        var r = ResolvePattern(DefaultPattern, t);
        Assert(r == @"2016\2016-05-16\X-T10_2\20160516_123456.raf", $"pattern: {r}");
        Assert(t.Resolve("unknown") == "{unknown}", "unknown token preserved");
        Assert(AppendSeq(@"a\b.raf", 2) == @"a\b_002.raf", "append seq");
        Console.WriteLine("SelfCheck OK");
    }

    static void Assert(bool ok, string what) { if (!ok) throw new Exception("self-check failed: " + what); }
}

record Tokens(DateTime When, string Make, string Model, string Ext, string OriginalName)
{
    public string Resolve(string token) => token switch
    {
        "yyyy" or "MM" or "dd" or "HH" or "mm" or "ss" => When.ToString(token),
        "make" => Clean(Make),
        "model" or "camera" => Clean(Model),
        "ext" => Ext,
        "name" => Clean(OriginalName),
        _ => "{" + token + "}"      // unknown token left visible, not silently dropped
    };

    static string Clean(string s) => string.IsNullOrWhiteSpace(s) ? "unknown"
        : string.Concat(s.Select(c => Path.GetInvalidFileNameChars().Contains(c) || c == ' ' ? '_' : c));
}

record ImportResult(string Src, string Dest, string Root, string Status, Tokens T, long Bytes, double Secs)
{
    public string RelDest => Path.GetRelativePath(Root, Dest);
    public DateTime When => T.When;
    public string Model => T.Model is { Length: > 0 } m ? m : "(no model)";
}
