using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using GroupDocs.Search;
using GroupDocs.Search.Events;
using GroupDocs.Search.Options;
using Index = GroupDocs.Search.Index;

namespace IndexingMemoryLab.Core;

/// <summary>
/// The setup every sample shares: license, index folder, index settings, error counting.
/// </summary>
public sealed class SampleHost
{
    private SampleHost(SampleOptions options, IReadOnlyList<string> documents, bool licensed)
    {
        Options = options;
        Documents = documents;
        IsLicensed = licensed;
    }

    public SampleOptions Options { get; }

    public IReadOnlyList<string> Documents { get; }

    public bool IsLicensed { get; }

    private int errorCount;
    private readonly List<string> firstErrors = [];
    private readonly object errorSync = new();

    /// <summary>
    /// Indexing errors seen so far. GroupDocs raises ErrorOccurred from whichever thread is
    /// doing the extraction, so this is updated under a lock - Index.Threads is a documented
    /// user-facing knob and a lost increment would corrupt the Verify expectation below.
    /// </summary>
    public int ErrorCount => Volatile.Read(ref errorCount);

    /// <summary>
    /// Loads configuration, applies the license, and builds the bounded document list.
    /// Prints what it did, because a sample that silently indexes the wrong thing is worse
    /// than one that fails.
    /// </summary>
    public static SampleHost Bootstrap(string sampleName)
    {
        Console.WriteLine(new string('=', 112));
        Console.WriteLine($"  {sampleName}");
        Console.WriteLine(new string('=', 112));

        SampleOptions options = SampleOptions.Load();
        options.Validate();

        bool licensed = TryApplyLicense(options.License);

        Console.WriteLine();
        Console.WriteLine("Configuration");
        Console.WriteLine($"  storage root      : {options.Storage.RootPath}");
        Console.WriteLine($"  max documents     : {options.Storage.MaxDocuments}");
        Console.WriteLine($"  max file size     : {MemoryMonitor.FormatBytes(options.Storage.MaxFileSizeBytes)}");
        Console.WriteLine($"  index type        : {options.Index.IndexType}");
        Console.WriteLine($"  indexing threads  : {options.Index.Threads}");
        Console.WriteLine($"  server GC         : {System.Runtime.GCSettings.IsServerGC}");
        Console.WriteLine($"  license           : {(licensed ? "applied" : "NOT APPLIED - trial limits apply, numbers are not representative")}");

        Console.WriteLine();
        Console.WriteLine("Collecting documents (bounded - the full corpus is never enumerated)");
        IReadOnlyList<string> documents = DocumentSource.Collect(options.Storage);
        Console.WriteLine($"  {documents.Count} documents selected");

        if (documents.Count == 0)
        {
            throw new InvalidOperationException(
                $"No documents matched under '{options.Storage.RootPath}'. Check Storage.Folders and Storage.Extensions in appsettings.json.");
        }

        return new SampleHost(options, documents, licensed);
    }

    private static bool TryApplyLicense(LicenseOptions licenseOptions)
    {
        string? licensePath = Environment.GetEnvironmentVariable(licenseOptions.EnvironmentVariable);

        if (string.IsNullOrWhiteSpace(licensePath))
        {
            string? fallbackFolder = string.IsNullOrWhiteSpace(licenseOptions.FallbackFolderVariable)
                || string.IsNullOrWhiteSpace(licenseOptions.FallbackRelativePath)
                    ? null
                    : Environment.GetEnvironmentVariable(licenseOptions.FallbackFolderVariable);
            if (!string.IsNullOrWhiteSpace(fallbackFolder))
            {
                string relative = licenseOptions.FallbackRelativePath
                    .Replace('\\', Path.DirectorySeparatorChar)
                    .Replace('/', Path.DirectorySeparatorChar);
                licensePath = Path.Combine(fallbackFolder, relative);
            }
        }

        if (string.IsNullOrWhiteSpace(licensePath) || !File.Exists(licensePath))
        {
            Console.WriteLine();
            Console.WriteLine($"  ! No license found. Set {licenseOptions.EnvironmentVariable} to the full path of a .lic file.");
            Console.WriteLine("  ! Without a license the number of indexed documents is capped, so the memory");
            Console.WriteLine("  ! figures below will NOT reflect real behaviour.");
            return false;
        }

        try
        {
            new License().SetLicense(licensePath);
            return true;
        }
        catch (Exception exception)
        {
            Console.WriteLine($"  ! Failed to apply license from '{licensePath}': {exception.Message}");
            return false;
        }
    }

