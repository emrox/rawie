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
