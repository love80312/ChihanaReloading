// ============================================================================
// Restorer.cs
// 將處理後的檔案還原到輸出目錄
// ============================================================================
using System.Text.Json;

namespace Kaguya_YaneKit.Formats.Picture;

public static class Restorer
{
    private sealed class BaseMetadata
    {
        public string OriginalRelativePath { get; set; } = "";
    }

    public readonly record struct RestoreSummary(
        int Total,
        int Restored,
        int Replenished,
        int Skipped,
        int Failed)
    {
        public int Copied => Restored + Replenished;
    }

    private sealed class RestoreProgress
    {
        private readonly Action<int, int>? _progress;
        private int _done;
        private int _restored;
        private int _replenished;
        private int _skipped;
        private int _failed;

        public RestoreProgress(int total, Action<int, int>? progress)
        {
            Total = total;
            _progress = progress;
            _progress?.Invoke(0, Total);
        }

        public int Total { get; }

        public void MarkRestored() =>
            Interlocked.Increment(ref _restored);

        public void MarkReplenished() =>
            Interlocked.Increment(ref _replenished);

        public void MarkSkipped() =>
            Interlocked.Increment(ref _skipped);

        public void MarkFailed() =>
            Interlocked.Increment(ref _failed);

        public void Advance()
        {
            var done = Interlocked.Increment(ref _done);
            _progress?.Invoke(done, Total);
        }

        public RestoreSummary ToSummary() => new(
            Total,
            Volatile.Read(ref _restored),
            Volatile.Read(ref _replenished),
            Volatile.Read(ref _skipped),
            Volatile.Read(ref _failed));
    }

    private readonly record struct RestoreSourceFile(
        string RootDir,
        string FilePath);

    private readonly record struct RestorePlan(
        RestoreSourceFile Source,
        string DestinationPath);

    private static readonly ParallelOptions RestoreParallelOptions = new()
    {
        MaxDegreeOfParallelism = ReadRestoreParallelism()
    };

    private static StringComparer PathComparer =>
        OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;

    public static RestoreSummary Restore(
        string workDir,
        string outputDir,
        Action<int, int>? progress = null)
    {
        workDir = Path.GetFullPath(workDir);
        outputDir = Path.GetFullPath(outputDir);

        Directory.CreateDirectory(outputDir);

        var files = EnumerateNamedDirFiles(
            workDir,
            "new",
            null).ToList();

        var restoreProgress = new RestoreProgress(
            files.Count,
            progress);

        RestoreFromNewFolders(
            files,
            outputDir,
            restoreProgress);

        return restoreProgress.ToSummary();
    }

