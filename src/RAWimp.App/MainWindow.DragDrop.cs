using System.IO;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;
using Windows.ApplicationModel.DataTransfer.DragDrop;
using Windows.Storage;
using Directory = System.IO.Directory;

namespace RAWimp.App;

// Drag and drop:
//   out  — drag photos/folders into Explorer, the desktop, or any other app
//   in   — drop onto a folder (in the tree or the grid) to move files there
//
// Moves go through IFileOperation like every other file change, so they land in Explorer's undo
// stack and show its progress UI.
public sealed partial class MainWindow
{
    /// Gap in pixels between the cursor and the dragged photo.
    private const int DragGap = 5;

    // --- dragging out of the grid ---
    //
    // Per-tile CanDrag rather than the GridView's own CanDragItems: DragItemsStarting carries no
    // DragUI, and ListViewBase does not raise DragStarting on the item container either, so with the
    // built-in route there is no way to replace the drag image — it stays the whole 184px tile.
    // Owning the drag here means owning the selection rule too (see below).
    private async void OnGridItemDragStarting(UIElement sender, DragStartingEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not PhotoItem grabbed) { e.Cancel = true; return; }

        // Explorer's rule: dragging one of the selected items takes the whole selection with it,
        // while dragging an unselected item takes only that one.
        var selection = ThumbGrid.SelectedItems.OfType<PhotoItem>().ToList();
        List<PhotoItem> dragged = selection.Contains(grabbed) ? selection : [grabbed];

        // Camera items have no filesystem path, so there is nothing another app could open.
        dragged = dragged.Where(i => !i.IsShell).ToList();
        if (dragged.Count == 0) { e.Cancel = true; return; }

        Offer(e.Data, dragged);

        if (grabbed.IsFolder || grabbed.IsShell) return;   // no photo to show; keep the default

