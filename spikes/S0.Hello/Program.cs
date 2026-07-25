using S0;

// Spike 0: prove the native imaging libs load + report version, and enumerate the test corpus.
//   dotnet run                -> versions + corpus counts
//   dotnet run -- <dir>       -> enumerate a specific folder
//   dotnet run -- selfcheck   -> Bench self-check only

if (args is ["selfcheck", ..])
{
    Bench.SelfCheck();
    return 0;
}

Console.WriteLine($"libvips     {NetVips.NetVips.Version(0)}.{NetVips.NetVips.Version(1)}.{NetVips.NetVips.Version(2)}");
Console.WriteLine($"ImageMagick {ImageMagick.MagickNET.Version}");
Console.WriteLine();

var dir = args.Length > 0 ? Path.GetFullPath(args[0]) : FindCorpus(AppContext.BaseDirectory);
if (dir is null || !Directory.Exists(dir))
{
    Console.WriteLine("No test corpus found — see testdata/README.md, then re-run.");
    return 0;
}

var groups = Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
    .GroupBy(f => Path.GetExtension(f).ToLowerInvariant())
    .OrderByDescending(g => g.Count())
    .ToList();

Console.WriteLine($"Corpus: {dir}");
var total = 0;
foreach (var g in groups)
{
    Console.WriteLine($"  {(g.Key.Length == 0 ? "(none)" : g.Key),-8} {g.Count()}");
    total += g.Count();
}
Console.WriteLine($"  {"total",-8} {total}");
return 0;

// Walk up from the binary looking for a sibling testdata/ folder.
static string? FindCorpus(string start)
{
    for (var d = new DirectoryInfo(start); d is not null; d = d.Parent)
    {
        var c = Path.Combine(d.FullName, "testdata");
        if (Directory.Exists(c)) return c;
    }
    return null;
}
