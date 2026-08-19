using GroupDocs.Search;
using GroupDocs.Search.Common;
using GroupDocs.Search.Options;
using GroupDocs.Search.Results;
using IndexingMemoryLab.Core;
using Index = GroupDocs.Search.Index;

// ---------------------------------------------------------------------------
// Sample 03 - many small indexes, queried as one through IndexRepository.
//
// Batching (Sample 02) reduces the cost *per operation*, but one structure keeps
// growing no matter how you batch: the per-document metadata and the term
// dictionary are held in RAM for the whole lifetime of an open Index, and they
// scale linearly with the number of documents in that index.
//
// Sharding bounds it. Each shard is built and disposed in turn, so the resident
// set never exceeds one shard's worth. Afterwards every shard is loaded into an
// IndexRepository and queried as a single logical index - so you do not give up
// search semantics to get the memory back.
//
// MEASUREMENT WINDOW - identical in all three samples, or the numbers do not
// compare: monitor.Start() runs before any Index is constructed, and the build
// phase is completed before the repository is loaded. The repository load and
// the cross-shard search are reported separately as a QUERY PHASE and are
// deliberately excluded from the build totals, because Samples 01 and 02 have
// no equivalent step.
// ---------------------------------------------------------------------------

const string SampleName = "Sample 03 - sharded indexes + IndexRepository";

try
{
    SampleHost host = SampleHost.Bootstrap(SampleName);

    int documentsPerShard = Math.Max(1, host.Options.Run.DocumentsPerShard);
    IndexSettings settings = host.CreateIndexSettings();
    IndexingOptions indexingOptions = host.CreateIndexingOptions();

    using MemoryMonitor monitor = new();

    int shardCount = (host.Documents.Count + documentsPerShard - 1) / documentsPerShard;
    List<string> shardFolders = new(shardCount);

    // Clear the whole shard tree once, so a previous run with more shards cannot
    // leave stale shard-NNN folders behind to confuse anyone inspecting out/.
    host.PrepareIndexFolder("sharded");

    Console.WriteLine();
    Console.WriteLine($"Building {shardCount} shard(s) of up to {documentsPerShard} documents each.");
    Console.WriteLine("Each shard is disposed before the next one starts, so resident memory stays bounded.");

    MemoryMonitor.WriteTableHeader();
    monitor.Start();

    int processed = 0;
    for (int shardIndex = 0; shardIndex < shardCount; shardIndex++)
    {
        int offset = shardIndex * documentsPerShard;
        int count = Math.Min(documentsPerShard, host.Documents.Count - offset);

        string shardFolder = host.PrepareIndexFolder(Path.Combine("sharded", $"shard-{shardIndex:D3}"));
        shardFolders.Add(shardFolder);

        // The using block is the whole point: the shard's metadata and term dictionary
        // leave memory when it closes.
        using (Index shard = new(shardFolder, settings))
        {
            host.TrackErrors(shard);

            Document[] batch = new Document[count];
            for (int i = 0; i < count; i++)
            {
                batch[i] = Document.CreateFromFile(host.Documents[offset + i]);
            }

            shard.Add(batch, indexingOptions);
        }

        processed += count;

        MemoryMonitor.WriteTableRow(monitor.Capture($"shard {shardIndex + 1}/{shardCount}", processed));
    }

    // End of the build phase - this is the point Samples 01 and 02 also stop at.
    RunTotals totals = monitor.CompleteBuildPhase(SampleName, processed, host.ErrorCount);

    host.PrintErrorDigest();

    // -----------------------------------------------------------------------
    // Query phase - measured separately, NOT part of the comparison table.
    // -----------------------------------------------------------------------

    Console.WriteLine();
    Console.WriteLine($"Query phase (excluded from the totals above): loading {shardFolders.Count} shard(s) into an IndexRepository.");

    long allocatedBeforeQueryPhase = GC.GetTotalAllocatedBytes(precise: false);

    using IndexRepository repository = new();
    foreach (string shardFolder in shardFolders)
    {
        repository.AddToRepository(shardFolder);
    }

    int documentsAcrossShards = repository.Indexes.Sum(shard => shard.GetIndexedDocuments().Length);
    SearchResultSummary verification = host.Verify("sharded repository", documentsAcrossShards, () =>
    {
        string query = host.Options.Run.VerificationQuery;
        SearchResult result = repository.Search(query);
        return new SearchResultSummary(query, result.DocumentCount, result.OccurrenceCount);
    });

    long queryPhaseAllocated = GC.GetTotalAllocatedBytes(precise: false) - allocatedBeforeQueryPhase;
    (long queryHeap, long queryLoh) = monitor.MeasureRetained();

    Console.WriteLine();
    Console.WriteLine($"  query phase allocated    : {MemoryMonitor.FormatBytes(queryPhaseAllocated)}");
    Console.WriteLine($"  retained with all shards open: {MemoryMonitor.FormatBytes(queryHeap)} managed, {MemoryMonitor.FormatBytes(queryLoh)} LOH");
    Console.WriteLine();
    Console.WriteLine("  Note: loading every shard into one IndexRepository puts all of their metadata back");
    Console.WriteLine("  in memory at once. The saving is in the BUILD phase, and in being able to keep only");
    Console.WriteLine("  the shards you need open - or to spread them across processes or machines.");

    if (host.Options.Run.WriteResultFiles)
    {
        monitor.SaveResults(
            host.ResultsFolder,
            "03-sharded",
            totals,
            host.CreateManifest("03-sharded", documentsAcrossShards, verification));
    }

    return 0;
}
catch (Exception exception)
{
    Console.Error.WriteLine();
    Console.Error.WriteLine($"ERROR: {exception.Message}");
    return 1;
}
