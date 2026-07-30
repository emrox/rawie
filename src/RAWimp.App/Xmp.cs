using System.Xml.Linq;

namespace RAWimp.App;

// Star ratings / reject flags stored in XMP sidecars next to the photo — the format Bridge and
// Lightroom read. We never write into the original image: culling must not touch the user's files.
//
// Convention (Adobe): xmp:Rating 1..5 = stars, 0 = unrated, -1 = rejected.
// Sidecar name is <basename>.xmp, so a RAW+JPEG pair (IMG_1.NEF + IMG_1.JPG) shares one rating —
// which is what you want when culling: they're the same shot.
static class Xmp
{
    private static readonly XNamespace X = "adobe:ns:meta/";
    private static readonly XNamespace Rdf = "http://www.w3.org/1999/02/22-rdf-syntax-ns#";
    private static readonly XNamespace Xap = "http://ns.adobe.com/xap/1.0/";

    public const int Rejected = -1;

    public static string SidecarFor(string photoPath) => Path.ChangeExtension(photoPath, ".xmp");

    /// The photo plus any file that must travel with it on delete/move/rename.
    /// The sidecar only comes along when no *other* media file with the same basename still needs
    /// it: a RAW+JPEG pair shares one sidecar, so deleting the NEF must not strip the JPEG's rating.
    public static List<string> FilesToMoveWith(string photoPath, Func<string, bool> isMedia)
    {
        var files = new List<string> { photoPath };
        try
        {
            var side = SidecarFor(photoPath);
            if (!File.Exists(side)) return files;

            var dir = Path.GetDirectoryName(photoPath);
            if (string.IsNullOrEmpty(dir)) return files;
            var stem = Path.GetFileNameWithoutExtension(photoPath);

            var partnerRemains = Directory.EnumerateFiles(dir, stem + ".*")
                .Any(f => !f.Equals(photoPath, StringComparison.OrdinalIgnoreCase) && isMedia(f));
            if (!partnerRemains) files.Add(side);
        }
        catch (Exception e) { Diag.Log("companions: " + e.Message); }
        return files;
    }

    public static int Read(string photoPath)
    {
        try
        {
            var side = SidecarFor(photoPath);
            if (!File.Exists(side)) return 0;
            var doc = XDocument.Load(side);

            // XMP allows a property as either an element or an attribute — accept both.
            var el = doc.Descendants(Xap + "Rating").FirstOrDefault();
            var raw = el?.Value ?? doc.Descendants(Rdf + "Description")
                                     .Select(d => d.Attribute(Xap + "Rating")?.Value)
                                     .FirstOrDefault(v => v is not null);
            return int.TryParse(raw, out var r) && r >= -1 && r <= 5 ? r : 0;
        }
        catch (Exception e) { Diag.Log($"xmp read {Path.GetFileName(photoPath)}: {e.Message}"); return 0; }
    }

    /// Set the rating, preserving everything else already in the sidecar (develop settings, keywords…).
    public static bool Write(string photoPath, int rating)
    {
        var side = SidecarFor(photoPath);
        try
        {
            XDocument doc;
            XElement desc;
            if (File.Exists(side))
            {
                doc = XDocument.Load(side);
                desc = doc.Descendants(Rdf + "Description").FirstOrDefault() ?? NewDescription(doc);
            }
            else
            {
                doc = NewDocument();
                desc = doc.Descendants(Rdf + "Description").First();
            }

            // Drop whichever form is present, then write the element form.
            desc.Attribute(Xap + "Rating")?.Remove();
            foreach (var stale in desc.Elements(Xap + "Rating").ToList()) stale.Remove();
            if (rating != 0)
            {
                desc.SetAttributeValue(XNamespace.Xmlns + "xmp", Xap.NamespaceName);
                desc.Add(new XElement(Xap + "Rating", rating));
            }

            var tmp = side + ".tmp";                       // write-then-rename: never leave a torn sidecar
            doc.Save(tmp);
            File.Move(tmp, side, overwrite: true);

            // An unrated photo with an otherwise-empty sidecar leaves no litter behind.
            if (rating == 0 && !desc.HasElements && !desc.Attributes().Any(a => !a.IsNamespaceDeclaration))
                File.Delete(side);
            return true;
        }
        catch (Exception e) { Diag.Log($"xmp write {Path.GetFileName(photoPath)}: {e.Message}"); return false; }
    }

    private static XDocument NewDocument() =>
        new(new XElement(X + "xmpmeta",
                new XAttribute(XNamespace.Xmlns + "x", X.NamespaceName),
                new XElement(Rdf + "RDF",
                    new XAttribute(XNamespace.Xmlns + "rdf", Rdf.NamespaceName),
                    new XElement(Rdf + "Description",
                        new XAttribute(Rdf + "about", "")))));

    private static XElement NewDescription(XDocument doc)
    {
        var rdf = doc.Descendants(Rdf + "RDF").FirstOrDefault();
        if (rdf is null)
        {
            rdf = new XElement(Rdf + "RDF", new XAttribute(XNamespace.Xmlns + "rdf", Rdf.NamespaceName));
            (doc.Root ?? new XElement(X + "xmpmeta")).Add(rdf);
        }
        var desc = new XElement(Rdf + "Description", new XAttribute(Rdf + "about", ""));
        rdf.Add(desc);
        return desc;
    }
}