    public static RestoreSummary RestoreWithReplenish(
        string workDir,
        string outputDir,
        HashSet<string>? excludeFormats = null,
        Action<int, int>? progress = null)
    {
        workDir = Path.GetFullPath(workDir);
        outputDir = Path.GetFullPath(outputDir);

        Directory.CreateDirectory(outputDir);

        var newFiles = EnumerateNamedDirFiles(
            workDir,
            "new",
            null).ToList();

        var origFiles = EnumerateNamedDirFiles(
            workDir,
            "orig",
            excludeFormats).ToList();

        var restoreProgress = new RestoreProgress(
            newFiles.Count + origFiles.Count,
            progress);

        RestoreFromNewFolders(
            newFiles,
            outputDir,
            restoreProgress);

        // Replenish 也先建立完整計畫，再處理目的路徑碰撞。
        // 不可讓 Parallel.ForEach 以「誰先完成誰獲勝」決定輸出內容。
        var replenishCandidates = new List<RestorePlan>();

        foreach (var origFile in origFiles)
        {
            try
            {
                var (_, _, relPath, jsonFile) =
                    ResolveMetadataPath(
                        origFile.RootDir,
                        origFile.FilePath);

                if (!File.Exists(jsonFile))
                {
                    throw new FileNotFoundException(
                        $"Metadata not found for replenish source: {relPath}",
                        jsonFile);
                }

                var metadata = ReadMetadata(jsonFile);

                if (metadata is null ||
                    string.IsNullOrWhiteSpace(metadata.OriginalRelativePath))
                {
                    restoreProgress.MarkSkipped();
                    restoreProgress.Advance();
                    continue;
                }

                var destination = GetSafeDestinationPath(
                    outputDir,
                    metadata.OriginalRelativePath);

                if (Directory.Exists(destination))
                {
                    throw new InvalidDataException(
                        $"Replenish destination is already a directory: {metadata.OriginalRelativePath}");
                }

                // New 階段可能已經寫出同一目的檔。
                // 這種情況應保留 New 的結果，不可由 Orig 覆蓋。
                if (File.Exists(destination))
                {
                    restoreProgress.MarkSkipped();
                    restoreProgress.Advance();
                    continue;
                }

                ValidateDestinationAgainstExistingFiles(
                    outputDir,
                    destination);

                replenishCandidates.Add(
                    new RestorePlan(origFile, destination));
            }
            catch (Exception ex)
            {
                restoreProgress.MarkFailed();

                PictureProcessing.WriteLine(
                    $"Failed to prepare replenish \"{Path.GetFileName(origFile.FilePath)}\": {ex.Message}");

                restoreProgress.Advance();
            }
        }

        // 同一目的地有多個 Orig 來源時，整組拒絕。
        // 不選第一個，也不依賴列舉或執行緒完成順序。
        var replenishPlans = new List<RestorePlan>();

        foreach (var group in replenishCandidates.GroupBy(
                     plan => plan.DestinationPath,
                     PathComparer))
        {
            if (group.Count() > 1)
            {
                foreach (var conflict in group)
                {
                    restoreProgress.MarkFailed();

                    PictureProcessing.WriteLine(
                        $"Replenish collision: multiple sources target \"{conflict.DestinationPath}\".");

                    restoreProgress.Advance();
                }

                continue;
            }

            replenishPlans.Add(group.First());
        }

        var replenishDestinations = replenishPlans
            .Select(plan => plan.DestinationPath)
            .ToHashSet(PathComparer);

        try
        {
            ValidateNoFileDirectoryConflicts(
                replenishDestinations);
        }
        catch (Exception ex)
        {
            PictureProcessing.WriteLine(
                $"Replenish cancelled: {ex.Message}");

            foreach (var _ in replenishPlans)
            {
                restoreProgress.MarkFailed();
                restoreProgress.Advance();
            }

            return restoreProgress.ToSummary();
        }

        Parallel.ForEach(
            replenishPlans,
            RestoreParallelOptions,
            plan =>
            {
                try
                {
                    // 再確認一次，避免其他外部程序在預檢後建立同名檔案。
                    if (File.Exists(plan.DestinationPath))
                    {
                        restoreProgress.MarkSkipped();
                        return;
                    }

                    ValidateDestinationAgainstExistingFiles(
                        outputDir,
                        plan.DestinationPath);

                    Directory.CreateDirectory(
                        Path.GetDirectoryName(plan.DestinationPath)!);

                    File.Copy(
                        plan.Source.FilePath,
                        plan.DestinationPath,
                        false);

                    restoreProgress.MarkReplenished();
                }
                catch (Exception ex)
                {
                    restoreProgress.MarkFailed();

                    PictureProcessing.WriteLine(
                        $"Failed to replenish \"{Path.GetFileName(plan.Source.FilePath)}\": {ex.Message}");
                }
                finally
                {
                    restoreProgress.Advance();
                }
            });

        return restoreProgress.ToSummary();
    }

    private static IEnumerable<RestoreSourceFile> EnumerateNamedDirFiles(
        string workDir,
        string dirName,
        HashSet<string>? excludeFormats)
    {
        foreach (var dir in Directory.EnumerateDirectories(
                     workDir,
                     dirName,
                     SearchOption.AllDirectories))
        {
            var formatDir = new DirectoryInfo(dir).Parent!.FullName;
            var formatTag = new DirectoryInfo(formatDir).Name;

            if (excludeFormats is not null &&
                excludeFormats.Contains(formatTag.ToLowerInvariant()))
            {
                continue;
            }

            foreach (var file in Directory.EnumerateFiles(
                         dir,
                         "*.*",
                         SearchOption.AllDirectories))
            {
                yield return new RestoreSourceFile(dir, file);
            }
        }
    }

