using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;

namespace RAWimp.App;

// On-disk thumbnail cache: %LOCALAPPDATA%\RAWimp\thumbs\<ab>\<hash>.thumb
// Stores the raw bytes the shell handed us (JPEG/PNG), so a cache hit decodes as a plain image and
// never re-enters the Windows RAW codec — the expensive (and crash-prone) part of a RAW folder.
// Disposable + self-healing: delete the folder and it rebuilds.
static class ThumbCache
{
    private static readonly string Dir = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RAWimp", "thumbs");

    // Key covers mtime + length so an edited file re-thumbnails instead of showing a stale image.
    public static string? KeyFor(string path, uint size)
    {
        try
        {
            var fi = new FileInfo(path);
            if (!fi.Exists) return null;
            var raw = $"{path.ToLowerInvariant()}|{fi.LastWriteTimeUtc.Ticks}|{fi.Length}|{size}";
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw)));
        }
        catch { return null; }
    }

    private static string FileFor(string key) =>
        System.IO.Path.Combine(Dir, key[..2], key + ".thumb");

    public static async Task<byte[]?> TryReadAsync(string key)
    {
        try
        {
            var f = FileFor(key);
            if (!File.Exists(f)) return null;
            var bytes = await File.ReadAllBytesAsync(f);
            KeepAlive(f);
            return bytes;
        }
        catch { return null; }
    }

    /// Mark a cache entry as recently used, so eviction drops genuinely cold thumbnails rather than
    /// the folder you browse every day.
    ///
    /// Windows disables last-access-time tracking by default, so the write time is what we have to
    /// work with. Touching it on every hit would mean a metadata write per thumbnail; once a day is
    /// enough to keep hot entries at the young end of the queue.
    private static void KeepAlive(string file)
    {
        try
        {
            if (File.GetLastWriteTimeUtc(file) > DateTime.UtcNow.AddDays(-1)) return;
            File.SetLastWriteTimeUtc(file, DateTime.UtcNow);
        }
        catch { /* a cache entry we can't touch is not worth failing a thumbnail over */ }
    }

    /// Total bytes on disk (for the settings dialog). Cheap enough for a few thousand small files.
    public static long SizeBytes()
    {
        try
        {
            return !Directory.Exists(Dir) ? 0
                : new DirectoryInfo(Dir).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);
        }
        catch { return 0; }
    }

    /// Delete every cached thumbnail. Safe: the cache is disposable and rebuilds on demand.
    public static void Clear()
    {
        try { if (Directory.Exists(Dir)) Directory.Delete(Dir, recursive: true); }
        catch (Exception e) { Diag.Log("thumb cache clear: " + e.Message); }
    }

    public static async Task WriteAsync(string key, byte[] bytes)
    {
        try
        {
            var f = FileFor(key);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(f)!);
            var tmp = f + ".tmp";                       // write-then-rename: no torn files on crash
            await File.WriteAllBytesAsync(tmp, bytes);
            File.Move(tmp, f, overwrite: true);
            CountWrite();
        }
        catch (Exception e) { Diag.Log("thumb cache write: " + e.Message); }
    }

    // --- size cap ---

    /// Cache limit in bytes; 0 means no limit. Set from settings at startup.
    public static long LimitBytes;

    private static int _writesSinceCheck;
    private static int _trimming;

    /// Measuring the whole cache means walking it, so don't do that per write — a browse session
    /// adds thumbnails steadily, and checking every few hundred is soon enough to hold the ceiling.
    private static void CountWrite()
    {
        if (LimitBytes <= 0) return;
        if (Interlocked.Increment(ref _writesSinceCheck) < 300) return;
        Interlocked.Exchange(ref _writesSinceCheck, 0);
        TrimInBackground();
    }

    /// Bring the cache under its limit, oldest entries first. Safe to call at any time; overlapping
    /// calls collapse into one.
    public static void TrimInBackground()
    {
        if (LimitBytes <= 0) return;
        if (Interlocked.Exchange(ref _trimming, 1) == 1) return;   // one trim at a time

        _ = Task.Run(() =>
        {
            try { Trim(LimitBytes); }
            catch (Exception e) { Diag.Log("thumb cache trim: " + e.Message); }
            finally { Interlocked.Exchange(ref _trimming, 0); }
        });
    }

    private static void Trim(long limitBytes)
    {
        if (!Directory.Exists(Dir)) return;

        var files = new DirectoryInfo(Dir).EnumerateFiles("*", SearchOption.AllDirectories).ToList();
        var total = files.Sum(f => f.Length);
        if (total <= limitBytes) return;

        // Clear down to 90% so a trim isn't triggered again on the very next write.
        var target = (long)(limitBytes * 0.9);
        var removed = 0;

        foreach (var file in files.OrderBy(f => f.LastWriteTimeUtc))
        {
            if (total <= target) break;
            try { total -= file.Length; file.Delete(); removed++; }
            catch { /* in use or already gone — skip it */ }
        }

        Diag.Log($"thumb cache trimmed: removed {removed} files, now {total / 1048576.0:F0} MB");
    }
}

// Serial thumbnail work queue.
//   Reset()   -> folder switched: everything already queued is abandoned (generation bump)
//   Pause()   -> big preview opened: stop generating until the user returns to the grid
//   Resume()  -> back in the grid: carry on
// One item at a time by design: concurrent decodes of RAW thumbnails crash the Windows codec, and
// serial keeps the UI responsive since the decode itself is async.
static class ThumbLoader
{
    private static readonly Channel<PhotoItem> Queue =
        Channel.CreateUnbounded<PhotoItem>(new UnboundedChannelOptions { SingleReader = true });

    private static int _generation;
    private static TaskCompletionSource? _pause;

    public static int Generation => Volatile.Read(ref _generation);

    /// Abandon queued work (folder change). Items keep their generation stamp, so stale ones are
    /// dropped when the pump reaches them — no need to drain the queue.
    public static void Reset() => Interlocked.Increment(ref _generation);

    public static void Pause() => _pause ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

    public static void Resume()
    {
        var p = _pause;
        _pause = null;
        p?.TrySetResult();
    }

    public static void Enqueue(PhotoItem item) => Queue.Writer.TryWrite(item);

    /// Start the pump. Call once from the UI thread — continuations then land back on the UI thread,
    /// which is where XAML bitmaps must be created.
    public static void Start() => _ = PumpAsync();

    private static async Task PumpAsync()
    {
        await foreach (var item in Queue.Reader.ReadAllAsync())
        {
            try
            {
                if (_pause is { } p) await p.Task;        // preview open -> hold
                if (item.Generation != Generation) continue;   // stale folder -> skip cheaply
                await item.LoadThumbAsync();
            }
            catch (Exception e) { Diag.Log("thumb pump: " + e.Message); }
        }
    }
}
