using GroupDocs.Search;
using GroupDocs.Search.Common;
using GroupDocs.Search.Options;
using GroupDocs.Search.Results;
using IndexingMemoryLab.Core;
using Index = GroupDocs.Search.Index;

// ---------------------------------------------------------------------------
// Sample 01 - one Add() call per document.
//
// This is the anti-pattern. Every Add() call is a complete indexing operation:
// it deep-copies the whole index metadata and term dictionary on the way in,
// allocates a fresh set of term-collector buffers, writes a new segment, then
// deep-copies everything back on commit and re-serializes index.info to disk.
//
// None of that cost is proportional to the single document being added, so the
// per-document allocation figure this sample prints is close to the fixed cost
// of one commit. Compare it with Sample 02.
//
// MEASUREMENT WINDOW - identical in all three samples, or the numbers do not
// compare: monitor.Start() runs before the Index is constructed, and the build
// phase is completed before any verification search.
// ---------------------------------------------------------------------------

const string SampleName = "Sample 01 - one Add() call per document (the anti-pattern)";

try
{
    SampleHost host = SampleHost.Bootstrap(SampleName);

    string indexFolder = host.PrepareIndexFolder("per-document");
    IndexSettings settings = host.CreateIndexSettings();
    IndexingOptions indexingOptions = host.CreateIndexingOptions();

    using MemoryMonitor monitor = new();
    int reportEvery = Math.Max(1, host.Options.Run.ReportEvery);

    Console.WriteLine();
    Console.WriteLine($"Indexing {host.Documents.Count} documents, one Add() call each.");
    Console.WriteLine("Watch 'alloc/step': it stays roughly constant no matter how small the document is.");

    MemoryMonitor.WriteTableHeader();
    monitor.Start();

    using Index index = new(indexFolder, settings);
    host.TrackErrors(index);

    for (int i = 0; i < host.Documents.Count; i++)
    {
        Document document = Document.CreateFromFile(host.Documents[i]);

        // One document, one full indexing operation.
        index.Add([document], indexingOptions);

        bool isLastDocument = i == host.Documents.Count - 1;
        if ((i + 1) % reportEvery == 0 || isLastDocument)
        {
            MemoryMonitor.WriteTableRow(monitor.Capture($"doc {i + 1}", i + 1));
        }
    }

    // End of the build phase. Everything after this point is excluded from the totals.
    RunTotals totals = monitor.CompleteBuildPhase(SampleName, host.Documents.Count, host.ErrorCount);

    host.PrintErrorDigest();

    int indexedDocumentCount = index.GetIndexedDocuments().Length;
    SearchResultSummary verification = host.Verify("per-document index", indexedDocumentCount, () =>
    {
        string query = host.Options.Run.VerificationQuery;
        SearchResult result = index.Search(query);
        return new SearchResultSummary(query, result.DocumentCount, result.OccurrenceCount);
    });

    if (host.Options.Run.WriteResultFiles)
    {
        monitor.SaveResults(
            host.ResultsFolder,
            "01-per-document",
            totals,
            host.CreateManifest("01-per-document", indexedDocumentCount, verification));
    }

    Console.WriteLine();
    Console.WriteLine("Now run Sample02.Batched over the same document count and compare 'allocated per document'.");
    return 0;
}
catch (Exception exception)
{
    // A stack trace here would leak absolute build-machine paths into every user's console,
    // and the messages this sample throws are already actionable on their own.
    Console.Error.WriteLine();
    Console.Error.WriteLine($"ERROR: {exception.Message}");
    return 1;
}
