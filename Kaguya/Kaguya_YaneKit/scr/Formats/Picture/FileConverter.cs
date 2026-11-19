// ============================================================================
// FileConverter.cs
// 圖片格式批次轉換與重新封裝引擎
//
// 職責:
//   ConvertAll   -- 遍歷工作目錄，將 orig/ 中的原始檔案轉換為 PNG 到 png/
//   RepackAll    -- 將 fix/ 中修改後的 PNG 重新封裝為原始格式到 new/
//   RepackPngAll -- 將 png/ 中的 PNG 直接重新封裝為原始格式到 new/
//   RepackFix    -- 對指定的單一 fix/ 目錄執行重新封裝
//
// 重新封裝安全原則:
//   1. 先完整讀取 metadata 並建立所有目的路徑計畫。
//   2. 拒絕絕對路徑、路徑穿越、重複目的地及檔案/資料夾衝突。
//   3. 全部預檢通過後才開始寫入；每個輸出先寫暫存檔，再替換正式檔案。
// ============================================================================
using Kaguya_YaneKit.Formats.Picture.Handlers;
using Kaguya_YaneKit.Core;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Kaguya_YaneKit.Formats.Picture;

public static class FileConverter
{
    public readonly record struct ConversionSummary(int Batches, int Total, int Success, int Failure)
    {
        public bool HasWork => Batches > 0;

        public ConversionSummary Add(ConversionSummary other) => new(
            Batches + other.Batches,
            Total + other.Total,
            Success + other.Success,
            Failure + other.Failure);
    }

    private sealed record RepackPlan(string SourcePath, string DestinationPath);

    private static StringComparer PathComparer => OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    private static readonly Dictionary<string, IFormatHandler> Handlers = new(StringComparer.OrdinalIgnoreCase)
    {
        { "ap0", new Ap0Handler() },
        { "ap2", new Ap2Handler() },
        { "ap3", new Ap3Handler() },
        { "anm", new AnmHandler() },
        { "plt", new PltHandler() },
        { "bmp", new BmpHandler() },
        { "ap", new ApHandler() },
    };

    public static ConversionSummary ConvertAll(string path, bool quiet = false, Action<int, int>? progress = null)
    {
        var stopwatch = Stopwatch.StartNew();
        path = Path.GetFullPath(path);
        var summary = new ConversionSummary();
        var progressTotal = progress is null ? 0 : CountConvertFiles(path);
        var progressCompleted = 0;
        Action? fileProgress = progress is null || progressTotal <= 0
            ? null
            : () => progress(Interlocked.Increment(ref progressCompleted), progressTotal);

        if (TryGetFormatHandler(path, out var singleHandler) && Directory.Exists(Path.Combine(path, "orig")))
        {
            summary = summary.Add(ProcessConvertFormat(path, singleHandler, quiet, fileProgress));
            PrintConvertSummary(summary, stopwatch.Elapsed, quiet);
            return summary;
        }

        var workDirSummary = ProcessConvertWorkDir(path, quiet, fileProgress);
        if (workDirSummary.HasWork)
        {
            summary = summary.Add(workDirSummary);
            PrintConvertSummary(summary, stopwatch.Elapsed, quiet);
            return summary;
        }

        foreach (var archiveDir in Directory.GetDirectories(path).OrderBy(dir => dir, StringComparer.OrdinalIgnoreCase))
        {
            summary = summary.Add(ProcessConvertWorkDir(archiveDir, quiet, fileProgress));
        }

        PrintConvertSummary(summary, stopwatch.Elapsed, quiet);
        return summary;
    }

    public static ConversionSummary RepackAll(string path, bool quiet = false, Action<int, int>? progress = null)
    {
        return RepackFromSourceAll(path, "fix", quiet, progress);
    }

    public static ConversionSummary RepackPngAll(string path, bool quiet = false, Action<int, int>? progress = null)
    {
        return RepackFromSourceAll(path, "png", quiet, progress);
    }

