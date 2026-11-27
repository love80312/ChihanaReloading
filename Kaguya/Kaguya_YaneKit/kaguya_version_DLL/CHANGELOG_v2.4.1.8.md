
## 6. `CHANGELOG_v2.4.1.8.md`

```markdown
# ChihanaGBK v2.4.1.8

- 恢復 v2.4.1.6 的 GBK 936 專用程式碼基準。
- ReaderWrap 範圍內固定 CP936，範圍外維持 CP932。
- 恢復 `[Font] Charset=134` 設定。
- 移除 CodePage 936／950 切換概念。
- 保留五個 params 欄位、FLS、ExtraSites、GBK 標點與空白修補。
- 保留 UTF-8 Log、PID／TID、LastError 保護、IAT Hook、字型 Hook 與版本 API 匯出。
- 版本標示更新為 2.4.1.8。
- `kaguya_scan.h` 不修改。

## WCTMB 長度紀錄

若需求長度查詢回傳 11，而後續實際轉換只提供 10 bytes，Windows 回傳 0／錯誤碼 122 是輸出緩衝區不足。

本版不自行擴張呼叫端緩衝區，也不更改 API 回傳語意。需配合遊戲實際顯示結果判斷。

## 驗證狀態

本版提供原始碼與設定檔，尚未經本次 GitHub Actions 編譯及目標遊戲實機驗證。