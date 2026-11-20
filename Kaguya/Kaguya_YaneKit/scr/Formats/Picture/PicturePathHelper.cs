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
using Kaguya_YaneKit.Core;

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
    /// 統一交由 SafePath 檢查路徑穿越、Windows 保留檔名及既有 Reparse Point。
    /// </summary>
    public static string GetSafeDestinationPath(string outputRoot, string relativePath)
    {
        return SafePath.GetSafeDestinationPath(outputRoot, relativePath);
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
