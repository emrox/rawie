using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Vanara.PInvoke;
using Vanara.Windows.Shell;
using Windows.Storage;
using Windows.Storage.FileProperties;
using Windows.Storage.Streams;

namespace Rawie.App;

// One file in the current folder. Two sources:
//   filesystem -> thumbnail via WinRT StorageFile.GetThumbnailAsync (fast shell cache)
//   camera/MTP -> thumbnail via Vanara ShellItem.GetImage (no filesystem path exists)
public sealed class PhotoItem : INotifyPropertyChanged
{
    public string Path { get; }
    public string Name { get; }
    private readonly ShellItem? _shell;   // non-null for camera/MTP items
    public bool IsShell => _shell is not null;

    public PhotoItem(string path)
    {
        Path = path;
        Name = System.IO.Path.GetFileName(path);
    }

    public PhotoItem(ShellItem shell)
    {
        _shell = shell;
        Name = shell.Name ?? "?";
        Path = shell.ParsingName ?? Name;
    }

    private ImageSource? _thumb;
    public ImageSource? Thumb { get => _thumb; private set { _thumb = value; Raise(); } }

    private bool _started;
    public async Task LoadThumbAsync(uint size = 256)
    {
        if (_started) return;
        _started = true;
        try
        {
            if (_shell is not null)
            {
                var png = await Task.Run(() => ShellImagePng(_shell, size));
                if (png is not null) Thumb = await ToBitmap(png);
                else Diag.Log($"thumb empty (shell): {Name}");
            }
            else
            {
                var file = await StorageFile.GetFileFromPathAsync(Path);
                using var t = await file.GetThumbnailAsync(ThumbnailMode.PicturesView, size, ThumbnailOptions.ResizeThumbnail);
                if (t is not null && t.Size > 0)
                {
                    var bmp = new BitmapImage();
                    await bmp.SetSourceAsync(t);
                    Thumb = bmp;
                }
            }
        }
        catch (Exception e) { Diag.Log($"thumb fail: {Name}: {e.Message}"); }
    }

    // Large preview for a camera item (same extraction at a bigger size).
    public async Task<ImageSource?> LoadShellPreviewAsync(uint size = 1600)
    {
        if (_shell is null) return null;
        var png = await Task.Run(() => ShellImagePng(_shell, size));
        return png is null ? null : await ToBitmap(png);
    }

    private static async Task<ImageSource> ToBitmap(byte[] png)
    {
        var ras = new InMemoryRandomAccessStream();
        using (var dw = new DataWriter(ras)) { dw.WriteBytes(png); await dw.StoreAsync(); dw.DetachStream(); }
        ras.Seek(0);
        var bi = new BitmapImage();
        await bi.SetSourceAsync(ras);
        return bi;
    }

    // MTP serves one image resource at a time; serialize + GC/retry on ERROR_BUSY (0x800700AA).
    private static readonly SemaphoreSlim ShellGate = new(1, 1);
    private static byte[]? ShellImagePng(ShellItem shell, uint size)
    {
        ShellGate.Wait();
        try
        {
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    using var h = shell.GetImage(new SIZE((int)size, (int)size),
                        ShellItemGetImageOptions.ThumbnailOnly | ShellItemGetImageOptions.BiggerSizeOk);
                    if (h is null || h.IsInvalid) return null;
                    using var bmp = System.Drawing.Image.FromHbitmap(h.DangerousGetHandle());
                    using var ms = new MemoryStream();
                    bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
                    return ms.ToArray();
                }
                catch (Exception e) when (attempt < 5 && (uint)e.HResult == 0x800700AA)
                {
                    GC.Collect(); GC.WaitForPendingFinalizers(); Thread.Sleep(150 * attempt);
                }
            }
        }
        finally { ShellGate.Release(); }
    }

    // No explicit Dispose of _shell: a background thumbnail task may still be extracting an image
    // from it when the folder changes — disposing the COM object out from under it crashes natively.
    // GC + Vanara's finalizer release the COM ref once no task references it. ponytail: transient
    // leak until GC, cheaper than tracking/cancelling in-flight tasks.

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Raise([CallerMemberName] string? n = null) => PropertyChanged?.Invoke(this, new(n));
}

// One row in the info/EXIF panel. Plain class with get-only props — x:Bind's type-info
// generator rejects record init-setters.
public sealed class ExifRow
{
    public string Label { get; }
    public string Value { get; }
    public ExifRow(string label, string value) { Label = label; Value = value; }
}

// A folder node in the left tree (bound-mode TreeView item). Children fill lazily on expand;
// a single placeholder child makes the expander chevron appear before real children load.
// Item is set for shell/camera folders (no filesystem path); null for filesystem folders.
public sealed class FolderNode
{
    public string Path { get; }
    public string Name { get; }
    public ShellItem? Item { get; set; }
    public ObservableCollection<FolderNode> Children { get; } = new();
    public bool Loaded { get; set; }

    public FolderNode(string path, string? name = null)
    {
        Path = path;
        Name = name ?? System.IO.Path.GetFileName(path.TrimEnd('\\', '/'));
        if (string.IsNullOrEmpty(Name)) Name = path;   // drive roots ("C:\") have no file name
    }
}
