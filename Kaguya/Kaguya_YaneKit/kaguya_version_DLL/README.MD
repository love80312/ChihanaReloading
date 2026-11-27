# ChihanaGBK v2.4.2.0

Kaguya 引擎用 x86 `version.dll` 代理 DLL。

本版以 v2.4.1.9 為基礎，固定使用 CP936，不提供 Big5 950 或 CodePage 切換。此版本只增加 `WideCharToMultiByte`（WCTMB）目的緩衝區診斷，不改變轉碼參數、目的緩衝區內容、API 回傳值或字型行為。

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

每筆 WCTMB 診斷會記錄：

- `caller`：呼叫返回位址。
- `rva`：返回位址相對於主 EXE 的 RVA；`FFFFFFFF` 代表呼叫位置不在主 EXE 映像內。
- `kind`：`QUERY` 是需求長度查詢；`CONVERT` 是實際輸出轉換。
- `paired`：是否與同一執行緒、來源字串指標及轉換參數相同的查詢配對。
- `query_rva`：配對到的查詢返回位址 RVA。
- `need`：查詢得到的需求容量。
- `sl`：遊戲實際提供給 WCTMB 的目的緩衝區容量。
- `gap`：`need - sl`；正數代表容量不足，0 或負數代表容量足夠。
- `dst_req`：目的緩衝區快照長度，等於 `min(sl, 96)`。
- `pre_read`／`pre_ok`／`pre`：呼叫前讀取的 bytes 數、是否完整讀取及原始位元組。
- `post_read`／`post_ok`／`post`：呼叫後讀取的 bytes 數、是否完整讀取及原始位元組。
- `changed`：前後快照可比較範圍內，內容不同的 byte 數；`-1` 代表無法比較。

當 `wl=-1` 且輸出指標為 NULL、容量為 0 時，WideCharToMultiByte 的需求長度包含結尾 NUL。實際轉換容量不足時，API 會回傳 0，並以 `GetLastError()` 提供錯誤資訊；錯誤碼 122 代表輸出容量不足。

只有在 `Debug=2`、ReaderWrap 範圍內且目的指標與容量有效時，才會擷取前後快照。快照使用 `ReadProcessMemory` 讀取目前程序，讀取長度不超過遊戲傳入的 `sl`，並限制最多 96 bytes。若記憶體區段不可讀，會記錄 `<unreadable>`，不直接解參考該位址。

本診斷版不寫入目的緩衝區、不擴大容量、不補寫 NUL，不改變 WCTMB 的原始參數、回傳值或 LastError。前後快照只用來確認 API 呼叫期間目的緩衝區是否發生變化，不應單憑快照內容認定轉換結果有效。查詢與轉換只有在同一執行緒、來源字串指標及轉換參數相符時才會配對。

## 原有功能

本版保留 UTF-8 Debug Log、PID／TID、LastError 保護、IAT Hook、FLS、ReaderWrap、五個 params 欄位掃描、ExtraSites、GBK 標點與空白修補、字型 Hook、動態模組字型 Hook、InitOnce 及三個 ANSI 版本 API 匯出。

`kaguya_scan.h` 不在本次修改範圍。

## 建置與測試

目標為 Windows x86 Release。使用 GitHub Actions 的手動 `workflow_dispatch` 重新建置；安裝時將 `version.dll` 與原有 `ChihanaGBK.ini` 放在遊戲 `Start.exe` 同一資料夾。

本版原始碼更新後，請使用 GitHub Actions 手動建置 x86 Release DLL。建置成功後，使用本機 `Debug=2` 設定進行目標遊戲實測，提供新的 `ChihanaGBK.log`。本次快照僅供診斷，尚未修改緩衝區不足的處理方式。
