// ============================================================================
// SafePath.cs
// 共用輸出路徑驗證：防止路徑穿越、Windows 特殊檔名及既有 Reparse Point。
// ============================================================================

namespace Kaguya_YaneKit.Core;

public static class SafePath
{
    private static readonly char[] WindowsInvalidFileNameChars =
    [
        '<', '>', ':', '"', '|', '?', '*'
    ];

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

    private static StringComparer PathComparer =>
        OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;

    /// <summary>
    /// 將相對路徑解析到輸出目錄下，並驗證路徑段、Windows 保留名稱、
    /// 路徑穿越，以及目前已存在的 Reparse Point。
    /// </summary>
    public static string GetSafeDestinationPath(string outputRoot, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            throw new InvalidDataException("Output relative path is empty.");
        }

        var normalized = relativePath
            .Replace('\\', Path.DirectorySeparatorChar)
            .Replace('/', Path.DirectorySeparatorChar);

        if (Path.IsPathRooted(normalized) ||
            (normalized.Length >= 2 &&
             char.IsAsciiLetter(normalized[0]) &&
             normalized[1] == ':'))
        {
            throw new InvalidDataException($"Output path must be relative: {relativePath}");
        }

        var segments = normalized.Split(Path.DirectorySeparatorChar, StringSplitOptions.None);
        if (segments.Length == 0 ||
            segments.Any(segment => segment.Length == 0 || segment is "." or ".."))
        {
            throw new InvalidDataException($"Output path contains an invalid path segment: {relativePath}");
        }

        foreach (var segment in segments)
        {
            if (segment.EndsWith(' ') ||
                segment.EndsWith('.') ||
                segment.Any(ch => ch < 0x20 ||
                    WindowsInvalidFileNameChars.Contains(ch) ||
                    Path.GetInvalidFileNameChars().Contains(ch)))
            {
                throw new InvalidDataException($"Output path contains an invalid Windows file name: {relativePath}");
            }

            // Windows 裝置名稱即使附加副檔名仍屬保留名稱。
            var deviceName = segment.Split('.')[0].TrimEnd(' ', '.');
            if (IsReservedWindowsDeviceName(deviceName))
            {
                throw new InvalidDataException($"Output path contains a reserved Windows device name: {relativePath}");
            }
        }

        var fullRoot = Path.GetFullPath(outputRoot);
        ValidateOutputRoot(fullRoot);

        var fullPath = Path.GetFullPath(Path.Combine(fullRoot, normalized));
        EnsureStrictChildPath(fullRoot, fullPath, relativePath);
        ValidateExistingPath(fullRoot, fullPath);
        return fullPath;
    }

    /// <summary>
    /// 驗證輸出根目錄本身及其所有既有祖先目錄。
    /// 選擇透過 junction/symlink 指向的輸出根目錄時會拒絕執行。
    /// </summary>
    public static void ValidateOutputRoot(string outputRoot)
    {
        var fullRoot = Path.GetFullPath(outputRoot);
        var current = fullRoot;

        while (!string.IsNullOrEmpty(current))
        {
            var attributes = TryGetAttributes(current);
            if (attributes.HasValue)
            {
                if ((attributes.Value & FileAttributes.ReparsePoint) != 0)
                {
                    throw new InvalidDataException($"Output path contains a reparse point: {current}");
                }

                if (PathComparer.Equals(current, fullRoot) &&
                    (attributes.Value & FileAttributes.Directory) == 0)
                {
                    throw new IOException($"Output root is an existing file: {current}");
                }
            }

            var parent = Path.GetDirectoryName(current);
            if (string.IsNullOrEmpty(parent) || PathComparer.Equals(parent, current))
            {
                break;
            }

            current = parent;
        }
    }

    /// <summary>
    /// 驗證目標路徑及從目標到輸出根目錄之間的每一個既有路徑元件。
    /// </summary>
    public static void ValidateExistingPath(string outputRoot, string destinationPath)
    {
        var fullRoot = Path.GetFullPath(outputRoot);
        var fullPath = Path.GetFullPath(destinationPath);

        EnsureStrictChildPath(fullRoot, fullPath, destinationPath);
        ValidateOutputRoot(fullRoot);

        if (Directory.Exists(fullPath))
        {
            throw new IOException($"Output destination is already a directory: {fullPath}");
        }

        var current = fullPath;
        while (true)
        {
            var attributes = TryGetAttributes(current);
            if (attributes.HasValue)
            {
                if ((attributes.Value & FileAttributes.ReparsePoint) != 0)
                {
                    throw new InvalidDataException($"Output path contains a reparse point: {current}");
                }

                if (!PathComparer.Equals(current, fullPath) &&
                    (attributes.Value & FileAttributes.Directory) == 0)
                {
                    throw new IOException($"Output parent is an existing file: {current}");
                }

                if (PathComparer.Equals(current, fullRoot) &&
                    (attributes.Value & FileAttributes.Directory) == 0)
                {
                    throw new IOException($"Output root is not a directory: {current}");
                }
            }

            if (PathComparer.Equals(current, fullRoot))
            {
                break;
            }

            current = Path.GetDirectoryName(current)
                ?? throw new InvalidDataException($"Output path is not inside its root: {destinationPath}");
        }
    }

    private static void EnsureStrictChildPath(string fullRoot, string fullPath, string originalPath)
    {
        var relative = Path.GetRelativePath(fullRoot, fullPath);
        if (relative == "." ||
            Path.IsPathRooted(relative) ||
            relative == ".." ||
            relative.StartsWith(".." + Path.DirectorySeparatorChar, PathComparison))
        {
            throw new InvalidDataException($"Output path escapes its root: {originalPath}");
        }
    }

    private static FileAttributes? TryGetAttributes(string path)
    {
        try
        {
            return File.GetAttributes(path);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }
    }

    private static bool IsReservedWindowsDeviceName(string name)
    {
        if (name.Equals("CON", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("AUX", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("NUL", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("CONIN$", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("CONOUT$", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Windows 同時保留 COM¹/²/³ 及 LPT¹/²/³。
        if (name.Length == 4 &&
            (name.StartsWith("COM", StringComparison.OrdinalIgnoreCase) ||
             name.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) &&
            (name[3] is >= '1' and <= '9' || name[3] is '¹' or '²' or '³'))
        {
            return true;
        }

        return false;
    }
}
