// ============================================================================
// Program.cs
// 应用程序入口点, 将命令行参数转交给 KaguyaApp 调度器
// ============================================================================

using System.Text;
using Kaguya_YaneKit.App;

// 統一主控台輸出編碼，避免中文路徑與訊息出現亂碼。
Console.OutputEncoding = new UTF8Encoding(false);
Console.InputEncoding = new UTF8Encoding(false);

return KaguyaApp.Run(args);
