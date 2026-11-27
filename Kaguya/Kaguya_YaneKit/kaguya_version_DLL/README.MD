# ChihanaGBK v2.4.1.2

`version.dll` 代理 DLL，供 Kaguya 引擎遊戲在簡中區域（Locale Emulator 轉簡中）下顯示指定 GBK 文字；不修改原版 EXE。

## 編碼與字型獨立設定

`[Options] EncodingEnabled=1`（預設）會維持原本 CP936/GBK 行為。

設為 `EncodingEnabled=0` 時，DLL 不會執行 CP936 `WideCharToMultiByte` 攔截、params `ReaderWrap`／`ExtraSites`，也不會修改 GBK 專用的標點與全形空白位元組；**字型 hook 仍會運作**，`Face`、`Charset`、`Weight`、`HeightScale` 都可照常使用。

日文版只想更換字型時，設定範例：

請同時使用日文系統地區設定或 Locale Emulator（日文）啟動遊戲。`EncodingEnabled=0` 只停用 DLL 的 CP936/GBK 覆寫，不會自行把 Windows 系統碼頁切換成 CP932。

```ini
[Font]
Face=你安裝的日文字型名稱
Charset=128

[Options]
EncodingEnabled=0
```

`Charset=128` 是日文 `SHIFTJIS_CHARSET`；繁體中文 Big5 為 `136`，簡體中文 GB2312 為 `134`。這些是字型字集代碼，不是 CP932／CP950／CP936 轉碼頁。

## 本階段修改

1. `DllMain` 僅執行 `DisableThreadLibraryCalls`。系統 `version.dll` 載入、INI 讀取、IAT 修補及 EXE 掃描延後到第一次呼叫代理版本 API 時，並由 `InitOnceExecuteOnce` 保證只初始化一次。
2. ReaderWrap 不再使用跨執行緒共用的返回位址與全域深度。每次呼叫的返回位址改存於該執行緒的 FLS 堆疊，支援多執行緒及巢狀呼叫；FLS 狀態以執行緒結束 callback 回收。
3. `EncodingEnabled=1` 時，自動 CP936(GBK) 套用五個 params 欄位：`GameTitle`、`DisplayTitle`、`Brand`、`DefaultFirst`、`DefaultSecond`。一般資源字串維持原系統 ACP。`EncodingEnabled=0` 時，以上文字編碼處理全部略過。
4. 字型 hook 不受 `EncodingEnabled` 影響；`Face`、`Charset`、`Weight`、`HeightScale` 在兩種模式都有效。
5. `EncodingEnabled=1` 時保留原有標點 `】【` 修補、全形空白修補、Debug 記錄及 `ExtraSites` 進階手動設定；設為 0 時不執行這些 GBK 專用文字修補。

## 安裝

將編譯後的 `version.dll` 與 `ChihanaGBK.ini` 放在遊戲 `Start.exe` 同一個資料夾。依需求修改 INI 後重新啟動遊戲。

## 版本相容性

本版沒有修改 `kaguya_scan.h` 的掃描簽章、Reader 定位及欄位辨識邏輯，也沒有修改 `pxxxx.dat` / `message.dat` 的讀取程式。已知基準是使用者已實測可正常讀取 Kaguya 5.7、5.8 的 v2.4.1.1 DLL；本版在 `EncodingEnabled=1` 時保留相同的資料讀取與編碼掛鉤路徑，仍須以新編譯 DLL 實機回歸測試。`EncodingEnabled=0` 則是新增的日文純字型模式，尚待日文版 EXE 實測。

## 建置

沿用專案既有 Windows x86 / MinGW 建置流程。此 DLL 必須編譯為 32 位元 x86，並保留原本三個 ANSI 匯出：

- `GetFileVersionInfoA`
- `GetFileVersionInfoSizeA`
- `VerQueryValueA`

`kaguya_scan.h` 未修改，請沿用 `_ai_fix` 分支中的原檔。本次提供的替換檔不包含 `kaguya_scan.h`，請勿以其他版本覆蓋。
