# ChihanaGBK v2.4.1.9

Kaguya 引擎用 x86 `version.dll` 代理 DLL。

本版以 v2.4.1.8 為基礎，固定使用 CP936，不提供 Big5 950 或 CodePage 切換。此版本只增加 `WideCharToMultiByte`（WCTMB）呼叫點及查詢／實際轉換配對診斷，不改變轉碼參數、緩衝區、API 回傳值或字型行為。

## 編碼行為

- ReaderWrap 範圍內的五個 params 欄位使用 CP936（GBK）。
- ReaderWrap 範圍外的一般文字維持 CP932。
- `params.dat` 保持原始日文資料；DLL 僅在執行時轉換五個指定欄位。
- `message.dat` 的匯入與回存編碼由 Kaguya_YaneKit 工具負責；DLL 不直接重新編碼該檔案。
- `】【`、全形空白與半形加全形空白保留 GBK 位元組修補。
- 不使用 `[Options] CodePage`；文字轉碼頁固定為 CP936。

## INI 設定

`ChihanaGBK.ini` 與遊戲執行檔、`version.dll` 放在同一資料夾。

```ini
[Options]
EncodingEnabled=1
Debug=2

[Font]
Face=LXGW Wenkai TC Medium
Charset=134
Weight=0
HeightScale=100

[Params]
ExtraSites=
```

### EncodingEnabled

- `1`：啟用 CP932／CP936 文字處理、五個 params ReaderWrap、ExtraSites 與 GBK 位元組修補。
- `0`：停用文字轉碼、params 掃描、ReaderWrap、ExtraSites 與文字位元組修補，但仍保留字型名稱修改。

### Charset

`Charset=134` 是 Windows `LOGFONT.lfCharSet` 字型字集代碼，不是轉碼頁。

- `128`：日文字集
- `136`：繁體中文字集
- `134`：簡體中文字集

它只控制字型字集；文字轉碼目標固定為 CP936。`EncodingEnabled=0` 時保留遊戲原始 `lfCharSet`，只替換字型名稱。

## WCTMB 診斷（Debug=2）

每筆 WCTMB 診斷會額外記錄：

- `caller`：呼叫返回位址。
- `rva`：返回位址相對於主 EXE 的 RVA；`FFFFFFFF` 代表呼叫位置不在主 EXE 映像內。
- `kind`：`QUERY` 是需求長度查詢；`CONVERT` 是實際輸出轉換。
- `paired`：是否與同一執行緒緊接在前、且來源字串與轉換參數相同的查詢配對。
- `query_rva`：配對到的查詢返回位址 RVA。
- `need`：查詢得到的需求容量。
- `sl`：遊戲實際提供的輸出容量。
- `gap`：`need - sl`；正數代表容量不足，0 或負數代表容量足夠。

當 `wl=-1` 且輸出指標為 NULL、容量為 0 時，WideCharToMultiByte 的需求長度包含結尾 NUL。若實際轉換容量少 1 byte，API 會回傳 0，並設定錯誤碼 122（`ERROR_INSUFFICIENT_BUFFER`）。

本診斷版不擅自擴張或替換遊戲提供的緩衝區，不補寫 NUL，不改變 API 回傳值或原始參數。查詢與轉換只有在同一執行緒、來源字串指標及轉換參數相符時才會配對；未配對不代表轉換一定失敗。

## 原有功能

本版保留 UTF-8 Debug Log、PID／TID、LastError 保護、IAT Hook、FLS、ReaderWrap、五個 params 欄位掃描、ExtraSites、GBK 標點與空白修補、字型 Hook、動態模組字型 Hook、InitOnce 及三個 ANSI 版本 API 匯出。

`kaguya_scan.h` 不在本次修改範圍。

## 建置與測試

目標為 Windows x86 Release。使用專案既有 GitHub Actions 手動建置；安裝時將 `version.dll` 與 `ChihanaGBK.ini` 放在遊戲 `Start.exe` 同一資料夾。

本診斷版尚未經 GitHub Actions 編譯及目標遊戲實機測試。請設定 `Debug=2` 執行遊戲，重現有 WCTMB `122` 的情況後，提供新的 `ChihanaGBK.log`。
