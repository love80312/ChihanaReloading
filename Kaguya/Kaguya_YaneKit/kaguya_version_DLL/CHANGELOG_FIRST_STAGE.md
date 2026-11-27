# v2.4.1.4 修正紀錄

## 修正目標

- 新增真正獨立的編碼開關：關閉時文字處理完全交回遊戲原版，只保留使用者設定的字型；開啟時維持完整 ChihanaGBK 功能。
- 以已測試可讀取 Kaguya 5.7／5.8 的 v2.4.1.1（commit `ed433f6`）為底稿，避免以後續試驗版本作為功能基礎。

## 本版修正

- 新增 `[Options] EncodingEnabled=1`，預設開啟，維持既有用戶原本功能。
- `EncodingEnabled=0` 時不安裝 `WideCharToMultiByte` IAT hook，不強制 CP932／CP936；也不安裝 Debug=2 的文字視窗追蹤掛鉤、不掃描 params、不掛 ReaderWrap、不執行 `ExtraSites`、GBK 標點及全形空白修補。
- 無論開關狀態，均保留 `CreateFontIndirectA` 字型掛鉤與 `LoadLibraryA` 後續模組字型掛鉤；`Face`、`Charset`、`Weight`、`HeightScale` 照常有效。
- `EncodingEnabled=1` 時，v2.4.1.1 的一般文字 CP932、五個 params 欄位 CP936、FLS／ReaderWrap、GBK 位元組修補及 `ExtraSites` 邏輯不變。

## 保留與驗證

- `kaguya_scan.h` 不修改。
- ReaderWrap 組語、FLS 狀態管理、InitOnce、延後初始化、版本 API 匯出及 params 欄位掃描邏輯均以 `ed433f6` 原始碼為準。
- 尚須以 GitHub Actions 完成 x86 Release 編譯，並分別實測 `EncodingEnabled=0` 的原版文字／自訂字型，以及 `EncodingEnabled=1` 的 Kaguya 5.7／5.8。

---

# v2.4.1.1 基準版本

- 恢復五個 params 文字欄位的自動 CP936：`GameTitle`、`DisplayTitle`、`Brand`、`DefaultFirst`、`DefaultSecond`。
- 保留延後初始化、`InitOnceExecuteOnce`、FLS 每執行緒返回位址堆疊、字型 hook、一般資源字串 ACP 932、`】【` 標點與全形空白修補、Debug 記錄及 `ExtraSites`。
- `kaguya_scan.h` 不修改；`pxxxx.dat`／`message.dat` 讀取流程不修改。