        var deferral = e.GetDeferral();
        try
        {
            if (await grabbed.DragBitmapAsync(gap: DragGap) is { } small)
                e.DragUI.SetContentFromSoftwareBitmap(small, new Windows.Foundation.Point(0, 0));
        }
        catch (Exception ex) { Diag.Log("grid drag visual: " + ex.Message); }
        finally { deferral.Complete(); }
    }

    // --- dragging the photo out of the big preview ---
    private async void OnPreviewDragStarting(object sender, DragStartingEventArgs e)
    {
        // Only what's on screen, not the whole selection: the user grabbed one visible image, and
        // handing the drop target four more files would be a surprise.
        if (ThumbGrid.SelectedItem is not PhotoItem { IsShell: false, IsFolder: false } shown)
        {
            e.Cancel = true;
            return;
        }

        Offer(e.Data, [shown]);

        // Replace the drag visual, which would otherwise be the full-size image and hide whatever
        // you are dragging onto. The deferral holds the drag open while the small copy is decoded;
        // if that fails we simply leave WinUI's default in place rather than block the drag.
        var deferral = e.GetDeferral();
        try
        {
            if (await shown.DragBitmapAsync(gap: DragGap) is { } small)
                // Anchor at the bitmap's top-left, which is now transparent padding — so the photo
                // itself hangs down and to the right of the cursor, DragGap pixels clear of it.
                e.DragUI.SetContentFromSoftwareBitmap(small, new Windows.Foundation.Point(0, 0));
        }
        catch (Exception ex) { Diag.Log("drag visual: " + ex.Message); }
        finally { deferral.Complete(); }
    }

    /// Advertise photos to a drop target.
    ///
    /// Resolving StorageFiles is async, but neither DragItemsStarting nor DragStarting can await —
    /// a data provider defers the work until the target actually asks for the files.
    private static void Offer(DataPackage data, List<PhotoItem> items)
    {
        data.RequestedOperation = DataPackageOperation.Copy | DataPackageOperation.Move;
        data.SetDataProvider(StandardDataFormats.StorageItems, async request =>
        {
            var deferral = request.GetDeferral();
            try { request.SetData(await ToStorageItems(items)); }
            catch (Exception ex) { Diag.Log("drag out: " + ex.Message); }
            finally { deferral.Complete(); }
        });
    }

    private static async Task<IReadOnlyList<IStorageItem>> ToStorageItems(IEnumerable<PhotoItem> items)
    {
        var result = new List<IStorageItem>();
        foreach (var item in items)
        {
            try
            {
                if (item.IsFolder)
                {
                    result.Add(await StorageFolder.GetFolderFromPathAsync(item.Path));
                    continue;
                }

                result.Add(await StorageFile.GetFileFromPathAsync(item.Path));

                // Take the XMP sidecar along so ratings survive the trip — but only when no other
                // photo still needs it (a RAW+JPEG pair shares one).
                foreach (var companion in WithCompanions(item.Path).Skip(1))
                    if (File.Exists(companion)) result.Add(await StorageFile.GetFileFromPathAsync(companion));
            }
            catch (Exception e) { Diag.Log($"drag out {item.Name}: {e.Message}"); }
        }
        return result;
    }

    // --- dropping onto a folder (from RAWimp, Explorer, or any other app) ---
    private void OnFolderDragOver(object sender, DragEventArgs e)
    {
        var target = TargetFolderOf(sender);
        if (target is null || !e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            e.AcceptedOperation = DataPackageOperation.None;
            return;
        }

        var copying = WantsCopy(e);
        e.AcceptedOperation = copying ? DataPackageOperation.Copy : DataPackageOperation.Move;
        e.DragUIOverride.Caption = $"{(copying ? "Copy" : "Move")} to {Path.GetFileName(target.TrimEnd('\\'))}";
        e.DragUIOverride.IsCaptionVisible = true;
        e.Handled = true;
    }

    /// Explorer's convention: Ctrl copies, Shift moves, and without either, dragging across volumes
    /// copies while dragging within one moves. Files from another app may not be ours to move.
    private static bool WantsCopy(DragEventArgs e)
    {
        if (e.Modifiers.HasFlag(DragDropModifiers.Control)) return true;
        if (e.Modifiers.HasFlag(DragDropModifiers.Shift)) return false;
        return false;   // refined per-item at drop time, once the source paths are known
    }

    private static bool DefaultsToCopy(string source, string target)
    {
        try
        {
            return !string.Equals(Path.GetPathRoot(Path.GetFullPath(source)),
                                  Path.GetPathRoot(Path.GetFullPath(target)),
                                  StringComparison.OrdinalIgnoreCase);
        }
        catch { return true; }   // unknown provenance -> copy is the safe default
    }

    private async void OnFolderDrop(object sender, DragEventArgs e)
    {
        var target = TargetFolderOf(sender);
        if (target is null || !e.DataView.Contains(StandardDataFormats.StorageItems)) return;
        e.Handled = true;

        var deferral = e.GetDeferral();
        try
        {
            var dropped = await e.DataView.GetStorageItemsAsync();
            var paths = dropped.Select(i => i.Path)
                               .Where(p => !string.IsNullOrEmpty(p))
                               .ToList();

            // Ignore no-op and impossible moves rather than letting the shell complain.
            paths = paths.Where(p => !SameFolder(Path.GetDirectoryName(p), target)
                                  && !IsInsideItself(p, target))
                         .ToList();
            if (paths.Count == 0) return;

            // Bring each photo's sidecar along, and drop duplicates if a sidecar was dragged too.
            var withCompanions = paths
                .SelectMany(p => Directory.Exists(p) ? [p] : WithCompanions(p))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            // Ctrl forces a copy; otherwise copy when it comes from another volume (an SD card, a
            // network share) and move when it's already on this one — the same rule Explorer uses.
            var copying = e.Modifiers.HasFlag(DragDropModifiers.Control)
                       || (!e.Modifiers.HasFlag(DragDropModifiers.Shift) && DefaultsToCopy(paths[0], target));

            var done = copying
                ? FileOps.Copy(withCompanions, target, Hwnd)
                : FileOps.Move(withCompanions, target, Hwnd);

            if (done)
            {
                StatusText.Text = $"{(copying ? "Copied" : "Moved")} {paths.Count} item"
                                + $"{(paths.Count == 1 ? "" : "s")} to {target}";

                // The watcher refreshes the folder we're viewing, but the destination is somewhere
                // else — if a folder landed there, its tree node has to be brought up to date too.
                RefreshTreeChildren(target);
                foreach (var movedFrom in paths.Select(Path.GetDirectoryName).Distinct(StringComparer.OrdinalIgnoreCase))
                    RefreshTreeChildren(movedFrom);
            }
        }
        catch (Exception ex) { Diag.Log("drop: " + ex.Message); }
        finally { deferral.Complete(); }
    }

    /// The folder a drop is aimed at — a node in the tree or a folder tile in the grid.
    private string? TargetFolderOf(object sender)
    {
        if (sender is not FrameworkElement element) return null;

        if (element.DataContext is FolderNode node)
            return node.Item is null && Directory.Exists(node.Path) ? node.Path : null;   // not a camera

        if (element.DataContext is PhotoItem { IsFolder: true, IsShell: false } tile)
            return Directory.Exists(tile.Path) ? tile.Path : null;

        // Empty space in the grid means "the folder I'm looking at" — the natural place to drop
        // files coming from Explorer. Not available while browsing a camera.
        if (ReferenceEquals(element, ThumbGrid) && _currentFolder is { } here && Directory.Exists(here))
            return here;

        return null;
    }

    private static bool SameFolder(string? a, string? b) =>
        a is not null && b is not null &&
        string.Equals(a.TrimEnd('\\'), b.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

    /// Guard against dropping a folder into itself or one of its own children.
    private static bool IsInsideItself(string source, string target)
    {
        if (!Directory.Exists(source)) return false;
        var from = Path.GetFullPath(source).TrimEnd('\\') + "\\";
        var to = Path.GetFullPath(target).TrimEnd('\\') + "\\";
        return to.StartsWith(from, StringComparison.OrdinalIgnoreCase);
    }
}
