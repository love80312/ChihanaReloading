// ============================================================================
// PicturePathHelper.cs
// 圖片路徑解析工具：在中繼資料和實際檔案路徑之間轉換
//
// 職責：
//   GetMetadataPathForSource  -- 從 fix/png/orig 中的來源檔案推導 metadata JSON 路徑
//   GetArchiveRelativePath    -- 從中繼資料還原封包內相對路徑
//   GetSafeDestinationPath    -- 驗證相對路徑並限制輸出在指定目錄內
// ============================================================================
using System.Text.Json;

namespace Kaguya_YaneKit.Formats.Picture;

internal static class PicturePathHelper
{
    public static string ChangeExtensionPreservingName(string path, string extension)
    {
        var oldExtension = Path.GetExtension(path);
        return oldExtension.Length > 0
            ? path[..^oldExtension.Length] + extension
            : path + extension;
    }

    public static string RemoveExtensionPreservingName(string path)
    {
        var extension = Path.GetExtension(path);
        return extension.Length > 0 ? path[..^extension.Length] : path;
    }

    public static string RemoveExtensionPreservingName(string path, string extension)
    {
        return path.EndsWith(extension, StringComparison.OrdinalIgnoreCase)
            ? path[..^extension.Length]
            : RemoveExtensionPreservingName(path);
    }

    public static string GetMetadataPathForSource(string sourcePath)
    {
        var sourceFullPath = Path.GetFullPath(sourcePath);
        var sourceRoot = FindAncestorDirectory(sourceFullPath, "fix")
            ?? FindAncestorDirectory(sourceFullPath, "png")
            ?? FindAncestorDirectory(sourceFullPath, "orig")
            ?? throw new DirectoryNotFoundException($"Cannot find fix/png/orig directory for {sourcePath}");
        var formatDir = Directory.GetParent(sourceRoot)?.FullName
            ?? throw new DirectoryNotFoundException($"Cannot find format directory for {sourcePath}");
        var relativePath = Path.GetRelativePath(sourceRoot, sourceFullPath);
        var metadataRelativePath = GetMetadataRelativePath(relativePath, sourceRoot);
        return Path.Combine(formatDir, "metadata", metadataRelativePath + ".json");
    }

    public static string GetArchiveRelativePath(
        Dictionary<string, JsonElement> metadata,
        string originalRelativePath,
        string formatDir)
    {
        _ = formatDir; // 保留既有方法簽章，避免影響呼叫端。

        if (metadata.TryGetValue("SourceArchive", out var archiveNode) &&
            archiveNode.ValueKind == JsonValueKind.String)
        {
            var sourceArchive = archiveNode.GetString();
            if (!string.IsNullOrWhiteSpace(sourceArchive))
            {
                // 先統一分隔符，避免 metadata 使用另一種斜線時無法移除封包前綴。
                var normalizedPath = originalRelativePath.Replace('\\', '/');
                var normalizedArchive = sourceArchive.Trim().Trim('\\', '/').Replace('\\', '/');
                var prefix = normalizedArchive + "/";

                if (normalizedArchive.Length > 0 &&
                    normalizedPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    return normalizedPath[prefix.Length..];
                }
            }
        }

        return originalRelativePath;
    }

    /// <summary>
    /// 將中繼資料中的相對路徑轉成完整輸出路徑。
    /// 拒絕絕對路徑、磁碟機路徑、. / .. 路徑段、非法檔名字元及輸出根目錄外路徑。
    /// </summary>
    public static string GetSafeDestinationPath(string outputRoot, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            throw new InvalidDataException("Metadata contains an empty output relative path.");
        }

        var normalized = relativePath
            .Replace('\\', Path.DirectorySeparatorChar)
            .Replace('/', Path.DirectorySeparatorChar);

        if (Path.IsPathRooted(normalized) ||
            (normalized.Length >= 2 &&
             char.IsAsciiLetter(normalized[0]) &&
             normalized[1] == ':'))
        {
            throw new InvalidDataException($"Metadata output path must be relative: {relativePath}");
        }

        var segments = normalized.Split(
            Path.DirectorySeparatorChar,
            StringSplitOptions.RemoveEmptyEntries);

        if (segments.Length == 0)
        {
            throw new InvalidDataException($"Metadata output path is empty: {relativePath}");
        }

        var invalidFileNameChars = Path.GetInvalidFileNameChars();
        foreach (var segment in segments)
        {
            if (segment is "." or ".." ||
                segment.IndexOfAny(invalidFileNameChars) >= 0 ||
                segment.EndsWith(' ') ||
                segment.EndsWith('.'))
            {
                throw new InvalidDataException($"Metadata output path contains an invalid path segment: {relativePath}");
            }

            // Windows 保留裝置名稱，即使後面帶副檔名也不能當成一般檔案使用。
            var deviceName = segment.Split('.')[0].TrimEnd(' ', '.');
            if (IsReservedWindowsDeviceName(deviceName))
            {
                throw new InvalidDataException($"Metadata output path contains a reserved Windows name: {relativePath}");
            }
        }

        var fullRoot = Path.GetFullPath(outputRoot);
        var fullPath = Path.GetFullPath(Path.Combine(fullRoot, normalized));
        var rootPrefix = Path.EndsInDirectorySeparator(fullRoot)
            ? fullRoot
            : fullRoot + Path.DirectorySeparatorChar;
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        if (!fullPath.StartsWith(rootPrefix, comparison))
        {
            throw new InvalidDataException($"Metadata output path escapes the output directory: {relativePath}");
        }

        return fullPath;
    }

    private static bool IsReservedWindowsDeviceName(string name)
    {
        if (name.Equals("CON", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("AUX", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("NUL", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (name.Length == 4 &&
            (name.StartsWith("COM", StringComparison.OrdinalIgnoreCase) ||
             name.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) &&
            name[3] is >= '1' and <= '9')
        {
            return true;
        }

        return false;
    }

    private static string? FindAncestorDirectory(string path, string directoryName)
    {
        var current = File.Exists(path)
            ? Directory.GetParent(path)
            : new DirectoryInfo(path);

        while (current is not null)
        {
            if (current.Name.Equals(directoryName, StringComparison.OrdinalIgnoreCase))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        return null;
    }

    private static string GetMetadataRelativePath(string relativePath, string sourceRoot)
    {
        var sourceRootName = new DirectoryInfo(sourceRoot).Name;
        if ((sourceRootName.Equals("png", StringComparison.OrdinalIgnoreCase) ||
             sourceRootName.Equals("fix", StringComparison.OrdinalIgnoreCase)) &&
            relativePath.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
        {
            return RemoveExtensionPreservingName(relativePath, ".png");
        }

        return relativePath;
    }
}
