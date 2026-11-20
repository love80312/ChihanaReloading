// ============================================================================
// Restorer.cs
// 將處理後的檔案還原到輸出目錄
// ============================================================================
using System.Text.Json;
using Kaguya_YaneKit.Core;

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

        SafePath.ValidateOutputRoot(outputDir);
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

        SafePath.ValidateOutputRoot(outputDir);
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

                    CopyFileAtomically(
                        plan.Source.FilePath,
                        plan.DestinationPath,
                        outputDir,
                        overwrite: false);

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

                ValidateDestinationAgainstExistingFiles(
                    outputDir,
                    destination);

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
                    CopyFileAtomically(
                        plan.Source.FilePath,
                        plan.DestinationPath,
                        outputDir,
                        overwrite: true);

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
        SafePath.ValidateExistingPath(outputDir, destinationPath);
    }

    private static string GetSafeDestinationPath(
        string outputDir,
        string originalRelativePath)
    {
        return SafePath.GetSafeDestinationPath(outputDir, originalRelativePath);
    }

    /// <summary>
    /// 先寫入目的目錄中的暫存檔，完成並 Flush 後才替換正式檔案。
    /// 因此讀取或複製中斷時，不會先截斷舊的目的檔。
    /// </summary>
    private static void CopyFileAtomically(
        string sourcePath,
        string destinationPath,
        string outputRoot,
        bool overwrite)
    {
        SafePath.ValidateExistingPath(outputRoot, destinationPath);

        var destinationDirectory = Path.GetDirectoryName(destinationPath)
            ?? throw new InvalidDataException($"Invalid restore destination: {destinationPath}");
        Directory.CreateDirectory(destinationDirectory);

        // 建立目錄後再次檢查，避免預先存在的目錄連結導向其他位置。
        SafePath.ValidateExistingPath(outputRoot, destinationPath);

        var temporaryPath = Path.Combine(
            destinationDirectory,
            $".{Path.GetFileName(destinationPath)}.{Guid.NewGuid():N}.tmp");

        try
        {
            using (var source = new FileStream(
                sourcePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                1024 * 1024,
                FileOptions.SequentialScan))
            using (var temporary = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                1024 * 1024,
                FileOptions.SequentialScan))
            {
                source.CopyTo(temporary);
                temporary.Flush(flushToDisk: true);
            }

            SafePath.ValidateExistingPath(outputRoot, destinationPath);

            if (Directory.Exists(destinationPath))
            {
                throw new IOException($"Restore destination is already a directory: {destinationPath}");
            }

            if (overwrite && File.Exists(destinationPath))
            {
                File.Replace(temporaryPath, destinationPath, destinationBackupFileName: null);
            }
            else
            {
                // 不覆蓋預檢後意外出現的檔案。
                File.Move(temporaryPath, destinationPath);
            }
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
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
