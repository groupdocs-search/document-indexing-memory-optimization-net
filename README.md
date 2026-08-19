# Document indexing memory optimization (.NET)

Measured, runnable evidence for how **GroupDocs.Search for .NET** spends memory while building an index — and what actually reduces it.

This repository exists because of a support question: a team indexing ~1 million files watched their process climb past 20 GB and die with `OutOfMemoryException`. Rather than answer from intuition, these three samples index the *same documents* three different ways and print what each one costs.

> [!IMPORTANT]
> The headline finding is simple: **the cost of indexing is dominated by how often you call `Add()`, not by how much text you pass it.** In the run below, switching from one `Add()` per document to one `Add()` per batch cut total allocation by **24.8×**.

---

## Measured results

60 documents drawn evenly from HTML, PDF, TXT, DOC/DOCX, XLS/XLSX and PPT/PPTX.
GroupDocs.Search 26.8.0, .NET 8.0.29, **Release build**, x64, workstation GC, `Threads = 1`.

All three runs indexed a **byte-identical document set** (same SHA-256 over the ordered path list)
and returned **identical search results** (24 documents, 121 occurrences for `information`). Those
facts are recorded in the run manifests, not asserted -- see `results/published/*.json`.

| | 01 - per document | 02 - one batch of 60 | 03 - 3 shards of 20 |
|---|---:|---:|---:|
| `Add()` calls | 60 | 1 | 3 |
| **Total allocated** | **91.22 GB** | **3.74 GB** | 6.84 GB |
| **Allocated per document** | **1.52 GB** | **63.9 MB** | 116.8 MB |
| Peak private bytes | 8.77 GB | 1.80 GB | 4.28 GB |
| Retained managed heap | 68.0 MB | 68.0 MB | 46.3 MB |
| **Retained LOH** | 126.7 MB | **1.43 GB** | **174.5 MB** |
| Build wall clock | 40.2 s | 31.4 s | 26.6 s |

All figures are **build phase only**. Sample 03's repository load and cross-shard search are
measured separately and excluded, because samples 01 and 02 have no equivalent step. "Peak private"
is sampled by a background thread every 25 ms, so it does not depend on how often each sample
happens to print a row. "Retained" figures are taken after a forced blocking collection.

Raw CSV and JSON for exactly this run are committed under [`results/published/`](results/published/).

Reproduce with:

```bash
export INDEXING_LAB_MAX_DOCUMENTS=60
dotnet run -c Release --project samples/Sample01.PerDocument
dotnet run -c Release --project samples/Sample02.Batched
INDEXING_LAB_DOCS_PER_SHARD=20 dotnet run -c Release --project samples/Sample03.Sharded
```

### The model

Fitting samples 01 and 02 gives a two-term cost model:

```
allocated  ~  1.483 GB x (number of Add() calls)  +  ~38.6 MB x (documents)
```

Sample 03 is the only out-of-sample point, and the model predicts it within **2.0%**. The 1.483 GB
fixed term also agrees within 6% with the ~1.4 GB of term-collector buffers you can add up by
reading the GroupDocs.Search source -- so this is the mechanism, not a curve fit.

The fixed term is what you are paying for, and it is entirely under your control:

| documents per `Add()` call | fixed overhead per document |
|---:|---:|
| 1 | 1518 MB |
| 20 | 76 MB |
| 100 | 15 MB |
| 1 000 | 1.5 MB |
| 10 000 | 0.15 MB |

### Reading the table

**1.52 GB to index one document.** That is not the document -- an empty text file costs the same.
Each `Add()` call is a complete indexing operation: it deep-copies the index metadata and term
dictionary on entry, allocates a fixed set of term-collector buffers, writes a segment, deep-copies
everything back on commit, and re-serializes the index metadata to disk. None of that scales down
with the size of the batch, so a batch of one pays the full price.

**Batching is the single biggest lever: 24.4x less allocation and 4.9x lower peak private bytes**,
for the same documents and the same search results. It needs no architectural rework -- just a
bigger array passed to `Add()`.

**Sharding's win is in the Large Object Heap, not the managed heap.** This is the subtle one, and
an earlier draft of this README got it wrong. Look at the "retained managed heap" row: after a
forced collection, per-document and batched retain the *same* 68 MB. Sharding retains 46 MB. That
is a real but unremarkable 1.5x.

