using System.Security.Cryptography;
using Vanara.PInvoke;
using Vanara.Windows.Shell;
using Directory = System.IO.Directory;

namespace Rawie.App;

/// Where photos are coming from: a card/folder with a real path, or an MTP camera (no drive letter).
public sealed record ImportSource(string Name, string? FolderPath, ShellItem? Device)
{
    public bool IsCamera => Device is not null;
    public override string ToString() => Name;
}

/// One file offered for import.
public sealed record ImportCandidate(string Name, string? FilePath, ShellItem? Item)
{
    public bool IsCamera => Item is not null;
}

public sealed record ImportProgress(int Done, int Total, string Current);

public sealed record ImportOutcome(
    int Copied, int Duplicates, int Failed, long Bytes, TimeSpan Elapsed,
    int Processed, int Total, bool Interrupted)
{
    public double MegabytesPerSecond => Elapsed.TotalSeconds > 0 ? Bytes / 1048576.0 / Elapsed.TotalSeconds : 0;
}

// Copies photos off a card or camera into a folder tree described by a user pattern, skipping files
// already imported and verifying every copy by hash.
//
// Proven end-to-end as a console spike (spikes/SC.Import) against a Nikon Z 6 over MTP before being
// brought into the app.
public static class ImportEngine
{
    private static readonly HashSet<string> MediaExts = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".heic", ".heif", ".tif", ".tiff", ".png",
        ".cr2", ".cr3", ".nef", ".nrw", ".arw", ".raf", ".orf", ".rw2", ".pef", ".dng", ".x3f",
        ".mp4", ".mov", ".m4v", ".avi", ".mts"
    };

    private static bool IsMedia(string name) => MediaExts.Contains(Path.GetExtension(name));

    /// Removable drives plus any MTP device (camera/phone) currently attached.
    public static List<ImportSource> DiscoverSources()
    {
        var sources = new List<ImportSource>();
        try
        {
            foreach (var d in DriveInfo.GetDrives().Where(d => d.IsReady && d.DriveType == DriveType.Removable))
                sources.Add(new ImportSource($"{d.VolumeLabel} ({d.Name.TrimEnd('\\')})".Trim(), d.RootDirectory.FullName, null));
        }
        catch (Exception e) { Diag.Log("import drives: " + e.Message); }

        try
        {
            var pc = new ShellFolder(Shell32.KNOWNFOLDERID.FOLDERID_ComputerFolder);
            foreach (var child in pc.EnumerateChildren(FolderItemFilter.Folders | FolderItemFilter.Storage, HWND.NULL))
            {
                bool isDrive;
                try { isDrive = !string.IsNullOrEmpty(child.FileSystemPath); } catch { isDrive = false; }
                if (isDrive) { child.Dispose(); continue; }
                sources.Add(new ImportSource(child.Name ?? "Camera", null, child));
            }
        }
        catch (Exception e) { Diag.Log("import devices: " + e.Message); }

        return sources;
    }

    /// Find the media files a source is offering. Runs off the UI thread — it can walk a whole card.
    public static List<ImportCandidate> Scan(ImportSource source, CancellationToken ct)
    {
        var found = new List<ImportCandidate>();
        try
        {
            if (source.FolderPath is { } dir)
            {
                foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                {
                    ct.ThrowIfCancellationRequested();
                    if (IsMedia(f)) found.Add(new ImportCandidate(Path.GetFileName(f), f, null));
                }
            }
            else if (source.Device is { } device)
            {
                CollectFromDevice(device, found, 0, ct);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception e) { Diag.Log("import scan: " + e.Message); }

        return found.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    // MTP content is an object graph, not a filesystem — recurse it.
    private static void CollectFromDevice(ShellItem item, List<ImportCandidate> into, int depth, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (depth > 12) return;

        if (!item.IsFolder)
        {
            if (IsMedia(item.Name ?? "")) into.Add(new ImportCandidate(item.Name ?? "?", null, item));
            return;
        }

        ShellFolder folder;
        try { folder = new ShellFolder(item); } catch { return; }
        foreach (var child in folder.EnumerateChildren(FolderItemFilter.Folders | FolderItemFilter.NonFolders, HWND.NULL))
            CollectFromDevice(child, into, depth + 1, ct);
    }

    /// What a candidate would be imported as, without copying anything (drives the live preview).
    /// Camera items can't be inspected without transferring them, so their tokens come from the name.
    public static string PreviewDestination(ImportCandidate candidate, string pattern)
    {
        try
        {
            if (candidate.FilePath is { } path)
                return ImportPattern.Resolve(pattern, ImportPattern.ReadTokens(path));

            // On-camera: we only know the file name until it's transferred.
            var tokens = new ImportTokens(DateTime.Now, "", "",
                Path.GetExtension(candidate.Name).TrimStart('.'),
                Path.GetFileNameWithoutExtension(candidate.Name));
            return ImportPattern.Resolve(pattern, tokens);
        }
        catch (Exception e) { Diag.Log("preview: " + e.Message); return candidate.Name; }
    }

    /// Is the card still in the reader / the camera still attached?
    /// Checked only after a failure — MTP fails transiently under load, so a failure on its own says
    /// nothing about whether the device is still there.
    public static bool IsSourceAvailable(ImportSource? source)
    {
        if (source is null) return true;                       // nothing to verify against
        if (source.FolderPath is { } dir) return Directory.Exists(dir);

        // A busy camera can momentarily fail to enumerate; confirm twice before declaring it gone,
        // so a hiccup isn't reported to the user as "disconnected".
        if (CameraPresent(source.Name)) return true;
        Thread.Sleep(400);
        return CameraPresent(source.Name);
    }

    private static bool CameraPresent(string name)
    {
        try { return DiscoverSources().Any(s => s.IsCamera && s.Name == name); }
        catch { return false; }
    }

    public static async Task<ImportOutcome> RunAsync(
        ImportSource? source, IReadOnlyList<ImportCandidate> items, string destinationRoot, string pattern,
        IProgress<ImportProgress>? progress, CancellationToken ct)
    {
        int copied = 0, duplicates = 0, failed = 0, processed = 0;
        long bytes = 0;
        var interrupted = false;
        var started = DateTime.UtcNow;

        for (var i = 0; i < items.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var item = items[i];
            progress?.Report(new ImportProgress(i, items.Count, item.Name));

            var ok = false;
            try
            {
                var result = await Task.Run(() => ImportOne(item, destinationRoot, pattern, ct), ct);
                switch (result.status)
                {
                    case "copied": copied++; bytes += result.bytes; ok = true; break;
                    case "duplicate": duplicates++; ok = true; break;
                    default: failed++; break;
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception e) { failed++; Diag.Log($"import {item.Name}: {e.Message}"); }

            processed++;

            // Only stop if the source has genuinely gone away. A transient failure (MTP reports the
            // device busy under load) must not be mistaken for the card being pulled.
            if (!ok && !await Task.Run(() => IsSourceAvailable(source), ct))
            {
                interrupted = true;
                Diag.Log($"import interrupted after {processed}/{items.Count} — source no longer available");
                break;
            }
        }

        progress?.Report(new ImportProgress(processed, items.Count, interrupted ? "interrupted" : "done"));
        return new ImportOutcome(copied, duplicates, failed, bytes, DateTime.UtcNow - started,
                                 processed, items.Count, interrupted);
    }

    private static (string status, long bytes) ImportOne(
        ImportCandidate item, string destinationRoot, string pattern, CancellationToken ct)
    {
        // Camera files must be pulled local before their metadata can be read, so stage first and
        // move into place afterwards; card files are read where they are.
        string localSource;
        string? staged = null;
        if (item.FilePath is { } path)
        {
            localSource = path;
        }
        else
        {
            staged = StageFromCamera(item, ct);
            if (staged is null) return ("failed", 0);
            localSource = staged;
        }

        try
        {
            var tokens = ImportPattern.ReadTokens(localSource);
            var relative = ImportPattern.Resolve(pattern, tokens);
            var destination = Path.Combine(destinationRoot, relative);
            var sourceHash = HashFile(localSource);

            for (var seq = 1; File.Exists(destination); seq++)
            {
                if (HashFile(destination) == sourceHash) return ("duplicate", 0);   // already imported
                destination = Path.Combine(destinationRoot, ImportPattern.WithSequence(relative, seq));
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(localSource, destination);

            if (HashFile(destination) != sourceHash)   // verify every copy
            {
                try { File.Delete(destination); } catch { }
                Diag.Log($"import verify failed: {item.Name}");
                return ("failed", 0);
            }
            return ("copied", new FileInfo(destination).Length);
        }
        finally
        {
            if (staged is not null) { try { File.Delete(staged); } catch { } }
        }
    }

    // MTP serves one transfer at a time; a second concurrent read fails with ERROR_BUSY.

    private static string? StageFromCamera(ImportCandidate item, CancellationToken ct)
    {
        Mtp.Gate.Wait(ct);
        try
        {
            var stageDir = Path.Combine(Path.GetTempPath(), "rawie_import_stage");
            Directory.CreateDirectory(stageDir);
            var temp = Path.Combine(stageDir, item.Name);

            // MTP is flaky under sustained transfer: as well as ERROR_BUSY it can briefly fail with
            // other transport errors. Retry generously — giving up too early looked like the camera
            // had been unplugged.
            const int maxAttempts = 6;
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    using (var src = item.Item!.GetStream(STGM.STGM_READ))
                    using (var dst = File.Create(temp))
                        src.CopyTo(dst, 1 << 20);

                    ReleaseDevice();
                    return temp;
                }
                catch (Exception e) when (attempt < maxAttempts)
                {
                    Diag.Log($"camera read {item.Name} attempt {attempt}/{maxAttempts}: 0x{(uint)e.HResult:X} {e.Message}");
                    ReleaseDevice();               // most likely we're still holding the last transfer
                    Thread.Sleep(150 * attempt);
                }
            }
        }
        catch (Exception e)
        {
            Diag.Log($"camera read {item.Name} gave up: 0x{(uint)e.HResult:X} {e.Message}");
            return null;
        }
        finally { Mtp.Gate.Release(); }
    }

    /// Hand the device's transfer resource back.
    ///
    /// Disposing the stream is not enough: it wraps a COM object whose reference is only dropped when
    /// the runtime collects the wrapper, and until then MTP reports the device busy — so the *next*
    /// file fails with ERROR_BUSY. Recorded in spikes/SC.Import/FINDINGS.md; leaving it out made every
    /// import after the first file fail.
    ///
    /// Always called while holding Mtp.Gate, so no other COM work of ours is in flight.
    private static void ReleaseDevice()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
    }

    private static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
}