    private static ConversionSummary RepackFromSourceAll(
        string path,
        string sourceDirectoryName,
        bool quiet = false,
        Action<int, int>? externalProgress = null)
    {
        var stopwatch = Stopwatch.StartNew();
        path = Path.GetFullPath(path);
        var summary = new ConversionSummary();
        var total = CountRepackFiles(path, sourceDirectoryName);
        var completed = 0;
        Action? fileProgress = total <= 0
            ? null
            : () =>
            {
                var current = Interlocked.Increment(ref completed);
                externalProgress?.Invoke(current, total);
            };

        using var progress = !quiet && total > 0
            ? PictureProcessing.StartProgress($"REPACK {sourceDirectoryName.ToUpperInvariant()}", total)
            : PictureProcessing.NullProgress;
        if (fileProgress is not null && externalProgress is null)
        {
            fileProgress = () => progress.Increment();
        }
        else if (fileProgress is not null && externalProgress is not null && !quiet)
        {
            fileProgress = () =>
            {
                var current = Interlocked.Increment(ref completed);
                externalProgress(current, total);
                progress.Increment();
            };
        }

        if (TryGetFormatHandler(path, out var singleHandler))
        {
            var sourceDir = Path.Combine(path, sourceDirectoryName);
            if (Directory.Exists(sourceDir) && Directory.EnumerateFileSystemEntries(sourceDir).Any())
            {
                summary = summary.Add(ProcessRepackFormat(path, singleHandler, sourceDir, quiet, fileProgress));
            }
            PrintRepackSummary(summary, stopwatch.Elapsed, sourceDirectoryName, quiet);
            return summary;
        }

        var workDirSummary = ProcessRepackWorkDir(path, sourceDirectoryName, quiet, fileProgress);
        if (workDirSummary.HasWork)
        {
            summary = summary.Add(workDirSummary);
            PrintRepackSummary(summary, stopwatch.Elapsed, sourceDirectoryName, quiet);
            return summary;
        }

        foreach (var archiveDir in Directory.GetDirectories(path).OrderBy(dir => dir, StringComparer.OrdinalIgnoreCase))
        {
            summary = summary.Add(ProcessRepackWorkDir(archiveDir, sourceDirectoryName, quiet, fileProgress));
        }

        PrintRepackSummary(summary, stopwatch.Elapsed, sourceDirectoryName, quiet);
        return summary;
    }

    private static ConversionSummary ProcessConvertWorkDir(string path, bool quiet = false, Action? fileProgress = null)
    {
        var summary = new ConversionSummary();
        foreach (var handler in Handlers.Values)
        {
            var formatDir = Path.Combine(path, handler.Tag);
            if (!Directory.Exists(formatDir))
            {
                continue;
            }

            summary = summary.Add(ProcessConvertFormat(formatDir, handler, quiet, fileProgress));
        }

        return summary;
    }

    private static ConversionSummary ProcessRepackWorkDir(
        string path,
        string sourceDirectoryName,
        bool quiet = false,
        Action? fileProgress = null)
    {
        var summary = new ConversionSummary();
        foreach (var handler in Handlers.Values)
        {
            var formatDir = Path.Combine(path, handler.Tag);
            if (!Directory.Exists(formatDir))
            {
                continue;
            }

            var sourceDir = Path.Combine(formatDir, sourceDirectoryName);
            if (Directory.Exists(sourceDir) && Directory.EnumerateFileSystemEntries(sourceDir).Any())
            {
                summary = summary.Add(ProcessRepackFormat(formatDir, handler, sourceDir, quiet, fileProgress));
            }
        }

        return summary;
    }

    private static bool TryGetFormatHandler(string path, out IFormatHandler handler)
    {
        return Handlers.TryGetValue(new DirectoryInfo(path).Name, out handler!);
    }

    public static ConversionSummary RepackFix(string fixDir, bool quiet = false, Action<int, int>? progress = null)
    {
        fixDir = Path.GetFullPath(fixDir);
        if (!Directory.Exists(fixDir) || !new DirectoryInfo(fixDir).Name.Equals("fix", StringComparison.OrdinalIgnoreCase))
        {
            return new ConversionSummary();
        }

        var formatDirInfo = new DirectoryInfo(fixDir).Parent!;
        var formatTag = formatDirInfo.Name;
        if (Handlers.TryGetValue(formatTag, out var handler))
        {
            var total = CountRepackFilesInFormat(formatDirInfo.FullName, "fix");
            var completed = 0;
            Action? fileProgress = total <= 0 || progress is null
                ? null
                : () => progress(Interlocked.Increment(ref completed), total);
            return ProcessRepackFormat(formatDirInfo.FullName, handler, fixDir, quiet, fileProgress);
        }

        return new ConversionSummary();
    }

