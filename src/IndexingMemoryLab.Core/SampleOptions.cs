using System.Text.Json;
using System.Text.Json.Serialization;

namespace IndexingMemoryLab.Core;

/// <summary>
/// Everything a sample needs to run, loaded from the sample's own appsettings.json.
/// Each sample project carries its own copy so its knobs live next to its code.
/// </summary>
public sealed class SampleOptions
{
    public StorageOptions Storage { get; set; } = new();

    public IndexOptions Index { get; set; } = new();

    public RunOptions Run { get; set; } = new();

    public LicenseOptions License { get; set; } = new();

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>
    /// Loads appsettings.json from the executable directory. If a git-ignored
    /// appsettings.local.json sits next to it, that file <em>replaces</em> the
    /// settings wholesale - it is not merged key by key, so a local file must be
    /// complete. Environment variables are applied last and win over both.
    /// </summary>
    public static SampleOptions Load()
    {
        string baseDirectory = AppContext.BaseDirectory;
        SampleOptions options = ReadFile(Path.Combine(baseDirectory, "appsettings.json"))
            ?? throw new FileNotFoundException(
                "appsettings.json was not found next to the executable. It should be copied to the output directory by the sample project.",
                Path.Combine(baseDirectory, "appsettings.json"));

        SampleOptions? local = ReadFile(Path.Combine(baseDirectory, "appsettings.local.json"));
        if (local is not null)
        {
            options = local;
        }

        ApplyEnvironmentOverrides(options);
        return options;
    }

    private static SampleOptions? ReadFile(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        string json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<SampleOptions>(json, SerializerOptions);
    }

    private static void ApplyEnvironmentOverrides(SampleOptions options)
    {
        string? storageRoot = Environment.GetEnvironmentVariable("INDEXING_LAB_STORAGE_ROOT");
        if (!string.IsNullOrWhiteSpace(storageRoot))
        {
            options.Storage.RootPath = storageRoot;
        }

        string? maxDocuments = Environment.GetEnvironmentVariable("INDEXING_LAB_MAX_DOCUMENTS");
        if (int.TryParse(maxDocuments, out int parsedMaxDocuments) && parsedMaxDocuments > 0)
        {
            options.Storage.MaxDocuments = parsedMaxDocuments;
        }

        string? batchSize = Environment.GetEnvironmentVariable("INDEXING_LAB_BATCH_SIZE");
        if (int.TryParse(batchSize, out int parsedBatchSize) && parsedBatchSize > 0)
        {
            options.Run.BatchSize = parsedBatchSize;
        }

        string? documentsPerShard = Environment.GetEnvironmentVariable("INDEXING_LAB_DOCS_PER_SHARD");
        if (int.TryParse(documentsPerShard, out int parsedDocumentsPerShard) && parsedDocumentsPerShard > 0)
        {
            options.Run.DocumentsPerShard = parsedDocumentsPerShard;
        }
    }

    /// <summary>Guard rail above which a run must be explicitly opted into. See Validate().</summary>
    public const int AbsoluteMaxDocuments = 50_000;

    /// <summary>
    /// Fails fast with an actionable message rather than letting a bad path surface
    /// as an empty run or a confusing exception deep inside the product.
    /// </summary>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Storage.RootPath))
        {
            throw new InvalidOperationException("Storage.RootPath is not set in appsettings.json.");
        }

        if (!Directory.Exists(Storage.RootPath))
        {
            throw new DirectoryNotFoundException(
                $"Storage.RootPath '{Storage.RootPath}' does not exist. " +
                "Point it at a folder of documents, or set the INDEXING_LAB_STORAGE_ROOT environment variable.");
        }

        if (Storage.MaxDocuments <= 0)
        {
            throw new InvalidOperationException(
                "Storage.MaxDocuments must be greater than zero. This is the safety cap that stops a sample " +
                "from walking an entire multi-hundred-thousand-file corpus.");
        }

        // System.Text.Json will write an explicit null straight over a property initializer,
        // so these are reachable from a hand-edited appsettings.json.
        if (Storage.Folders is null || Storage.Folders.Length == 0)
        {
            throw new InvalidOperationException("Storage.Folders must list at least one subfolder of Storage.RootPath.");
        }

        if (Storage.Extensions is null)
        {
            throw new InvalidOperationException("Storage.Extensions must be an array. Use [] to accept every extension.");
        }

