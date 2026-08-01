using System.IO;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Directory = System.IO.Directory;

namespace RAWimp.App;

// The import dialog: pick a card or camera, choose where the photos should land and how they should
// be named, watch what that will produce, then copy with verification.
public sealed partial class MainWindow
{
    private CancellationTokenSource? _importCts;

    private async void OnOpenImport(object sender, RoutedEventArgs e)
    {
        var sourceBox = new ComboBox { MinWidth = 260, PlaceholderText = "No card or camera detected" };

        // Rebuild the list in place, keeping the current pick if it's still attached.
        void RefreshSources()
        {
            var chosen = (sourceBox.SelectedItem as ImportSource)?.Name;
            var found = ImportEngine.DiscoverSources();

            if (sourceBox.Items.Count == found.Count &&
                sourceBox.Items.OfType<ImportSource>().Select(s => s.Name).SequenceEqual(found.Select(s => s.Name)))
                return;   // nothing changed — don't disturb the selection

            sourceBox.Items.Clear();
            foreach (var s in found) sourceBox.Items.Add(s);
            var again = found.FindIndex(s => s.Name == chosen);
            if (again >= 0) sourceBox.SelectedIndex = again;
            else if (found.Count > 0) sourceBox.SelectedIndex = 0;
        }

        RefreshSources();

        var destBox = new TextBox { MinWidth = 300, Text = _settings.ImportFolder ?? DefaultImportFolder(), IsReadOnly = true };
        var browse = new Button { Content = LucideIcon.Label("folder", "Browse…") };

        var patternBox = new TextBox { MinWidth = 380, Text = _settings.ImportPattern ?? ImportPattern.Default };

        var conflictBox = new ComboBox { MinWidth = 300 };
        conflictBox.Items.Add("Keep both — import as name_001");
        conflictBox.Items.Add("Skip — leave the existing file");
        conflictBox.Items.Add("Overwrite — replace the existing file");
        conflictBox.SelectedIndex = Enum.TryParse<ImportConflict>(_settings.ImportConflict, out var savedConflict)
            ? (int)savedConflict : (int)ImportConflict.KeepBoth;

        var status = new TextBlock
        {
            Text = sourceBox.Items.Count == 0 ? "Attach a camera or insert a card." : "Scanning…",
            VerticalAlignment = VerticalAlignment.Center,
        };
        var scanRing = new ProgressRing { Width = 16, Height = 16, IsActive = false, Visibility = Visibility.Collapsed };
        var previewList = new TextBlock
        {
            FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"),
            FontSize = 12,
            TextWrapping = TextWrapping.NoWrap,
        };
        var previewScroll = new ScrollViewer
        {
            Height = 150,
            HorizontalScrollMode = ScrollMode.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = previewList,
        };
        var bar = new ProgressBar { Minimum = 0, Maximum = 1, Value = 0, Visibility = Visibility.Collapsed };

        List<ImportCandidate> candidates = new();

        // Show what the current pattern would produce for the first few files.
        void RefreshPreview()
        {
            if (candidates.Count == 0) { previewList.Text = ""; return; }
            var pattern = patternBox.Text.Trim();
            var lines = candidates.Take(8)
                .Select(c => $"{c.Name,-20} ->  {ImportEngine.PreviewDestination(c, pattern)}");
            previewList.Text = string.Join("\n", lines)
                             + (candidates.Count > 8 ? $"\n… and {candidates.Count - 8} more" : "");
        }

        // Scanning a camera over MTP takes a while. Import stays disabled until it finishes —
        // pressing it early used to report "nothing to import", which looked like a failure.
        //
        // Deliberately *not* importing while the scan runs: an MTP device serves one operation at a
        // time (see Mtp.Gate), so overlapping the two would contend for the device rather than
        // overlap, and risks the ERROR_BUSY failures that cost us dearly. For a card the scan is a
        // directory walk of milliseconds, so there is nothing to win there either.
        CancellationTokenSource? scanCts = null;

        // The dialog doesn't exist yet at this point; wired up once it does.
        var setImportEnabled = (bool on) => { };

        async Task ScanSelectedAsync()
        {
            if (sourceBox.SelectedItem is not ImportSource src) return;

            scanCts?.Cancel();                       // a previous scan must not overwrite this one
            var cts = new CancellationTokenSource();
            scanCts = cts;

            candidates = new();
            previewList.Text = "";
            scanRing.IsActive = true;
            scanRing.Visibility = Visibility.Visible;
            setImportEnabled(false);
            status.Text = "Scanning…";

            var progress = new Progress<int>(n =>
            {
                if (!cts.IsCancellationRequested) status.Text = $"Scanning… {n} files so far";
            });

            try
            {
                var found = await Task.Run(() => ImportEngine.Scan(src, cts.Token, progress), cts.Token);
                if (cts.IsCancellationRequested) return;   // superseded by a newer scan

                candidates = found;
                status.Text = found.Count == 0 ? "No photos found on this source." : $"{found.Count} files found.";
                RefreshPreview();
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) { status.Text = "Scan failed: " + ex.Message; Diag.Log("import scan: " + ex.Message); }
            finally
            {
                if (!cts.IsCancellationRequested)
                {
                    scanRing.IsActive = false;
                    scanRing.Visibility = Visibility.Collapsed;
                    setImportEnabled(candidates.Count > 0);
                }
            }
        }

        sourceBox.SelectionChanged += async (_, _) => await ScanSelectedAsync();
        patternBox.TextChanged += (_, _) => RefreshPreview();
        browse.Click += async (_, _) =>
        {
            var picked = await PickFolderAsync();
            if (picked is not null) destBox.Text = picked;
        };

        // "Really stop?" can't be a second ContentDialog — WinUI allows only one at a time. A Flyout
        // with Full placement gives the same effect: a popup centred over everything, so it can't end
        // up scrolled out of sight the way an inline panel did once this dialog grew.
        var stopButton = new Button { Content = LucideIcon.Label("ban", "Stop import") }.AsDanger();
        var keepButton = new Button { Content = "Keep importing" }.AsPrimary();
        var confirmFlyout = new Flyout
        {
            Content = new StackPanel
            {
                Spacing = 12,
                MaxWidth = 300,          // hug the content instead of stretching
                Children =
                {
                    new TextBlock
                    {
                        Text = "An import is running. Stop it?",
                        FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                        TextWrapping = TextWrapping.Wrap,
                    },
                    Row(stopButton, keepButton),
                },
            },
        };

        var panel = new StackPanel { Spacing = 8, MinWidth = 620, MaxWidth = 660 };
        panel.Children.Add(Label("Import from"));
        panel.Children.Add(sourceBox);
        panel.Children.Add(Label("Copy to"));
        panel.Children.Add(Row(destBox, browse));
        panel.Children.Add(Label("Folder and file pattern"));
        panel.Children.Add(patternBox);
        panel.Children.Add(new TextBlock
        {
            Text = "Tokens: {yyyy} {MM} {dd} {HH} {mm} {ss} {make} {model} {ext} {name} {seq}\n"
                 + "{ext} and {name} keep the camera's casing — {lower-ext} / {upper-ext} "
                 + "(and {lower-name} / {upper-name}) force it.",
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,   // long hint must fold, not run past the dialog edge
            MaxWidth = 620,
            Foreground = Secondary(),
        });
        var conflictHint = new TextBlock
        {
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 620,
            Foreground = Secondary(),
        };
        void UpdateConflictHint() => conflictHint.Text = (ImportConflict)Math.Max(0, conflictBox.SelectedIndex) switch
        {
            ImportConflict.Skip =>
                "Existing files are never touched. Photos already imported are skipped too.",
            ImportConflict.Overwrite =>
                "A photo whose name is taken replaces the file that's there. "
                + "Photos already imported unchanged are skipped, so nothing is rewritten needlessly.",
            _ =>
                "A different photo with the same name is imported as name_001, so nothing is lost. "
                + "Photos already imported are skipped.",
        };
        conflictBox.SelectionChanged += (_, _) => UpdateConflictHint();
        UpdateConflictHint();

        panel.Children.Add(Label("If a file already exists"));
        panel.Children.Add(conflictBox);
        panel.Children.Add(conflictHint);
        panel.Children.Add(Label("Preview"));
        panel.Children.Add(previewScroll);
        panel.Children.Add(bar);
        panel.Children.Add(Row(scanRing, status));

        var dlg = new ContentDialog
        {
            Title = "Import photos",
            Content = panel,
            PrimaryButtonText = "Import",
            CloseButtonText = "Close",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = Content.XamlRoot,
        };

        // ContentDialog is capped near 548px by default, which clipped the wider content below.
        dlg.Resources["ContentDialogMaxWidth"] = 760.0;

        setImportEnabled = on => dlg.IsPrimaryButtonEnabled = on;
        dlg.IsPrimaryButtonEnabled = false;      // nothing to import until a scan has finished

        var importing = false;

        void SaveImportSettings()
        {
            _settings.ImportFolder = destBox.Text.Trim();
            _settings.ImportPattern = patternBox.Text.Trim();
            _settings.ImportConflict = ((ImportConflict)Math.Max(0, conflictBox.SelectedIndex)).ToString();
            _settings.Save();
        }

        // Closing mid-import must not silently abandon it.
        dlg.CloseButtonClick += (_, args) =>
        {
            SaveImportSettings();                     // remember the pattern even when just closing
            if (!importing) return;
            args.Cancel = true;                       // hold the dialog open and ask
            // Anchored, not Full: Full stretches the flyout across the whole window.
            confirmFlyout.ShowAt(status, new FlyoutShowOptions { Placement = FlyoutPlacementMode.Top });
        };

        stopButton.Click += (_, _) =>
        {
            _importCts?.Cancel();
            confirmFlyout.Hide();
            dlg.Hide();
        };
        keepButton.Click += (_, _) => confirmFlyout.Hide();

        // Keep the dialog open while copying so progress stays visible.
        // Note: no deferral here. Holding one keeps the dialog "busy" for the whole import, and the
        // Close button then never raises its click event — so the "stop the import?" prompt could
        // never appear. args.Cancel alone keeps the dialog open; the copy runs alongside it.
        dlg.PrimaryButtonClick += (_sender, args) =>
        {
            if (candidates.Count == 0) { args.Cancel = true; status.Text = "Nothing to import."; return; }
            var destination = destBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(destination)) { args.Cancel = true; status.Text = "Choose a destination folder."; return; }

            args.Cancel = true;                     // stay open; we close ourselves when finished
            _ = RunImportAsync(destination);
        };

