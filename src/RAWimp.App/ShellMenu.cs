using System.Runtime.InteropServices;
using Vanara.PInvoke;
using static Vanara.PInvoke.Shell32;
using static Vanara.PInvoke.User32;

namespace RAWimp.App;

// Hosts the genuine Explorer context menu (IContextMenu), including third-party shell extensions.
//
// We deliberately do NOT use Vanara's ShellContextMenu wrapper: it corrupts the process heap
// (crash 0xc0000374 in ntdll) on the very first use — the fault only surfaces at the next GC, which
// is why menus appeared to work for a while. Proven in spikes/SB.ShellMenu:
//     SB.ShellMenu --probe <path> menu-all 1   -> crashes
//     SB.ShellMenu --probe <path> manual  200  -> survives
// So this is the classic path: parent IShellFolder -> GetUIObjectOf -> IContextMenu -> TrackPopupMenuEx.
static class ShellMenu
{
    // Owner-drawn items and fly-outs (7-Zip, "Send to", …) only render if these are forwarded to
    // IContextMenu2/3 while the menu is up. See HandleMenuMessage, called from the window proc.
    private const uint WM_INITMENUPOPUP = 0x0117, WM_DRAWITEM = 0x002B,
                       WM_MEASUREITEM = 0x002C, WM_MENUCHAR = 0x0120;
    private const uint IdFirst = 1, IdLast = 0x7FFF;

    private static IContextMenu2? _cm2;
    private static IContextMenu3? _cm3;

    /// Returns true if the message was consumed by the live context menu.
    public static bool HandleMenuMessage(uint msg, IntPtr wParam, IntPtr lParam, out IntPtr result)
    {
        result = IntPtr.Zero;
        if (msg is not (WM_INITMENUPOPUP or WM_DRAWITEM or WM_MEASUREITEM or WM_MENUCHAR)) return false;
        try
        {
            if (_cm3 is not null) { _cm3.HandleMenuMsg2(msg, wParam, lParam, out result); return true; }
            if (_cm2 is not null) { _cm2.HandleMenuMsg(msg, wParam, lParam); return true; }
        }
        catch (Exception e) { Diag.Log("menu msg: " + e.Message); }
        return false;
    }

    public static void ShowForPath(string path, HWND owner, POINT pt)
    {
        try
        {
            SHParseDisplayName(path, null, out var pidl, 0, IntPtr.Zero).ThrowIfFailed();
            using (pidl) Show(pidl, owner, pt);
        }
        catch (Exception e) { Diag.Log($"shell menu ({path}): " + e.Message); }
    }

    /// For items that have no filesystem path (camera/MTP): show using their PIDL directly.
    public static void ShowForPidl(PIDL pidl, HWND owner, POINT pt)
    {
        try { Show(pidl, owner, pt); }
        catch (Exception e) { Diag.Log("shell menu (pidl): " + e.Message); }
    }

    private static void Show(PIDL pidl, HWND owner, POINT pt)
    {
        SHBindToParent(pidl, typeof(IShellFolder).GUID, out var ppv, out var child).ThrowIfFailed();
        var folder = (IShellFolder)ppv;
        IContextMenu? cm = null;
        HMENU hmenu = default;
        try
        {
            var iid = typeof(IContextMenu).GUID;
            folder.GetUIObjectOf(owner, 1, [child], ref iid, IntPtr.Zero, out var obj);
            cm = (IContextMenu)obj;

            hmenu = CreatePopupMenu();
            cm.QueryContextMenu(hmenu, 0, IdFirst, IdLast, CMF.CMF_NORMAL | CMF.CMF_EXTENDEDVERBS);

            _cm2 = cm as IContextMenu2;
            _cm3 = cm as IContextMenu3;

            // A popup menu needs the owner foreground and NO mouse capture outstanding, or
            // TrackPopupMenuEx returns immediately with 0. XAML's TreeView holds pointer capture on
            // right-click (the GridView doesn't) — hence "tree menu only worked right after a grid menu".
            ReleaseCapture();
            SetForegroundWindow(owner);

            // TPM_RETURNCMD: blocks until dismissed, returns the chosen id instead of posting WM_COMMAND.
            var cmd = TrackPopupMenuEx(hmenu, TrackPopupMenuFlags.TPM_RETURNCMD | TrackPopupMenuFlags.TPM_LEFTALIGN
                                            | TrackPopupMenuFlags.TPM_RIGHTBUTTON, pt.X, pt.Y, owner);

            // Standard follow-up: lets the menu close cleanly if the user clicked elsewhere.
            PostMessage(owner, 0x0000 /* WM_NULL */);

            if (cmd > 0) Invoke(cm, (uint)cmd - IdFirst, owner);
        }
        finally
        {
            _cm2 = null;
            _cm3 = null;
            if (!hmenu.IsNull) DestroyMenu(hmenu);
            if (cm is not null) Marshal.ReleaseComObject(cm);
            Marshal.ReleaseComObject(folder);
        }
    }

    private static void Invoke(IContextMenu cm, uint id, HWND owner)
    {
        var info = new CMINVOKECOMMANDINFOEX
        {
            cbSize = (uint)Marshal.SizeOf<CMINVOKECOMMANDINFOEX>(),
            hwnd = owner,
            lpVerb = (IntPtr)id,     // MAKEINTRESOURCE(id): invoke by menu offset, not verb name
            nShow = ShowWindowCommand.SW_SHOWNORMAL,
        };
        var p = Marshal.AllocHGlobal((int)info.cbSize);
        try
        {
            Marshal.StructureToPtr(info, p, false);
            cm.InvokeCommand(p);
        }
        catch (Exception e) { Diag.Log("menu invoke: " + e.Message); }
        finally { Marshal.FreeHGlobal(p); }
    }
}