    private static ConversionSummary ProcessConvertFormat(
        string formatDir,
        IFormatHandler handler,
        bool quiet = false,
        Action? fileProgress = null)
    {
        var origDir = Path.Combine(formatDir, "orig");
        var pngDir = Path.Combine(formatDir, "png");
        var metaDir = Path.Combine(formatDir, "metadata");
        if (!Directory.Exists(origDir)) return new ConversionSummary();
        Directory.CreateDirectory(pngDir);

        var files = Directory.GetFiles(origDir, "*.*", SearchOption.AllDirectories);
        int success = 0, failure = 0;

        var archiveLabel = new DirectoryInfo(formatDir).Parent?.Name ?? new DirectoryInfo(formatDir).Name;
        using var progress = quiet
            ? PictureProcessing.NullProgress
            : PictureProcessing.StartProgress($"CONVERT {archiveLabel}/{handler.Tag.ToUpperInvariant()}", files.Length);
        Parallel.ForEach(Enumerable.Range(0, files.Length), PictureProcessing.ParallelOptions, i =>
        {
            var file = files[i];
            try
            {
                var relPath = Path.GetRelativePath(origDir, file);
                string destPathBase;
                string metaPath;
                if (handler is AnmHandler or PltHandler)
                {
                    destPathBase = Path.Combine(pngDir, relPath);
                    metaPath = Path.Combine(metaDir, relPath + ".json");
                }
                else
                {
                    destPathBase = Path.Combine(pngDir, PicturePathHelper.RemoveExtensionPreservingName(relPath));
                    metaPath = Path.Combine(metaDir, PicturePathHelper.ChangeExtensionPreservingName(relPath, ".json"));
                }

                if (!File.Exists(metaPath))
                {
                    if (!quiet)
                    {
                        PictureProcessing.WriteLine($"Warning: metadata not found for \"{relPath}\", skipped.");
                    }
                    Interlocked.Increment(ref failure);
                    return;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(destPathBase)!);
                var handlerMetadata = handler.Convert(file, destPathBase);
                var baseNode = JsonNode.Parse(File.ReadAllText(metaPath))!.AsObject();
                var handlerNode = JsonNode.Parse(JsonSerializer.Serialize(handlerMetadata))!.AsObject();

                if (handler is Ap2Handler)
                {
                    baseNode.Remove("HeaderExtra");
                }

                foreach (var property in handlerNode.ToList())
                {
                    var node = property.Value;
                    handlerNode.Remove(property.Key);
                    baseNode[property.Key] = node;
                }

                var serializerOptions = new JsonSerializerOptions
                {
                    WriteIndented = true,
                    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
                };
                ReadableUnicodeJson.WriteAllText(metaPath, baseNode.ToJsonString(serializerOptions));
                Interlocked.Increment(ref success);
            }
            catch (Exception ex)
            {
                if (!quiet)
                {
                    PictureProcessing.WriteLine($"Failed to convert \"{Path.GetFileName(file)}\": {ex.Message}");
                }
                Interlocked.Increment(ref failure);
            }
            finally
            {
                progress.Increment();
                fileProgress?.Invoke();
            }
        });

        if (!quiet)
        {
            Console.WriteLine($"  [CONVERT {archiveLabel}/{handler.Tag.ToUpperInvariant()}] done: {files.Length} total, {success} success, {failure} failure.");
        }
        return new ConversionSummary(1, files.Length, success, failure);
    }

    private static int CountConvertFiles(string path)
    {
        if (TryGetFormatHandler(path, out _) && Directory.Exists(Path.Combine(path, "orig")))
        {
            return CountFormatFiles(path);
        }

        var workDirCount = CountConvertFilesInWorkDir(path);
        if (workDirCount > 0)
        {
            return workDirCount;
        }

        return Directory.GetDirectories(path)
            .Sum(CountConvertFilesInWorkDir);
    }