        async Task RunImportAsync(string destination)
        {
            dlg.IsPrimaryButtonEnabled = false;
            bar.Visibility = Visibility.Visible;
            bar.Maximum = candidates.Count;
            importing = true;

            // The grid's thumbnail loader also reads over MTP. Let the import have the device to
            // itself, or the two queue against each other and everything crawls.
            ThumbLoader.Pause();

            SaveImportSettings();

            _importCts = new CancellationTokenSource();
            var progress = new Progress<ImportProgress>(p =>
            {
                bar.Value = p.Done;
                status.Text = $"{p.Done} / {p.Total}   {p.Current}";
            });

            try
            {
                var outcome = await ImportEngine.RunAsync(sourceBox.SelectedItem as ImportSource, candidates,
                                                          destination, patternBox.Text.Trim(),
                                                          (ImportConflict)Math.Max(0, conflictBox.SelectedIndex),
                                                          progress, _importCts.Token);

                var summary = $"Imported {outcome.Copied}, skipped {outcome.Duplicates} already there"
                            + (outcome.Failed > 0 ? $", {outcome.Failed} failed" : "")
                            + $"  ({outcome.MegabytesPerSecond:F0} MB/s)";
                status.Text = outcome.Interrupted
                    ? $"Import interrupted after {outcome.Processed} of {outcome.Total} files — "
                    + $"card or camera disconnected?  {summary}"
                    : summary;
                Diag.Log("import: " + status.Text);

                if (outcome.Copied > 0 && Directory.Exists(destination))
                {
                    LoadFolder(destination);       // show what just landed
                    RevealInTree(destination);
                }
            }
            catch (OperationCanceledException) { status.Text = "Import stopped."; }
            catch (Exception ex) { status.Text = "Import failed: " + ex.Message; Diag.Log("import: " + ex); }
            finally
            {
                importing = false;
                ThumbLoader.Resume();
                RefreshSources();   // the card may have been removed while we copied
                dlg.IsPrimaryButtonEnabled = true;
                bar.Visibility = Visibility.Collapsed;
            }
        }

        // Follow devices being plugged in/out while the dialog is open.
        void OnDevices() => DispatcherQueue.TryEnqueue(() => { if (!importing) RefreshSources(); });
        DevicesChanged += OnDevices;

        var scan = ScanSelectedAsync();
        try
        {
            await ShowModalAsync(dlg);
        }
        finally
        {
            DevicesChanged -= OnDevices;
            SaveImportSettings();
            _importCts?.Cancel();
            await scan;   // let any in-flight scan finish before dropping the dialog
        }
    }

    private static string DefaultImportFolder() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Imported");

    private static TextBlock Label(string text) =>
        new() { Text = text, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold };

    private static Microsoft.UI.Xaml.Media.Brush Secondary() =>
        (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];

    private static StackPanel Row(params UIElement[] children)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        foreach (var c in children) row.Children.Add(c);
        return row;
    }
}
