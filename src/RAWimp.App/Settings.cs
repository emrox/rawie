using System.Text.Json;

namespace RAWimp.App;

// User settings in %LOCALAPPDATA%\RAWimp\settings.json. Small and forgiving: a corrupt or missing
// file just yields defaults rather than blocking startup.
sealed class Settings
{
    /// Folder to open on start. Blank/null = reopen whatever was open last time.
    public string? StartFolder { get; set; }

    /// Last folder the user browsed (used when StartFolder is blank).
    public string? LastFolder { get; set; }

    /// Where imports are copied to, and the folder/name pattern used (remembered between imports).
    public string? ImportFolder { get; set; }
    public string? ImportPattern { get; set; }

    /// What to do when a destination name is already taken: KeepBoth / Skip / Overwrite.
    public string? ImportConflict { get; set; }

    /// Width of the folder pane, so a resize survives a restart.
    public double? TreeWidth { get; set; }

    private static string Dir => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RAWimp");
    private static string File_ => System.IO.Path.Combine(Dir, "settings.json");

    public static Settings Load()
    {
        try
        {
            if (File.Exists(File_))
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(File_)) ?? new Settings();
        }
        catch (Exception e) { Diag.Log("settings load: " + e.Message); }
        return new Settings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(File_, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception e) { Diag.Log("settings save: " + e.Message); }
    }

    /// Which folder to show at startup, honouring the default-vs-last rule.
    public string? ResolveStartFolder()
    {
        if (!string.IsNullOrWhiteSpace(StartFolder) && Directory.Exists(StartFolder)) return StartFolder;
        if (!string.IsNullOrWhiteSpace(LastFolder) && Directory.Exists(LastFolder)) return LastFolder;
        return null;
    }
}
