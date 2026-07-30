using System.Collections.ObjectModel;
using System.IO;
using System.Runtime.InteropServices;
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
    private int _exifToken;          // guards against stale async EXIF/preview updates
    private PhotoItem? _selectedPhoto;   // so the previous item's selection border can be cleared
    private readonly Settings _settings = Settings.Load();

    public MainWindow()
    {
        InitializeComponent();
        Title = "Rawie";

        // handledEventsToo: the GridView swallows Enter in grid mode, so a normal KeyDown never sees it.
        Root.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler(OnKeyDown), handledEventsToo: true);

        BuildToolMenu();
        if (_settings.TreeWidth is { } saved)
            Root.ColumnDefinitions[0].Width = new GridLength(Math.Clamp(saved, TreeMinWidth, TreeMaxWidth));

        ThumbLoader.Start();   // pump runs on the UI thread's context
        PopulateTreeRoots();
        HookDeviceChanges();

        var args = Environment.GetCommandLineArgs();
        var idx = Array.IndexOf(args, "--folder");
        var start = idx >= 0 && idx + 1 < args.Length
            ? args[idx + 1]                              // explicit override wins
            : _settings.ResolveStartFolder() ?? FindTestData();
        if (start is not null && Directory.Exists(start))
        {
            LoadFolder(start);
            // Defer: the tree needs a layout pass before selection/expansion sticks. Focus lands in
            // the grid afterwards so the arrow keys work immediately, without clicking a photo first.
            DispatcherQueue.TryEnqueue(() =>
            {
                RevealInTree(start);
                RestoreListFocus();
            });
        }
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

        // Ratings must be known for every item, not just the visible ones, or filtering and sorting
        // by rating would act on whatever the thumbnail pump happened to have reached. Cheap: for
        // most photos it's a single File.Exists miss.
        var sw = System.Diagnostics.Stopwatch.StartNew();
        HashSet<string>? sidecars = null;
        try { sidecars = Directory.EnumerateFiles(path, "*.xmp").ToHashSet(StringComparer.OrdinalIgnoreCase); }
        catch (Exception ex) { Diag.Log("sidecar scan: " + ex.Message); }
        foreach (var item in list) item.LoadRating(sidecars);
        sw.Stop();

        SetItems(list);
        Diag.Log($"loaded {list.Count} items from {path} (ratings {sw.ElapsedMilliseconds}ms)");

        // Remember where we were, so a blank "start folder" setting reopens here next time.
        if (!string.Equals(_settings.LastFolder, path, StringComparison.OrdinalIgnoreCase))
        {
            _settings.LastFolder = path;
            _settings.Save();
        }
    }

    /// Everything in the folder; the grid shows a filtered/sorted view of this (see ApplyView).
    private List<PhotoItem> _all = new();

    private void SetItems(List<PhotoItem> list)
    {
        _all = list;
        ApplyView(selectFirst: true);
    }

    /// Rebuild the grid from _all using the current filter and sort.
    /// Folders always show — they have no rating, and hiding them would break navigation.
    private void ApplyView(bool selectFirst)
    {
        var minStars = (FilterBox?.SelectedItem as FrameworkElement)?.Tag as string ?? "all";
        var sortBy = (SortBox?.SelectedItem as FrameworkElement)?.Tag as string ?? "name";

        IEnumerable<PhotoItem> view = _all.Where(i => i.IsFolder || Passes(i, minStars));

        view = sortBy == "rating"
            // best first; rejects sink below unrated, name breaks ties
            ? view.OrderByDescending(i => i.IsFolder)
                  .ThenByDescending(i => i.Rating == Xmp.Rejected ? -1 : i.Rating)
                  .ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase)
            : view.OrderByDescending(i => i.IsFolder)
                  .ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase);

        var list = view.ToList();
        _current = list;
        ThumbGrid.ItemsSource = list;   // single assignment — no per-item CollectionChanged churn

        var folders = list.Count(i => i.IsFolder);
        var hidden = _all.Count - list.Count;
        StatusText.Text = $"{folders} folders, {list.Count - folders} files"
                        + (hidden > 0 ? $"   ({hidden} hidden by filter)" : "");

        if (selectFirst && list.Count > 0) ThumbGrid.SelectedIndex = 0;
    }

    private static bool Passes(PhotoItem item, string filter) => filter switch
    {
        "all" => true,
        "rej" => item.Rating == Xmp.Rejected,
        "none" => item.Rating == 0,
        _ => int.TryParse(filter, out var min) && item.Rating >= min,
    };

    // Deliberately NOT called when a rating changes: having a photo vanish from under the cursor
    // mid-cull is disorienting. The view refreshes when the filter/sort or folder changes.
    private void OnViewOptionChanged(object sender, SelectionChangedEventArgs e)
    {
        SyncToolMenuChecks();
        if (_all.Count > 0) ApplyView(selectFirst: true);
    }

    // --- resizable folder pane ---
    private const double TreeMinWidth = 140, TreeMaxWidth = 700;
    private double _dragStartX, _dragStartWidth;
    private bool _draggingSplitter;

    private void OnSplitterPressed(object sender, PointerRoutedEventArgs e)
    {
        var bar = (UIElement)sender;
        _dragStartX = e.GetCurrentPoint(Root).Position.X;
        _dragStartWidth = Root.ColumnDefinitions[0].ActualWidth;
        _draggingSplitter = bar.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void OnSplitterMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_draggingSplitter) return;
        var delta = e.GetCurrentPoint(Root).Position.X - _dragStartX;
        var width = Math.Clamp(_dragStartWidth + delta, TreeMinWidth, TreeMaxWidth);
        Root.ColumnDefinitions[0].Width = new GridLength(width);
        e.Handled = true;
    }

    private void OnSplitterReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_draggingSplitter) return;
        ((UIElement)sender).ReleasePointerCapture(e.Pointer);
        _draggingSplitter = false;

        _settings.TreeWidth = Root.ColumnDefinitions[0].ActualWidth;   // remember it for next launch
        _settings.Save();
        e.Handled = true;
    }

    // --- narrow-window toolbar: collapse into a hamburger menu ---

    /// Mirror the filter/sort combos into the overflow menu. The combos stay the source of truth —
    /// the menu items just move their selection, so there is only one list to maintain.
    private void BuildToolMenu()
    {
        void Fill(MenuFlyoutSubItem menu, ComboBox combo, string group)
        {
            for (var i = 0; i < combo.Items.Count; i++)
            {
                var index = i;
                var entry = new RadioMenuFlyoutItem
                {
                    Text = (combo.Items[i] as ComboBoxItem)?.Content?.ToString() ?? $"{i}",
                    GroupName = group,
                    IsChecked = combo.SelectedIndex == i,
                };
                entry.Click += (_, _) => combo.SelectedIndex = index;
                menu.Items.Add(entry);
            }
        }

        Fill(FilterMenu, FilterBox, "filter");
        Fill(SortMenu, SortBox, "sort");
    }

    private void SyncToolMenuChecks()
    {
        void Sync(MenuFlyoutSubItem menu, ComboBox combo)
        {
            for (var i = 0; i < menu.Items.Count; i++)
                if (menu.Items[i] is RadioMenuFlyoutItem r) r.IsChecked = combo.SelectedIndex == i;
        }

        if (FilterMenu is null || SortMenu is null) return;   // during InitializeComponent
        Sync(FilterMenu, FilterBox);
        Sync(SortMenu, SortBox);
    }

    private void OnCenterPaneSizeChanged(object sender, SizeChangedEventArgs e)
    {
        // Below this the buttons and dropdowns start running off the edge of the pane.
        const double needed = 640;
        var narrow = e.NewSize.Width < needed;
        WideTools.Visibility = narrow ? Visibility.Collapsed : Visibility.Visible;
        MenuButton.Visibility = narrow ? Visibility.Visible : Visibility.Collapsed;
    }

    // --- shared window helpers (focus, modal dialogs, HWND) ---
    private HWND Hwnd => WindowNative.GetWindowHandle(this);

    private int _modalDepth;

    /// Show a dialog with app hotkeys suppressed for its lifetime, then hand focus back to the
    /// photos — otherwise focus lands in the folder tree and the arrow keys stop navigating images.
    private async Task<ContentDialogResult> ShowModalAsync(ContentDialog dlg)
    {
        _modalDepth++;
        try { return await dlg.ShowAsync(); }
        finally { _modalDepth--; RestoreListFocus(); }
    }

    private void ToggleTreeGridFocus()
    {
        var focused = Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(Content.XamlRoot) as DependencyObject;
        if (IsInside(focused, FolderTree))
            RestoreListFocus();                          // tree -> grid (or preview)
        else
            FolderTree.Focus(FocusState.Programmatic);   // anywhere else -> tree
    }

    private static bool IsInside(DependencyObject? node, DependencyObject container)
    {
        for (; node is not null; node = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(node))
            if (ReferenceEquals(node, container)) return true;
        return false;
    }

    private void RestoreListFocus()
    {
        void Apply()
        {
            if (_preview) { PreviewPanel.Focus(FocusState.Programmatic); return; }

            // Focus the selected item's container, not just the GridView: arrow keys only move the
            // selection when the item itself holds focus.
            if (ThumbGrid.SelectedIndex >= 0 &&
                ThumbGrid.ContainerFromIndex(ThumbGrid.SelectedIndex) is Control item)
                item.Focus(FocusState.Programmatic);
            else
                ThumbGrid.Focus(FocusState.Programmatic);
        }

        Apply();
        // A ContentDialog restores focus to whatever it stole it from *after* it closes, which would
        // undo the line above — so apply again once that has happened.
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, Apply);
    }

    // --- thumbnails: load only realized (visible) cells ---
    private void OnContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (!args.InRecycleQueue && args.Item is PhotoItem p) ThumbLoader.Enqueue(p);
    }

    // --- selection drives the info panel + (in preview mode) the big image ---
    private async void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // Drive the template's selection border (see PhotoItem.SelectionBrush) for every item that
        // joined or left the selection — with multi-select this is no longer a single item.
        foreach (var removed in e.RemovedItems.OfType<PhotoItem>()) removed.IsSelected = false;
        foreach (var added in e.AddedItems.OfType<PhotoItem>()) added.IsSelected = true;

        if (ThumbGrid.SelectedItem is not PhotoItem p) { UpdateRatingStars(null); return; }

        var token = ++_exifToken;
        var count = ThumbGrid.SelectedItems.Count;
        StatusText.Text = count > 1
            ? $"{count} selected"
            : $"{ThumbGrid.SelectedIndex + 1} / {_current.Count}   {p.Name}";
        ShowPreviewRating(p);
        UpdateRatingStars(p);

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
            var rows = await Task.Run(() => ExifInfo.Read(p.Path));
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
        // The global handler is registered with handledEventsToo, so it also sees keys a dialog's
        // TextBox already consumed — typing "x" would reject the photo, Enter would open preview.
        if (_modalDepth > 0) return;

        // Same problem with the folder tree: it handles Enter itself (open folder), and the photo
        // actions below would then fire on the grid's selection too — Enter popping open the preview,
        // or worse, Delete recycling a photo while the user is just walking the tree.
        if (e.Key != VirtualKey.Tab &&
            IsInside(Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(Content.XamlRoot) as DependencyObject, FolderTree))
            return;

        switch (e.Key)
        {
            case VirtualKey.Enter when ThumbGrid.SelectedItem is PhotoItem sel:
                if (_preview) ExitPreview();
                else if (sel.IsFolder) OpenFolderItem(sel);   // Enter on a folder navigates into it
                else EnterPreview();
                e.Handled = true;
                break;
            // Tab toggles between the folder tree and the photo grid only — the toolbar buttons and
            // rating stars are reachable by mouse, and cycling through them just slows navigation.
            case VirtualKey.Tab:
                ToggleTreeGridFocus(); e.Handled = true; break;

            case VirtualKey.Application when ThumbGrid.SelectedItem is PhotoItem ctx:   // Menu key
                ShowShellMenu(ctx); e.Handled = true; break;

            // culling: 0 clears, 1-5 stars, X rejects (works in the grid and in preview)
            case >= VirtualKey.Number0 and <= VirtualKey.Number5:
                SetRating(e.Key - VirtualKey.Number0); e.Handled = true; break;
            case >= VirtualKey.NumberPad0 and <= VirtualKey.NumberPad5:
                SetRating(e.Key - VirtualKey.NumberPad0); e.Handled = true; break;
            case VirtualKey.X when ThumbGrid.SelectedItem is PhotoItem rej:
                SetRating(rej.Rating == Xmp.Rejected ? 0 : Xmp.Rejected); e.Handled = true; break;

            // file operations (Recycle Bin / rename / move) — all undoable via Explorer
            case VirtualKey.Delete:
                DeleteSelected(); e.Handled = true; break;
            case VirtualKey.F2:
                RenameSelected(); e.Handled = true; break;
            case VirtualKey.M when IsCtrlDown():
                MoveSelected(); e.Handled = true; break;
            case VirtualKey.Escape when _preview:
                ExitPreview(); e.Handled = true; break;
            case VirtualKey.Left when _preview:
                Move(-1); e.Handled = true; break;
            case VirtualKey.Right when _preview:
                Move(1); e.Handled = true; break;
        }
    }

    private static bool IsCtrlDown() =>
        Microsoft.UI.Input.InputKeyboardSource
            .GetKeyStateForCurrentThread(VirtualKey.Control)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

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

    // --- settings ---
    private async void OnOpenSettings(object sender, RoutedEventArgs e)
    {
        var startBox = new TextBox
        {
            Text = _settings.StartFolder ?? "",
            PlaceholderText = "(blank — reopen the last folder)",
            Width = 320,
        };
        var browse = new Button { Content = "Browse…" };
        var clearStart = new Button { Content = "Use last folder" };
        browse.Click += async (_, _) =>
        {
            var picked = await PickFolderAsync();
            if (picked is not null) startBox.Text = picked;
        };
        clearStart.Click += (_, _) => startBox.Text = "";

        var cacheText = new TextBlock { Text = CacheSizeText(), Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"] };
        var clearCache = new Button { Content = "Clear thumbnail cache" };
        clearCache.Click += (_, _) =>
        {
            ThumbCache.Clear();
            cacheText.Text = CacheSizeText() + "  — cleared";
        };

        var panel = new StackPanel { Spacing = 8, MinWidth = 420 };
        panel.Children.Add(new TextBlock { Text = "Folder to open at startup", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        row.Children.Add(startBox);
        row.Children.Add(browse);
        panel.Children.Add(row);
        panel.Children.Add(clearStart);
        panel.Children.Add(new TextBlock
        {
            Text = "Leave blank to reopen the folder you had open last time.",
            FontSize = 12,
            Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
            Margin = new Thickness(0, 0, 0, 8),
        });

        panel.Children.Add(new TextBlock { Text = "Thumbnail cache", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        panel.Children.Add(cacheText);
        panel.Children.Add(clearCache);

        var dlg = new ContentDialog
        {
            Title = "Settings",
            Content = panel,
            PrimaryButtonText = "Save",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = Content.XamlRoot,
        };

        if (await ShowModalAsync(dlg) == ContentDialogResult.Primary)
        {
            var v = startBox.Text.Trim();
            _settings.StartFolder = string.IsNullOrWhiteSpace(v) ? null : v;
            _settings.Save();
        }
    }

    private static string CacheSizeText()
    {
        var mb = ThumbCache.SizeBytes() / 1048576.0;
        return mb < 0.1 ? "Cache is empty" : $"Currently using {mb:F1} MB";
    }

    private async Task<string?> PickFolderAsync()
    {
        var picker = new FolderPicker();
        picker.FileTypeFilter.Add("*");
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
        return (await picker.PickSingleFolderAsync())?.Path;
    }

    private async void OnPickFolder(object sender, RoutedEventArgs e)
    {
        var picker = new FolderPicker();
        picker.FileTypeFilter.Add("*");
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
        var folder = await picker.PickSingleFolderAsync();
        if (folder is not null) { LoadFolder(folder.Path); RevealInTree(folder.Path); }
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
