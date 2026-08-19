using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace IndexingMemoryLab.Core;

/// <summary>One row of the memory table.</summary>
public sealed record MemorySample(
    int Step,
    string Label,
    int DocumentsProcessed,
    long AllocatedSinceStartBytes,
    long AllocatedThisStepBytes,
    long ManagedHeapBytes,
    long PrivateBytes,
    long LargeObjectHeapBytes,
    int Gen0Collections,
    int Gen1Collections,
    int Gen2Collections,
    double ElapsedSeconds);

/// <summary>The end-of-build figures that the published comparison table is drawn from.</summary>
public sealed record RunTotals(
    int Documents,
    long TotalAllocatedBytes,
    long PeakPrivateBytes,
    long RetainedManagedHeapBytes,
    long RetainedLargeObjectHeapBytes,
    double BuildSeconds);

/// <summary>
/// Samples the numbers that matter for this investigation and prints them as a table.
/// </summary>
/// <remarks>
/// <para>
/// <b>Allocated</b> (<see cref="GC.GetTotalAllocatedBytes(bool)"/>) is cumulative allocation.
/// It is the only way to see a large short-lived buffer: a 1.4 GB array that is allocated and
/// collected within one call never appears in a live-heap reading, but it is exactly what
/// drives Large Object Heap churn. This is the most robust number here.
/// </para>
/// <para>
/// <b>Peak private</b> is sampled by a <i>background thread</i>, not by the reporting loop.
/// That distinction is essential: the three samples call <see cref="Capture"/> a wildly
/// different number of times (once per batch, once per shard, once per N documents), so a peak
/// tracked only at capture points would measure the reporting cadence rather than the workload
/// - and would flatter whichever sample reports least often.
/// </para>
/// <para>
/// <b>Retained</b> heap and LOH are read once, after a forced blocking collection, by
/// <see cref="MeasureRetained"/>. The per-step "managed" and "LOH" columns are cheap unforced
/// reads and are useful only as a trend: <see cref="GC.GetTotalMemory(bool)"/> without a
/// collection still counts uncollected garbage, and <see cref="GC.GetGCMemoryInfo()"/> describes
/// the <i>last completed GC</i>, so its LOH figure can be arbitrarily stale. Never quote the
/// per-step values as a residency result.
/// </para>
/// </remarks>
public sealed class MemoryMonitor : IDisposable
{
    private const int PeakSampleIntervalMilliseconds = 25;

    private readonly List<MemorySample> samples = [];
    private readonly Process process = Process.GetCurrentProcess();
    private readonly Stopwatch stopwatch = new();
    private readonly CancellationTokenSource peakSamplerCancellation = new();

    private Thread? peakSamplerThread;
    private long peakPrivateBytes;
    private long baselineAllocated;
    private long previousAllocated;
    private int step;
    private bool disposed;

    public IReadOnlyList<MemorySample> Samples => samples;

    public long PeakPrivateBytes => Interlocked.Read(ref peakPrivateBytes);

    public long TotalAllocatedBytes => previousAllocated - baselineAllocated;

    public TimeSpan Elapsed => stopwatch.Elapsed;

    /// <summary>
    /// Establishes the baseline and starts the background peak sampler.
    /// </summary>
    /// <remarks>
    /// Every sample must call this at the same logical point - after the license is applied and
    /// the document list is built, but <i>before</i> any index is constructed - or the totals are
    /// not comparable across samples.
    /// </remarks>
    public void Start()
    {
        baselineAllocated = GC.GetTotalAllocatedBytes(precise: false);
        previousAllocated = baselineAllocated;
        step = 0;
        Interlocked.Exchange(ref peakPrivateBytes, ReadPrivateBytes());

        peakSamplerThread = new Thread(SamplePeakLoop)
        {
            IsBackground = true,
            Name = "peak-private-bytes-sampler",
            Priority = ThreadPriority.AboveNormal,
        };
        peakSamplerThread.Start();

        stopwatch.Restart();
    }

    /// <summary>Stops the background sampler. Call at the end of the build phase.</summary>
    public void StopSampling()
    {
        stopwatch.Stop();

        if (peakSamplerThread is null)
        {
            return;
        }

        peakSamplerCancellation.Cancel();
        peakSamplerThread.Join(TimeSpan.FromSeconds(2));
        peakSamplerThread = null;
    }