    private static int CountConvertFilesInWorkDir(string path)
    {
        var count = 0;
        foreach (var handler in Handlers.Values)
        {
            var formatDir = Path.Combine(path, handler.Tag);
            if (Directory.Exists(formatDir))
            {
                count += CountFormatFiles(formatDir);
            }
        }

        return count;
    }

    private static int CountFormatFiles(string formatDir)
    {
        var origDir = Path.Combine(formatDir, "orig");
        return Directory.Exists(origDir)
            ? Directory.GetFiles(origDir, "*.*", SearchOption.AllDirectories).Length
            : 0;
    }

    private static int CountRepackFiles(string path, string sourceDirectoryName)
    {
        if (TryGetFormatHandler(path, out _) && Directory.Exists(Path.Combine(path, sourceDirectoryName)))
        {
            return CountRepackFilesInFormat(path, sourceDirectoryName);
        }

        var workDirCount = CountRepackFilesInWorkDir(path, sourceDirectoryName);
        if (workDirCount > 0)
        {
            return workDirCount;
        }

        return Directory.GetDirectories(path)
            .Sum(archiveDir => CountRepackFilesInWorkDir(archiveDir, sourceDirectoryName));
    }

    private static int CountRepackFilesInWorkDir(string path, string sourceDirectoryName)
    {
        var count = 0;
        foreach (var handler in Handlers.Values)
        {
            var formatDir = Path.Combine(path, handler.Tag);
            if (Directory.Exists(formatDir))
            {
                count += CountRepackFilesInFormat(formatDir, sourceDirectoryName);
            }
        }

        return count;
    }

    private static int CountRepackFilesInFormat(string formatDir, string sourceDirectoryName)
    {
        var sourceDir = Path.Combine(formatDir, sourceDirectoryName);
        var metaDir = Path.Combine(formatDir, "metadata");
        if (!Directory.Exists(sourceDir) || !Directory.Exists(metaDir))
        {
            return 0;
        }

        return new DirectoryInfo(formatDir).Name.Equals("anm", StringComparison.OrdinalIgnoreCase) ||
               new DirectoryInfo(formatDir).Name.Equals("plt", StringComparison.OrdinalIgnoreCase)
            ? Directory.GetDirectories(sourceDir, "*", SearchOption.AllDirectories)
                .Count(dir => !Directory.EnumerateDirectories(dir).Any())
            : Directory.GetFiles(sourceDir, "*.png", SearchOption.AllDirectories).Length;
    }

    private static void PrintConvertSummary(ConversionSummary summary, TimeSpan elapsed, bool quiet = false)
    {
        if (quiet)
        {
            return;
        }

        if (!summary.HasWork)
        {
            Console.WriteLine($"  [CONVERT SUMMARY] no picture format directories found. elapsed={elapsed.TotalSeconds:F2}s");
            return;
        }

        Console.WriteLine($"  [CONVERT SUMMARY] {summary.Batches} batches, {summary.Total} total, {summary.Success} success, {summary.Failure} failure, elapsed={elapsed.TotalSeconds:F2}s.");
    }

    private static void PrintRepackSummary(ConversionSummary summary, TimeSpan elapsed, string sourceDirectoryName, bool quiet = false)
    {
        if (quiet)
        {
            return;
        }

        var label = sourceDirectoryName.Equals("png", StringComparison.OrdinalIgnoreCase)
            ? "REPACK PNG SUMMARY"
            : "REPACK SUMMARY";

        if (!summary.HasWork)
        {
            Console.WriteLine($"  [{label}] no picture format directories found. elapsed={elapsed.TotalSeconds:F2}s");
            return;
        }

        Console.WriteLine($"  [{label}] {summary.Batches} batches, {summary.Total} total, {summary.Success} success, {summary.Failure} failure, elapsed={elapsed.TotalSeconds:F2}s.");
    }

