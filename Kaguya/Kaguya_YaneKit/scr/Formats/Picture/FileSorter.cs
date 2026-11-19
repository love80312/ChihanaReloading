// ============================================================================
// FileSorter.cs
// 按檔案格式簽名將解包後的遊戲檔案分拣到對應子目錄
//
// 職責:
//   Sort                   -- 掃描來源目錄所有檔案，按 IFormatHandler.Identify 分拣
//   SortArchiveDirectories -- 偵測 _link_manifest.json / _archive_manifest.json 並逐個分拣
//
// 分拣演算法:
//   對每個檔案，依序用所有註冊的 Handler 嘗試識別
//   匹配成功後複製到 {workDir}/{format}/orig/ 並產生 metadata JSON
//   跳過 .json/.txt/.xml/.ini/.csv/.log 等非二進位檔案
//
// 依賴: IFormatHandler、PictureProcessing、Core.ReadableUnicodeJson
// ============================================================================
using Kaguya_YaneKit.Formats.Picture.Handlers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Kaguya_YaneKit.Core;

namespace Kaguya_YaneKit.Formats.Picture;

public static class FileSorter
{
    public readonly record struct SortSummary(int Total, int Success, int Failure, int Skipped, int Unrecognized)
    {
        public SortSummary Add(SortSummary other) => new(
            Total + other.Total,
            Success + other.Success,
            Failure + other.Failure,
            Skipped + other.Skipped,
            Unrecognized + other.Unrecognized);
    }

    private sealed class BaseMetadata
    {
        public string OriginalRelativePath { get; set; } = "";
        public string? SourceArchive { get; set; }
    }

    private static readonly List<IFormatHandler> Handlers = new()
    {
        new Ap0Handler(),
        new Ap2Handler(),
        new Ap3Handler(),
        new AnmHandler(),
        new PltHandler(),
        new BmpHandler(),
        new ApHandler(),
    };

    private static readonly Dictionary<string, IFormatHandler> HandlersByTag = Handlers
        .ToDictionary(handler => handler.Tag, StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> SkipExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".json", ".txt", ".xml", ".ini", ".csv", ".log"
    };

    public static SortSummary Sort(string sourceDir, string workDir)
    {
        return Sort(sourceDir, workDir, originalPathPrefix: null, sourceArchive: null);
    }