    /// <summary>Resolves and optionally empties an index folder under the configured output root.</summary>
    public string PrepareIndexFolder(string relativeName)
    {
        string root = Path.IsPathRooted(Options.Index.OutputRoot)
            ? Options.Index.OutputRoot
            : Path.Combine(AppContext.BaseDirectory, Options.Index.OutputRoot);

        string folder = Path.Combine(root, relativeName);

        if (Options.Index.CleanBeforeRun && Directory.Exists(folder))
        {
            Directory.Delete(folder, recursive: true);
        }

        Directory.CreateDirectory(folder);
        return folder;
    }

    public string ResultsFolder =>
        Path.IsPathRooted(Options.Index.OutputRoot)
            ? Path.Combine(Options.Index.OutputRoot, "results")
            : Path.Combine(AppContext.BaseDirectory, Options.Index.OutputRoot, "results");

    public IndexSettings CreateIndexSettings()
    {
        IndexSettings settings = new()
        {
            IndexType = Enum.Parse<IndexType>(Options.Index.IndexType, ignoreCase: true),
            MaxIndexingReportCount = Options.Index.MaxIndexingReportCount,
            MaxSearchReportCount = 0,
        };

        return settings;
    }

    public IndexingOptions CreateIndexingOptions() => new() { Threads = Options.Index.Threads };

    /// <summary>
    /// Counts indexing errors instead of letting them scroll past. Some documents in any
    /// real corpus are corrupt, password protected, or an unsupported format; that is
    /// expected and must not be mistaken for a memory problem.
    /// </summary>
    public void TrackErrors(Index index)
    {
        index.Events.ErrorOccurred += OnErrorOccurred;
    }

    private void OnErrorOccurred(object? sender, IndexErrorEventArgs e)
    {
        Interlocked.Increment(ref errorCount);
        lock (errorSync)
        {
            if (firstErrors.Count < 5)
            {
                firstErrors.Add(e.Message);
            }
        }
    }

    public void PrintErrorDigest()
    {
        if (ErrorCount == 0)
        {
            return;
        }

        string[] snapshot;
        lock (errorSync)
        {
            snapshot = firstErrors.ToArray();
        }

        Console.WriteLine();
        Console.WriteLine($"  {ErrorCount} indexing error(s). First {snapshot.Length}:");
        foreach (string message in snapshot)
        {
            string singleLine = message.ReplaceLineEndings(" ");
            Console.WriteLine($"    - {(singleLine.Length > 160 ? singleLine[..160] + "..." : singleLine)}");
        }
    }

