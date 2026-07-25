using System.Diagnostics;

namespace S0;

/// Tiny benchmark helper for the spikes: run an action N times, report p50/p95/max + ops/sec.
/// ponytail: nearest-rank percentile, no interpolation — fine for spike-sized samples.
static class Bench
{
    public static BenchResult Run(string name, int iterations, Action action, int warmup = 3)
    {
        for (var i = 0; i < warmup; i++) action();
        var ms = new double[iterations];
        var sw = new Stopwatch();
        for (var i = 0; i < iterations; i++)
        {
            sw.Restart();
            action();
            sw.Stop();
            ms[i] = sw.Elapsed.TotalMilliseconds;
        }
        Array.Sort(ms);
        return new BenchResult(name, iterations, P(ms, 50), P(ms, 95), ms[^1], ms.Sum());
    }

    // nearest-rank percentile on a sorted array
    static double P(double[] sorted, int pct)
    {
        if (sorted.Length == 0) return 0;
        var rank = (int)Math.Ceiling(pct / 100.0 * sorted.Length);
        return sorted[Math.Clamp(rank - 1, 0, sorted.Length - 1)];
    }

    public static void SelfCheck()
    {
        var d = Enumerable.Range(1, 100).Select(i => (double)i).ToArray();
        Check(P(d, 50) == 50, "p50");
        Check(P(d, 95) == 95, "p95");
        Check(P(d, 100) == 100, "p100");
        Check(P(d, 1) == 1, "p1");
        Check(P([], 50) == 0, "empty");
        Console.WriteLine("Bench.SelfCheck OK");
    }

    static void Check(bool ok, string what)
    {
        if (!ok) throw new InvalidOperationException($"Bench self-check failed: {what}");
    }
}

record BenchResult(string Name, int N, double P50, double P95, double Max, double TotalMs)
{
    public double PerSec => TotalMs > 0 ? N / (TotalMs / 1000.0) : 0;
    public static string CsvHeader => "name,n,p50_ms,p95_ms,max_ms,per_sec";
    public string Csv() => $"{Name},{N},{P50:F2},{P95:F2},{Max:F2},{PerSec:F1}";
    public override string ToString() =>
        $"{Name,-22} n={N} p50={P50:F2}ms p95={P95:F2}ms max={Max:F2}ms {PerSec:F1}/s";
}
