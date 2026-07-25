using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage;
using Windows.Storage.FileProperties;

namespace Rawie.App;

// One file in the current folder. Thumbnail loads lazily (only when its grid cell is realized),
// via the OS shell thumbnail cache — the fast, RAW/video-capable path proven in Spike A/D.
public sealed class PhotoItem : INotifyPropertyChanged
{
    public string Path { get; }
    public string Name { get; }

    public PhotoItem(string path)
    {
        Path = path;
        Name = System.IO.Path.GetFileName(path);
    }

    private ImageSource? _thumb;
    public ImageSource? Thumb { get => _thumb; private set { _thumb = value; Raise(); } }

    private bool _started;
    public async Task LoadThumbAsync(uint size = 256)
    {
        if (_started) return;   // load once
        _started = true;
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(Path);
            using var t = await file.GetThumbnailAsync(ThumbnailMode.PicturesView, size, ThumbnailOptions.ResizeThumbnail);
            if (t is not null && t.Size > 0)
            {
                var bmp = new BitmapImage();
                await bmp.SetSourceAsync(t);
                Thumb = bmp;
                Diag.Log($"thumb ok: {Name} ({t.OriginalWidth}x{t.OriginalHeight})");
            }
            else Diag.Log($"thumb empty: {Name}");
        }
        catch (Exception e) { Diag.Log($"thumb fail: {Name}: {e.Message}"); }
    }

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
public sealed class FolderNode
{
    public string Path { get; }
    public string Name { get; }
    public ObservableCollection<FolderNode> Children { get; } = new();
    public bool Loaded { get; set; }

    public FolderNode(string path, string? name = null)
    {
        Path = path;
        Name = name ?? System.IO.Path.GetFileName(path.TrimEnd('\\', '/'));
        if (string.IsNullOrEmpty(Name)) Name = path;   // drive roots ("C:\") have no file name
    }
}