    private static ConversionSummary ProcessRepackFormat(
        string formatDir,
        IFormatHandler handler,
        string sourceDir,
        bool quiet = false,
        Action? fileProgress = null)
    {
        var metaDir = Path.Combine(formatDir, "metadata");
        var newDir = Path.Combine(formatDir, "new");
        if (!Directory.Exists(sourceDir) || !Directory.Exists(metaDir))
        {
            return new ConversionSummary();
        }

        var isAnimation = handler is AnmHandler or PltHandler;
        var sourceItems = isAnimation
            ? Directory.GetDirectories(sourceDir, "*", SearchOption.AllDirectories)
                .Where(dir => !Directory.EnumerateDirectories(dir).Any())
                .ToArray()
            : Directory.GetFiles(sourceDir, "*.png", SearchOption.AllDirectories);

        if (sourceItems.Length == 0)
        {
            return new ConversionSummary();
        }

        // 第一階段：只讀取輸入和 metadata，建立完整的輸出計畫，不建立或覆寫任何輸出檔案。
        var plans = new List<RepackPlan>(sourceItems.Length);
        var prepareErrors = new List<string>();

        foreach (var item in sourceItems)
        {
            try
            {
                var relativePath = Path.GetRelativePath(sourceDir, item);
                string metadataPath;
                string handlerSourcePath;

                if (isAnimation)
                {
                    metadataPath = Path.Combine(metaDir, relativePath + ".json");
                    handlerSourcePath = item;
                }
                else
                {
                    var baseName = PicturePathHelper.RemoveExtensionPreservingName(relativePath, ".png");
                    metadataPath = Path.Combine(metaDir, baseName + ".json");
                    handlerSourcePath = Path.Combine(sourceDir, baseName);
                }

                if (!File.Exists(metadataPath))
                {
                    throw new FileNotFoundException($"Metadata not found for \"{relativePath}\".", metadataPath);
                }

                var metadata = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
                    File.ReadAllText(metadataPath),
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                    ?? throw new InvalidDataException($"Metadata JSON is empty or invalid: {metadataPath}");

                if (!metadata.TryGetValue("OriginalRelativePath", out var originalPathNode) ||
                    originalPathNode.ValueKind != JsonValueKind.String)
                {
                    throw new InvalidDataException($"Metadata does not contain a string OriginalRelativePath: {metadataPath}");
                }

                var originalRelativePath = originalPathNode.GetString();
                if (string.IsNullOrWhiteSpace(originalRelativePath))
                {
                    throw new InvalidDataException($"Metadata contains an empty OriginalRelativePath: {metadataPath}");
                }

                var archiveRelativePath = PicturePathHelper.GetArchiveRelativePath(
                    metadata,
                    originalRelativePath,
                    formatDir);
                var destinationPath = PicturePathHelper.GetSafeDestinationPath(newDir, archiveRelativePath);
                plans.Add(new RepackPlan(handlerSourcePath, destinationPath));
            }
            catch (Exception ex)
            {
                prepareErrors.Add($"{Path.GetFileName(item)}: {ex.Message}");
            }
        }

        if (prepareErrors.Count > 0)
        {
            if (!quiet)
            {
                foreach (var error in prepareErrors)
                {
                    PictureProcessing.WriteLine($"Failed to prepare repack: {error}");
                }
                PictureProcessing.WriteLine("Repack cancelled: preflight failed; no output files were written.");
            }

            for (var i = 0; i < sourceItems.Length; i++)
            {
                fileProgress?.Invoke();
            }

            // 預檢有任何一筆失敗，就不只跳過該筆，而是停止整個格式批次，避免產生不完整 new/。
            return new ConversionSummary(1, sourceItems.Length, 0, sourceItems.Length);
        }

        // 第二階段：檢查重複目的地、檔案/資料夾衝突與既有 reparse point。
        try
        {
            ValidateRepackPlan(newDir, plans);
        }
        catch (Exception ex)
        {
            if (!quiet)
            {
                PictureProcessing.WriteLine($"Repack cancelled: {ex.Message}");
                PictureProcessing.WriteLine("No output files were written.");
            }

            for (var i = 0; i < sourceItems.Length; i++)
            {
                fileProgress?.Invoke();
            }

            return new ConversionSummary(1, sourceItems.Length, 0, sourceItems.Length);
        }

        // 第三階段：所有計畫通過預檢後才開始建立輸出目錄及寫檔。
        Directory.CreateDirectory(newDir);
        int success = 0, failure = 0;

        Parallel.ForEach(plans, PictureProcessing.ParallelOptions, plan =>
        {
            var destinationDirectory = Path.GetDirectoryName(plan.DestinationPath)!;
            var temporaryPath = Path.Combine(
                destinationDirectory,
                $".{Path.GetFileName(plan.DestinationPath)}.{Guid.NewGuid():N}.tmp");

            try
            {
                Directory.CreateDirectory(destinationDirectory);
                handler.Repack(plan.SourcePath, temporaryPath);

                if (!File.Exists(temporaryPath))
                {
                    throw new IOException("The format handler did not create its temporary output file.");
                }

                // 使用同目錄暫存檔替換，單一檔案回封失敗時不會截斷上一版輸出。
                File.Move(temporaryPath, plan.DestinationPath, true);
                Interlocked.Increment(ref success);
            }
            catch (Exception ex)
            {
                try
                {
                    if (File.Exists(temporaryPath))
                    {
                        File.Delete(temporaryPath);
                    }
                }
                catch
                {
                    // 保留主要回封錯誤；暫存檔清理失敗不覆蓋原始例外。
                }

                if (!quiet)
                {
                    PictureProcessing.WriteLine($"Failed to repack \"{Path.GetFileName(plan.SourcePath)}\": {ex.Message}");
                }
                Interlocked.Increment(ref failure);
            }
            finally
            {
                fileProgress?.Invoke();
            }
        });

        if (!quiet)
        {
            Console.WriteLine($"  [REPACK {handler.Tag.ToUpperInvariant()}] done: {sourceItems.Length} total, {success} success, {failure} failure.");
        }

        return new ConversionSummary(1, sourceItems.Length, success, failure);
    }

