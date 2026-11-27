# ChihanaGBK v2.4.1.5

`version.dll` 代理 DLL，供 Kaguya 引擎遊戲使用。以已測試可讀取 Kaguya 5.7／5.8 的 v2.4.1.1（commit `ed433f6`）為基礎，新增可獨立控制編碼介入的 `EncodingEnabled` 開關。

## 編碼與字型開關

`ChihanaGBK.ini` 的 `[Options]` 區塊提供 `EncodingEnabled`：

- `EncodingEnabled=1`：正常 ChihanaGBK 模式。維持一般文字 `CP_THREAD_ACP` 轉為 CP932、進入五個 params ReaderWrap 欄位時使用 CP936，並保留 `ExtraSites`、GBK 標點 `】【` 與全形空白修補。
- `EncodingEnabled=0`：原版文字模式。不安裝 `WideCharToMultiByte` 編碼掛鉤、不掃描 params、不掛 ReaderWrap、不執行 `ExtraSites`，也不修改 GBK 標點或全形空白位元組。文字解碼完全交由遊戲原本的處理流程；DLL 只替換字型名稱，並保留遊戲原本的 `lfCharSet`。

字型名稱 `Face`、粗細 `Weight`、高度 `HeightScale` 在兩種模式均可設定。`Charset` 則有特別規則：只有 `EncodingEnabled=1` 時才會覆寫遊戲字集；`EncodingEnabled=0` 時保留遊戲原本的 `lfCharSet`，忽略 INI 的 `Charset`。例如只替換日文原版遊戲字型：

```ini
[Options]
EncodingEnabled=0

[Font]
Face=你安裝的字型名稱
; Charset 在 EncodingEnabled=0 時不套用，保留遊戲原始字集
```

`Charset` 是 Windows `LOGFONT.lfCharSet` 字型字集代碼，不是文字轉碼頁：`128` = 日文 `SHIFTJIS_CHARSET`、`136` = 繁體中文 `CHINESEBIG5_CHARSET`、`134` = 簡體中文 `GB2312_CHARSET`。只有 `EncodingEnabled=1` 時套用；關閉時不覆寫遊戲原始字集。

## v2.4.1.5 修正

- 修正 v2.4.1.4 在 `EncodingEnabled=0` 時仍無條件覆寫 `LOGFONT.lfCharSet` 的問題。
- 關閉編碼時，`My_CFIA` 保留遊戲傳入的原始 `lfCharSet`，仍替換 `lfFaceName`，並保留 `Weight`／`HeightScale` 設定。
- INI 的 `Charset` 只在 `EncodingEnabled=1` 時生效；關閉模式不必再為日文遊戲手動把 134 改成 128。

## 本階段修改

1. 以 v2.4.1.1（`ed433f6`）為基礎新增 `[Options] EncodingEnabled`，預設為 `1`，避免既有使用者未新增設定時失去原本 ChihanaGBK 功能。
2. `EncodingEnabled=0` 時不安裝 `WideCharToMultiByte` IAT hook，也不執行 params 掃描、五個 ReaderWrap 呼叫點、`ExtraSites` 或 GBK 標點／全形空白修補。
3. 字型掛鉤始終啟用：主程式仍掛載 `CreateFontIndirectA`，並透過 `LoadLibraryA` 掛鉤後續載入模組的字型 API。關閉編碼不會關閉字型修改。
4. `EncodingEnabled=1` 時，原本 v2.4.1.1 的五個 params 欄位、FLS／ReaderWrap 組語、掃描器呼叫、GBK 位元組修補與 `ExtraSites` 邏輯均維持不變。
5. 不修改 `kaguya_scan.h`、`pxxxx.dat`／`message.dat` 的資料讀取程式、版本 API 匯出或延後初始化流程。

## 安裝

將編譯後的 `version.dll` 與 `ChihanaGBK.ini` 放在遊戲 `Start.exe` 同一個資料夾。修改 INI 後重新啟動遊戲即可生效。

## 版本相容性與驗證

本版以使用者曾實測可正常讀取 Kaguya 5.7、5.8 的 v2.4.1.1 原始碼為底稿。啟用模式的資料讀取與轉換邏輯應與該版本一致；仍需由 GitHub Actions 編譯後，以 `EncodingEnabled=1` 回歸測試 5.7／5.8，以及以 `EncodingEnabled=0` 確認原版文字流程與自訂字型。靜態檢查不能取代實機驗證。

## 建置

沿用專案既有 Windows x86／MinGW 建置流程。此 DLL 必須編譯為 32 位元 x86，並保留原本三個 ANSI 匯出：

- `GetFileVersionInfoA`
- `GetFileVersionInfoSizeA`
- `VerQueryValueA`

`kaguya_scan.h` 未修改，請保留 `_ai_fix` 分支原檔；本 ZIP 不包含該檔，避免不必要地覆蓋掃描器。
