using System.Runtime.InteropServices;
using Vanara.PInvoke;
using static Vanara.PInvoke.Shell32;

namespace RAWimp.App;

// Delete / rename / move through the shell's IFileOperation, so the user gets Explorer's own
// progress UI, confirmation prompts, undo support and Recycle Bin semantics — instead of a
// File.Delete that is silent and unrecoverable.
//
// NOTE: this uses the raw COM interface from Vanara.PInvoke, not the Vanara.Windows.Shell wrappers
// (ShellContextMenu in that layer corrupts the process heap — see spikes/SB.ShellMenu/FINDINGS.md).
static class FileOps
{
    private const uint COPYENGINE_E_USER_CANCELLED = 0x80270000;

    /// Send to the Recycle Bin (recoverable).
    public static bool Recycle(IReadOnlyList<string> paths, HWND owner) =>
        Run(owner, FILEOP_FLAGS.FOF_ALLOWUNDO, op =>
        {
            foreach (var p in paths) op.DeleteItem(Item(p), null);
        });

    public static bool Rename(string path, string newName, HWND owner) =>
        Run(owner, FILEOP_FLAGS.FOF_ALLOWUNDO, op => op.RenameItem(Item(path), newName, null));

    public static bool Copy(IReadOnlyList<string> paths, string destFolder, HWND owner) =>
        Run(owner, FILEOP_FLAGS.FOF_ALLOWUNDO, op =>
        {
            var dest = Item(destFolder);
            foreach (var p in paths) op.CopyItem(Item(p), dest, null, null);
        });

    public static bool Move(IReadOnlyList<string> paths, string destFolder, HWND owner) =>
        Run(owner, FILEOP_FLAGS.FOF_ALLOWUNDO, op =>
        {
            var dest = Item(destFolder);
            foreach (var p in paths) op.MoveItem(Item(p), dest, null, null);
        });

    private static IShellItem Item(string path) => SHCreateItemFromParsingName<IShellItem>(path)!;

    private static bool Run(HWND owner, FILEOP_FLAGS flags, Action<IFileOperation> build)
    {
        IFileOperation? op = null;
        try
        {
            op = (IFileOperation)new CFileOperations();
            op.SetOwnerWindow(owner);
            op.SetOperationFlags(flags);
            build(op);
            op.PerformOperations();
            return !op.GetAnyOperationsAborted();
        }
        catch (COMException e) when ((uint)e.HResult == COPYENGINE_E_USER_CANCELLED)
        {
            return false;   // user hit Cancel — not an error
        }
        catch (Exception e)
        {
            Diag.Log("file op: " + e.Message);
            return false;
        }
        finally
        {
            if (op is not null) Marshal.ReleaseComObject(op);
        }
    }
}