    public static bool SortArchiveDirectories(string sourceRoot, string workRoot)
    {
        sourceRoot = Path.GetFullPath(sourceRoot);
        workRoot = Path.GetFullPath(workRoot);

        var archiveDirs = Directory.EnumerateFiles(sourceRoot, "_link_manifest.json", SearchOption.AllDirectories)
            .Concat(Directory.EnumerateFiles(sourceRoot, "_archive_manifest.json", SearchOption.AllDirectories))
            .Select(Path.GetDirectoryName)
            .Where(dir => !string.IsNullOrWhiteSpace(dir))
            .Select(dir => dir!)
            .Distinct(OperatingSystem.IsWindows()
                ? StringComparer.OrdinalIgnoreCase
                : StringComparer.Ordinal)
            .OrderBy(dir => dir, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (archiveDirs.Count == 0)
        {
            return false;
        }

        // 以封包目錄相對於 sourceRoot 的完整路徑作為工作目錄鍵，
        // 避免 GameA\arc 與 GameB\arc 因 basename 相同而共用同一工作目錄。
        var outputRoots = new HashSet<string>(OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal);

        foreach (var archiveDir in archiveDirs)
        {
            var relativeArchivePath = Path.GetRelativePath(sourceRoot, archiveDir);
            var isRootArchive = relativeArchivePath == ".";
            var sourceArchiveKey = isRootArchive
                ? new DirectoryInfo(archiveDir).Name
                : relativeArchivePath;
            var workFolderName = GetArchiveWorkFolderName(relativeArchivePath, archiveDir);
            var archiveWorkDir = Path.GetFullPath(Path.Combine(workRoot, workFolderName));

            if (!outputRoots.Add(archiveWorkDir))
            {
                throw new InvalidDataException(
                    $"Multiple archive directories resolve to the same picture work directory: {archiveWorkDir}");
            }

            // 工作目錄維持單層 archive 資料夾，符合 FileConverter 的既有掃描方式；
            // 若來源封包位於子資料夾，使用穩定雜湊區分同名封包，避免 basename 碰撞。
            // metadata 仍保存相對 sourceRoot 的完整路徑，回封時可正確移除封包前綴。
            Sort(
                archiveDir,
                archiveWorkDir,
                sourceArchiveKey,
                sourceArchiveKey);
        }

        return true;
    }

    private static string GetArchiveWorkFolderName(string relativeArchivePath, string archiveDir)
    {
        if (relativeArchivePath == ".")
        {
            return new DirectoryInfo(archiveDir).Name;
        }

        var normalized = relativeArchivePath.Replace('\\', '/').Trim('/');
        if (!normalized.Contains('/'))
        {
            return normalized;
        }

        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)))[..12];
        return $"{new DirectoryInfo(archiveDir).Name}__{hash}";
    }

    public static SortSummary Sort(
        string sourceDir,
        string workDir,
        string? originalPathPrefix,
        string? sourceArchive,
        bool quiet = false,
        Action<int, int>? progress = null)
    {
        sourceDir = Path.GetFullPath(sourceDir);
        workDir = Path.GetFullPath(workDir);

        var allFiles = Directory.GetFiles(sourceDir, "*.*", SearchOption.AllDirectories);
        int success = 0, failure = 0, skipped = 0, unrecognized = 0;
        var completed = 0;
        var total = allFiles.Length;

        Parallel.ForEach(allFiles, PictureProcessing.ParallelOptions, file =>
        {
            try
            {
                var ext = Path.GetExtension(file);
                if (SkipExtensions.Contains(ext))
                {
                    Interlocked.Increment(ref skipped);
                    return;
                }

                using var stream = File.OpenRead(file);
                using var reader = new BinaryReader(stream);
                var matched = TryIdentifyHandler(reader, out var handler);
                if (matched)
                {
                    var relativePath = Path.GetRelativePath(sourceDir, file);

                    var formatDir = Path.Combine(workDir, handler.Tag);
                    var origDir = Path.Combine(formatDir, "orig");
                    var metaDir = Path.Combine(formatDir, "metadata");

                    var destOrigPath = Path.Combine(origDir, relativePath);
                    Directory.CreateDirectory(Path.GetDirectoryName(destOrigPath)!);
                    CopyIfChanged(file, destOrigPath);

                    var originalRelativePath = string.IsNullOrWhiteSpace(originalPathPrefix)
                        ? relativePath
                        : Path.Combine(originalPathPrefix, relativePath);
                    var metadata = new BaseMetadata
                    {
                        OriginalRelativePath = originalRelativePath,
                        SourceArchive = sourceArchive
                    };
                    var serializerOptions = new JsonSerializerOptions
                    {
                        WriteIndented = true,
                        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
                    };
                    var json = JsonSerializer.Serialize(metadata, serializerOptions);

                    string destMetaPath;
                    if (handler is AnmHandler or PltHandler)
                    {
                        destMetaPath = Path.Combine(metaDir, relativePath + ".json");
                    }
                    else
                    {
                        destMetaPath = Path.Combine(metaDir, PicturePathHelper.ChangeExtensionPreservingName(relativePath, ".json"));
                    }

                    Directory.CreateDirectory(Path.GetDirectoryName(destMetaPath)!);
                    WriteTextIfChanged(destMetaPath, json);
                    Interlocked.Increment(ref success);
                }

                if (!matched)
                {
                    if (!quiet)
                    {
                        PictureProcessing.WriteLine($"[Unrecognized] {Path.GetRelativePath(sourceDir, file)}");
                    }
                    Interlocked.Increment(ref unrecognized);
                }
            }
            catch (Exception ex)
            {
                if (!quiet)
                {
                    PictureProcessing.WriteLine($"Failed to process \"{Path.GetFileName(file)}\": {ex.Message}");
                }
                Interlocked.Increment(ref failure);
            }
            finally
            {
                progress?.Invoke(Interlocked.Increment(ref completed), total);
            }
        });

        var summary = new SortSummary(allFiles.Length, success, failure, skipped, unrecognized);
        if (!quiet)
        {
            Console.WriteLine($"  [SORT] done: {summary.Total} total, {summary.Success} success, {summary.Failure} failure, {summary.Skipped} skipped, {summary.Unrecognized} unrecognized.");
        }

        return summary;
    }

    private static void CopyIfChanged(string sourcePath, string destinationPath)
    {
        if (File.Exists(destinationPath))
        {
            var source = new FileInfo(sourcePath);
            var destination = new FileInfo(destinationPath);
            if (source.Length == destination.Length)
            {
                byte[] sourceHash;
                byte[] destinationHash;

                using (var sourceStream = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (var destinationStream = new FileStream(destinationPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    sourceHash = SHA256.HashData(sourceStream);
                    destinationHash = SHA256.HashData(destinationStream);
                }

                if (sourceHash.AsSpan().SequenceEqual(destinationHash))
                {
                    // 內容相同時同步時間戳，避免工作副本留下過時的檔案時間。
                    File.SetLastWriteTimeUtc(destinationPath, source.LastWriteTimeUtc);
                    return;
                }
            }
        }

        File.Copy(sourcePath, destinationPath, true);
    }

    private static void WriteTextIfChanged(string path, string text)
    {
        if (File.Exists(path) && string.Equals(File.ReadAllText(path), text, StringComparison.Ordinal))
        {
            return;
        }

        ReadableUnicodeJson.WriteAllText(path, text);
    }

    private static bool TryIdentifyHandler(BinaryReader reader, out IFormatHandler handler)
    {
        handler = null!;
        var stream = reader.BaseStream;
        if (stream.Length < 2)
        {
            return false;
        }

        Span<byte> header = stackalloc byte[5];
        stream.Position = 0;
        var read = stream.Read(header);
        stream.Position = 0;

        var tag = GetTagFromHeader(header[..read]);
        if (tag is null || !HandlersByTag.TryGetValue(tag, out var candidate))
        {
            return false;
        }

        handler = candidate;
        var identified = handler.Identify(reader);
        stream.Position = 0;
        if (!identified)
        {
            handler = null!;
        }

        return identified;
    }

    private static string? GetTagFromHeader(ReadOnlySpan<byte> header)
    {
        if (header.Length >= 4)
        {
            var signature = Encoding.ASCII.GetString(header[..4]);
            return signature switch
            {
                "AP-0" => "ap0",
                "AP-2" => "ap2",
                "AP-3" => "ap3",
                "AN00" or "AN01" or "AN20" or "AN21" => "anm",
                "PL00" or "PL01" or "PL10" or "PL11" or "PL20" or "PL30" => "plt",
                _ => GetContainerTagFromHeader(header)
            };
        }

        return GetContainerTagFromHeader(header);
    }

    private static string? GetContainerTagFromHeader(ReadOnlySpan<byte> header)
    {
        if (header.Length >= 5 && header[0] == 4)
        {
            var signature = Encoding.ASCII.GetString(header.Slice(1, 4));
            if (signature is "APS3" or "APS4")
            {
                return "ap3";
            }
        }

        if (header.Length >= 2)
        {
            var magic = (ushort)(header[0] | (header[1] << 8));
            return magic switch
            {
                0x4D42 => "bmp",
                0x5041 or 0x4F41 => "ap",
                _ => null
            };
        }

        return null;
    }
}
