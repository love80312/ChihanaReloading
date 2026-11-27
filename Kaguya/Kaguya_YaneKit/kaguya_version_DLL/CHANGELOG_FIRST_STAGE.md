# v2.4.1.3 修正紀錄

## 修正原因

- v2.4.1.2 在 `EncodingEnabled=0` 時連 `WideCharToMultiByte` IAT hook 一併關閉，導致一般文字回到 Locale Emulator 的執行緒 ACP；在簡中 ACP 環境下，日文 `message.dat` 可能因此亂碼。

## 本版修正

- `WideCharToMultiByte` hook 在兩種模式都保留。
- 一般 `CP_THREAD_ACP` 一律轉成 CP932；只有 `EncodingEnabled=1` 且位於五個 params ReaderWrap 欄位時才轉成 CP936。
- `EncodingEnabled=0` 仍完全略過 params 掃描、ReaderWrap、ExtraSites，以及 GBK 專用標點／全形空白修補。
- 字型 hook、動態載入模組字型 hook、Face/Charset/Weight/HeightScale 均保持啟用。
- INI 的 `[Options]` 移至第一個區塊，`EncodingEnabled=1` 成為最上方第一個設定值。

## 保留與驗證

- `kaguya_scan.h`、ReaderWrap 組語、FLS 狀態管理、InitOnce、版本 API 匯出及 `pxxxx.dat`／`message.dat` 資料讀取程式未修改。
- 本次功能修正集中在 CP_THREAD_ACP 對應邏輯與 WCTMB IAT hook 不再隨開關移除。
- 仍須用 `EncodingEnabled=0` 的日文 EXE 實測 message.dat，並用 `EncodingEnabled=1` 回歸測試 Kaguya 5.7／5.8。

---

# v2.4.1.2 修正紀錄

## 新增功能

- 新增 `[Options] EncodingEnabled=1` 開關，預設為 1，維持 v2.4.1.1 原本 CP936/GBK 行為。
- 設為 0 時略過 CP936 `WideCharToMultiByte`、五個 params `ReaderWrap`、`ExtraSites`、GBK 專用 `】【` 標點及全形空白位元組修補。
- `CreateFontIndirectA` 字型 hook 獨立保留；日文版可在關閉編碼功能時設定 `Charset=128` 修改字型。
- `Charset` 註解補上日文 128、繁體中文 136、簡體中文 134，並說明它們是 Windows 字型字集值，非轉碼頁。

## 保留與相容性

- `EncodingEnabled` 未設定時預設為 1，維持舊版行為。
- 不修改 `kaguya_scan.h`、FLS／ReaderWrap 實作（啟用時）、InitOnce、版本 API 匯出及字型 hook 本身。
- `EncodingEnabled=0` 時不執行 params 掃描或任何 CP936/GBK 文字位元組修補；不影響字型設定。
- `pxxxx.dat`／`message.dat` 的資料讀取程式未修改。

## 驗證狀態

- 目前已完成原始碼變更；仍須由 GitHub Actions 編譯，再以日文版 EXE 驗證 `EncodingEnabled=0` 的字型替換，並以原已測試的 5.7／5.8 樣本驗證 `EncodingEnabled=1` 回歸。
- 靜態檢查不能代替實機驗證。

---

# v2.4.1.1 修正紀錄

## 修正原因

- v2.4.1.0 將自動 CP936 ReaderWrap hook 限縮為 `DefaultFirst` / `DefaultSecond` 兩個預設姓名欄位。
- 實際測試 Kaguya 5.8 時，遊戲標題因此顯示錯誤；這是 CP936 套用範圍縮小造成的回歸，不是 `[SCR-PARAMS]v05.8` 標頭格式錯誤。

## 本版修正

- 恢復五個 params 文字欄位的自動 CP936：`GameTitle`、`DisplayTitle`、`Brand`、`DefaultFirst`、`DefaultSecond`。
- 保留 v2.4.1.0 的 `DllMain` 延後初始化、`InitOnceExecuteOnce` 及 FLS 每執行緒返回位址堆疊。
- 保留原有字型 hook、一般資源字串 ACP 932、`】【` 標點與全形空白修補、Debug 記錄及 `ExtraSites`。
- `kaguya_scan.h` 不修改；`pxxxx.dat` / `message.dat` 讀取流程不修改。

## 驗證狀態

- 此版尚待 GitHub Actions x86 DLL 編譯及 Kaguya 5.7 / 5.8 實機回歸測試。
- 優先確認遊戲標題、品牌、預設姓名，以及 `pxxxx.dat` / `message.dat` 讀取與顯示。

## 本次比對與檢查

- 對照 v2.4.1.0，`chihana_gbk.c` 的執行邏輯變更僅將自動 ReaderWrap 迴圈由 `site[3..4]` 恢復為 `site[0..4]`；FLS 狀態管理、ReaderWrap 組語、InitOnce 初始化及掃描器均未改動。
- 使用者提供的 Debug=2 記錄中，視窗標題包含 CP932 位元組與 CP936 全形空白修補結果，而預設姓名轉碼記錄已使用 CP936。這支持標題欄位仍走 ACP 932 的判斷。
- 修正版 ReaderWrap x86 COFF 組語片段已由 Clang 交叉組譯成功；完整 Windows DLL 尚須由專案 GitHub Actions 編譯。
- 本次只做原始碼與組譯層檢查，不能取代 Kaguya 5.7 / 5.8 實機回歸測試。
