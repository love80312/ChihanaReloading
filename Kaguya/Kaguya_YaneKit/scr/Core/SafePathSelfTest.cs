// ============================================================================
// SafePathSelfTest.cs
// --self-test 實際驗證安全路徑、LINK/AF01 路徑委派，以及 Restorer 檔案還原。
// ============================================================================
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using Kaguya_YaneKit.Formats.Archive;
using Kaguya_YaneKit.Formats.Picture;

namespace Kaguya_YaneKit.Core;

internal static class SafePathSelfTest
{
    public static int Run()
    {
        var testRoot = Path.Combine(
            Path.GetTempPath(),
            $"Kaguya_YaneKit_SelfTest_{Guid.NewGuid():N}");

        try
        {
            Directory.CreateDirectory(testRoot);

            TestSafePathValidation(testRoot);
            TestArchivePathDelegation(testRoot);
            TestArchiveAtomicReplacement(testRoot);
            TestRestoreAndReplenish(testRoot);

            Console.WriteLine("PASS: SafePath traversal, invalid-name, and reparse-point checks.");
            Console.WriteLine("PASS: LINK/AF01 extraction path validation delegates.");
            Console.WriteLine("PASS: LINK/AF01 atomic extraction keeps old files on write failure.");
            Console.WriteLine("PASS: Restorer atomic replacement, replenish, and timestamps.");
            Console.WriteLine("All Kaguya_YaneKit core self-tests passed.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Self-test failed: {ex}");
            return 1;
        }
        finally
        {
            try
            {
                if (Directory.Exists(testRoot))
                {
                    Directory.Delete(testRoot, recursive: true);
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Self-test cleanup warning: {ex.Message}");
            }
        }
    }

    private static void TestSafePathValidation(string testRoot)
    {
        var outputRoot = Path.Combine(testRoot, "safe-output");
        Directory.CreateDirectory(outputRoot);

        var expected = Path.GetFullPath(Path.Combine(outputRoot, "folder", "file.bin"));
        var actual = SafePath.GetSafeDestinationPath(outputRoot, "folder\\file.bin");
        Assert(PathComparer.Equals(expected, actual), "Safe relative path resolved incorrectly.");

        ExpectInvalidData(
            () => SafePath.GetSafeDestinationPath(outputRoot, "..\\escape.bin"),
            "Parent traversal was not rejected.");
        ExpectInvalidData(
            () => SafePath.GetSafeDestinationPath(outputRoot, "C:\\outside.bin"),
            "Drive-qualified path was not rejected.");
        ExpectInvalidData(
            () => SafePath.GetSafeDestinationPath(outputRoot, "folder\\CON.txt"),
            "Windows CON device name was not rejected.");
        ExpectInvalidData(
            () => SafePath.GetSafeDestinationPath(outputRoot, "folder\\COM¹.txt"),
            "Windows COM¹ device name was not rejected.");
        ExpectInvalidData(
            () => SafePath.GetSafeDestinationPath(outputRoot, "folder\\LPT³.bin"),
            "Windows LPT³ device name was not rejected.");
        ExpectInvalidData(
            () => SafePath.GetSafeDestinationPath(outputRoot, "folder\\CONIN$"),
            "Windows CONIN$ device name was not rejected.");
        ExpectInvalidData(
            () => SafePath.GetSafeDestinationPath(outputRoot, "folder\\bad?.bin"),
            "Windows invalid filename character was not rejected.");
        ExpectInvalidData(
            () => SafePath.GetSafeDestinationPath(outputRoot, "folder\\trailing."),
            "Trailing dot was not rejected.");
        ExpectInvalidData(
            () => SafePath.GetSafeDestinationPath(outputRoot, "folder\\trailing "),
            "Trailing space was not rejected.");

        // 建立目錄符號連結在部分 Windows CI 執行器上需要開發人員模式或權限。
        // 能建立時必須確認連結路徑會被拒絕；無權限時明確標記為 SKIP。
        var symlinkTarget = Path.Combine(testRoot, "symlink-target");
        var symlinkPath = Path.Combine(outputRoot, "linked-folder");
        Directory.CreateDirectory(symlinkTarget);
        var symlinkCreated = false;
        try
        {
            Directory.CreateSymbolicLink(symlinkPath, symlinkTarget);
            symlinkCreated = true;
        }
        catch (Exception ex) when (
            ex is UnauthorizedAccessException or
            IOException or
            NotSupportedException)
        {
            Console.WriteLine($"SKIP: directory symlink test unavailable ({ex.GetType().Name}).");
        }

        if (symlinkCreated)
        {
            ExpectInvalidData(
                () => SafePath.GetSafeDestinationPath(outputRoot, "linked-folder\\escaped.bin"),
                "Existing directory symlink was not rejected.");
            Directory.Delete(symlinkPath, recursive: false);
        }
    }

    private static void TestArchivePathDelegation(string outputRoot)
    {
        AssertArchivePathMethod(
            typeof(LinkArchiveCodec),
            outputRoot,
            "LinkArchiveCodec");
        AssertArchivePathMethod(
            typeof(Af01ArchiveCodec),
            outputRoot,
            "Af01ArchiveCodec");
    }

    private static void TestArchiveAtomicReplacement(string testRoot)
    {
        AssertAtomicReplacement(
            typeof(LinkArchiveCodec),
            Path.Combine(testRoot, "link-atomic"),
            "LinkArchiveCodec");
        AssertAtomicReplacement(
            typeof(Af01ArchiveCodec),
            Path.Combine(testRoot, "af01-atomic"),
            "Af01ArchiveCodec");
    }

    private static void AssertAtomicReplacement(Type codecType, string outputRoot, string label)
    {
        Directory.CreateDirectory(outputRoot);
        var destination = Path.Combine(outputRoot, "existing.bin");
        var original = new byte[] { 0x11, 0x22, 0x33 };
        var replacement = new byte[] { 0xA1, 0xB2, 0xC3, 0xD4 };
        File.WriteAllBytes(destination, original);

        var method = codecType.GetMethod(
            "WriteExtractionPathAtomically",
            BindingFlags.NonPublic | BindingFlags.Static);
        if (method is null)
        {
            throw new InvalidOperationException($"{label}.WriteExtractionPathAtomically was not found.");
        }

        Action<string> failingWriter = temporaryPath =>
        {
            File.WriteAllBytes(temporaryPath, [0xFE, 0xED]);
            throw new IOException("Intentional self-test failure before commit.");
        };

        try
        {
            InvokePrivateMethod(method, [outputRoot, destination, true, failingWriter]);
            throw new InvalidOperationException($"{label} atomic writer did not propagate a write failure.");
        }
        catch (IOException ex) when (ex.Message.Contains("Intentional self-test failure", StringComparison.Ordinal))
        {
            // The failing writer must leave the previous destination intact.
        }

        AssertBytes(original, File.ReadAllBytes(destination), $"{label} damaged the previous file after a failed write.");

        Action<string> successfulWriter = temporaryPath => File.WriteAllBytes(temporaryPath, replacement);
        InvokePrivateMethod(method, [outputRoot, destination, true, successfulWriter]);
        AssertBytes(replacement, File.ReadAllBytes(destination), $"{label} did not atomically replace the destination.");

        var leftoverTemps = Directory.GetFiles(outputRoot, "*.tmp", SearchOption.TopDirectoryOnly);
        Assert(leftoverTemps.Length == 0, $"{label} left temporary extraction files behind.");
    }

    private static void InvokePrivateMethod(MethodInfo method, object?[] arguments)
    {
        try
        {
            method.Invoke(null, arguments);
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
            throw;
        }
    }

    private static void AssertArchivePathMethod(Type codecType, string outputRoot, string label)
    {
        var method = codecType.GetMethod(
            "GetSafeOutputPath",
            BindingFlags.NonPublic | BindingFlags.Static);

        if (method is null)
        {
            throw new InvalidOperationException($"{label}.GetSafeOutputPath was not found.");
        }

        var valid = InvokePathMethod(method, outputRoot, "folder\\asset.bin");
        var expected = Path.GetFullPath(Path.Combine(outputRoot, "folder", "asset.bin"));
        Assert(PathComparer.Equals(valid, expected), $"{label} did not resolve a valid relative path.");

        ExpectInvalidData(
            () => InvokePathMethod(method, outputRoot, "..\\outside.bin"),
            $"{label} did not reject parent traversal.");
        ExpectInvalidData(
            () => InvokePathMethod(method, outputRoot, "CON.txt"),
            $"{label} did not reject Windows reserved names.");
    }

    private static string InvokePathMethod(MethodInfo method, string root, string relativePath)
    {
        try
        {
            return (string)(method.Invoke(null, [root, relativePath])
                ?? throw new InvalidOperationException("Archive path helper returned null."));
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
            throw;
        }
    }

    private static void TestRestoreAndReplenish(string testRoot)
    {
        var workRoot = Path.Combine(testRoot, "work");
        var formatRoot = Path.Combine(workRoot, "test-format");
        var newRoot = Path.Combine(formatRoot, "new");
        var originalRoot = Path.Combine(formatRoot, "orig");
        var metadataRoot = Path.Combine(formatRoot, "metadata");
        var outputRoot = Path.Combine(testRoot, "restore-output");

        Directory.CreateDirectory(newRoot);
        Directory.CreateDirectory(originalRoot);
        Directory.CreateDirectory(metadataRoot);

        var newSource = Path.Combine(newRoot, "new-source.bin");
        var originalSource = Path.Combine(originalRoot, "original-source.bin");
        var newPayload = new byte[] { 0x10, 0x20, 0x30, 0x40, 0x50 };
        var originalPayload = new byte[] { 0xA1, 0xB2, 0xC3, 0xD4 };
        File.WriteAllBytes(newSource, newPayload);
        File.WriteAllBytes(originalSource, originalPayload);

        var newCreation = new DateTime(2020, 2, 3, 4, 5, 6, DateTimeKind.Utc);
        var newWrite = new DateTime(2021, 3, 4, 5, 6, 8, DateTimeKind.Utc);
        var originalCreation = new DateTime(2019, 5, 6, 7, 8, 10, DateTimeKind.Utc);
        var originalWrite = new DateTime(2022, 6, 7, 8, 9, 12, DateTimeKind.Utc);

        File.SetCreationTimeUtc(newSource, newCreation);
        File.SetLastWriteTimeUtc(newSource, newWrite);
        File.SetCreationTimeUtc(originalSource, originalCreation);
        File.SetLastWriteTimeUtc(originalSource, originalWrite);

        File.WriteAllText(
            Path.Combine(metadataRoot, "new-source.json"),
            JsonSerializer.Serialize(new { OriginalRelativePath = "restored\\new.bin" }));
        File.WriteAllText(
            Path.Combine(metadataRoot, "original-source.json"),
            JsonSerializer.Serialize(new { OriginalRelativePath = "restored\\original.bin" }));

        var newDestination = Path.Combine(outputRoot, "restored", "new.bin");
        Directory.CreateDirectory(Path.GetDirectoryName(newDestination)!);
        File.WriteAllBytes(newDestination, [0xFF, 0xEE, 0xDD]);

        var summary = Restorer.RestoreWithReplenish(workRoot, outputRoot);
        Assert(summary.Total == 2, $"Unexpected restore total: {summary.Total}.");
        Assert(summary.Restored == 1, $"Expected one restored file, got {summary.Restored}.");
        Assert(summary.Replenished == 1, $"Expected one replenished file, got {summary.Replenished}.");
        Assert(summary.Failed == 0, $"Restore reported {summary.Failed} failures.");

        AssertBytes(newPayload, File.ReadAllBytes(newDestination), "Restored file content mismatch.");
        var originalDestination = Path.Combine(outputRoot, "restored", "original.bin");
        AssertBytes(originalPayload, File.ReadAllBytes(originalDestination), "Replenished file content mismatch.");

        AssertTimeClose(newCreation, File.GetCreationTimeUtc(newDestination), "Restored creation time was not preserved.");
        AssertTimeClose(newWrite, File.GetLastWriteTimeUtc(newDestination), "Restored last-write time was not preserved.");
        AssertTimeClose(originalCreation, File.GetCreationTimeUtc(originalDestination), "Replenished creation time was not preserved.");
        AssertTimeClose(originalWrite, File.GetLastWriteTimeUtc(originalDestination), "Replenished last-write time was not preserved.");

        var leftoverTemps = Directory.GetFiles(outputRoot, "*.tmp", SearchOption.AllDirectories);
        Assert(leftoverTemps.Length == 0, "Temporary restore files were left behind.");
    }

    private static void ExpectInvalidData(Action action, string failureMessage)
    {
        try
        {
            action();
        }
        catch (InvalidDataException)
        {
            return;
        }

        throw new InvalidOperationException(failureMessage);
    }

    private static void AssertBytes(byte[] expected, byte[] actual, string message)
    {
        Assert(expected.AsSpan().SequenceEqual(actual), message);
    }

    private static void AssertTimeClose(DateTime expected, DateTime actual, string message)
    {
        if (Math.Abs((expected - actual).TotalSeconds) > 2)
        {
            throw new InvalidOperationException($"{message} Expected={expected:O}, Actual={actual:O}.");
        }
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static StringComparer PathComparer =>
        OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
}