    // 先完整建立還原計畫，再依目的路徑分組。
    // 若多個來源指向同一目的檔，整組拒絕，不保留第一個或最後一個。
    private static void RestoreFromNewFolders(
        IReadOnlyList<RestoreSourceFile> files,
        string outputDir,
        RestoreProgress restoreProgress)
    {
        var candidates = new List<RestorePlan>(files.Count);

        foreach (var source in files)
        {
            try
            {
                var (_, _, relPath, jsonFile) =
                    ResolveMetadataPath(
                        source.RootDir,
                        source.FilePath);

                if (!File.Exists(jsonFile))
                {
                    throw new FileNotFoundException(
                        $"Metadata not found for restore source: {relPath}",
                        jsonFile);
                }

                var metadata = ReadMetadata(jsonFile);

                if (metadata is null ||
                    string.IsNullOrWhiteSpace(metadata.OriginalRelativePath))
                {
                    PictureProcessing.WriteLine(
                        $"Warning: invalid metadata for \"{relPath}\", skipped.");

                    restoreProgress.MarkSkipped();
                    restoreProgress.Advance();
                    continue;
                }

                var destination = GetSafeDestinationPath(
                    outputDir,
                    metadata.OriginalRelativePath);

                candidates.Add(
                    new RestorePlan(source, destination));
            }
            catch (Exception ex)
            {
                restoreProgress.MarkFailed();

                PictureProcessing.WriteLine(
                    $"Failed to prepare restore \"{Path.GetFileName(source.FilePath)}\": {ex.Message}");

                restoreProgress.Advance();
            }
        }

        var plans = new List<RestorePlan>();

        foreach (var group in candidates.GroupBy(
                     plan => plan.DestinationPath,
                     PathComparer))
        {
            if (group.Count() > 1)
            {
                foreach (var conflict in group)
                {
                    restoreProgress.MarkFailed();

                    PictureProcessing.WriteLine(
                        $"Restore collision: multiple sources target \"{conflict.DestinationPath}\".");

                    restoreProgress.Advance();
                }

                continue;
            }

            plans.Add(group.First());
        }

        var destinationSet = plans
            .Select(plan => plan.DestinationPath)
            .ToHashSet(PathComparer);

        try
        {
            ValidateNoFileDirectoryConflicts(destinationSet);
        }
        catch (Exception ex)
        {
            // 路徑結構衝突時整批停止，避免先寫入部分檔案後才發現衝突。
            PictureProcessing.WriteLine(
                $"Restore cancelled: {ex.Message}");

            foreach (var _ in plans)
            {
                restoreProgress.MarkFailed();
                restoreProgress.Advance();
            }

            return;
        }

        Parallel.ForEach(
            plans,
            RestoreParallelOptions,
            plan =>
            {
                try
                {
                    Directory.CreateDirectory(
                        Path.GetDirectoryName(plan.DestinationPath)!);

                    File.Copy(
                        plan.Source.FilePath,
                        plan.DestinationPath,
                        true);

                    restoreProgress.MarkRestored();
                }
                catch (Exception ex)
                {
                    restoreProgress.MarkFailed();

                    PictureProcessing.WriteLine(
                        $"Failed to restore \"{Path.GetFileName(plan.Source.FilePath)}\": {ex.Message}");
                }
                finally
                {
                    restoreProgress.Advance();
                }
            });
    }

    private static void ValidateNoFileDirectoryConflicts(
        HashSet<string> destinations)
    {
        foreach (var destination in destinations)
        {
            var parent = Path.GetDirectoryName(destination);

            while (!string.IsNullOrEmpty(parent))
            {
                if (destinations.Contains(parent))
                {
                    throw new InvalidDataException(
                        $"Restore output has a file/directory path conflict: {parent}");
                }

                parent = Path.GetDirectoryName(parent);
            }
        }
    }

