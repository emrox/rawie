using System.Xml.Linq;
using RAWimp.App;

// Assertion checks for the logic that touches user data: XMP sidecar ratings and the import
// destination pattern. Plain console app — run it with `dotnet run --project tests/RAWimp.Tests`.

var failures = 0;
void Check(bool ok, string what)
{
    Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {what}");
    if (!ok) failures++;
}

var dir = Path.Combine(Path.GetTempPath(), "rawimp_tests");
Directory.CreateDirectory(dir);
foreach (var f in Directory.GetFiles(dir)) File.Delete(f);

string NewFile(string name)
{
    var p = Path.Combine(dir, name);
    File.WriteAllText(p, "not really an image");
    return p;
}

// ---------------------------------------------------------------- import pattern
Console.WriteLine("import pattern:");
var tokens = new ImportTokens(new DateTime(2024, 5, 11, 13, 8, 51), "NIKON CORPORATION", "NIKON Z 6", "nef", "_NZ67855");

Check(ImportPattern.Resolve(ImportPattern.Default, tokens)
      == @"2024\2024-05-11\NIKON_Z_6\20240511_130851.nef", "default pattern resolves");
Check(ImportPattern.Resolve(@"{make}\{name}.{ext}", tokens) == @"NIKON_CORPORATION\_NZ67855.nef",
      "make/name/ext tokens");
Check(!ImportPattern.Resolve(@"{model}\x.{ext}", tokens).Contains(' '), "spaces are stripped from folder names");
Check(ImportPattern.Resolve("{nonsense}", tokens) == "{nonsense}", "unknown token left visible, not dropped");
Check(ImportPattern.Resolve("{seq}", tokens, 7) == "007", "sequence token pads");
Check(ImportPattern.Resolve("{yyyy}/{MM}", tokens) == @"2024\05", "forward slashes normalise to backslashes");

var noModel = new ImportTokens(new DateTime(2026, 1, 2, 3, 4, 5), "", "", "mov", "CLIP");
Check(ImportPattern.Resolve(@"{model}\{name}.{ext}", noModel) == @"unknown\CLIP.mov",
      "missing camera model becomes 'unknown' (Nikon video carries none)");

Check(ImportPattern.WithSequence(@"a\b.nef", 2) == @"a\b_002.nef", "collision suffix goes before the extension");

Console.WriteLine("extension / name casing:");
var cased = new ImportTokens(new DateTime(2024, 1, 1), "", "", "NEF", "DSC_1234");
Check(ImportPattern.Resolve("{ext}", cased) == "NEF", "{ext} keeps the camera's casing");
Check(ImportPattern.Resolve("{lower-ext}", cased) == "nef", "{lower-ext} lowercases");
Check(ImportPattern.Resolve("{upper-ext}", cased) == "NEF", "{upper-ext} uppercases");
Check(ImportPattern.Resolve("{name}", cased) == "DSC_1234", "{name} keeps casing");
Check(ImportPattern.Resolve("{lower-name}", cased) == "dsc_1234", "{lower-name} lowercases");
Check(ImportPattern.Resolve("{upper-name}", cased) == "DSC_1234", "{upper-name} uppercases");

var lowerCased = new ImportTokens(new DateTime(2024, 1, 1), "", "", "jpg", "img_9");
Check(ImportPattern.Resolve("{upper-ext}", lowerCased) == "JPG", "{upper-ext} lifts a lowercase source");
Check(ImportPattern.Resolve("{ext}", lowerCased) == "jpg", "{ext} leaves a lowercase source alone");

Console.WriteLine("token reading:");
var plain = NewFile("NOEXIF.NEF");
var read = ImportPattern.ReadTokens(plain);
Check(read.Ext == "NEF", "extension read with the file's own casing, not lowercased");
Check(read.OriginalName == "NOEXIF", "original name read");
Check(read.When.Year > 2000, "falls back to file time when there is no capture date");

