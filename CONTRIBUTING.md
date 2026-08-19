# Contributing

Thanks for your interest. This repository is a **measurement** project, so the contribution that helps most is not usually code.

## Most valuable contribution: your numbers

Run the samples against your own corpus and open a [measurement report](../../issues/new?template=measurement_report.yml). Results from corpora other than ours are the single best way to make the published guidance trustworthy — especially if your numbers *disagree* with ours.

Please include the full summary block each sample prints, plus your build configuration, runtime, GC mode, and confirmation that the licence was applied. An unlicensed run is document-capped and its figures are not meaningful.

## Code changes

```bash
git clone <this repo>
cd document-indexing-memory-optimization-net
dotnet build DocumentIndexingMemoryOptimization.sln
```

You need .NET SDK 8.0 or later. To actually run a sample you also need a folder of documents and a GroupDocs licence — see the README.

Before opening a pull request:

- Read [AGENTS.md](AGENTS.md). It documents the traps (stop words, `System.Index` ambiguity, `InvariantGlobalization` breaking `.xls` extraction, `appsettings.local.json` replacing rather than merging) and the rules about not raising the document safety cap.
- Keep the build warning-clean on Linux and Windows.
- Keep the file count low. This repo deliberately favours fewer, denser files.
- **If your change could affect what is measured, say so.** The results table in the README is quoted to customers. Do not update it with estimated figures; either re-run all three samples under identical conditions, or flag in the PR that a re-run is needed.

## What does not belong here

- Questions about GroupDocs.Search itself → [product forum](https://forum.groupdocs.com/c/search)
- Licence keys, trial limits, pricing → [purchase site](https://purchase.groupdocs.com/)
- Licence files, customer documents, or anything from `out/`, `bin/`, `obj/`

## Licence

Contributions are accepted under the [MIT licence](LICENSE) that covers the sample code in this repository.
