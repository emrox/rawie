using System.Collections.ObjectModel;
using System.IO;
using System.Runtime.InteropServices;
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
using Vanara.PInvoke;
using Vanara.Windows.Shell;
using Directory = System.IO.Directory;
using Visibility = Microsoft.UI.Xaml.Visibility;

namespace Rawie.App;

public sealed partial class MainWindow : Window
{
    private static readonly HashSet<string> MediaExts = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".heic", ".heif", ".tif", ".tiff", ".png",
        ".cr2", ".cr3", ".nef", ".nrw", ".arw", ".raf", ".orf", ".rw2", ".pef", ".dng", ".x3f",
        ".mp4", ".mov", ".m4v", ".avi", ".mts"
    };

    // The grid's items are assigned as a whole new list per folder (see LoadFolder). Adding items one
    // at a time to a *bound* collection fires a CollectionChanged per item; doing that for hundreds of
    // items while the virtualizer is busy is what crashed Microsoft.UI.Xaml (stowed exception).
    private IReadOnlyList<PhotoItem> _current = Array.Empty<PhotoItem>();
    public ObservableCollection<ExifRow> Exif { get; } = new();

    private bool _preview;
    private int _exifToken;   // guards against stale async EXIF/preview updates

    public MainWindow()
    {
        InitializeComponent();
        Title = "Rawie";

        // handledEventsToo: the GridView swallows Enter in grid mode, so a normal KeyDown never sees it.
        Root.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler(OnKeyDown), handledEventsToo: true);

        ThumbLoader.Start();   // pump runs on the UI thread's context
        PopulateTreeRoots();
        HookDeviceChanges();

        var args = Environment.GetCommandLineArgs();
        var idx = Array.IndexOf(args, "--folder");
        var start = idx >= 0 && idx + 1 < args.Length ? args[idx + 1] : FindTestData();
        if (start is not null && Directory.Exists(start))
        {
            LoadFolder(start);
            // Defer: the tree needs a layout pass before selection/expansion sticks.
            DispatcherQueue.TryEnqueue(() => RevealInTree(start));
        }
    }

    // --- dynamic device detection: refresh drive/camera roots on plug/unplug ---
    private delegate nint SubclassProc(nint hWnd, uint msg, nint wParam, nint lParam, nuint id, nint data);
    [DllImport("comctl32.dll", SetLastError = true)]
    private static extern bool SetWindowSubclass(nint hWnd, SubclassProc cb, nuint id, nuint data);
    [DllImport("comctl32.dll")]
    private static extern nint DefSubclassProc(nint hWnd, uint msg, nint wParam, nint lParam);

    private const uint WM_DEVICECHANGE = 0x0219;
    private SubclassProc? _subclass;         // keep the delegate alive (GC would crash the pump)
    private DispatcherTimer? _deviceDebounce;

    private void HookDeviceChanges()
    {
        _deviceDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
        _deviceDebounce.Tick += (_, _) => { _deviceDebounce!.Stop(); RefreshRoots(); };

        _subclass = OnWindowMessage;
        var hwnd = WindowNative.GetWindowHandle(this);
        var ok = SetWindowSubclass(hwnd, _subclass, 1, 0);
        Diag.Log($"device hook installed: hwnd={hwnd:X}, ok={ok}");
    }

    private nint OnWindowMessage(nint hWnd, uint msg, nint wParam, nint lParam, nuint id, nint data)
    {
        // This runs for EVERY window message via native code — an exception escaping here corrupts
        // the message pump. Never let one out.
        try
        {
            // Owner-drawn menu items / fly-outs need these routed to the live IContextMenu2/3.
            if (ShellMenu.HandleMenuMessage(msg, wParam, lParam, out var menuResult)) return menuResult;

            if (msg == WM_DEVICECHANGE)
            {
                Diag.Log($"WM_DEVICECHANGE wParam={wParam:X}");
                _deviceDebounce?.Stop();
                _deviceDebounce?.Start();   // debounce the burst
            }
        }
        catch (Exception e) { Diag.Log("wndproc: " + e.Message); }
        return DefSubclassProc(hWnd, msg, wParam, lParam);
    }

    // Reconcile drive + camera roots against reality; leave Pictures and any expanded folders intact.
    private void RefreshRoots()
    {
        try
        {
            // drop all camera/shell device roots (re-added fresh — reconnects get a valid ShellItem)
            for (var i = Roots.Count - 1; i >= 0; i--)
                if (Roots[i].Item is not null) Roots.RemoveAt(i);

            // drives: remove gone, add new (USB sticks / card readers)
            var current = DriveInfo.GetDrives().Where(d => d.IsReady)
                .Select(d => d.RootDirectory.FullName).ToHashSet(StringComparer.OrdinalIgnoreCase);
            for (var i = Roots.Count - 1; i >= 0; i--)
                if (Roots[i].IsDriveRoot && !current.Contains(Roots[i].Path)) Roots.RemoveAt(i);
            var have = Roots.Where(r => r.IsDriveRoot).Select(r => r.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var d in DriveInfo.GetDrives().Where(x => x.IsReady))
                if (!have.Contains(d.RootDirectory.FullName)) Roots.Add(NewDriveNode(d.RootDirectory.FullName, d.Name));

            AddPortableDevices();
            Diag.Log($"refresh roots -> {Roots.Count}");
        }
        catch (Exception e) { Diag.Log("refresh roots fail: " + e.Message); }
    }

    // --- left folder tree (bound mode; children fill lazily on expand) ---
    public ObservableCollection<FolderNode> Roots { get; } = new();
    private FolderNode? _selectedNode;   // so a programmatic reveal can clear the previous highlight

    private void PopulateTreeRoots()
    {
        try
        {
            var pics = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
            if (Directory.Exists(pics)) Roots.Add(NewFolder(pics, "Pictures"));
            foreach (var drive in DriveInfo.GetDrives().Where(d => d.IsReady))
                Roots.Add(NewDriveNode(drive.RootDirectory.FullName, drive.Name));
            AddPortableDevices();
        }
        catch (Exception e) { Diag.Log("tree roots fail: " + e.Message); }
    }

    // Cameras/phones in MTP mode: no drive letter, they live under "This PC" as shell items.
    private void AddPortableDevices()
    {
        try
        {
            var pc = new ShellFolder(Shell32.KNOWNFOLDERID.FOLDERID_ComputerFolder);
            foreach (var child in pc.EnumerateChildren(FolderItemFilter.Folders | FolderItemFilter.Storage, HWND.NULL))
            {
                bool isDrive;
                try { isDrive = !string.IsNullOrEmpty(child.FileSystemPath); } catch { isDrive = false; }
                if (isDrive) { child.Dispose(); continue; }   // drives already added via DriveInfo
                Roots.Add(ShellFolderNode(child));            // keep the ShellItem alive in the node
                Diag.Log($"tree: portable device '{child.Name}'");
            }
        }
        catch (Exception e) { Diag.Log("portable devices fail: " + e.Message); }
    }

    private static FolderNode NewFolder(string path, string? name = null)
    {
        var fn = new FolderNode(path, name);
        if (HasSubdirs(path)) fn.Children.Add(new FolderNode(path, "…"));   // placeholder -> shows expander
        return fn;
    }

    private static FolderNode NewDriveNode(string path, string name)
    {
        var fn = NewFolder(path, name);
        fn.IsDriveRoot = true;
        return fn;
    }

    private static FolderNode ShellFolderNode(ShellItem item)
    {
        var fn = new FolderNode(item.ParsingName ?? item.Name, item.Name) { Item = item };
        if (ShellHasSubfolders(item)) fn.Children.Add(new FolderNode(fn.Path, "…"));
        return fn;
    }

    private static bool ShellHasSubfolders(ShellItem item)
    {
        try
        {
            var sf = new ShellFolder(item);   // no using: don't dispose the item we keep in the tree
            foreach (var c in sf.EnumerateChildren(FolderItemFilter.Folders, HWND.NULL)) { c.Dispose(); return true; }
            return false;
        }
        catch { return false; }
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
        if (args.Item is FolderNode fn) EnsureChildren(fn);
    }

    /// Populate a node's real children (replacing the "…" placeholder). Idempotent.
    private static void EnsureChildren(FolderNode fn)
    {
        if (fn.Loaded) return;
        fn.Loaded = true;
        fn.Children.Clear();   // drop the placeholder
        try
        {
            if (fn.Item is not null)   // shell (camera) folder
            {
                var sf = new ShellFolder(fn.Item);
                foreach (var child in sf.EnumerateChildren(FolderItemFilter.Folders, HWND.NULL)
                             .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase))
                    fn.Children.Add(ShellFolderNode(child));
            }
            else                       // filesystem folder
            {
                foreach (var dir in Directory.EnumerateDirectories(fn.Path)
                             .Where(d => !IsHiddenDir(d))
                             .OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
                    fn.Children.Add(NewFolder(dir));
            }
        }
        catch (Exception e) { Diag.Log("expand fail: " + e.Message); }
    }

    /// Expand the tree down to `path` and select it, so the pane shows where we are.
    private void RevealInTree(string path)
    {
        try
        {
            path = path.TrimEnd('\\');
            // Deepest root that contains the path (prefer "Pictures" over "C:\" when both match).
            var root = Roots.Where(r => r.Item is null && !string.IsNullOrEmpty(r.Path)
                                        && path.StartsWith(r.Path.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                            .OrderByDescending(r => r.Path.Length)
                            .FirstOrDefault();
            if (root is null) return;

            var node = root;
            EnsureChildren(node);
            node.IsExpanded = true;

            var walked = root.Path.TrimEnd('\\');
            foreach (var seg in path[walked.Length..].Split('\\', StringSplitOptions.RemoveEmptyEntries))
            {
                walked += "\\" + seg;
                var next = node.Children.FirstOrDefault(
                    c => string.Equals(c.Path.TrimEnd('\\'), walked, StringComparison.OrdinalIgnoreCase));
                if (next is null) return;   // hidden/inaccessible somewhere along the way
                node = next;
                EnsureChildren(node);
                node.IsExpanded = true;
            }
            if (_selectedNode is { } prev && prev != node) prev.IsSelected = false;
            _selectedNode = node;
            node.IsSelected = true;

            // The deep node's container may not exist yet (expansion runs through bindings), so
            // re-apply once layout has caught up.
            var target = node;
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                target.IsSelected = true;
                FolderTree.SelectedItem = target;
            });
        }
        catch (Exception e) { Diag.Log("reveal fail: " + e.Message); }
    }

    private void OnFolderInvoked(TreeView sender, TreeViewItemInvokedEventArgs args)
    {
        if (args.InvokedItem is not FolderNode fn) return;
        if (fn.Item is not null) LoadShellFolder(fn.Item, fn.Name);
        else LoadFolder(fn.Path);
    }

    // Load a camera folder's media (shell items) into the grid.
    private void LoadShellFolder(ShellItem folder, string displayName)
    {
        if (_preview) ExitPreview();
        ThumbLoader.Reset();
        PathText.Text = displayName + "  (camera)";

        var list = new List<PhotoItem>();
        try
        {
            var sf = new ShellFolder(folder);
            foreach (var sub in sf.EnumerateChildren(FolderItemFilter.Folders, HWND.NULL)
                         .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase))
                list.Add(new PhotoItem(sub, isFolder: true));

            foreach (var child in sf.EnumerateChildren(FolderItemFilter.NonFolders, HWND.NULL)
                         .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase))
            {
                if (MediaExts.Contains(Path.GetExtension(child.Name))) list.Add(new PhotoItem(child));
                else child.Dispose();
            }
        }
        catch (Exception e) { Diag.Log("load shell folder fail: " + e.Message); }

        SetItems(list);
        Diag.Log($"loaded {list.Count} shell items from '{displayName}'");
    }


    private void LoadFolder(string path)
    {
        if (_preview) ExitPreview();   // a new folder always lands in the grid
        ThumbLoader.Reset();           // abandon thumbnail work queued for the previous folder
        PathText.Text = path;

        var list = new List<PhotoItem>();
        try
        {
            // Subfolders first, like Explorer — double-click opens them.
            foreach (var d in Directory.EnumerateDirectories(path)
                         .Where(d => !IsHiddenDir(d))
                         .OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
                list.Add(new PhotoItem(d, isFolder: true));

            foreach (var f in Directory.EnumerateFiles(path)
                         .Where(f => MediaExts.Contains(Path.GetExtension(f)))
                         .OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
                list.Add(new PhotoItem(f));
        }
        catch (Exception e) { Diag.Log("LoadFolder failed: " + e.Message); }

        SetItems(list);
        Diag.Log($"loaded {list.Count} items from {path}");
    }

    private void SetItems(List<PhotoItem> list)
    {
        _current = list;
        ThumbGrid.ItemsSource = list;             // single assignment — no per-item CollectionChanged churn
        var folders = list.Count(i => i.IsFolder);
        StatusText.Text = folders > 0 ? $"{folders} folders, {list.Count - folders} files" : $"{list.Count} items";
        if (list.Count > 0) ThumbGrid.SelectedIndex = 0;
    }

    // --- thumbnails: load only realized (visible) cells ---
    private void OnContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (!args.InRecycleQueue && args.Item is PhotoItem p) ThumbLoader.Enqueue(p);
    }

    // --- selection drives the info panel + (in preview mode) the big image ---
    private async void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ThumbGrid.SelectedItem is not PhotoItem p) return;
        var token = ++_exifToken;
        StatusText.Text = $"{ThumbGrid.SelectedIndex + 1} / {_current.Count}   {p.Name}";

        if (p.IsFolder)
        {
            Exif.Clear();
            Exif.Add(new ExifRow("Folder", p.Name));
            Exif.Add(new ExifRow("Open", "Double-click or Enter"));
        }
        else if (p.IsShell)
        {
            // camera items have no local file to read EXIF from — show basics until imported
            Exif.Clear();
            Exif.Add(new ExifRow("File", p.Name));
            Exif.Add(new ExifRow("Location", "On camera"));
            Exif.Add(new ExifRow("Note", "Import to disk for full EXIF"));
        }
        else
        {
            var rows = await Task.Run(() => ReadInfo(p.Path));
            if (token != _exifToken) return;   // selection moved on; drop stale result
            Exif.Clear();
            foreach (var r in rows) Exif.Add(r);
        }

        if (_preview) await ShowPreview(p, token);
    }

    private async Task ShowPreview(PhotoItem p, int token)
    {
        try
        {
            var img = await p.LoadPreviewAsync(1600);   // cached, decoded off the XAML pipeline
            if (token != _exifToken) return;            // selection moved on -> drop stale image
            PreviewImage.Source = img;
        }
        catch (Exception e) { Diag.Log("preview fail: " + e.Message); }
    }

    // --- keys: Enter toggles preview, Esc exits, ← → navigate in preview mode ---
    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        switch (e.Key)
        {
            case VirtualKey.Enter when ThumbGrid.SelectedItem is PhotoItem sel:
                if (_preview) ExitPreview();
                else if (sel.IsFolder) OpenFolderItem(sel);   // Enter on a folder navigates into it
                else EnterPreview();
                e.Handled = true;
                break;
            case VirtualKey.Application when ThumbGrid.SelectedItem is PhotoItem ctx:   // Menu key
                ShowShellMenu(ctx); e.Handled = true; break;
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
        if (i >= 0 && i < _current.Count) ThumbGrid.SelectedIndex = i;
    }

    // --- real Explorer context menu (Vanara hosts IContextMenu; it pumps the menu messages itself,
    //     so no window subclassing / HandleMenuMsg2 plumbing is needed here) ---
    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    private void OnItemRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        var p = FindDataContext<PhotoItem>(e.OriginalSource);
        if (p is null) return;
        ThumbGrid.SelectedItem = p;      // right-click selects, like Explorer
        e.Handled = true;
        ShowShellMenu(p);
    }

    private void ShowShellMenu(PhotoItem p)
    {
        // Camera items own a live ShellItem (never dispose it); filesystem items get a temporary one.
        if (p.ShellRef is { } live) ShowMenuFor(live);
        else ShowMenuForPath(p.Path);

        // A verb may have deleted/renamed the file (Delete, Rename, Move to…). If it's gone, resync.
        // ponytail: existence check instead of a FileSystemWatcher — cheap, covers the destructive verbs.
        var stillThere = p.IsFolder ? Directory.Exists(p.Path) : File.Exists(p.Path);
        if (!p.IsShell && !stillThere && Directory.Exists(PathText.Text))
            LoadFolder(PathText.Text);
    }

    // Right-click in the folder tree: same real Explorer menu for folders, drives and cameras.
    private void OnTreeRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        // Walk up the visual tree: a click on the row's padding/indent/expander has a DataContext
        // that isn't the FolderNode, so checking OriginalSource alone silently misses most of the row.
        var fn = FindDataContext<FolderNode>(e.OriginalSource);
        if (fn is null) return;
        e.Handled = true;
        if (fn.Item is { } live) ShowMenuFor(live);          // camera / shell node
        else if (Directory.Exists(fn.Path)) ShowMenuForPath(fn.Path);
    }

    private static T? FindDataContext<T>(object? source) where T : class
    {
        var d = source as DependencyObject;
        while (d is not null)
        {
            if (d is FrameworkElement fe && fe.DataContext is T hit) return hit;
            d = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(d);
        }
        return null;
    }

    // A shell menu runs its own modal message loop. Starting that from *inside* a XAML input handler
    // races the island's input processing — the menu often never appears. Always defer to the
    // dispatcher so the right-click event finishes first.
    private void ShowMenuForPath(string path)
    {
        GetCursorPos(out var pt);   // capture now; the deferred call happens a few ms later
        DispatcherQueue.TryEnqueue(() =>
            ShellMenu.ShowForPath(path, WindowNative.GetWindowHandle(this), pt));
    }

    private void ShowMenuFor(ShellItem item)
    {
        GetCursorPos(out var pt);
        var pidl = item.PIDL;
        DispatcherQueue.TryEnqueue(() =>
            ShellMenu.ShowForPidl(pidl, WindowNative.GetWindowHandle(this), pt));
    }

    private void OnItemDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        var p = FindDataContext<PhotoItem>(e.OriginalSource) ?? ThumbGrid.SelectedItem as PhotoItem;
        if (p is null) return;
        if (p.IsFolder) OpenFolderItem(p);
        else EnterPreview();
    }

    /// Navigate into a folder shown in the grid, and follow along in the tree.
    private void OpenFolderItem(PhotoItem p)
    {
        if (p.IsShell)
        {
            if (p.ShellRef is { } si) LoadShellFolder(si, p.Name);   // camera subfolder
        }
        else
        {
            LoadFolder(p.Path);
            RevealInTree(p.Path);
        }
    }

    private void EnterPreview()
    {
        if (ThumbGrid.SelectedItem is PhotoItem { IsFolder: true }) return;   // folders have no preview
        _preview = true;
        ThumbLoader.Pause();   // big view has the stage: stop grinding out grid thumbnails
        ThumbGrid.Visibility = Visibility.Collapsed;
        PreviewPanel.Visibility = Visibility.Visible;
        PreviewPanel.Focus(FocusState.Programmatic);
        if (ThumbGrid.SelectedItem is PhotoItem p) _ = ShowPreview(p, _exifToken);
    }

    private void ExitPreview()
    {
        _preview = false;
        ThumbLoader.Resume();   // back in the grid: carry on where we left off
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
        if (p.IsShell) { Diag.Log("open external: not supported for camera items (import first)"); return; }
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
        if (folder is not null) { LoadFolder(folder.Path); RevealInTree(folder.Path); }
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