// ---------------------------------------------------------------- xmp sidecars
Console.WriteLine("xmp round-trip:");
var photo = NewFile("IMG_0001.NEF");
Check(Xmp.Read(photo) == 0, "unrated when no sidecar exists");
Xmp.Write(photo, 3);
Check(Xmp.Read(photo) == 3, "reads back 3 stars");
Xmp.Write(photo, Xmp.Rejected);
Check(Xmp.Read(photo) == -1, "reject round-trips");
Xmp.Write(photo, 0);
Check(Xmp.Read(photo) == 0, "clearing returns to unrated");

Console.WriteLine("xmp preserves existing content:");
var lr = NewFile("IMG_0002.NEF");
File.WriteAllText(Xmp.SidecarFor(lr), """
<?xml version="1.0"?>
<x:xmpmeta xmlns:x="adobe:ns:meta/">
  <rdf:RDF xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#">
    <rdf:Description rdf:about="" xmlns:crs="http://ns.adobe.com/camera-raw-settings/1.0/"
                     xmlns:dc="http://purl.org/dc/elements/1.1/" crs:Exposure2012="+0.55">
      <dc:subject><rdf:Bag><rdf:li>holiday</rdf:li></rdf:Bag></dc:subject>
    </rdf:Description>
  </rdf:RDF>
</x:xmpmeta>
""");
Xmp.Write(lr, 4);
var after = XDocument.Load(Xmp.SidecarFor(lr));
Check(Xmp.Read(lr) == 4, "rating added to an existing sidecar");
Check(after.Descendants().Any(e => e.Name.LocalName == "subject"), "existing keywords preserved");
Check(after.Descendants().Attributes().Any(a => a.Name.LocalName == "Exposure2012"), "develop settings preserved");

var bad = NewFile("IMG_0003.NEF");
File.WriteAllText(Xmp.SidecarFor(bad), "this is not xml <<<");
Check(Xmp.Read(bad) == 0, "corrupt sidecar reads as unrated instead of throwing");

Console.WriteLine("companion files (must not orphan a partner's rating):");
bool IsMedia(string f) => new[] { ".nef", ".jpg", ".dng" }.Contains(Path.GetExtension(f).ToLowerInvariant());

var lone = NewFile("LONE.NEF");
Xmp.Write(lone, 3);
Check(Xmp.FilesToMoveWith(lone, IsMedia).Count == 2, "lone RAW takes its sidecar along");

var rawOfPair = NewFile("PAIR.NEF");
var jpgOfPair = NewFile("PAIR.JPG");
Xmp.Write(rawOfPair, 5);
Check(Xmp.FilesToMoveWith(rawOfPair, IsMedia).Count == 1, "RAW of a pair travels alone");
Check(Xmp.Read(jpgOfPair) == 5, "the partner keeps its rating");
File.Delete(jpgOfPair);
Check(Xmp.FilesToMoveWith(rawOfPair, IsMedia).Count == 2, "last file standing takes the sidecar");

// ---------------------------------------------------------------- import end-to-end (card source)
Console.WriteLine("import copy / verify / dedupe:");
var card = Path.Combine(dir, "card", "DCIM");
var vault = Path.Combine(dir, "vault");
Directory.CreateDirectory(card);
if (Directory.Exists(vault)) Directory.Delete(vault, true);
File.WriteAllText(Path.Combine(card, "SHOT1.NEF"), "raw one");
File.WriteAllText(Path.Combine(card, "SHOT1.JPG"), "jpeg one");
File.WriteAllText(Path.Combine(card, "notes.txt"), "ignore me");

var cardSource = new ImportSource("Test card", Path.Combine(dir, "card"), null);
var found = ImportEngine.Scan(cardSource, CancellationToken.None);
Check(found.Count == 2, $"scan finds only media files (got {found.Count})");