    private static void ValidateDestinationAgainstExistingFiles(
        string outputDir,
        string destinationPath)
    {
        var root = Path.GetFullPath(outputDir);
        var parent = Path.GetDirectoryName(destinationPath);

        while (!string.IsNullOrEmpty(parent))
        {
            if (File.Exists(parent))
            {
                throw new InvalidDataException(
                    $"Restore output parent is an existing file: {parent}");
            }

            if (PathComparer.Equals(parent, root))
            {
                break;
            }

            parent = Path.GetDirectoryName(parent);
        }
    }

    private static string GetSafeDestinationPath(
        string outputDir,
        string originalRelativePath)
    {
        if (string.IsNullOrWhiteSpace(originalRelativePath))
        {
            throw new InvalidDataException(
                "Metadata contains an empty OriginalRelativePath.");
        }

        // 同時正規化兩種分隔符，避免 Windows 風格路徑
        // 在其他平台被當成普通字元。
        var normalized = originalRelativePath
            .Replace('\\', Path.DirectorySeparatorChar)
            .Replace('/', Path.DirectorySeparatorChar);

        if (Path.IsPathRooted(normalized) ||
            (normalized.Length >= 2 &&
             char.IsAsciiLetter(normalized[0]) &&
             normalized[1] == ':'))
        {
            throw new InvalidDataException(
                $"Metadata path must be relative: {originalRelativePath}");
        }

        var segments = normalized.Split(
            Path.DirectorySeparatorChar,
            StringSplitOptions.RemoveEmptyEntries);

        if (segments.Length == 0 ||
            segments.Any(segment => segment is "." or ".."))
        {
            throw new InvalidDataException(
                $"Metadata path contains an invalid path segment: {originalRelativePath}");
        }

        var fullRoot = Path.GetFullPath(outputDir);
        var fullPath = Path.GetFullPath(
            Path.Combine(fullRoot, normalized));

        var rootPrefix = Path.EndsInDirectorySeparator(fullRoot)
            ? fullRoot
            : fullRoot + Path.DirectorySeparatorChar;

        if (!fullPath.StartsWith(
                rootPrefix,
                OperatingSystem.IsWindows()
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Metadata path escapes the output directory: {originalRelativePath}");
        }

        return fullPath;
    }

    private static BaseMetadata? ReadMetadata(string jsonFile)
    {
        var json = File.ReadAllText(jsonFile);

        return JsonSerializer.Deserialize<BaseMetadata>(
            json,
            new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
    }

    private static int ReadRestoreParallelism()
    {
        var value = Environment.GetEnvironmentVariable(
            "KAGUYA_RESTORE_PARALLELISM");

        if (int.TryParse(value, out var parsed) && parsed > 0)
        {
            return Math.Clamp(parsed, 1, 16);
        }

        return Math.Clamp(
            Environment.ProcessorCount / 2,
            2,
            8);
    }

    private static (
        string FormatDir,
        string FormatTag,
        string RelPath,
        string JsonFile)
        ResolveMetadataPath(
            string rootDir,
            string file)
    {
        var formatDir = new DirectoryInfo(rootDir).Parent!.FullName;
        var formatTag = new DirectoryInfo(formatDir).Name;
        var metaDir = Path.Combine(formatDir, "metadata");
        var relPath = Path.GetRelativePath(rootDir, file);

        var jsonFile =
            formatTag.Equals(
                "anm",
                StringComparison.OrdinalIgnoreCase) ||
            formatTag.Equals(
                "plt",
                StringComparison.OrdinalIgnoreCase)
                ? Path.Combine(metaDir, relPath + ".json")
                : Path.Combine(
                    metaDir,
                    PicturePathHelper.ChangeExtensionPreservingName(
                        relPath,
                        ".json"));

        return (
            formatDir,
            formatTag,
            relPath,
            jsonFile);
    }
}
