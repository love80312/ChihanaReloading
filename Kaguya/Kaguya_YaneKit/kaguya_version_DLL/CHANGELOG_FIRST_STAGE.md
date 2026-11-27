# v2.4.1.0 第一階段修改紀錄

## 修改內容

- `DllMain` 不再執行 `LoadLibraryA`、INI 讀取、EXE 掃描或 IAT / 程式碼修補；改由代理 API 首次呼叫時透過 `InitOnceExecuteOnce` 執行初始化。
- 移除全域 `g_savedRet` 與 `g_gbkDepth`。
- ReaderWrap 的返回位址存入執行緒專屬 FLS 堆疊，不使用可能被原 reader 函式序言覆寫的 caller-stack 暫存區；保留原 reader 的 EAX/ECX/EDX 輸入暫存器、EAX/EDX 返回值及 cdecl / callee-clean 堆疊效果。
- 使用 `FlsAlloc` / `FlsGetValue` / `FlsSetValue` 管理每個執行緒的 reader 返回位址堆疊，並以 FLS callback 回收執行緒狀態；FLS 建立失敗時跳過 ReaderWrap hook，不中止其他字型與標點修補。
- 自動 ReaderWrap hook 限定掃描器 `site[3]`、`site[4]`，即 `DefaultFirst`、`DefaultSecond`。
- `ExtraSites` 保留作為明確手動追加項目；預設空白，不會額外套用。
- 更新版本資源、INI 註解及 README。

## 未修改

- `kaguya_scan.h` 全部掃描邏輯與簽章。
- `pxxxx.dat`、`message.dat` 讀取流程。
- 原有三個 ANSI DLL 匯出名稱與呼叫介面。
- 字型、標點、全形空白與 Debug 功能。

## 驗證狀態

- x86 COFF 組語片段已用 Clang 17 組譯成功。
- 尚未使用專案原本的 Windows MinGW 環境完成 DLL 全量編譯；目前執行環境缺少 MinGW-w64 Windows SDK headers / linker。
- 尚未在 Kaguya 5.7 / 5.8 遊戲內執行新 DLL。必須先通過 GitHub Actions x86 build，再用原本已成功的 5.7、5.8 樣本驗證 `pxxxx.dat`、`message.dat`、姓名欄位、標題、字型及視窗顯示。

`kaguya_scan.h` 不包含於替換包；請保留 `_ai_fix` 分支原檔。此次刻意不改動掃描器，避免改變已驗證的 5.7 / 5.8 定位結果。