var first = await ImportEngine.RunAsync(cardSource, found, vault, @"{name}.{ext}", ImportConflict.KeepBoth, null, CancellationToken.None);
Check(first.Copied == 2 && first.Failed == 0, $"copies both files (copied {first.Copied}, failed {first.Failed})");
Check(File.Exists(Path.Combine(vault, "SHOT1.NEF")), "destination path comes from the pattern");
Check(File.ReadAllText(Path.Combine(vault, "SHOT1.NEF")) == "raw one", "content matches after verified copy");
// Windows paths are case-insensitive, so check the name actually on disk.
Check(Directory.GetFiles(vault).Any(f => Path.GetFileName(f) == "SHOT1.NEF"),
      "imported file keeps the original extension case (SHOT1.NEF, not .nef)");

var second = await ImportEngine.RunAsync(cardSource, found, vault, @"{name}.{ext}", ImportConflict.KeepBoth, null, CancellationToken.None);
Check(second.Copied == 0 && second.Duplicates == 2, $"re-import skips duplicates (copied {second.Copied}, dup {second.Duplicates})");

// Same destination name, different content -> kept as a separate file rather than overwritten.
File.WriteAllText(Path.Combine(card, "SHOT1.NEF"), "raw one CHANGED");
var third = await ImportEngine.RunAsync(cardSource,
    ImportEngine.Scan(cardSource, CancellationToken.None).Where(c => c.Name.EndsWith(".NEF")).ToList(),
    vault, @"{name}.{ext}", ImportConflict.KeepBoth, null, CancellationToken.None);
Check(third.Copied == 1, "a different file with the same name is still imported");
Check(File.Exists(Path.Combine(vault, "SHOT1_001.nef")), "collision gets a sequence suffix, nothing overwritten");

// Scanning a camera is slow, so the dialog shows a running count. Verify the engine reports it.
Console.WriteLine("scan progress:");
var bigCard = Path.Combine(dir, "bigcard");
Directory.CreateDirectory(bigCard);
for (var i = 0; i < 60; i++) File.WriteAllText(Path.Combine(bigCard, $"IMG_{i:D3}.NEF"), $"x{i}");
File.WriteAllText(Path.Combine(bigCard, "readme.txt"), "not media");

var reports = new List<int>();
var bigSource = new ImportSource("Big card", bigCard, null);
var scanned = ImportEngine.Scan(bigSource, CancellationToken.None, new Progress<int>(n => { lock (reports) reports.Add(n); }));
await Task.Delay(150);   // Progress<T> marshals asynchronously

Check(scanned.Count == 60, $"scan finds every media file and ignores others (got {scanned.Count})");
lock (reports)
{
    Check(reports.Count > 0, $"progress is reported during the scan ({reports.Count} updates)");
    Check(reports.Count < scanned.Count, "progress is throttled, not one update per file");
    Check(reports.Contains(60), "final count is reported");
}

var cancelledScan = new CancellationTokenSource();
cancelledScan.Cancel();
try
{
    ImportEngine.Scan(bigSource, cancelledScan.Token);
    Check(false, "a cancelled scan throws");
}
catch (OperationCanceledException) { Check(true, "a cancelled scan throws"); }

Console.WriteLine("conflict policy (same name, different content):");
var conflictCard = Path.Combine(dir, "conflict");
var conflictVault = Path.Combine(dir, "conflict_vault");
Directory.CreateDirectory(conflictCard);
if (Directory.Exists(conflictVault)) Directory.Delete(conflictVault, true);
var conflictSource = new ImportSource("Conflict card", conflictCard, null);

async Task<ImportOutcome> ImportOnce(string content, ImportConflict policy)
{
    File.WriteAllText(Path.Combine(conflictCard, "CLASH.NEF"), content);
    return await ImportEngine.RunAsync(conflictSource, ImportEngine.Scan(conflictSource, CancellationToken.None),
                                       conflictVault, @"{name}.{ext}", policy, null, CancellationToken.None);
}

