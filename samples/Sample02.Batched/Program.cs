using GroupDocs.Search;
using GroupDocs.Search.Common;
using GroupDocs.Search.Options;
using GroupDocs.Search.Results;
using IndexingMemoryLab.Core;
using Index = GroupDocs.Search.Index;

// ---------------------------------------------------------------------------
// Sample 02 - one Add() call per batch of documents.
//
// The fixed cost of an indexing operation - two deep copies of the index
// metadata and term dictionary, one set of term-collector buffers, one segment,
// one re-serialization of index.info - is paid once per Add() call rather than
// once per document. Indexing the same documents in batches of N therefore pays
// that cost N times less often.
//
// Run this after Sample 01 with the same Storage.MaxDocuments and compare the
// 'allocated per document' line in each summary.
//
// MEASUREMENT WINDOW - identical in all three samples, or the numbers do not
// compare: monitor.Start() runs before the Index is constructed, and the build
// phase is completed before any verification search.
// ---------------------------------------------------------------------------

const string SampleName = "Sample 02 - one Add() call per batch";

try
{
    SampleHost host = SampleHost.Bootstrap(SampleName);

    int batchSize = Math.Max(1, host.Options.Run.BatchSize);
    string indexFolder = host.PrepareIndexFolder("batched");
    IndexSettings settings = host.CreateIndexSettings();
    IndexingOptions indexingOptions = host.CreateIndexingOptions();

    using MemoryMonitor monitor = new();

    int batchCount = (host.Documents.Count + batchSize - 1) / batchSize;
    Console.WriteLine();
    Console.WriteLine($"Indexing {host.Documents.Count} documents in {batchCount} batch(es) of up to {batchSize}.");
    Console.WriteLine("Watch 'alloc/step': one batch costs about what one single-document Add() cost in Sample 01.");

    MemoryMonitor.WriteTableHeader();
    monitor.Start();

    using Index index = new(indexFolder, settings);
    host.TrackErrors(index);

    int processed = 0;
    for (int batchIndex = 0; batchIndex < batchCount; batchIndex++)
    {
        int offset = batchIndex * batchSize;
        int count = Math.Min(batchSize, host.Documents.Count - offset);

        Document[] batch = new Document[count];
        for (int i = 0; i < count; i++)
        {
            batch[i] = Document.CreateFromFile(host.Documents[offset + i]);
        }

        // One Add() call for the whole batch.
        index.Add(batch, indexingOptions);
        processed += count;

        MemoryMonitor.WriteTableRow(monitor.Capture($"batch {batchIndex + 1}/{batchCount}", processed));
    }

    // End of the build phase. Everything after this point is excluded from the totals.
    RunTotals totals = monitor.CompleteBuildPhase(SampleName, processed, host.ErrorCount);

    host.PrintErrorDigest();

    int indexedDocumentCount = index.GetIndexedDocuments().Length;
    SearchResultSummary verification = host.Verify("batched index", indexedDocumentCount, () =>
    {
        string query = host.Options.Run.VerificationQuery;
        SearchResult result = index.Search(query);
        return new SearchResultSummary(query, result.DocumentCount, result.OccurrenceCount);
    });

    if (host.Options.Run.WriteResultFiles)
    {
        monitor.SaveResults(
            host.ResultsFolder,
            "02-batched",
            totals,
            host.CreateManifest("02-batched", indexedDocumentCount, verification));
    }

    Console.WriteLine();
    Console.WriteLine("Batching lowers the cost per document, but the index metadata and term dictionary");
    Console.WriteLine("still grow without bound in RAM. Run Sample03.Sharded to see how that is bounded.");
    return 0;
}
catch (Exception exception)
{
    Console.Error.WriteLine();
    Console.Error.WriteLine($"ERROR: {exception.Message}");
    return 1;
}
