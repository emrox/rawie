using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;

namespace Rawie.App;

// On-disk thumbnail cache: %LOCALAPPDATA%\Rawie\thumbs\<ab>\<hash>.thumb
// Stores the raw bytes the shell handed us (JPEG/PNG), so a cache hit decodes as a plain image and
// never re-enters the Windows RAW codec — the expensive (and crash-prone) part of a RAW folder.
// Disposable + self-healing: delete the folder and it rebuilds.
static class ThumbCache
{
    private static readonly string Dir = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Rawie", "thumbs");

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
            return File.Exists(f) ? await File.ReadAllBytesAsync(f) : null;
        }
        catch { return null; }
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
        }
        catch (Exception e) { Diag.Log("thumb cache write: " + e.Message); }
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
                item.LoadRating();
                await item.LoadThumbAsync();
            }
            catch (Exception e) { Diag.Log("thumb pump: " + e.Message); }
        }
    }
}