        if (Storage.MaxDocuments > AbsoluteMaxDocuments && !Storage.AllowUnboundedRun)
        {
            throw new InvalidOperationException(
                $"Storage.MaxDocuments is {Storage.MaxDocuments}, above the {AbsoluteMaxDocuments} guard rail. " +
                "The reference corpus holds ~894,000 files; a per-document run at that size would allocate on the " +
                "order of a petabyte and take days. Set Storage.AllowUnboundedRun to true if you really mean it.");
        }
    }
}

public sealed class StorageOptions
{
    /// <summary>Root folder that holds the document subfolders.</summary>
    public string RootPath { get; set; } = "<path to your documents folder>";

    /// <summary>Subfolders of <see cref="RootPath"/> to draw documents from, in order.</summary>
    public string[] Folders { get; set; } = ["Html", "Pdf", "Text", "Words", "Spreadsheets", "Presentations"];

    /// <summary>
    /// Hard cap on documents per run. The reference corpus has ~894 000 files;
    /// no sample should ever try to index all of them.
    /// </summary>
    public int MaxDocuments { get; set; } = 1000;

    /// <summary>Files larger than this are skipped, to keep extraction time predictable.</summary>
    public long MaxFileSizeBytes { get; set; } = 10L * 1024 * 1024;

    /// <summary>Only files with these extensions are considered. Empty means "any extension".</summary>
    public string[] Extensions { get; set; } = [".html", ".htm", ".pdf", ".txt", ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx"];

    /// <summary>
    /// Required to raise <see cref="MaxDocuments"/> above
    /// <see cref="SampleOptions.AbsoluteMaxDocuments"/>. Exists so that a very large run is
    /// always a deliberate act rather than an arithmetic slip.
    /// </summary>
    public bool AllowUnboundedRun { get; set; }
}

public sealed class IndexOptions
{
    /// <summary>Where index folders are created. Relative paths are resolved against the executable directory.</summary>
    public string OutputRoot { get; set; } = "out";

    /// <summary>NormalIndex, CompactIndex or MetadataIndex.</summary>
    public string IndexType { get; set; } = "NormalIndex";

    /// <summary>
    /// Indexing threads. Each thread allocates its own term-collector buffers,
    /// so this multiplies the per-operation memory cost.
    /// </summary>
    public int Threads { get; set; } = 1;

    /// <summary>
    /// Retained indexing reports. Each one holds the full document-path list of its
    /// batch, so large batches make this expensive. 0 disables retention.
    /// </summary>
    public int MaxIndexingReportCount { get; set; } = 0;

    /// <summary>Deletes the index folder before the run so every run starts from empty.</summary>
    public bool CleanBeforeRun { get; set; } = true;
}

public sealed class RunOptions
{
    /// <summary>Documents per Add() call. Used by the batched sample.</summary>
    public int BatchSize { get; set; } = 500;

    /// <summary>Documents per shard. Used by the sharded sample.</summary>
    public int DocumentsPerShard { get; set; } = 500;

    /// <summary>Print a memory row every N steps. 1 means every step.</summary>
    public int ReportEvery { get; set; } = 1;

    /// <summary>Query used to verify the index is actually usable after the run.</summary>
    public string VerificationQuery { get; set; } = "information";

    /// <summary>Writes results.csv and results.json next to the index output.</summary>
    public bool WriteResultFiles { get; set; } = true;
}

public sealed class LicenseOptions
{
    /// <summary>
    /// Environment variable holding the full path to a .lic file.
    /// Without a license GroupDocs.Search runs in trial mode and caps the
    /// number of indexed documents, which invalidates the measurements.
    /// </summary>
    public string EnvironmentVariable { get; set; } = "GROUPDOCS_LIC_PATH";

    /// <summary>
    /// Optional fallback environment variable holding a licenses *folder*, used together
    /// with <see cref="FallbackRelativePath"/>. Empty by default: set both in a
    /// git-ignored appsettings.local.json if your machine stores licenses that way.
    /// </summary>
    public string FallbackFolderVariable { get; set; } = "";

    /// <summary>
    /// Path of the license file relative to the fallback folder, written with '/' separators.
    /// Normalised for the current platform before use - a backslash-literal path would silently
    /// never resolve on Linux.
    /// </summary>
    public string FallbackRelativePath { get; set; } = "";
}