The interesting row is **retained LOH**. The single 60-document batch leaves **1.43 GB** of Large
Object Heap standing, because the term-collector buffers are LOH-sized and the LOH is not
decommitted eagerly. Sharding holds that to 174.5 MB -- an **8.4x** difference -- because each
shard's buffers are released when the shard is disposed. Since the LOH is precisely what fragments
and drives a process's private bytes upward over a long run, that is the number that decides whether
a long-running indexing service stays healthy.

**Sharding does not reduce allocation.** Our 3-shard run allocated *more* than the single batch
(6.84 GB vs 3.74 GB), because each shard re-pays the same 1.483 GB fixed cost. Twenty documents per
shard is deliberately extreme here, to make three shards out of sixty documents.

## Why memory never comes back

Almost every allocation above is ≥ 85 KB, so it lands on the **Large Object Heap**, which .NET does not compact by default. Over a long indexing run the LOH fragments: the managed heap looks stable while the process's private bytes ratchet upward, until a large contiguous request fails on a machine that still reports free memory.

That is why the samples report **both** `GC.GetTotalAllocatedBytes` (cumulative — the only way to see a 1.4 GB buffer that is allocated and freed inside one call) **and** `Process.PrivateMemorySize64` (what an operations team watches in Task Manager). When allocation races ahead while the managed heap stays flat and private bytes still climb, you are looking at LOH churn.

---

## Getting started

### Prerequisites

- .NET SDK 8.0 or later
- A folder of documents to index
- A GroupDocs licence (optional, but see the warning below)

### 1. Point the samples at your documents

Each sample owns its `appsettings.json`, so its knobs sit next to its code:

```jsonc
{
  "Storage": {
    "RootPath": "<path to your documents folder>",
    "Folders": [ "Html", "Pdf", "Text", "Words", "Spreadsheets", "Presentations" ],
    "MaxDocuments": 300,
    "MaxFileSizeBytes": 10485760,
    "Extensions": [ ".html", ".pdf", ".txt", ".docx", ".xlsx", ".pptx" ]
  },
  "Index":   { "OutputRoot": "out", "IndexType": "NormalIndex", "Threads": 1, "MaxIndexingReportCount": 0, "CleanBeforeRun": true },
  "Run":     { "BatchSize": 100, "DocumentsPerShard": 100, "ReportEvery": 1, "VerificationQuery": "information", "WriteResultFiles": true },
  "License": { "EnvironmentVariable": "GROUPDOCS_LIC_PATH" }
}
```

For machine-specific paths, drop an `appsettings.local.json` next to it — it is git-ignored and **replaces** the settings wholesale (it is not merged key by key, so keep it complete).

> [!CAUTION]
> **`MaxDocuments` is a safety cap, and it matters.** The reference corpus behind these numbers holds ~894,000 files across ~425 GB. Simply *listing* it takes minutes. `DocumentSource` enumerates lazily and stops at the cap, spreading the quota evenly across the configured folders so every sample sees the same mix of formats. Raise the cap deliberately.

### 2. Set your licence

```bash
# Windows (PowerShell)
$env:GROUPDOCS_LIC_PATH = "C:\licenses\GroupDocs.Total.lic"

# Linux / macOS
export GROUPDOCS_LIC_PATH=/opt/licenses/GroupDocs.Total.lic
```

> [!WARNING]
> Without a licence GroupDocs.Search runs in trial mode and caps the number of documents it will index. The samples still run and still print a table, but **the numbers no longer mean anything**. Each sample prints `license: applied` or a loud warning at startup — check it before trusting a result.

