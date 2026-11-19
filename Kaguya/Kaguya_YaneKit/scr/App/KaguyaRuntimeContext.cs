// ============================================================================
// KaguyaRuntimeContext.cs
// 运行时上下文: 保存一次会话所需的全部全局状态
// ============================================================================
using Kaguya_YaneKit.Formats.Params;

namespace Kaguya_YaneKit.App;

public sealed class KaguyaRuntimeContext
{
    public string ToolDirectory { get; init; } = "";
    public string GameRoot { get; init; } = "";
    public string WorkDirectory { get; init; } = "";
    public string? ParamsPath { get; init; }
    public string? TblstrArchivePath { get; init; }
    public ParamsDatDocument? Params { get; init; }
    public byte[]? LinkEncryptionKey { get; init; }
    public string? ParamsVersion => Params is null ? null : ParamsDatCodec.DescribeVersion(Params.Header);

    // 只有實際找到對應檔案時才判定引擎類型，避免「缺少 params.dat」
    // 被錯誤地當成 TBLSTR 系列。
    public bool IsParamsFamily => ParamsPath is not null;
    public bool IsTblstrFamily => ParamsPath is null && TblstrArchivePath is not null;

    public string EngineProfile => IsParamsFamily
        ? "Params系"
        : IsTblstrFamily
            ? "TBLSTR系"
            : "未識別";

    public static KaguyaRuntimeContext Create(
        string? gameRoot = null,
        string? workDirectory = null,
        string? paramsPath = null)
    {
        var toolDirectory = AppContext.BaseDirectory;
        var resolvedParamsPath = ResolveParamsPath(gameRoot, paramsPath);
        var resolvedGameRoot = ResolveGameRoot(gameRoot, resolvedParamsPath);
        var resolvedWorkDirectory = Path.GetFullPath(
            workDirectory ?? Path.Combine(toolDirectory, "workplace"));
        var resolvedTblstrArchivePath = ResolveTblstrArchivePath(resolvedGameRoot);

        ParamsDatDocument? document = null;
        byte[]? linkKey = null;

        if (resolvedParamsPath is not null)
        {
            document = new ParamsDatCodec().Read(File.ReadAllBytes(resolvedParamsPath));
            linkKey = Convert.FromBase64String(document.GameSystem.RawBlob.LinkXorKeyBase64);
        }

        return new KaguyaRuntimeContext
        {
            ToolDirectory = toolDirectory,
            GameRoot = resolvedGameRoot,
            WorkDirectory = resolvedWorkDirectory,
            ParamsPath = resolvedParamsPath,
            TblstrArchivePath = resolvedTblstrArchivePath,
            Params = document,
            LinkEncryptionKey = linkKey
        };
    }

    // 優先級: 命令列 --game-root > params.dat 所在目錄 > CWD
    private static string ResolveGameRoot(string? gameRoot, string? paramsPath)
    {
        if (!string.IsNullOrWhiteSpace(gameRoot))
        {
            return Path.GetFullPath(gameRoot);
        }

        if (!string.IsNullOrWhiteSpace(paramsPath))
        {
            return Path.GetDirectoryName(Path.GetFullPath(paramsPath))
                ?? Environment.CurrentDirectory;
        }

        return Environment.CurrentDirectory;
    }

    // 優先級: 命令列指定 > game root > CWD > 工具目錄
    private static string? ResolveParamsPath(string? gameRoot, string? paramsPath)
    {
        if (!string.IsNullOrWhiteSpace(paramsPath))
        {
            var fullParamsPath = Path.GetFullPath(paramsPath);
            if (!File.Exists(fullParamsPath))
            {
                throw new FileNotFoundException(
                    $"params.dat was specified but does not exist: {fullParamsPath}",
                    fullParamsPath);
            }

            return fullParamsPath;
        }

        var candidates = new List<string>();

        if (!string.IsNullOrWhiteSpace(gameRoot))
        {
            candidates.Add(Path.Combine(Path.GetFullPath(gameRoot), "params.dat"));
        }

        candidates.Add(Path.Combine(Environment.CurrentDirectory, "params.dat"));
        candidates.Add(Path.Combine(AppContext.BaseDirectory, "params.dat"));

        return candidates.FirstOrDefault(File.Exists);
    }

    private static string? ResolveTblstrArchivePath(string gameRoot)
    {
        var root = Path.GetFullPath(gameRoot);
        var candidates = new[]
        {
            Path.Combine(root, "tblstr.arc"),
            Path.Combine(root, "TBLSTR.ARC"),
            Path.Combine(root, "arc", "tblstr.arc"),
            Path.Combine(root, "ARC", "TBLSTR.ARC")
        };

        return candidates.FirstOrDefault(File.Exists);
    }
}
