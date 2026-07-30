using System.Collections.ObjectModel;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinRT.Interop;
using Vanara.PInvoke;
using Vanara.Windows.Shell;
using Directory = System.IO.Directory;

namespace Rawie.App;

// Folder tree (left pane) and the device detection that keeps its roots in sync with reality.
public sealed partial class MainWindow
{
    // --- dynamic device detection: refresh drive/camera roots on plug/unplug ---
    private delegate nint SubclassProc(nint hWnd, uint msg, nint wParam, nint lParam, nuint id, nint data);
    [DllImport("comctl32.dll", SetLastError = true)]
    private static extern bool SetWindowSubclass(nint hWnd, SubclassProc cb, nuint id, nuint data);
    [DllImport("comctl32.dll")]
    private static extern nint DefSubclassProc(nint hWnd, uint msg, nint wParam, nint lParam);

    /// Raised after drives/cameras have been re-detected, so open UI can follow along.
    private event Action? DevicesChanged;

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
            DevicesChanged?.Invoke();   // e.g. the import dialog rebuilding its source list
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
        _currentFolder = null;   // camera: no filesystem path
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
        WatchFolder(null);   // MTP has no filesystem path to watch
        Diag.Log($"loaded {list.Count} shell items from '{displayName}'");
    }


}
