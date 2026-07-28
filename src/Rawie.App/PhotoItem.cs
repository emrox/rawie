using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Vanara.PInvoke;
using Vanara.Windows.Shell;
using Windows.Graphics.Imaging;
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

    /// The live ShellItem for camera items (do NOT dispose — background thumbnail work may use it).
    /// Filesystem items return null; callers create a short-lived ShellItem from Path instead.
    public ShellItem? ShellRef => _shell;

    /// Folders appear in the grid too (Explorer-style); double-click opens them.
    public bool IsFolder { get; }

    public PhotoItem(string path, bool isFolder = false)
    {
        Path = path;
        IsFolder = isFolder;
        Name = isFolder
            ? new DirectoryInfo(path).Name          // GetFileName is empty for "D:\" style paths
            : System.IO.Path.GetFileName(path);
    }

    public PhotoItem(ShellItem shell, bool isFolder = false)
    {
        _shell = shell;
        IsFolder = isFolder;
        Name = shell.Name ?? "?";
        Path = shell.ParsingName ?? Name;
    }

    private ImageSource? _thumb;
    public ImageSource? Thumb { get => _thumb; private set { _thumb = value; Raise(); } }

    // --- culling: star rating / reject flag, persisted in an XMP sidecar ---
    private static readonly SolidColorBrush Gold = new(Microsoft.UI.Colors.Gold);
    private static readonly SolidColorBrush Red = new(Microsoft.UI.Colors.OrangeRed);

    private int _rating;
    public int Rating
    {
        get => _rating;
        set
        {
            if (_rating == value) return;
            _rating = value;
            Raise(); Raise(nameof(RatingText)); Raise(nameof(RatingBrush)); Raise(nameof(RatingVisibility));
        }
    }

    public string RatingText => Rating switch
    {
        Xmp.Rejected => "✕",
        > 0 => new string('★', Rating),
        _ => ""
    };

    public Brush RatingBrush => Rating == Xmp.Rejected ? Red : Gold;

    // Hide the badge entirely when unrated — an empty box on every thumbnail is just noise.
    public Microsoft.UI.Xaml.Visibility RatingVisibility =>
        Rating == 0 ? Microsoft.UI.Xaml.Visibility.Collapsed : Microsoft.UI.Xaml.Visibility.Visible;

    // Selection is drawn by the item template rather than the GridViewItem chrome: the card in the
    // template is opaque and hides the container's own selection border, and the system focus ring
    // duplicated it. One border, always visible, whether or not the grid has focus.
    private static readonly SolidColorBrush Clear = new(Microsoft.UI.Colors.Transparent);
    private static SolidColorBrush? _accent;
    private static SolidColorBrush Accent => _accent ??=
        Microsoft.UI.Xaml.Application.Current.Resources["AccentFillColorDefaultBrush"] as SolidColorBrush
        ?? new SolidColorBrush(Microsoft.UI.Colors.DodgerBlue);

    private bool _isSelected;
    public bool IsSelected
    {
        get => _isSelected;
        set { if (_isSelected != value) { _isSelected = value; Raise(); Raise(nameof(SelectionBrush)); } }
    }

    public Brush SelectionBrush => IsSelected ? Accent : Clear;

    /// Read the stored rating (cheap: usually just a File.Exists miss).
    public void LoadRating()
    {
        if (!IsShell && !IsFolder) Rating = Xmp.Read(Path);
    }

    // The loader generation this item belongs to. The pump drops items whose generation is stale,
    // so switching folders abandons queued work instead of draining it.
    public int Generation { get; } = ThumbLoader.Generation;

    private bool _started;
    public async Task LoadThumbAsync(uint size = 256)
    {
        if (_started) return;
        _started = true;
        try
        {
            if (_shell is not null)
            {
                using var h = await Task.Run(() => ShellGetHBitmap(_shell, size, Generation));   // MTP off UI thread
                if (h is null || Generation != ThumbLoader.Generation) return;
                Thumb = await HBitmapToImage(h);
                return;
            }

            if (IsFolder)   // folder icon/preview from the shell; not worth caching
            {
                var dir = await StorageFolder.GetFolderFromPathAsync(Path);
                var ft = await dir.GetThumbnailAsync(ThumbnailMode.SingleItem, size, ThumbnailOptions.ResizeThumbnail);
                if (ft is null || ft.Size == 0 || Generation != ThumbLoader.Generation) return;
                var fbytes = new byte[ft.Size];
                await ft.AsStreamForRead().ReadExactlyAsync(fbytes);
                if (Generation != ThumbLoader.Generation) return;
                Thumb = await BytesToBitmap(fbytes);
                return;
            }

            // Cache hit: plain JPEG/PNG bytes -> never re-enters the RAW codec.
            var key = ThumbCache.KeyFor(Path, size);
            if (key is not null && await ThumbCache.TryReadAsync(key) is { } cached)
            {
                if (Generation != ThumbLoader.Generation) return;
                Thumb = await BytesToBitmap(cached);
                return;
            }

            // Miss: ask the shell (this is the slow, RAW-codec path), then cache the bytes.
            var file = await StorageFile.GetFileFromPathAsync(Path);
            if (Generation != ThumbLoader.Generation) return;
            var t = await file.GetThumbnailAsync(ThumbnailMode.PicturesView, size, ThumbnailOptions.ResizeThumbnail);
            if (t is null || t.Size == 0) return;

            var bytes = new byte[t.Size];
            await t.AsStreamForRead().ReadExactlyAsync(bytes);
            if (key is not null) await ThumbCache.WriteAsync(key, bytes);
            if (Generation != ThumbLoader.Generation) return;
            Thumb = await BytesToBitmap(bytes);
        }
        catch (Exception e) { Diag.Log($"thumb fail: {Name}: {e.Message}"); }
    }

    // Large preview for the big view: same cache + off-XAML decode as thumbnails, bigger size.
    // Cached under its own key (size is part of the key), so re-opening an image is instant and
    // never re-reads the RAW.
    public async Task<ImageSource?> LoadPreviewAsync(uint size = 1600)
    {
        if (_shell is not null) return await LoadShellPreviewAsync(size);
        try
        {
            var key = ThumbCache.KeyFor(Path, size);
            if (key is not null && await ThumbCache.TryReadAsync(key) is { } cached)
                return await BytesToBitmap(cached);

            var file = await StorageFile.GetFileFromPathAsync(Path);
            var t = await file.GetThumbnailAsync(ThumbnailMode.SingleItem, size, ThumbnailOptions.ResizeThumbnail);
            if (t is null || t.Size == 0) return null;

            var bytes = new byte[t.Size];
            await t.AsStreamForRead().ReadExactlyAsync(bytes);
            if (key is not null) await ThumbCache.WriteAsync(key, bytes);
            return await BytesToBitmap(bytes);
        }
        catch (Exception e) { Diag.Log($"preview fail: {Name}: {e.Message}"); return null; }
    }

    // Large preview for a camera item (same extraction at a bigger size).
    public async Task<ImageSource?> LoadShellPreviewAsync(uint size = 1600)
    {
        if (_shell is null) return null;
        using var h = await Task.Run(() => ShellGetHBitmap(_shell, size, Generation));
        return h is null ? null : await HBitmapToImage(h);
    }

    // MUST run on the UI thread: System.Drawing/GDI+ is not safe across threads. Called only after
    // an await, so we're back on the WinUI dispatcher.
    private static async Task<ImageSource?> HBitmapToImage(Gdi32.SafeHBITMAP h)
    {
        using var bmp = System.Drawing.Image.FromHbitmap(h.DangerousGetHandle());
        using var ms = new MemoryStream();
        bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
        var ras = new InMemoryRandomAccessStream();
        using (var dw = new DataWriter(ras)) { dw.WriteBytes(ms.ToArray()); await dw.StoreAsync(); dw.DetachStream(); }
        ras.Seek(0);
        return await ToWriteableBitmap(ras);
    }

    private static async Task<ImageSource?> BytesToBitmap(byte[] bytes)
    {
        var ras = new InMemoryRandomAccessStream();
        using (var dw = new DataWriter(ras)) { dw.WriteBytes(bytes); await dw.StoreAsync(); dw.DetachStream(); }
        ras.Seek(0);
        return await ToWriteableBitmap(ras);
    }

    // Decode a thumbnail/preview stream to a fully-formed WriteableBitmap. BitmapDecoder runs OUTSIDE
    // the XAML image pipeline, so nothing is mid-decode inside XAML when a grid container recycles
    // during a fast folder switch (BitmapImage.SetSourceAsync decodes inside XAML -> crashes on recycle).
    private static async Task<ImageSource?> ToWriteableBitmap(IRandomAccessStream stream)
    {
        var decoder = await BitmapDecoder.CreateAsync(stream);
        var pd = await decoder.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
            new BitmapTransform(), ExifOrientationMode.RespectExifOrientation, ColorManagementMode.DoNotColorManage);
        var bytes = pd.DetachPixelData();
        var wb = new WriteableBitmap((int)decoder.OrientedPixelWidth, (int)decoder.OrientedPixelHeight);
        using (var s = wb.PixelBuffer.AsStream()) await s.WriteAsync(bytes, 0, bytes.Length);
        return wb;
    }

    // Background only: the slow MTP GetImage. MTP serves one resource at a time -> serialize + GC/retry
    // on ERROR_BUSY. No System.Drawing here. Returns the HBITMAP for the UI thread to convert.
    private static readonly SemaphoreSlim ShellGate = new(1, 1);
    private static Gdi32.SafeHBITMAP? ShellGetHBitmap(ShellItem shell, uint size, int gen)
    {
        ShellGate.Wait();
        try
        {
            if (gen != ThumbLoader.Generation) return null;   // superseded while queued -> skip the MTP work
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    var h = shell.GetImage(new SIZE((int)size, (int)size),
                        ShellItemGetImageOptions.ThumbnailOnly | ShellItemGetImageOptions.BiggerSizeOk);
                    return h is null || h.IsInvalid ? null : h;
                }
                // ERROR_BUSY: just wait and retry. NO forced GC here — running Vanara's COM finalizers
                // on this background thread while COM is in use elsewhere raises E_UNEXPECTED (crash).
                catch (Exception e) when (attempt < 4 && (uint)e.HResult == 0x800700AA)
                {
                    Thread.Sleep(120 * attempt);
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
public sealed class FolderNode : INotifyPropertyChanged
{
    public string Path { get; }
    public string Name { get; }
    public ShellItem? Item { get; set; }          // set for camera/shell folders
    public bool IsDriveRoot { get; set; }         // set for drive-letter roots (for device refresh)
    public ObservableCollection<FolderNode> Children { get; } = new();
    public bool Loaded { get; set; }

    // Two-way bound to TreeViewItem.IsExpanded so we can expand nodes programmatically
    // (bound-mode TreeView gives no direct handle on its TreeViewNodes).
    private bool _isExpanded;
    public bool IsExpanded
    {
        get => _isExpanded;
        set { if (_isExpanded != value) { _isExpanded = value; PropertyChanged?.Invoke(this, new(nameof(IsExpanded))); } }
    }

    // Likewise for selection: setting TreeView.SelectedItem in bound mode doesn't paint the
    // highlight, but the container's own IsSelected does.
    private bool _isSelected;
    public bool IsSelected
    {
        get => _isSelected;
        set { if (_isSelected != value) { _isSelected = value; PropertyChanged?.Invoke(this, new(nameof(IsSelected))); } }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public FolderNode(string path, string? name = null)
    {
        Path = path;
        Name = name ?? System.IO.Path.GetFileName(path.TrimEnd('\\', '/'));
        if (string.IsNullOrEmpty(Name)) Name = path;   // drive roots ("C:\") have no file name
    }
}
