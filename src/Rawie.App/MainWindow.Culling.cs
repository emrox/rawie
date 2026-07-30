using System.IO;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Directory = System.IO.Directory;
using Visibility = Microsoft.UI.Xaml.Visibility;

namespace Rawie.App;

// Culling: star ratings / reject flags (stored in XMP sidecars) and the file operations that act on
// them — delete, rename, move — all routed through the shell so they land in the Recycle Bin and
// stay undoable.
public sealed partial class MainWindow
{
    // --- interactive 5-star control in the info pane ---
    private static readonly Microsoft.UI.Xaml.Media.SolidColorBrush StarGold = new(Microsoft.UI.Colors.Gold);
    private static readonly Microsoft.UI.Xaml.Media.SolidColorBrush StarDim = new(Microsoft.UI.Colors.Gray);
    private static readonly Microsoft.UI.Xaml.Media.SolidColorBrush StarRed = new(Microsoft.UI.Colors.OrangeRed);

    private void UpdateRatingStars(PhotoItem? p)
    {
        // Nothing rateable selected (folder / camera item) -> hide the control rather than lie.
        var rateable = p is { IsFolder: false, IsShell: false };
        RatingStars.Visibility = rateable ? Visibility.Visible : Visibility.Collapsed;
        if (!rateable) return;

        var rating = p!.Rating;
        var n = 1;
        foreach (var star in RatingStars.Children.OfType<TextBlock>())
        {
            if (ReferenceEquals(star, RejectMark)) continue;
            var filled = rating >= n;
            star.Text = filled ? "★" : "☆";
            star.Foreground = filled ? StarGold : StarDim;
            n++;
        }
        RejectMark.Foreground = rating == Xmp.Rejected ? StarRed : StarDim;
    }

