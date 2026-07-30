using System.IO;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;
using Windows.ApplicationModel.DataTransfer.DragDrop;
using Windows.Storage;
using Directory = System.IO.Directory;

namespace Rawie.App;

// Drag and drop:
//   out  — drag photos/folders into Explorer, the desktop, or any other app
//   in   — drop onto a folder (in the tree or the grid) to move files there
//
// Moves go through IFileOperation like every other file change, so they land in Explorer's undo
// stack and show its progress UI.
public sealed partial class MainWindow
{
    // --- dragging out of the grid ---
    private void OnGridDragItemsStarting(object sender, DragItemsStartingEventArgs e)
    {
        // Camera items have no filesystem path, so there is nothing another app could open.
        var dragged = e.Items.OfType<PhotoItem>().Where(i => !i.IsShell).ToList();
        if (dragged.Count == 0) { e.Cancel = true; return; }

        e.Data.RequestedOperation = DataPackageOperation.Copy | DataPackageOperation.Move;

        // Resolving StorageFiles is async but DragItemsStarting can't await; a data provider lets the
        // work happen when the drop target actually asks for it.
        e.Data.SetDataProvider(StandardDataFormats.StorageItems, async request =>
        {
            var deferral = request.GetDeferral();
            try { request.SetData(await ToStorageItems(dragged)); }
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

    // --- dropping onto a folder (from Rawie, Explorer, or any other app) ---
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
                StatusText.Text = $"{(copying ? "Copied" : "Moved")} {paths.Count} item"
                                + $"{(paths.Count == 1 ? "" : "s")} to {target}";
            // The folder watcher picks the change up and refreshes; nothing to do here.
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