    private void SamplePeakLoop()
    {
        CancellationToken token = peakSamplerCancellation.Token;
        while (!token.IsCancellationRequested)
        {
            long current = ReadPrivateBytes();
            long observed = Interlocked.Read(ref peakPrivateBytes);
            while (current > observed)
            {
                long previous = Interlocked.CompareExchange(ref peakPrivateBytes, current, observed);
                if (previous == observed)
                {
                    break;
                }

                observed = previous;
            }

            token.WaitHandle.WaitOne(PeakSampleIntervalMilliseconds);
        }
    }

    private long ReadPrivateBytes()
    {
        try
        {
            process.Refresh();
            return process.PrivateMemorySize64;
        }
        catch (InvalidOperationException)
        {
            return 0;
        }
    }

    public MemorySample Capture(string label, int documentsProcessed)
    {
        step++;

        long allocatedNow = GC.GetTotalAllocatedBytes(precise: false);

        MemorySample sample = new(
            Step: step,
            Label: label,
            DocumentsProcessed: documentsProcessed,
            AllocatedSinceStartBytes: allocatedNow - baselineAllocated,
            AllocatedThisStepBytes: allocatedNow - previousAllocated,
            ManagedHeapBytes: GC.GetTotalMemory(forceFullCollection: false),
            PrivateBytes: ReadPrivateBytes(),
            LargeObjectHeapBytes: GetLargeObjectHeapBytes(),
            Gen0Collections: GC.CollectionCount(0),
            Gen1Collections: GC.CollectionCount(1),
            Gen2Collections: GC.CollectionCount(2),
            ElapsedSeconds: stopwatch.Elapsed.TotalSeconds);

        previousAllocated = allocatedNow;
        samples.Add(sample);
        return sample;
    }

    /// <summary>
    /// Forces a full blocking collection and reports what is genuinely still held.
    /// This is the only residency number worth publishing.
    /// </summary>
    public (long ManagedHeapBytes, long LargeObjectHeapBytes) MeasureRetained()
    {
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true);

