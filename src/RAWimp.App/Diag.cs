namespace RAWimp.App;

// Runtime log next to the binary. The app is a GUI, so this is often the only way to see what
// happened — several crashes this project hit produced no visible error at all.
static class Diag
{
    private static readonly string Path = System.IO.Path.Combine(AppContext.BaseDirectory, "startup.log");

    public static void Log(string m)
    {
        try { System.IO.File.AppendAllText(Path, $"{DateTime.Now:HH:mm:ss.fff} {m}\n"); } catch { }
    }
}
