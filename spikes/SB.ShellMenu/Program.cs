using System.Collections;
using Vanara.PInvoke;
using Vanara.Windows.Shell;
using WinForms = System.Windows.Forms;

// Spike B (headless part) — prove the real Explorer context menu can be built and invoked.
// The interactive display (right-click, owner-drawn submenus, click-to-invoke) needs a human at
// the screen; this covers everything else automatically:
//   1. build the genuine shell menu (incl. third-party extensions) and enumerate every item
//   2. prove the invoke pipeline end-to-end via a benign 'copy' -> clipboard check
// All shell-menu COM + message pumping is Vanara.ShellContextMenu; we host it, we don't reimplement it.

class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        if (args is ["--probe", var probePath, var mode, ..])
            return Probe.Run(probePath, mode, args.Length > 3 && int.TryParse(args[3], out var it) ? it : 40);

        var show = args.Contains("--show");
        var pathArgs = args.Where(a => a != "--show").ToArray();
        var files = (pathArgs.Length > 0 ? pathArgs : DefaultFiles()).Where(File.Exists).ToArray();
        if (files.Length == 0) { Console.WriteLine("No target files. Pass paths, or add samples to testdata/."); return 1; }

        if (show) return Show(files);   // interactive: right-click a window, real Explorer menu appears

        Console.WriteLine($"Targets: {string.Join(", ", files.Select(Path.GetFileName))}\n");
        var shellItems = files.Select(f => new ShellItem(f)).ToArray();
        try
        {
            Section($"single file: {Path.GetFileName(files[0])}", [shellItems[0]]);
            if (shellItems.Length > 1)
                Section($"multi-select: {shellItems.Length} files", shellItems);

            CopyProof(shellItems[0]);
        }
        finally { foreach (var si in shellItems) si.Dispose(); }
        return 0;
    }

    // Interactive click-test: Vanara's ShowContextMenu displays the menu AND (null callback) invokes
    // the picked verb. Hosted on a WinForms HWND here; a WinUI frame HWND behaves the same since
    // Vanara pumps the menu messages itself. Human confirms: menu shows, submenus render, click acts.
    static int Show(string[] files)
    {
        var f = new WinForms.Form { Text = "Spike B — right-click me", Width = 460, Height = 180 };
        f.Controls.Add(new WinForms.Label
        {
            Dock = WinForms.DockStyle.Fill,
            Text = $"Right-click anywhere for the real Explorer menu.\nTarget: {Path.GetFileName(files[0])}\n"
                 + "Try Copy / Properties / a 7-Zip or PowerToys entry."
        });
        void OnRightClick(object? s, WinForms.MouseEventArgs e)
        {
            if (e.Button != WinForms.MouseButtons.Right) return;
            var pt = WinForms.Cursor.Position;
            using var si = new ShellItem(files[0]);
            var scm = ShellContextMenu.CreateFromItems([si], out var keep);
            using (keep)
            using (scm)
                scm.ShowContextMenu(new POINT(pt.X, pt.Y),
                    Shell32.CMF.CMF_NORMAL | Shell32.CMF.CMF_EXTENDEDVERBS, null, f.Handle);
        }
        f.MouseUp += OnRightClick;
        foreach (WinForms.Control c in f.Controls) c.MouseUp += OnRightClick;
        WinForms.Application.Run(f);
        return 0;
    }

    static void Section(string title, ShellItem[] items)
    {
        Console.WriteLine($"=== {title} ===");
        var scm = ShellContextMenu.CreateFromItems(items, out var keepAlive);
        using (keepAlive)
        using (scm)
        {
            var menu = scm.GetItems(Shell32.CMF.CMF_NORMAL | Shell32.CMF.CMF_EXTENDEDVERBS);
            PrintNode(menu, 0);
        }
        Console.WriteLine();
    }

    static void CopyProof(ShellItem item)
    {
        Console.WriteLine("=== invoke 'copy' -> clipboard check ===");
        WinForms.Clipboard.Clear();
        var scm = ShellContextMenu.CreateFromItems([item], out var keepAlive);
        using (keepAlive)
        using (scm)
        {
            scm.InvokeCopy();
        }
        var drop = WinForms.Clipboard.GetFileDropList();
        Console.WriteLine(drop.Count > 0
            ? $"PASS: clipboard holds {drop.Count} file(s): {drop[0]}"
            : "FAIL: clipboard has no CF_HDROP after copy verb");
        Console.WriteLine();
    }

    // Vanara's GetItems shape is opaque here — print generically: find a label, recurse child collections.
    static readonly string[] LabelProps = { "Text", "Name", "Caption", "Title", "DisplayName", "Verb" };
    static void PrintNode(object? node, int depth)
    {
        if (node is null || depth > 6) return;
        if (node is string s) { Line(depth, s); return; }
        if (node is IEnumerable seq) { foreach (var it in seq) PrintNode(it, depth); return; }

        var t = node.GetType();
        var label = LabelProps.Select(p => t.GetProperty(p)?.GetValue(node) as string)
                              .FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
        Line(depth, string.IsNullOrWhiteSpace(label) ? "──────" : label!.Replace("&", ""));

        foreach (var p in t.GetProperties())
        {
            if (p.PropertyType == typeof(string) || !typeof(IEnumerable).IsAssignableFrom(p.PropertyType)) continue;
            var nm = p.Name.ToLowerInvariant();
            if (!(nm.Contains("item") || nm.Contains("sub") || nm.Contains("child") || nm.Contains("menu"))) continue;
            if (p.GetValue(node) is IEnumerable kids)
                foreach (var c in kids) PrintNode(c, depth + 1);
        }
    }

    static void Line(int depth, string text) => Console.WriteLine(new string(' ', depth * 2) + text);

    static string[] DefaultFiles()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
        {
            var td = Path.Combine(d.FullName, "testdata");
            if (Directory.Exists(td))
                return Directory.EnumerateFiles(td).Where(f => !f.EndsWith(".md")).Take(3).ToArray();
        }
        return [];
    }
}