If your machine keeps licences in a shared folder rather than at a fixed file path, set `License.FallbackFolderVariable` (the name of an environment variable holding that folder) and `License.FallbackRelativePath` (the licence's path inside it) in a git-ignored `appsettings.local.json`. Both are empty by default.

### 3. Run

```bash
dotnet run --project samples/Sample01.PerDocument   # the anti-pattern
dotnet run --project samples/Sample02.Batched       # the fix
dotnet run --project samples/Sample03.Sharded       # bounding resident memory
```

Each writes `out/results/*.csv` and `*.json` next to its executable, for charting or regression tracking.

### Environment overrides

Handy for sweeps without editing files. These win over both JSON files:

| Variable | Effect |
|---|---|
| `INDEXING_LAB_STORAGE_ROOT` | Overrides `Storage.RootPath` |
| `INDEXING_LAB_MAX_DOCUMENTS` | Overrides `Storage.MaxDocuments` |
| `INDEXING_LAB_BATCH_SIZE` | Overrides `Run.BatchSize` |
| `INDEXING_LAB_DOCS_PER_SHARD` | Overrides `Run.DocumentsPerShard` |
| `GROUPDOCS_LIC_PATH` | Full path to the `.lic` file |

---

## Repository layout

```
DocumentIndexingMemoryOptimization.sln
├─ src/IndexingMemoryLab.Core/          shared machinery
│    SampleOptions.cs                   configuration + environment overrides
│    DocumentSource.cs                  bounded, reproducible document selection
│    MemoryMonitor.cs                   sampling, console table, CSV/JSON output
│    SampleHost.cs                      licence, index folders, error tracking, verification
└─ samples/
     Sample01.PerDocument/              one Add() per document
     Sample02.Batched/                  one Add() per batch
     Sample03.Sharded/                  N indexes + IndexRepository
```

`Directory.Packages.props` pins the GroupDocs.Search version in one place for the whole solution.

---

## Things worth knowing before you interpret a run

- **Stop words are not indexed.** `the`, `and` and friends are stop words by default, so searching for them returns zero hits and looks like a broken index. `Run.VerificationQuery` defaults to `information` for this reason.
- **`Threads` multiplies memory.** Each indexing thread allocates its own term-collector buffers, so `Threads = 4` costs roughly four times the per-operation buffer memory. The samples default to `1`.
- **`CompactIndex` saves disk, not RAM.** Its term collector is *larger* than `NormalIndex`'s. If you picked it hoping to reduce memory, measure it here first — set `Index.IndexType` and re-run.
- **`MaxIndexingReportCount` retains document paths.** Each retained report holds the full document-path list of its batch. The samples set it to `0`.
- **There is a hard ceiling on `MaxDocuments`.** Above `50,000` a run is refused unless you also set `Storage.AllowUnboundedRun`. The corpus this was built against holds ~894,000 files; a per-document run at that size would allocate on the order of a petabyte.
- **Extraction failures are normal.** Any real corpus contains corrupt, encrypted, or unsupported files. Each sample counts them and prints the first few, so a failed extraction is never mistaken for a memory problem.
- **Build configuration.** The published numbers are from a **Release** build. We also ran the same workload in Debug: the fitted fixed cost per `Add()` call was 1.483 GB in both, which makes sense -- the dominant allocations happen inside `GroupDocs.Search.dll`, which is a Release NuGet binary either way.

### Trying the GC settings

The samples deliberately run with workstation GC, matching a typical service and the environment the original report came from. To measure Server GC, edit `Directory.Build.props`:

```xml
<ServerGarbageCollection>true</ServerGarbageCollection>
<ConcurrentGarbageCollection>false</ConcurrentGarbageCollection>
```

To fight LOH fragmentation directly in your own code, between batches:

```csharp
GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
GC.Collect();
```

---

## Continuous integration

CI builds the solution on Linux and Windows and checks that a sample fails with a clear, actionable error when no document storage is configured. **The samples themselves are not executed in CI** — a hosted runner has neither a document corpus nor a licence, and running them there would produce trial-capped numbers that look real but measure nothing.

---

## Contributing

The most useful contribution is **your numbers**. Run the samples against your own corpus and open a [measurement report](../../issues/new?template=measurement_report.yml) — results that disagree with the table above are especially welcome. See [CONTRIBUTING.md](CONTRIBUTING.md).

Working with an AI coding agent? [AGENTS.md](AGENTS.md) carries the guardrails that matter here, including why the safety cap must not be raised and why the results table must never be edited to unmeasured figures.

## Licence

Sample code: [MIT](LICENSE). GroupDocs.Search is a commercial product with [its own terms](https://purchase.groupdocs.com/policies/).
