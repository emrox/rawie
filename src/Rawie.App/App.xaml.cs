using Microsoft.UI.Xaml;

namespace Rawie.App;

public partial class App : Application
{
    private Window? _window;

    public App()
    {
        Diag.Log("App ctor");
        UnhandledException += (_, e) => Diag.Log("UNHANDLED: " + e.Message + "\n" + e.Exception);
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        Diag.Log("OnLaunched");
        _window = new MainWindow();
        _window.Closed += (_, _) => Diag.Log("window Closed");
        _window.Activate();
        Diag.Log("window Activated");
    }
}

static class Diag
{
    static readonly string Path = System.IO.Path.Combine(AppContext.BaseDirectory, "startup.log");
    public static void Log(string m)
    {
        try { System.IO.File.AppendAllText(Path, $"{DateTime.Now:HH:mm:ss.fff} {m}\n"); } catch { }
    }
}
