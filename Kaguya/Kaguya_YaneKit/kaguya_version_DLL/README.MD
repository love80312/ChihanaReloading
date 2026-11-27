# ChihanaGBK v2.4.1.3

`version.dll` 代理 DLL，供 Kaguya 引擎遊戲在簡中區域（Locale Emulator 轉簡中）下顯示指定 GBK 文字；不修改原版 EXE。

## 編碼與字型獨立設定

`ChihanaGBK.ini` 最上方的 `[Options]` 區塊提供 `EncodingEnabled` 開關；預設 `1`，保留原本 CP936/GBK 模式。

- `EncodingEnabled=1`：一般 `CP_THREAD_ACP` 轉碼維持 CP932；只有進入五個 params ReaderWrap 欄位時切換 CP936。保留 ExtraSites、GBK 標點及全形空白修補。
- `EncodingEnabled=0`：不掃描或掛載 params ReaderWrap，不執行 ExtraSites 與 GBK 標點／全形空白位元組修補；但 `WideCharToMultiByte` hook 仍保留，所有一般 `CP_THREAD_ACP` 轉碼固定為 CP932，避免日文劇本失去原本的 Shift-JIS 路徑。字型 hook 仍會運作。

日文版只想更換字型時，設定範例：

```ini
[Options]
EncodingEnabled=0

[Font]
Face=你安裝的日文字型名稱
Charset=128
```

`Charset` 是 Windows `LOGFONT.lfCharSet` 字型字集代碼，不是文字轉碼頁：日文 `128`（SHIFTJIS_CHARSET）、繁體中文 `136`（CHINESEBIG5_CHARSET）、簡體中文 `134`（GB2312_CHARSET）。`EncodingEnabled=0` 不會自行切換 Windows 地區設定，日文版仍應用日文系統地區設定或 Locale Emulator（日文）啟動。

## 本階段修改

1. `DllMain` 僅執行 `DisableThreadLibraryCalls`。系統 `version.dll` 載入、INI 讀取、IAT 修補及 EXE 掃描延後到第一次呼叫代理版本 API 時，並由 `InitOnceExecuteOnce` 保證只初始化一次。
2. ReaderWrap 不再使用跨執行緒共用的返回位址與全域深度。每次呼叫的返回位址改存於該執行緒的 FLS 堆疊，支援多執行緒及巢狀呼叫；FLS 狀態以執行緒結束 callback 回收。
3. `EncodingEnabled=1` 時，自動 CP936(GBK) 套用五個 params 欄位：`GameTitle`、`DisplayTitle`、`Brand`、`DefaultFirst`、`DefaultSecond`。一般資源字串維持原系統 ACP。`EncodingEnabled=0` 時只略過 CP936 params/GBK 專用修補，一般文字仍保留 CP932。
4. 字型 hook 不受 `EncodingEnabled` 影響；`Face`、`Charset`、`Weight`、`HeightScale` 在兩種模式都有效。
5. `EncodingEnabled=1` 時保留原有標點 `】【` 修補、全形空白修補、Debug 記錄及 `ExtraSites` 進階手動設定；設為 0 時不執行這些 GBK 專用文字修補，但一般 CP932 轉碼仍啟用。

## 安裝

將編譯後的 `version.dll` 與 `ChihanaGBK.ini` 放在遊戲 `Start.exe` 同一個資料夾。依需求修改 INI 後重新啟動遊戲。

## 版本相容性

本版沒有修改 `kaguya_scan.h` 的掃描簽章、Reader 定位及欄位辨識邏輯，也沒有修改 `pxxxx.dat` / `message.dat` 的讀取程式。已知基準是使用者已實測可正常讀取 Kaguya 5.7、5.8 的 v2.4.1.1 DLL；本版在 `EncodingEnabled=1` 時保留相同的資料讀取與編碼掛鉤路徑，仍須以新編譯 DLL 實機回歸測試。`EncodingEnabled=0` 則關閉 CP936/GBK params 修補，保留一般 CP932 轉碼與純字型模式；仍須以日文版 EXE 實測。

## 建置

沿用專案既有 Windows x86 / MinGW 建置流程。此 DLL 必須編譯為 32 位元 x86，並保留原本三個 ANSI 匯出：

- `GetFileVersionInfoA`
- `GetFileVersionInfoSizeA`
- `VerQueryValueA`

`kaguya_scan.h` 未修改，請沿用 `_ai_fix` 分支中的原檔。本次提供的替換檔不包含 `kaguya_scan.h`，請勿以其他版本覆蓋。
