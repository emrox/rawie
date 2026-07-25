using System.Collections.ObjectModel;
using System.IO;
using MetadataExtractor;
using MetadataExtractor.Formats.Exif;
using MetadataExtractor.Formats.GeoTiff;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage;
using Windows.Storage.FileProperties;
using Windows.Storage.Pickers;
using Windows.System;
using WinRT.Interop;
using Directory = System.IO.Directory;

namespace Rawie.App;

public sealed partial class MainWindow : Window
{
    private static readonly HashSet<string> MediaExts = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".heic", ".heif", ".tif", ".tiff", ".png",
        ".cr2", ".cr3", ".nef", ".nrw", ".arw", ".raf", ".orf", ".rw2", ".pef", ".dng", ".x3f",
        ".mp4", ".mov", ".m4v", ".avi", ".mts"
    };

    private readonly ObservableCollection<PhotoItem> _items = new();
    public ObservableCollection<ExifRow> Exif { get; } = new();

    private bool _preview;
    private int _exifToken;   // guards against stale async EXIF/preview updates

    public MainWindow()
    {
        InitializeComponent();
        Title = "Rawie";
        ThumbGrid.ItemsSource = _items;

        // handledEventsToo: the GridView swallows Enter in grid mode, so a normal KeyDown never sees it.
        Root.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler(OnKeyDown), handledEventsToo: true);

        PopulateTreeRoots();

        var start = FindTestData();
        if (start is not null) LoadFolder(start);
    }

    // --- left folder tree (bound mode; children fill lazily on expand) ---
    public ObservableCollection<FolderNode> Roots { get; } = new();

    private void PopulateTreeRoots()
    {
        try
        {
            var pics = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
            if (Directory.Exists(pics)) Roots.Add(NewFolder(pics, "Pictures"));
            foreach (var drive in DriveInfo.GetDrives().Where(d => d.IsReady))
                Roots.Add(NewFolder(drive.RootDirectory.FullName, drive.Name));
        }
        catch (Exception e) { Diag.Log("tree roots fail: " + e.Message); }
    }

    private static FolderNode NewFolder(string path, string? name = null)
    {
        var fn = new FolderNode(path, name);
        if (HasSubdirs(path)) fn.Children.Add(new FolderNode(path, "…"));   // placeholder -> shows expander
        return fn;
    }

    private static bool HasSubdirs(string path)
    {
        try { return Directory.EnumerateDirectories(path).Any(d => !IsHiddenDir(d)); }
        catch { return false; }   // access-denied / not-ready volumes just show no expander
    }

    private static bool IsHiddenDir(string dir)
    {
        try { var a = File.GetAttributes(dir); return a.HasFlag(System.IO.FileAttributes.Hidden) || a.HasFlag(System.IO.FileAttributes.System); }
        catch { return true; }
    }

    private void OnFolderExpanding(TreeView sender, TreeViewExpandingEventArgs args)
    {
        if (args.Item is not FolderNode fn || fn.Loaded) return;
        fn.Loaded = true;
        fn.Children.Clear();   // drop the placeholder
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(fn.Path)
                         .Where(d => !IsHiddenDir(d))
                         .OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
                fn.Children.Add(NewFolder(dir));
        }
        catch (Exception e) { Diag.Log("expand fail: " + e.Message); }
    }

    private void OnFolderInvoked(TreeView sender, TreeViewItemInvokedEventArgs args)
    {
        if (args.InvokedItem is FolderNode fn) LoadFolder(fn.Path);
    }

    private void LoadFolder(string path)
    {
        if (_preview) ExitPreview();   // a new folder always lands in the grid
        _items.Clear();
        PathText.Text = path;
        try
        {
            foreach (var f in Directory.EnumerateFiles(path)
                         .Where(f => MediaExts.Contains(Path.GetExtension(f)))
                         .OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
                _items.Add(new PhotoItem(f));
        }
        catch (Exception e) { Diag.Log("LoadFolder failed: " + e.Message); }

        StatusText.Text = $"{_items.Count} items";
        Diag.Log($"loaded {_items.Count} items from {path}");
        if (_items.Count > 0) ThumbGrid.SelectedIndex = 0;
    }

    // --- thumbnails: load only realized (visible) cells ---
    private void OnContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (!args.InRecycleQueue && args.Item is PhotoItem p) _ = p.LoadThumbAsync();
    }

    // --- selection drives the info panel + (in preview mode) the big image ---
    private async void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ThumbGrid.SelectedItem is not PhotoItem p) return;
        var token = ++_exifToken;
        StatusText.Text = $"{ThumbGrid.SelectedIndex + 1} / {_items.Count}   {p.Name}";

        var rows = await Task.Run(() => ReadInfo(p.Path));
        if (token != _exifToken) return;   // selection moved on; drop stale result
        Exif.Clear();
        foreach (var r in rows) Exif.Add(r);
        Diag.Log($"info {p.Name}: {rows.Count} rows -> {string.Join(", ", rows.Take(4).Select(r => r.Label))}");

        if (_preview) await ShowPreview(p, token);
    }

    private async Task ShowPreview(PhotoItem p, int token)
    {
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(p.Path);
            using var t = await file.GetThumbnailAsync(ThumbnailMode.SingleItem, 1600, ThumbnailOptions.ResizeThumbnail);
            if (token != _exifToken) return;
            var bmp = new BitmapImage();
            await bmp.SetSourceAsync(t);
            if (token != _exifToken) return;
            PreviewImage.Source = bmp;
        }
        catch (Exception e) { Diag.Log("preview fail: " + e.Message); }
    }

    // --- keys: Enter toggles preview, Esc exits, ← → navigate in preview mode ---
    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        switch (e.Key)
        {
            case VirtualKey.Enter when ThumbGrid.SelectedItem is not null:
                if (_preview) ExitPreview(); else EnterPreview();
                e.Handled = true;
                break;
            case VirtualKey.Escape when _preview:
                ExitPreview(); e.Handled = true; break;
            case VirtualKey.Left when _preview:
                Move(-1); e.Handled = true; break;
            case VirtualKey.Right when _preview:
                Move(1); e.Handled = true; break;
        }
    }

    private void Move(int delta)
    {
        var i = ThumbGrid.SelectedIndex + delta;
        if (i >= 0 && i < _items.Count) ThumbGrid.SelectedIndex = i;
    }

    private void OnItemDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (ThumbGrid.SelectedItem is not null) EnterPreview();
    }

    private void EnterPreview()
    {
        _preview = true;
        ThumbGrid.Visibility = Visibility.Collapsed;
        PreviewPanel.Visibility = Visibility.Visible;
        PreviewPanel.Focus(FocusState.Programmatic);
        if (ThumbGrid.SelectedItem is PhotoItem p) _ = ShowPreview(p, _exifToken);
    }

    private void ExitPreview()
    {
        _preview = false;
        PreviewPanel.Visibility = Visibility.Collapsed;
        ThumbGrid.Visibility = Visibility.Visible;
        ThumbGrid.Focus(FocusState.Programmatic);
    }

    private void OnOpenExternalAccel(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (ThumbGrid.SelectedItem is PhotoItem p) { _ = OpenDefault(p); args.Handled = true; }
    }

    private static async Task OpenDefault(PhotoItem p)
    {
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(p.Path);
            await Launcher.LaunchFileAsync(file);
        }
        catch (Exception e) { Diag.Log("open failed: " + e.Message); }
    }

    private async void OnPickFolder(object sender, RoutedEventArgs e)
    {
        var picker = new FolderPicker();
        picker.FileTypeFilter.Add("*");
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
        var folder = await picker.PickSingleFolderAsync();
        if (folder is not null) LoadFolder(folder.Path);
    }

    // --- info/EXIF (MetadataExtractor, off the UI thread) ---
    private static List<ExifRow> ReadInfo(string path)
    {
        var rows = new List<ExifRow>();
        try { rows.Add(new ExifRow("File", Path.GetFileName(path))); } catch { }

        try
        {
            var dirs = ImageMetadataReader.ReadMetadata(path);
            var exif = dirs.OfType<ExifDirectoryBase>().ToList();
            var gps = dirs.OfType<GpsDirectory>().FirstOrDefault();

            // Search across every EXIF IFD (IFD0, the real Exif SubIFD, preview SubIFDs, …).
            // Different RAW makers scatter the same tags into different sub-IFDs, so picking one
            // directory misses fields (NEF/DNG put exposure/ISO in a SubIFD that isn't the first).
            string? Val(int tag) => exif.Select(d => d.GetDescription(tag))
                                        .FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
            void Add(string label, string? val) { if (!string.IsNullOrWhiteSpace(val)) rows.Add(new ExifRow(label, val!)); }

            var cam = string.Join(" ", new[] { Val(ExifDirectoryBase.TagMake), Val(ExifDirectoryBase.TagModel) }
                .Where(x => !string.IsNullOrWhiteSpace(x)));
            Add("Camera", cam);
            Add("Lens", Val(ExifDirectoryBase.TagLensModel));
            Add("Date taken", Val(ExifDirectoryBase.TagDateTimeOriginal) ?? Val(ExifDirectoryBase.TagDateTime));
            Add("Exposure", Val(ExifDirectoryBase.TagExposureTime));
            Add("Aperture", Val(ExifDirectoryBase.TagFNumber));
            Add("ISO", Val(ExifDirectoryBase.TagIsoEquivalent));
            Add("Focal length", Val(ExifDirectoryBase.TagFocalLength));
            Add("Exposure bias", Val(ExifDirectoryBase.TagExposureBias));
            Add("Flash", Val(ExifDirectoryBase.TagFlash));

            var w = Val(ExifDirectoryBase.TagExifImageWidth);
            var h = Val(ExifDirectoryBase.TagExifImageHeight);
            if (w is not null && h is not null) Add("Dimensions", $"{w} × {h}");
            if (gps?.GetGeoLocation() is { } loc) Add("GPS", $"{loc.Latitude:F5}, {loc.Longitude:F5}");
        }
        catch (Exception e) { Diag.Log("exif fail: " + e.Message); }

        try { rows.Add(new ExifRow("Size", $"{new FileInfo(path).Length / 1048576.0:F1} MB")); } catch { }
        return rows;
    }

    private static string? FindTestData()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
        {
            var td = Path.Combine(d.FullName, "testdata");
            if (Directory.Exists(td)) return td;
        }
        return null;
    }
}
