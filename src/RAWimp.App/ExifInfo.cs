using MetadataExtractor;
using MetadataExtractor.Formats.Exif;
using MetadataExtractor.Formats.GeoTiff;
using Directory = System.IO.Directory;

namespace RAWimp.App;

// Reads the metadata shown in the info pane. Pure: a path in, display rows out — no UI, so it can
// run off the UI thread (and be tested on its own).
static class ExifInfo
{
    public static List<ExifRow> Read(string path)
    {
        var rows = new List<ExifRow>();
        try { rows.Add(new ExifRow("File", Path.GetFileName(path))); } catch { }

        try
        {
            var dirs = ImageMetadataReader.ReadMetadata(path);
            var exif = dirs.OfType<ExifDirectoryBase>().ToList();
            var gps = dirs.OfType<GpsDirectory>().FirstOrDefault();

            // Search across every EXIF IFD (IFD0, the real Exif SubIFD, preview SubIFDs, …).
            // Different RAW makers scatter the same tags into different sub-IFDs, so picking one
            // directory misses fields (NEF/DNG put exposure/ISO in a SubIFD that isn't the first).
            string? Val(int tag) => exif.Select(d => d.GetDescription(tag))
                                        .FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
            void Add(string label, string? val) { if (!string.IsNullOrWhiteSpace(val)) rows.Add(new ExifRow(label, val!)); }

            var cam = string.Join(" ", new[] { Val(ExifDirectoryBase.TagMake), Val(ExifDirectoryBase.TagModel) }
                .Where(x => !string.IsNullOrWhiteSpace(x)));
            Add("Camera", cam);
            Add("Lens", Val(ExifDirectoryBase.TagLensModel));
            Add("Date taken", Val(ExifDirectoryBase.TagDateTimeOriginal) ?? Val(ExifDirectoryBase.TagDateTime));
            Add("Exposure", Val(ExifDirectoryBase.TagExposureTime));
            Add("Aperture", Val(ExifDirectoryBase.TagFNumber));
            Add("ISO", Val(ExifDirectoryBase.TagIsoEquivalent));
            Add("Focal length", Val(ExifDirectoryBase.TagFocalLength));
            Add("Exposure bias", Val(ExifDirectoryBase.TagExposureBias));
            Add("Flash", Val(ExifDirectoryBase.TagFlash));

            var w = Val(ExifDirectoryBase.TagExifImageWidth);
            var h = Val(ExifDirectoryBase.TagExifImageHeight);
            if (w is not null && h is not null) Add("Dimensions", $"{w} × {h}");
            if (gps?.GetGeoLocation() is { } loc) Add("GPS", $"{loc.Latitude:F5}, {loc.Longitude:F5}");
        }
        catch (Exception e) { Diag.Log("exif fail: " + e.Message); }

        try { rows.Add(new ExifRow("Size", $"{new FileInfo(path).Length / 1048576.0:F1} MB")); } catch { }
        return rows;
    }
}
