using System.Text.RegularExpressions;
using MetadataExtractor;
using MetadataExtractor.Formats.Exif;
using MetadataExtractor.Formats.QuickTime;

namespace Rawie.App;

/// Values a destination pattern can refer to, read from a photo/video's metadata.
public sealed record ImportTokens(DateTime When, string Make, string Model, string Ext, string OriginalName)
{
    // {ext} and {name} keep the case the camera used; the lower-/upper- variants force it.
    public string Resolve(string token, int seq) => token switch
    {
        "yyyy" or "MM" or "dd" or "HH" or "mm" or "ss" => When.ToString(token),
        "make" => Clean(Make),
        "model" or "camera" => Clean(Model),

        "ext" => Ext,
        "lower-ext" => Ext.ToLowerInvariant(),
        "upper-ext" => Ext.ToUpperInvariant(),

        "name" => Clean(OriginalName),
        "lower-name" => Clean(OriginalName).ToLowerInvariant(),
        "upper-name" => Clean(OriginalName).ToUpperInvariant(),

        "seq" => seq.ToString("D3"),
        _ => "{" + token + "}",     // unknown token stays visible instead of silently vanishing
    };

    private static string Clean(string s) => string.IsNullOrWhiteSpace(s)
        ? "unknown"
        : string.Concat(s.Trim().Select(c => Path.GetInvalidFileNameChars().Contains(c) || c == ' ' ? '_' : c));
}

// Turns a user pattern like
//     {yyyy}\{yyyy}-{MM}-{dd}\{model}\{yyyy}{MM}{dd}_{HH}{mm}{ss}.{ext}
// into a relative destination path. Pure string/metadata work — no shell, no I/O beyond reading the
// file's metadata — so it can be tested on its own.
public static class ImportPattern
{
    public const string Default = @"{yyyy}\{yyyy}-{MM}-{dd}\{model}\{yyyy}{MM}{dd}_{HH}{mm}{ss}.{ext}";

    public static string Resolve(string pattern, ImportTokens tokens, int seq = 0) =>
        Regex.Replace(pattern, @"\{([\w-]+)\}", m => tokens.Resolve(m.Groups[1].Value, seq))
             .Replace('/', '\\');

    /// Append a counter before the extension, for a genuine name collision.
    public static string WithSequence(string relativePath, int n)
    {
        var dir = Path.GetDirectoryName(relativePath) ?? "";
        var name = Path.GetFileNameWithoutExtension(relativePath);
        var ext = Path.GetExtension(relativePath);
        return Path.Combine(dir, $"{name}_{n:D3}{ext}");
    }

    /// Read tokens from a local file. Falls back to the file timestamp when there's no capture date
    /// (some cameras write none for video, and Nikon MOV files carry no model at all).
    public static ImportTokens ReadTokens(string path)
    {
        var when = SafeWriteTime(path);
        string make = "", model = "";
        try
        {
            var dirs = ImageMetadataReader.ReadMetadata(path);

            var exif = dirs.OfType<ExifDirectoryBase>().ToList();
            string? Val(int tag) => exif.Select(d => d.GetDescription(tag)).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));

            foreach (var d in exif)
                if (d.TryGetDateTime(ExifDirectoryBase.TagDateTimeOriginal, out var dto)) { when = dto; break; }

            make = Val(ExifDirectoryBase.TagMake) ?? "";
            model = Val(ExifDirectoryBase.TagModel) ?? "";

            // Video: capture time lives in the QuickTime/MP4 movie header, not EXIF.
            if (dirs.OfType<QuickTimeMovieHeaderDirectory>().FirstOrDefault() is { } qt &&
                qt.TryGetDateTime(QuickTimeMovieHeaderDirectory.TagCreated, out var created) && created.Year > 1904)
                when = created;
        }
        catch (Exception e) { Diag.Log($"tokens {Path.GetFileName(path)}: {e.Message}"); }

        // Keep the camera's own casing (NEF, JPG); {lower-ext}/{upper-ext} force it if wanted.
        return new ImportTokens(when, make, model,
                                Path.GetExtension(path).TrimStart('.'),
                                Path.GetFileNameWithoutExtension(path));
    }

    private static DateTime SafeWriteTime(string path)
    {
        try { return File.GetLastWriteTime(path); } catch { return DateTime.Now; }
    }
}
