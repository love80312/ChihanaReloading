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
- 使用者提供的 Debug=2 記錄中，視窗標題包含 `96 85`（CP932 的「妹」）及 `81 49`（CP932 的全形驚嘆號），而預設姓名轉碼記錄已使用 CP936。這支持標題欄位仍走 ACP 932 的判斷。
- 修正版 ReaderWrap x86 COFF 組語片段已由 Clang 交叉組譯成功；完整 Windows DLL 尚須由專案 GitHub Actions 編譯。
- 本次只做原始碼與組譯層檢查，不能取代 Kaguya 5.7 / 5.8 實機回歸測試。