        return (GC.GetTotalMemory(forceFullCollection: true), GetLargeObjectHeapBytes());
    }

    private static long GetLargeObjectHeapBytes()
    {
        GCMemoryInfo info = GC.GetGCMemoryInfo();
        ReadOnlySpan<GCGenerationInfo> generations = info.GenerationInfo;

        // Generations are ordered gen0, gen1, gen2, LOH, POH.
        const int LargeObjectHeapIndex = 3;
        return generations.Length > LargeObjectHeapIndex
            ? generations[LargeObjectHeapIndex].SizeAfterBytes
            : 0;
    }

    public static void WriteTableHeader()
    {
        Console.WriteLine();
        Console.WriteLine(
            "{0,5} {1,-18} {2,7} {3,12} {4,12} {5,11} {6,11} {7,11} {8,8}",
            "step", "stage", "docs", "alloc/step", "alloc total", "managed", "private", "LOH", "elapsed");
        Console.WriteLine(new string('-', 103));
    }

    public static void WriteTableRow(MemorySample sample)
    {
        Console.WriteLine(
            "{0,5} {1,-18} {2,7} {3,12} {4,12} {5,11} {6,11} {7,11} {8,7:F1}s",
            sample.Step,
            Truncate(sample.Label, 18),
            sample.DocumentsProcessed,
            FormatBytes(sample.AllocatedThisStepBytes),
            FormatBytes(sample.AllocatedSinceStartBytes),
            FormatBytes(sample.ManagedHeapBytes),
            FormatBytes(sample.PrivateBytes),
            FormatBytes(sample.LargeObjectHeapBytes),
            sample.ElapsedSeconds);
    }

    /// <summary>
    /// Stops sampling, measures the retained set, and prints the comparable summary.
    /// Every sample must call this at the same logical point: the end of the build phase,
    /// before any verification search.
    /// </summary>
    public RunTotals CompleteBuildPhase(string sampleName, int documentCount, int errorCount)
    {
        StopSampling();
        (long retainedHeap, long retainedLoh) = MeasureRetained();

        RunTotals totals = new(
            Documents: documentCount,
            TotalAllocatedBytes: TotalAllocatedBytes,
            PeakPrivateBytes: PeakPrivateBytes,
            RetainedManagedHeapBytes: retainedHeap,
            RetainedLargeObjectHeapBytes: retainedLoh,
            BuildSeconds: stopwatch.Elapsed.TotalSeconds);

        MemorySample? last = samples.Count > 0 ? samples[^1] : null;

        Console.WriteLine();
        Console.WriteLine(new string('=', 103));
        Console.WriteLine($"  {sampleName}");
        Console.WriteLine("  (build phase only - verification and any query phase are excluded)");
        Console.WriteLine(new string('=', 103));
        Console.WriteLine($"  documents indexed        : {documentCount}");
        Console.WriteLine($"  indexing errors          : {errorCount}");
        Console.WriteLine($"  build wall clock         : {totals.BuildSeconds:F1} s");
        Console.WriteLine($"  total allocated          : {FormatBytes(totals.TotalAllocatedBytes)}");
        Console.WriteLine($"  allocated per document   : {FormatBytes(documentCount > 0 ? totals.TotalAllocatedBytes / documentCount : 0)}");
        Console.WriteLine($"  peak private bytes       : {FormatBytes(totals.PeakPrivateBytes)}   (background sampler, {PeakSampleIntervalMilliseconds} ms)");
        Console.WriteLine($"  retained managed heap    : {FormatBytes(totals.RetainedManagedHeapBytes)}   (after forced full GC)");
        Console.WriteLine($"  retained LOH             : {FormatBytes(totals.RetainedLargeObjectHeapBytes)}   (after forced full GC)");

        if (last is not null)
        {
            Console.WriteLine($"  gen0/gen1/gen2           : {last.Gen0Collections} / {last.Gen1Collections} / {last.Gen2Collections}");
        }

        Console.WriteLine(new string('=', 103));
        return totals;
    }

    public void SaveResults(string outputFolder, string sampleName, RunTotals totals, RunManifest manifest)
    {
        Directory.CreateDirectory(outputFolder);

        string csvPath = Path.Combine(outputFolder, $"{sampleName}.csv");
        StringBuilder csv = new();
        csv.AppendLine("step,label,documents,allocated_step_bytes,allocated_total_bytes,managed_heap_bytes,private_bytes,loh_bytes,gen0,gen1,gen2,elapsed_seconds");
        foreach (MemorySample s in samples)
        {
            csv.Append(s.Step).Append(',')
               .Append('"').Append(s.Label.Replace("\"", "\"\"", StringComparison.Ordinal)).Append('"').Append(',')
               .Append(s.DocumentsProcessed).Append(',')
               .Append(s.AllocatedThisStepBytes).Append(',')
               .Append(s.AllocatedSinceStartBytes).Append(',')
               .Append(s.ManagedHeapBytes).Append(',')
               .Append(s.PrivateBytes).Append(',')
               .Append(s.LargeObjectHeapBytes).Append(',')
               .Append(s.Gen0Collections).Append(',')
               .Append(s.Gen1Collections).Append(',')
               .Append(s.Gen2Collections).Append(',')
               .Append(s.ElapsedSeconds.ToString("F3", CultureInfo.InvariantCulture))
               .AppendLine();
        }

        File.WriteAllText(csvPath, csv.ToString());

        JsonSerializerOptions jsonOptions = new() { WriteIndented = true };
        string jsonPath = Path.Combine(outputFolder, $"{sampleName}.json");
        File.WriteAllText(jsonPath, JsonSerializer.Serialize(new { manifest, totals, samples }, jsonOptions));

        Console.WriteLine();
        Console.WriteLine($"  results written to {csvPath}");
        Console.WriteLine($"                     {jsonPath}");
    }

    public static string FormatBytes(long bytes)
    {
        const double Kilobyte = 1024;
        const double Megabyte = Kilobyte * 1024;
        const double Gigabyte = Megabyte * 1024;

        double absolute = Math.Abs((double)bytes);
        return absolute switch
        {
            >= Gigabyte => (bytes / Gigabyte).ToString("F2", CultureInfo.InvariantCulture) + " GB",
            >= Megabyte => (bytes / Megabyte).ToString("F1", CultureInfo.InvariantCulture) + " MB",
            >= Kilobyte => (bytes / Kilobyte).ToString("F0", CultureInfo.InvariantCulture) + " KB",
            _ => bytes.ToString(CultureInfo.InvariantCulture) + " B",
        };
    }

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength];

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        StopSampling();
        peakSamplerCancellation.Dispose();
        process.Dispose();
    }
}
