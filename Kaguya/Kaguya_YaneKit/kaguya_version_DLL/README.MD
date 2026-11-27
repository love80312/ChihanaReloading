# ChihanaGBK v2.4.1.1

`version.dll` 代理 DLL，供 Kaguya 引擎遊戲在簡中區域（Locale Emulator 轉簡中）下顯示指定 GBK 文字；不修改原版 EXE。

## 本階段修改

1. `DllMain` 僅執行 `DisableThreadLibraryCalls`。系統 `version.dll` 載入、INI 讀取、IAT 修補及 EXE 掃描延後到第一次呼叫代理版本 API 時，並由 `InitOnceExecuteOnce` 保證只初始化一次。
2. ReaderWrap 不再使用跨執行緒共用的返回位址與全域深度。每次呼叫的返回位址改存於該執行緒的 FLS 堆疊，支援多執行緒及巢狀呼叫；FLS 狀態以執行緒結束 callback 回收。
3. 自動 CP936(GBK) 套用原本已驗證的五個 params 欄位：`GameTitle`、`DisplayTitle`、`Brand`、`DefaultFirst`、`DefaultSecond`。一般資源字串仍維持 ACP 932。v2.4.1.0 曾將範圍縮小為兩個姓名欄位，造成 GBK 遊戲標題顯示錯誤；本版已恢復五個欄位的行為。
4. 保留原有字型 hook、標點 `】【` 修補、全形空白修補、Debug 記錄，以及空白預設的 `ExtraSites` 進階手動設定。

## 安裝

將編譯後的 `version.dll` 與 `ChihanaGBK.ini` 放在遊戲 `Start.exe` 同一個資料夾。依需求修改 INI 後重新啟動遊戲。

## 版本相容性

本次沒有修改 `kaguya_scan.h` 的掃描簽章、Reader 定位及欄位辨識邏輯，也沒有修改 `pxxxx.dat` / `message.dat` 的讀取程式。已知基準是使用者已實測可正常讀取 Kaguya 5.7、5.8 的 Claude v2.4 版本；本次變更仍須以新編譯 DLL 在相同遊戲樣本重新回歸測試，不能僅憑靜態檢查宣稱已完成 5.7 / 5.8 實機驗證。

## 建置

沿用專案既有 Windows x86 / MinGW 建置流程。此 DLL 必須編譯為 32 位元 x86，並保留原本三個 ANSI 匯出：

- `GetFileVersionInfoA`
- `GetFileVersionInfoSizeA`
- `VerQueryValueA`

`kaguya_scan.h` 未修改，請沿用分支中的原檔。

本次提供的替換檔不包含 `kaguya_scan.h`。請保留 `_ai_fix` 分支原檔，不要以其他版本覆蓋。