await ImportOnce("original", ImportConflict.KeepBoth);
Check(File.ReadAllText(Path.Combine(conflictVault, "CLASH.NEF")) == "original", "first import lands");

var again = await ImportOnce("original", ImportConflict.Overwrite);
Check(again.Duplicates == 1 && again.Copied == 0, "identical file is skipped even when overwriting");

await ImportOnce("changed", ImportConflict.Skip);
Check(File.ReadAllText(Path.Combine(conflictVault, "CLASH.NEF")) == "original",
      "Skip leaves the existing file untouched");
Check(!File.Exists(Path.Combine(conflictVault, "CLASH_001.NEF")), "Skip doesn't add a copy either");

await ImportOnce("changed", ImportConflict.Overwrite);
Check(File.ReadAllText(Path.Combine(conflictVault, "CLASH.NEF")) == "changed", "Overwrite replaces the file");

await ImportOnce("third version", ImportConflict.KeepBoth);
Check(File.ReadAllText(Path.Combine(conflictVault, "CLASH.NEF")) == "changed", "KeepBoth preserves the original");
Check(File.Exists(Path.Combine(conflictVault, "CLASH_001.NEF")), "KeepBoth adds the newcomer alongside");

// Source vanishing mid-import (card pulled / camera unplugged) must be reported honestly.
Console.WriteLine("interrupted import:");
var gone = Path.Combine(dir, "gone");
Directory.CreateDirectory(gone);
var goneFiles = new List<ImportCandidate>();
for (var i = 0; i < 10; i++)
{
    var p = Path.Combine(gone, $"G{i}.NEF");
    File.WriteAllText(p, $"file {i}");
    goneFiles.Add(new ImportCandidate(Path.GetFileName(p), p, null));
}
var goneSource = new ImportSource("Pulled card", gone, null);
Directory.Delete(gone, true);   // simulate the card being pulled before anything is copied

var lost = await ImportEngine.RunAsync(goneSource, goneFiles, vault, @"{name}.{ext}", ImportConflict.KeepBoth, null, CancellationToken.None);
Check(lost.Interrupted, "unavailable source is flagged as interrupted");
Check(lost.Processed < lost.Total, $"stops early instead of marching to the end ({lost.Processed}/{lost.Total})");
Check(lost.Copied == 0, "reports nothing copied rather than claiming success");

// The opposite case, which is what misfired in practice: individual files fail (MTP reports the
// device busy) while the source is still perfectly present — that must NOT read as interrupted.
Console.WriteLine("transient failures with the source still attached:");
var flaky = Path.Combine(dir, "flaky");
Directory.CreateDirectory(flaky);
var flakySource = new ImportSource("Still here", flaky, null);
var ghosts = Enumerable.Range(0, 6)
    .Select(i => new ImportCandidate($"MISSING{i}.NEF", Path.Combine(flaky, $"MISSING{i}.NEF"), null))
    .ToList();   // candidates that will all fail, but the folder itself still exists

var flakyRun = await ImportEngine.RunAsync(flakySource, ghosts, vault, @"{name}.{ext}", ImportConflict.KeepBoth, null, CancellationToken.None);
Check(!flakyRun.Interrupted, "failures alone are not treated as a disconnect");
Check(flakyRun.Processed == ghosts.Count, $"keeps going through all files ({flakyRun.Processed}/{ghosts.Count})");
Check(flakyRun.Failed == ghosts.Count, "and reports them as failures");

var cancelled = new CancellationTokenSource();
cancelled.Cancel();
try
{
    await ImportEngine.RunAsync(cardSource, found, vault, @"{name}.{ext}", ImportConflict.KeepBoth, null, cancelled.Token);
    Check(false, "cancellation throws");
}
catch (OperationCanceledException) { Check(true, "cancellation throws"); }

Console.WriteLine(failures == 0 ? "\nALL CHECKS OK" : $"\nFAILED ({failures})");
return failures == 0 ? 0 : 1;
