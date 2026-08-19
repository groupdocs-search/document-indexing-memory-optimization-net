## What this changes

<!-- One or two sentences. -->

## Does this affect what is measured?

- [ ] No — documentation, comments, CI, or tooling only.
- [ ] Yes — code that affects indexing, batching, sharding, or the memory sampler changed.

**If yes**, the published results table in `README.md` is now suspect. Either:

- [ ] I re-ran all three samples with identical settings and updated the table, or
- [ ] I have flagged below that the table needs re-running by someone with a corpus and a licence.

Re-running one sample is not enough — the table is only meaningful if all three rows come from the same conditions. See "Comparability rules" in [AGENTS.md](../AGENTS.md).

## Checklist

- [ ] `dotnet build DocumentIndexingMemoryOptimization.sln` is clean
- [ ] No `out/`, `bin/`, `obj/`, `.lic` file or customer document is included
- [ ] `Storage.MaxDocuments` safety caps are still in place and were not raised
- [ ] Any new measured figure in the docs came from a run I actually performed
