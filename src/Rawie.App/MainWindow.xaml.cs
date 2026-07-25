using System.Collections.ObjectModel;
using System.IO;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.System;
using WinRT.Interop;

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

    public MainWindow()
    {
        InitializeComponent();
        Title = "Rawie";
        Grid.ItemsSource = _items;

        var start = FindTestData();
        if (start is not null) LoadFolder(start);
    }

    private void LoadFolder(string path)
    {
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
    }

    // Virtualization: fires as cells scroll into view -> load only realized thumbnails.
    private void OnContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (!args.InRecycleQueue && args.Item is PhotoItem p)
            _ = p.LoadThumbAsync();
    }

    private async void OnItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is PhotoItem p) await OpenDefault(p);
    }

    private async void OnGridKeyDown(object sender, KeyRoutedEventArgs e)
    {
        // GridView already handles arrow keys for selection; Enter opens with the OS default app.
        if (e.Key == VirtualKey.Enter && Grid.SelectedItem is PhotoItem p)
        {
            await OpenDefault(p);
            e.Handled = true;
        }
    }

    private static async Task OpenDefault(PhotoItem p)
    {
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(p.Path);
            await Launcher.LaunchFileAsync(file);   // OS default program
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
