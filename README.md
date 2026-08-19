# document-indexing-memory-optimization-net
Measured .NET benchmarks showing where GroupDocs.Search spends memory during indexing — and why batching Add() calls cuts allocation 24×. Three runnable samples: per-document, batched, and sharded via IndexRepository.
