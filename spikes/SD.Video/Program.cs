using System.Diagnostics;
using MetadataExtractor;
using Vanara.PInvoke;
using Vanara.Windows.Shell;
using Directory = System.IO.Directory;

// Spike D — video: poster frames + import tokens + file-lock-vs-culling check.
//   poster frame  : same shell IShellItemImageFactory path as Spike A (also decodes a frame via
//                   Media Foundation -> doubles as an HEVC-codec-present check: it fails if absent)
//   tokens        : MetadataExtractor on the MP4/MOV container (creation date, model, dims, duration)
//   lock check    : after reading, can we rename the file? (our read path must not hold a lock,
//                   or the culling move/delete flow breaks)
// MediaPlayerElement playback itself is WinUI + visual -> a human step; but if the poster frame
// decodes, Media Foundation can decode the codec, which is what the player uses.

class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        var vids = (args.Length > 0 ? args : FindVideos()).Where(File.Exists).ToArray();
        if (vids.Length == 0)
        {
            Console.WriteLine("No video found. Drop a short .mp4/.mov into testdata/ (H.264, and HEVC if you have one),");
            Console.WriteLine("or record 2s on the camera and grab it:  SC.Import -- --grab MOV");
            return 0;
        }

        var outDir = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(vids[0])!, "..", "out_spikeD"));
        Directory.CreateDirectory(outDir);

        foreach (var v in vids)
        {
            var name = Path.GetFileName(v);
            Console.WriteLine($"\n#### {name}  ({new FileInfo(v).Length / 1048576.0:F1} MB) ####");

            var (g, gd) = Poster(v, 256, null);
            var (p, pd) = Poster(v, 1280, Path.Combine(outDir, name + ".poster.png"));
            Console.WriteLine($"poster:  grid256 {Fmt(g)} {gd}   preview1280 {Fmt(p)} {pd}"
                            + (g < 0 ? "   <- FAIL: no thumbnail (codec/HEVC extension missing?)" : ""));

            Console.WriteLine("tokens:");
            DumpTokens(v);

            Console.WriteLine($"lock:    {LockTest(v)}");
        }
        Console.WriteLine($"\nPosters -> {outDir}");
        return 0;
    }

    // Shell poster frame (same path as Spike A). ThumbnailOnly throws if there's no real thumbnail.
    static (double ms, string dim) Poster(string path, int px, string? saveTo)
    {
        try
        {
            using var si = new ShellItem(path);
            var opt = ShellItemGetImageOptions.ThumbnailOnly | ShellItemGetImageOptions.BiggerSizeOk;
            var sw = Stopwatch.StartNew();
            using var h = si.GetImage(new SIZE(px, px), opt);
            sw.Stop();
            var dim = "-";
            if (h is not null && !h.IsInvalid)
            {
                using var bmp = System.Drawing.Image.FromHbitmap(h.DangerousGetHandle());
                dim = $"{bmp.Width}x{bmp.Height}";
                if (saveTo is not null) bmp.Save(saveTo);
            }
            return (sw.Elapsed.TotalMilliseconds, dim);
        }
        catch (Exception e) { return (-1, e.Message.Split('\n')[0].Trim()); }
    }

    // Show the tokens an import pattern could use from the video container.
    static void DumpTokens(string path)
    {
        try
        {
            var dirs = ImageMetadataReader.ReadMetadata(path);
            Console.WriteLine("  dirs: " + string.Join(", ", dirs.Select(d => d.Name)));
            foreach (var d in dirs)
                foreach (var t in d.Tags)
                {
                    var n = t.Name.ToLowerInvariant();
                    if (n.Contains("creat") || n.Contains("date") || n.Contains("model") || n.Contains("make")
                        || n.Contains("manufact") || n.Contains("duration") || n.Contains("width") || n.Contains("height"))
                        Console.WriteLine($"    {d.Name}/{t.Name} = {t.Description}");
                }
        }
        catch (Exception e) { Console.WriteLine("  metadata FAIL: " + e.Message.Split('\n')[0].Trim()); }
    }

    // Our read path must not lock the file, or culling (move/delete) breaks.
    static string LockTest(string path)
    {
        var tmp = path + ".locktest";
        try { File.Move(path, tmp); File.Move(tmp, path); return "no lock — rename OK (culling move/delete is safe)"; }
        catch (Exception e)
        {
            if (File.Exists(tmp)) { try { File.Move(tmp, path); } catch { } }
            return "LOCKED: " + e.Message.Split('\n')[0].Trim();
        }
    }

    static string Fmt(double ms) => ms < 0 ? "FAIL" : $"{ms:F1}ms";

    static string[] FindVideos()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
        {
            var td = Path.Combine(d.FullName, "testdata");
            if (Directory.Exists(td))
                return Directory.EnumerateFiles(td)
                    .Where(f => new[] { ".mp4", ".mov", ".m4v", ".avi", ".mts" }.Contains(Path.GetExtension(f).ToLowerInvariant()))
                    .ToArray();
        }
        return [];
    }
}
