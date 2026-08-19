# Changelog

All notable changes to this repository are documented here.
Format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

## [Unreleased]

### Added
- `AGENTS.md` with guardrails for AI coding agents, plus thin pointers from `CLAUDE.md` and `.github/copilot-instructions.md`.
- Community health files, issue and pull request templates, Dependabot configuration, and a dev container.
- Run manifests recording a SHA-256 of the ordered document list, licence state, GC mode, build configuration and verification counts, so comparability between runs is checkable rather than claimed.
- Raw CSV and JSON behind the published table, committed under `results/published/`.
- A `50,000` document guard rail requiring `Storage.AllowUnboundedRun` to exceed.

### Changed
- Peak private bytes is now sampled by a background thread every 25 ms instead of only at reporting points, which previously made the figure depend on each sample's reporting cadence rather than on the workload.
- All three samples now start and stop measurement at identical logical points; sample 03's repository load and search are reported as a separate query phase.
- Residency is measured after a forced blocking collection.
- Published results regenerated from a Release build.

### Fixed
- CI's success check matched .NET's own "does not exist" message and would have passed on a broken build.
- Unhandled exceptions printed stack traces containing absolute build-machine paths.
- Indexing error counts were not thread-safe despite `Threads` being a documented option.
- `PlatformTarget=x64` prevented running on arm64; the licence fallback path was backslash-literal and never resolved on Linux.

### Corrected
- **The previously published "sharding cuts resident memory 7.7x" result was an artifact.** It compared `GC.GetTotalMemory(false)` readings, which include uncollected garbage. Measured after a forced collection, retained managed heap is 68.0 MB for both per-document and batched, and 46.3 MB sharded - a modest 1.5x. The genuine sharding win is in the Large Object Heap: 174.5 MB against 1.43 GB for a single open index, an 8.4x difference.
- Batching's peak-private improvement corrected from an inflated 8.2x to a measured 4.9x. The allocation reduction is unchanged at 24.4x.

## [0.1.0] - 2026-08-19

### Added
- `IndexingMemoryLab.Core`: configuration, bounded document selection, memory sampling, and shared sample host.
- Three console samples: per-document indexing, batched indexing, and sharded indexing queried through `IndexRepository`.
- Published measurements for 60 mixed-format documents on GroupDocs.Search 26.8.0 / .NET 8 / x64 / workstation GC.

### Measured
Batching cut total allocation **24.8x** (91.14 GB to 3.67 GB) versus one `Add()` call per document.
Sharding cut the final managed heap **7.7x** (1.62 GB to 209.5 MB) but *raised* total allocation, because each
shard re-pays the fixed per-operation cost. Fitting the runs gives
`allocated ~ 1.483 GB x AddCalls + 37.3 MB x documents`, which predicts the third run within 5.5%.