    /// <summary>
    /// Confirms the index really holds what we think it does, so a suspiciously fast run
    /// cannot quietly be an empty one. The document count is the assertion; the sample
    /// query is informational only, because whether any given word appears in an arbitrary
    /// corpus is not something this repository can guarantee.
    /// </summary>
    public SearchResultSummary Verify(string description, int indexedDocumentCount, Func<SearchResultSummary> search)
    {
        int attempted = Documents.Count;
        int expected = attempted - ErrorCount;

        Console.WriteLine();
        Console.WriteLine($"Verification - {description}");
        Console.WriteLine($"  documents in index : {indexedDocumentCount} (attempted {attempted}, {ErrorCount} failed extraction)");

        if (indexedDocumentCount == 0)
        {
            throw new InvalidOperationException(
                "The index reports zero documents. The run produced no usable index, so the memory figures above measure nothing.");
        }

        if (indexedDocumentCount < expected)
        {
            Console.WriteLine(
                $"  ! fewer documents than expected ({indexedDocumentCount} < {expected}). " +
                (IsLicensed
                    ? "Some documents may have been skipped by the document filter."
                    : "This is almost certainly the unlicensed trial cap - set the license environment variable."));
        }

        SearchResultSummary summary = search();
        Console.WriteLine($"  sample query       : '{summary.Query}' matched {summary.DocumentCount} document(s), {summary.OccurrenceCount} occurrence(s)");

        if (summary.DocumentCount == 0)
        {
            Console.WriteLine(
                "  ! the sample query matched nothing. Note that common words such as 'the' and 'and' are");
            Console.WriteLine(
                "  ! stop words by default and are never indexed. Change Run.VerificationQuery in appsettings.json.");
        }

        return summary;
    }

    /// <summary>
    /// Records everything needed to prove two runs are comparable. Diffing the manifests of the
    /// three samples turns "they indexed the same documents and returned the same results" from a
    /// claim into a check: DocumentSetHash and the query counts must match exactly.
    /// </summary>
    public RunManifest CreateManifest(string sampleName, int indexedDocumentCount, SearchResultSummary verification)
    {
        return new RunManifest(
            SampleName: sampleName,
            RunUtc: DateTime.UtcNow.ToString("O"),
            GroupDocsSearchVersion: typeof(Index).Assembly.GetName().Version?.ToString() ?? "unknown",
            RuntimeVersion: RuntimeInformation.FrameworkDescription,
            OperatingSystem: RuntimeInformation.OSDescription,
            ProcessArchitecture: RuntimeInformation.ProcessArchitecture.ToString(),
            BuildConfiguration: BuildConfiguration,
            IsServerGC: System.Runtime.GCSettings.IsServerGC,
            IsLicensed: IsLicensed,
            IndexType: Options.Index.IndexType,
            IndexingThreads: Options.Index.Threads,
            DocumentsRequested: Options.Storage.MaxDocuments,
            DocumentsSelected: Documents.Count,
            DocumentsIndexed: indexedDocumentCount,
            ExtractionErrors: ErrorCount,
            DocumentSetHash: ComputeDocumentSetHash(),
            VerificationQuery: verification.Query,
            VerificationDocumentCount: verification.DocumentCount,
            VerificationOccurrenceCount: verification.OccurrenceCount);
    }

    private static string BuildConfiguration =>
#if DEBUG
        "Debug";
#else
        "Release";
#endif

    /// <summary>
    /// SHA-256 over the ordered document path list. Two runs with the same hash indexed exactly
    /// the same files in exactly the same order.
    /// </summary>
    private string ComputeDocumentSetHash()
    {
        using SHA256 sha = SHA256.Create();
        StringBuilder builder = new();
        foreach (string path in Documents)
        {
            builder.Append(path).Append('\n');
        }

        byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(builder.ToString()));
        return Convert.ToHexString(hash);
    }
}

public sealed record SearchResultSummary(string Query, int DocumentCount, int OccurrenceCount);

/// <summary>Provenance for one measurement run. Written next to the CSV so results are checkable.</summary>
public sealed record RunManifest(
    string SampleName,
    string RunUtc,
    string GroupDocsSearchVersion,
    string RuntimeVersion,
    string OperatingSystem,
    string ProcessArchitecture,
    string BuildConfiguration,
    bool IsServerGC,
    bool IsLicensed,
    string IndexType,
    int IndexingThreads,
    int DocumentsRequested,
    int DocumentsSelected,
    int DocumentsIndexed,
    int ExtractionErrors,
    string DocumentSetHash,
    string VerificationQuery,
    int VerificationDocumentCount,
    int VerificationOccurrenceCount);
