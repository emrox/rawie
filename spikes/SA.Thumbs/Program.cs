using System.Diagnostics;
using NetVips;
using Vanara.PInvoke;
using Vanara.Windows.Shell;
using S0;                       // Bench (linked from Spike 0)
using Drawing = System.Drawing;

// Spike A — thumbnail/preview speed.
// Compares two extract-don't-decode strategies over the corpus:
//   shell : IShellItemImageFactory (what Explorer shows; needs an OS codec per format)
//   vips  : libvips direct decode+resize (JPEG/HEIC/TIFF/PNG only — no RAW loader)
// Probes at grid size (256) and preview size (2048) to reveal embedded-preview resolution.
// LibRaw (codec-independent embedded-preview) is the intended fallback — added once its
// native binary is available; see FINDINGS. Numbers are WARM (OS thumbnail cache).

class Program
{
    static readonly HashSet<string> Exts = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".heic", ".heif", ".tif", ".tiff", ".png",
        ".cr2", ".cr3", ".nef", ".nrw", ".arw", ".raf", ".orf", ".rw2", ".pef", ".dng", ".x3f"
    };

    const int Grid = 256, Preview = 2048, Iters = 15;
    const ShellItemGetImageOptions ThumbOpt =
        ShellItemGetImageOptions.ThumbnailOnly | ShellItemGetImageOptions.BiggerSizeOk;

    [STAThread]
    static int Main(string[] args)
    {
        var dir = args.Length > 0 ? Path.GetFullPath(args[0]) : FindCorpus(AppContext.BaseDirectory);
        if (dir is null) { Console.WriteLine("No testdata/ found."); return 0; }

        var files = Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
            .Where(f => Exts.Contains(Path.GetExtension(f)))
            .OrderBy(f => f).ToList();
        if (files.Count == 0) { Console.WriteLine($"No images in {dir} — add samples per README."); return 0; }

        var outDir = Path.GetFullPath(Path.Combine(dir, "..", "out_spikeA"));
        Directory.CreateDirectory(outDir);

        Console.WriteLine($"libvips {NetVips.NetVips.Version(0)}.{NetVips.NetVips.Version(1)}.{NetVips.NetVips.Version(2)}"
                        + $"   {files.Count} files   warm-cache p50 over {Iters} iters\n");
        Console.WriteLine($"{"file",-20}{"MB",6}   {"shell256",-9}{"dim",-11}{"shell2048",-10}{"dim",-12}{"vips256",-9}{"dim",-10}");

        var csv = new List<string> { "file,ext,mb,shell256_ms,shell256_dim,shell2048_ms,shell2048_dim,vips256_ms,vips256_dim" };
        foreach (var f in files)
        {
            var name = Path.GetFileName(f);
            var mb = new FileInfo(f).Length / 1048576.0;
            var (g, gd) = ShellMs(f, Grid, null);
            var (p, pd) = ShellMs(f, Preview, Path.Combine(outDir, name + ".shell.png"));
            var (v, vd) = VipsMs(f, Grid, Path.Combine(outDir, name + ".vips.webp"));
            Console.WriteLine($"{name,-20}{mb,6:F1}   {Cell(g),-9}{gd,-11}{Cell(p),-10}{pd,-12}{Cell(v),-9}{vd,-10}");
            csv.Add($"{name},{Path.GetExtension(f).TrimStart('.').ToLower()},{mb:F1},{Num(g)},{gd},{Num(p)},{pd},{Num(v)},{vd}");
        }
        File.WriteAllLines(Path.Combine(outDir, "results.csv"), csv);
        Console.WriteLine($"\nSample outputs + results.csv  ->  {outDir}");

        Throughput(files);
        return 0;
    }

    // (p50 ms, "WxH") on success; (-1, short reason) on failure — a failure IS the coverage answer.
    static (double ms, string dim) ShellMs(string path, int px, string? saveTo)
    {
        try
        {
            using var si = new ShellItem(path);
            Gdi32.SafeHBITMAP? h = null;
            var r = Bench.Run("shell", Iters, () => { h?.Dispose(); h = si.GetImage(new SIZE(px, px), ThumbOpt); });
            var dim = "-";
            if (h is not null && !h.IsInvalid)
            {
                using var bmp = Drawing.Image.FromHbitmap(h.DangerousGetHandle());
                dim = $"{bmp.Width}x{bmp.Height}";
                if (saveTo is not null) bmp.Save(saveTo);
            }
            h?.Dispose();
            return (r.P50, dim);
        }
        catch (Exception e) { return (-1, Reason(e)); }
    }

    static (double ms, string dim) VipsMs(string path, int px, string saveTo)
    {
        try
        {
            Image? img = null;
            var r = Bench.Run("vips", Iters, () => { img?.Dispose(); img = Image.Thumbnail(path, px); });
            var dim = img is null ? "-" : $"{img.Width}x{img.Height}";
            try { img?.WriteToFile(saveTo); } catch { /* webp save optional — don't taint timing */ }
            img?.Dispose();
            return (r.P50, dim);
        }
        catch (Exception e) { return (-1, Reason(e)); }
    }

    // Serial vs parallel wall-clock producing one 256 thumb per file (warm cache).
    static void Throughput(List<string> files)
    {
        int One(string f) { try { using var si = new ShellItem(f); using var b = si.GetImage(new SIZE(Grid, Grid), ThumbOpt); return 1; } catch { return 0; } }

        var sw = Stopwatch.StartNew();
        var okS = files.Sum(One);
        var serial = sw.Elapsed.TotalSeconds;

        sw.Restart();
        var okP = 0;
        Parallel.ForEach(files, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount },
            f => { if (One(f) == 1) Interlocked.Increment(ref okP); });
        var par = sw.Elapsed.TotalSeconds;

        Console.WriteLine($"\nThroughput (shell {Grid}, {files.Count} files):");
        Console.WriteLine($"  serial    {okS}/{files.Count} ok   {files.Count / serial,6:F1} img/s");
        Console.WriteLine($"  parallel  {okP}/{files.Count} ok   {files.Count / par,6:F1} img/s   (DOP {Environment.ProcessorCount})");
    }

    static string Cell(double ms) => ms < 0 ? "FAIL" : $"{ms:F1}ms";
    static string Num(double ms) => ms < 0 ? "" : $"{ms:F2}";

    static string Reason(Exception e)
    {
        if (e is NetVips.VipsException) return "no-loader";
        var m = e.InnerException?.Message ?? e.Message;
        m = m.Split('\n')[0].Trim();
        return m.Length > 18 ? m[..18] : m;
    }

    static string? FindCorpus(string start)
    {
        for (var d = new DirectoryInfo(start); d is not null; d = d.Parent)
        {
            var c = Path.Combine(d.FullName, "testdata");
            if (Directory.Exists(c)) return c;
        }
        return null;
    }
}
