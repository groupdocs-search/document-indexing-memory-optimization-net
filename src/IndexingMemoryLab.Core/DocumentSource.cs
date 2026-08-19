namespace IndexingMemoryLab.Core;

/// <summary>
/// Picks a bounded, reproducible set of documents out of a very large corpus.
/// </summary>
/// <remarks>
/// The reference corpus is ~894 000 files across ~425 GB. Materialising that list
/// costs minutes before any indexing starts, so this type enumerates lazily and
/// stops as soon as the cap is reached. It also spreads the quota evenly across the
/// configured folders, so every sample indexes the same mix of formats and the
/// numbers they print are comparable.
/// </remarks>
public static class DocumentSource
{
    public static IReadOnlyList<string> Collect(StorageOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        int folderCount = options.Folders.Length;
        int quotaPerFolder = Math.Max(1, options.MaxDocuments / folderCount);

        HashSet<string> allowedExtensions = new(options.Extensions, StringComparer.OrdinalIgnoreCase);
        List<string> selected = new(options.MaxDocuments);

        foreach (string folder in options.Folders)
        {
            string folderPath = Path.Combine(options.RootPath, folder);
            if (!Directory.Exists(folderPath))
            {
                Console.WriteLine($"  ! skipping '{folder}' - folder not found under {options.RootPath}");
                continue;
            }

            int takenFromFolder = 0;
            foreach (string file in EnumerateCandidates(folderPath, allowedExtensions, options.MaxFileSizeBytes))
            {
                selected.Add(file);
                takenFromFolder++;

                if (takenFromFolder >= quotaPerFolder || selected.Count >= options.MaxDocuments)
                {
                    break;
                }
            }

            Console.WriteLine($"  {folder,-16} {takenFromFolder,6} documents");

            if (selected.Count >= options.MaxDocuments)
            {
                break;
            }
        }

        return selected;
    }

    private static IEnumerable<string> EnumerateCandidates(
        string folderPath,
        HashSet<string> allowedExtensions,
        long maxFileSizeBytes)
    {
        // IgnoreInaccessible keeps a single locked or permission-denied subfolder
        // from aborting the whole enumeration part-way through.
        EnumerationOptions enumerationOptions = new()
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.System | FileAttributes.Hidden | FileAttributes.ReparsePoint,
        };

        foreach (string file in Directory.EnumerateFiles(folderPath, "*", enumerationOptions))
        {
            if (allowedExtensions.Count > 0 && !allowedExtensions.Contains(Path.GetExtension(file)))
            {
                continue;
            }

            long length;
            try
            {
                length = new FileInfo(file).Length;
            }
            catch (IOException)
            {
                continue;
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }

            if (length == 0 || length > maxFileSizeBytes)
            {
                continue;
            }

            yield return file;
        }
    }
}
