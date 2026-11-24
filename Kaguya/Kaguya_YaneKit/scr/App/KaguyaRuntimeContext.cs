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
        string? paramsPath = null,
        bool loadParams = true)
    {
        var toolDirectory = AppContext.BaseDirectory;
        var resolvedParamsPath = ResolveParamsPath(
            gameRoot,
            paramsPath,
            validateExplicitPath: loadParams);
        var resolvedGameRoot = ResolveGameRoot(gameRoot, resolvedParamsPath);
        var resolvedWorkDirectory = Path.GetFullPath(
            workDirectory ?? Path.Combine(toolDirectory, "workplace"));
        var resolvedTblstrArchivePath = ResolveTblstrArchivePath(resolvedGameRoot);

        ParamsDatDocument? document = null;
        byte[]? linkKey = null;

        // params.dat 的格式解析和 LINK key 擷取延後到真正需要內容的命令。
        // list / verify / raw extract 等命令只需要保留解析後的路徑，不應讀取檔案。
        if (loadParams && resolvedParamsPath is not null)
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

    // 優先級:
    //   1. 命令列明確指定 --params
    //   2. 明確指定 --game-root 時，只搜尋該遊戲根目錄
    //   3. 未指定 game root 時，才依序搜尋 CWD 與工具目錄
    //
    // 不可在明確指定 game root 卻找不到 params.dat 時，
    // 回頭讀取其他遊戲的檔案。
    private static string? ResolveParamsPath(
        string? gameRoot,
        string? paramsPath,
        bool validateExplicitPath)
    {
        if (!string.IsNullOrWhiteSpace(paramsPath))
        {
            var fullParamsPath = Path.GetFullPath(paramsPath);

            if (validateExplicitPath && !File.Exists(fullParamsPath))
            {
                throw new FileNotFoundException(
                    $"params.dat was specified but does not exist: {fullParamsPath}",
                    fullParamsPath);
            }

            return fullParamsPath;
        }

        if (!string.IsNullOrWhiteSpace(gameRoot))
        {
            var explicitGameRoot = Path.GetFullPath(gameRoot);
            var gameRootCandidate = Path.Combine(explicitGameRoot, "params.dat");

            return File.Exists(gameRootCandidate)
                ? gameRootCandidate
                : null;
        }

        var candidates = new[]
        {
            Path.Combine(Environment.CurrentDirectory, "params.dat"),
            Path.Combine(AppContext.BaseDirectory, "params.dat")
        };

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