    private void OnStarTapped(object sender, TappedRoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string tag } || !int.TryParse(tag, out var stars)) return;
        var current = (ThumbGrid.SelectedItem as PhotoItem)?.Rating ?? 0;
        SetRating(current == stars ? 0 : stars);   // clicking the current rating clears it
        e.Handled = true;
    }

    private void OnRejectTapped(object sender, TappedRoutedEventArgs e)
    {
        var current = (ThumbGrid.SelectedItem as PhotoItem)?.Rating ?? 0;
        SetRating(current == Xmp.Rejected ? 0 : Xmp.Rejected);
        e.Handled = true;
    }

    private void ShowPreviewRating(PhotoItem p)
    {
        PreviewRating.Text = p.RatingText;
        PreviewRating.Foreground = p.RatingBrush;
        PreviewRatingBox.Visibility = p.RatingVisibility;
    }

    // --- delete / rename / move (IFileOperation) ---

    private static List<string> WithCompanions(string photoPath) =>
        Xmp.FilesToMoveWith(photoPath, f => MediaExts.Contains(Path.GetExtension(f)));

    private void DeleteSelected()
    {
        var targets = SelectedPhotos();
        if (targets.Count == 0)
        {
            if (ThumbGrid.SelectedItem is PhotoItem p) Rateable(p, "deleted");
            return;
        }

        var index = ThumbGrid.SelectedIndex;
        // One call for the whole batch: a single confirmation, a single progress dialog, and a
        // single undo entry in Explorer.
        var paths = targets.SelectMany(t => WithCompanions(t.Path)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (FileOps.Recycle(paths, Hwnd))
        {
            StatusText.Text = targets.Count == 1
                ? $"{targets[0].Name} — moved to Recycle Bin"
                : $"{targets.Count} photos — moved to Recycle Bin";
            ReloadKeepingPosition(index);
        }
    }

    private async void RenameSelected()
    {
        if (ThumbGrid.SelectedItems.Count > 1)
        {
            StatusText.Text = "Select a single photo to rename";   // batch rename would need a pattern
            return;
        }
        if (ThumbGrid.SelectedItem is not PhotoItem p || !Rateable(p, "renamed")) return;
        var index = ThumbGrid.SelectedIndex;

        // Pre-select the name but not the extension, like Explorer's rename.
        var box = new TextBox { Text = p.Name, SelectionStart = 0, SelectionLength = Path.GetFileNameWithoutExtension(p.Name).Length };
        box.Loaded += (_, _) => box.Focus(FocusState.Programmatic);
        var dlg = new ContentDialog
        {
            Title = "Rename",
            Content = box,
            PrimaryButtonText = "Rename",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = Content.XamlRoot,
        };
        if (await ShowModalAsync(dlg) != ContentDialogResult.Primary) return;

        var newName = box.Text.Trim();
        if (newName.Length == 0 || newName == p.Name) return;

        var ok = FileOps.Rename(p.Path, newName, Hwnd);
        // Keep the sidecar matched to its photo (only when it isn't shared with a partner file).
        var side = Xmp.SidecarFor(p.Path);
        if (ok && WithCompanions(p.Path).Contains(side) && File.Exists(side))
            FileOps.Rename(side, Path.GetFileNameWithoutExtension(newName) + ".xmp", Hwnd);

        if (ok) { StatusText.Text = $"Renamed to {newName}"; ReloadKeepingPosition(index); }
    }

    private async void MoveSelected()
    {
        var targets = SelectedPhotos();
        if (targets.Count == 0)
        {
            if (ThumbGrid.SelectedItem is PhotoItem p) Rateable(p, "moved");
            return;
        }

        var index = ThumbGrid.SelectedIndex;
        var dest = await PickFolderAsync();
        if (dest is null) return;
        if (string.Equals(dest, Path.GetDirectoryName(targets[0].Path), StringComparison.OrdinalIgnoreCase)) return;

        var paths = targets.SelectMany(t => WithCompanions(t.Path)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (FileOps.Move(paths, dest, Hwnd))
        {
            StatusText.Text = targets.Count == 1
                ? $"{targets[0].Name} — moved to {dest}"
                : $"{targets.Count} photos — moved to {dest}";
            RefreshTreeChildren(dest);        // destination isn't watched — update its tree node
            ReloadKeepingPosition(index);
        }
    }

    /// Guard: folders and camera items aren't file-operable here.
    private bool Rateable(PhotoItem p, string verb)
    {
        if (p.IsShell) { StatusText.Text = $"Camera items can't be {verb} — import first"; return false; }
        if (p.IsFolder) { StatusText.Text = $"Folders can't be {verb} from here (use right-click)"; return false; }
        return true;
    }

    /// Re-read the folder after a file operation, keeping the user where they were — the culling
    /// flow depends on landing on the next photo rather than jumping back to the start.
    private void ReloadKeepingPosition(int index)
    {
        var wasPreview = _preview;
        var folder = _currentFolder;
        if (!Directory.Exists(folder)) return;

        LoadFolder(folder);
        if (_current.Count == 0) return;

        ThumbGrid.SelectedIndex = Math.Min(index, _current.Count - 1);
        if (wasPreview) EnterPreview();
        else RestoreListFocus();   // keep arrow-key navigation alive after a file operation
    }

    /// Photos in the current selection that can actually be rated / operated on.
    private List<PhotoItem> SelectedPhotos() =>
        ThumbGrid.SelectedItems.OfType<PhotoItem>().Where(i => !i.IsFolder && !i.IsShell).ToList();

    /// Write a rating to the sidecar(s) and reflect it in the UI. Applies to the whole selection.
    private void SetRating(int rating)
    {
        var targets = SelectedPhotos();
        if (targets.Count == 0)
        {
            if (ThumbGrid.SelectedItem is PhotoItem { IsShell: true })
                StatusText.Text = "Rating needs the file on disk — import it first";
            else if (ThumbGrid.SelectedItem is PhotoItem { IsFolder: true })
                StatusText.Text = "Folders can't be rated";
            return;
        }

        var failed = 0;
        foreach (var p in targets)
        {
            if (!Xmp.Write(p.Path, rating)) { failed++; continue; }

            // A RAW+JPEG pair shares one sidecar, so update every item pointing at it.
            var side = Xmp.SidecarFor(p.Path);
            foreach (var it in _current)
                if (!it.IsFolder && !it.IsShell &&
                    string.Equals(Xmp.SidecarFor(it.Path), side, StringComparison.OrdinalIgnoreCase))
                    it.Rating = rating;
        }

        if (ThumbGrid.SelectedItem is PhotoItem shown) { ShowPreviewRating(shown); UpdateRatingStars(shown); }

        var what = rating switch
        {
            Xmp.Rejected => "rejected",
            0 => "rating cleared",
            _ => $"{rating} star{(rating == 1 ? "" : "s")}",
        };
        StatusText.Text = targets.Count == 1 ? $"{targets[0].Name} — {what}" : $"{targets.Count} photos — {what}"
                        + (failed > 0 ? $" ({failed} failed)" : "");
    }

}
