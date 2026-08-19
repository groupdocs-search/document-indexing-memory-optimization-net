# AGENTS.md

Guidance for AI coding agents working in this repository. Humans should read [README.md](README.md) first — this file covers what an agent needs that the README does not spell out.

## What this repository is

A **measurement** repository, not a library. Its three console samples index the same documents three different ways and report what each costs, so that support guidance given to customers of GroupDocs.Search for .NET is backed by evidence rather than intuition.

The consequence: **the numbers are the product.** Code changes that alter what is measured, or how, are far more consequential here than in a normal sample repo. Treat the published results table in `README.md` as a data artifact.

## Hard rules

These are not style preferences. Violating them produces either a very expensive mistake or a misleading result.

1. **Never remove or raise `Storage.MaxDocuments` to run over the whole corpus.**
   The reference corpus is ~894,000 files across ~425 GB. Simply *enumerating* it takes minutes; indexing it per-document would allocate on the order of a petabyte and take days. `DocumentSource` enumerates lazily and stops at the cap by design. If a task seems to need a bigger run, ask the human for an explicit document count first.

2. **Never edit the results table in `README.md` to numbers you did not measure.**
   Every figure there came from an actual run on a specific machine, and it is quoted to a paying customer. If you change code that could affect measurement, say the table needs re-running — do not estimate, extrapolate, or "adjust" it.

3. **A run without a license measures nothing.**
   Unlicensed, GroupDocs.Search caps how many documents it will index. The samples still print a full table, which is exactly the trap. Every sample prints `license: applied` or a loud warning at startup — check that line before believing any output you produce.

4. **Do not commit anything from `out/`, `bin/`, or `obj/`.**
   Generated indexes are large. `.gitignore` covers them; do not add exceptions.

5. **Do not add a `.lic` file, a license path, or a customer document to the repository.** Licenses come from an environment variable only.

## Build and run

```bash
dotnet build DocumentIndexingMemoryOptimization.sln       # must stay warning-clean
```

Running a sample requires a document corpus and a license, so it usually cannot be done in CI or in a sandbox:

```bash
# Set the license first
export GROUPDOCS_LIC_PATH=/path/to/GroupDocs.Total.lic     # Windows: $env:GROUPDOCS_LIC_PATH

# Small, safe run
INDEXING_LAB_MAX_DOCUMENTS=60 dotnet run --project samples/Sample01.PerDocument
INDEXING_LAB_MAX_DOCUMENTS=60 dotnet run --project samples/Sample02.Batched
INDEXING_LAB_MAX_DOCUMENTS=60 INDEXING_LAB_DOCS_PER_SHARD=20 dotnet run --project samples/Sample03.Sharded
```

Environment overrides beat both `appsettings.json` and `appsettings.local.json`:
`INDEXING_LAB_STORAGE_ROOT`, `INDEXING_LAB_MAX_DOCUMENTS`, `INDEXING_LAB_BATCH_SIZE`, `INDEXING_LAB_DOCS_PER_SHARD`, `GROUPDOCS_LIC_PATH`.

**If you cannot run the samples**, say so plainly rather than reasoning about what the output would have been. "I could not run this because no corpus is configured" is a useful answer; a predicted table is not.

## Comparability rules

The three samples are only meaningful *against each other*. A comparison is valid only if all runs share:

- the same `Storage.MaxDocuments`, `Storage.Folders`, `Storage.Extensions` and `Storage.MaxFileSizeBytes`
- the same build configuration (Debug or Release — do not mix)
- the same `Index.IndexType` and `Index.Threads`
- the same machine, with nothing else heavy running

If you change any of these for one sample, re-run all three. The published table was produced with a Debug build; ratios hold in Release but absolute figures shift.

## Architecture

```
src/IndexingMemoryLab.Core/
  SampleOptions.cs    config POCOs + JSON load + env overrides + validation
  DocumentSource.cs   bounded, quota-per-folder document selection
  MemoryMonitor.cs    sampling, console table, CSV/JSON output
  SampleHost.cs       license, index folders, index settings, error tracking, verification
samples/Sample0{1,2,3}.*/
  Program.cs          top-level statements, one measurement scenario each
  appsettings.json    that sample's own options (deliberately not centralized)
```

Shared machinery goes in `Core`; anything specific to one scenario stays in that sample's `Program.cs`. Each sample owns its configuration on purpose — do not consolidate the three `appsettings.json` files into one.

## Conventions

- `net8.0`, C# 12, nullable enabled, implicit usings on.
- **`using Index = GroupDocs.Search.Index;` is required** in any file that uses `Index`, because implicit usings pull in `System.Index` and the reference is otherwise ambiguous. This is a real compile error, not a preference.
- Explicit types over `var`, matching the surrounding code.
- The GroupDocs.Search version is pinned once in `Directory.Packages.props`. Do not add `Version=` to individual `PackageReference` elements — Central Package Management is on.
- Do not set `InvariantGlobalization`. It was removed deliberately: it breaks `.xls` text extraction with `CultureNotFoundException`.

## Known traps

- **Stop words.** `the`, `and` and similar are stop words by default and are never indexed, so searching for them returns zero hits and looks like a broken index. `Run.VerificationQuery` defaults to `information` for this reason.
- **`appsettings.local.json` replaces, it does not merge.** A partial local file silently drops every setting it omits. Keep local files complete.
- **`CompactIndex` uses more RAM than `NormalIndex`**, not less. It saves disk. Do not "optimize" memory by switching to it.
- **`Threads` multiplies memory.** Each indexing thread allocates its own term-collector buffers. Samples default to `1`; changing it invalidates comparison with the published table.
- **Extraction failures are normal.** Any real corpus has corrupt, encrypted, and unsupported files. `SampleHost` counts them and prints the first few. A non-zero error count is not automatically a bug.

## Making changes

- Keep the file count low. This repo deliberately favours fewer, denser files; adding a new file needs a reason.
- There is intentionally no unit test project — the samples verify themselves at runtime via `SampleHost.Verify`, which asserts the index is non-empty and reports whether the document count matches expectation. If you add logic complex enough to need unit tests, that is a signal to discuss adding a test project rather than shipping untested logic.
- The build must stay warning-clean on both Linux and Windows.
- Avoid Windows-only assumptions. Use `Path.Combine`, never string concatenation with `\`.