    private static void ValidateRepackPlan(string outputRoot, IReadOnlyList<RepackPlan> plans)
    {
        var fullRoot = Path.GetFullPath(outputRoot);
        if (File.Exists(fullRoot))
        {
            throw new IOException($"Repack output root is an existing file: {fullRoot}");
        }

        if (Directory.Exists(fullRoot) && IsReparsePoint(fullRoot))
        {
            throw new InvalidDataException($"Repack output root is a reparse point: {fullRoot}");
        }

        var destinations = new HashSet<string>(PathComparer);
        foreach (var plan in plans)
        {
            if (!destinations.Add(plan.DestinationPath))
            {
                throw new InvalidDataException($"Multiple image sources target the same output file: {plan.DestinationPath}");
            }
        }

        // 同一批次不能同時把某路徑當成檔案，又把它當成其他輸出的上層資料夾。
        foreach (var destination in destinations)
        {
            var parent = Path.GetDirectoryName(destination);
            while (!string.IsNullOrEmpty(parent))
            {
                if (destinations.Contains(parent))
                {
                    throw new InvalidDataException($"Repack output has a file/directory path conflict: {parent}");
                }

                if (PathComparer.Equals(parent, fullRoot))
                {
                    break;
                }

                parent = Path.GetDirectoryName(parent);
            }
        }

        // 檢查已存在的路徑元件，避免透過 junction/symlink 將輸出導向 new/ 之外。
        foreach (var destination in destinations)
        {
            if (Directory.Exists(destination))
            {
                throw new IOException($"Repack destination is already a directory: {destination}");
            }

            var parent = Path.GetDirectoryName(destination);
            while (!string.IsNullOrEmpty(parent))
            {
                if (File.Exists(parent))
                {
                    throw new IOException($"Repack output parent is an existing file: {parent}");
                }

                if (Directory.Exists(parent) && IsReparsePoint(parent))
                {
                    throw new InvalidDataException($"Repack output path contains a reparse point: {parent}");
                }

                if (PathComparer.Equals(parent, fullRoot))
                {
                    break;
                }

                parent = Path.GetDirectoryName(parent);
            }
        }
    }

    private static bool IsReparsePoint(string path)
    {
        return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
    }
}
