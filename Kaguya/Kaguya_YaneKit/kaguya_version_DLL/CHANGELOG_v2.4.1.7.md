# ChihanaGBK v2.4.1.7 — CodePage 936 / 950

## 修改內容

- 新增 `[Options] CodePage=936|950`，預設 936，支援以同一份 DLL 切換 GBK／Big5。
- `WideCharToMultiByte(CP_THREAD_ACP)` 僅在 ReaderWrap 內改用所選目標碼頁；ReaderWrap 外仍使用 CP932。
- 目標碼頁 936 自動指定 LOGFONT charset 134；950 自動指定 charset 136。移除 INI 的獨立 `Charset` 讀取，避免兩個設定互相衝突。
- `】`、`【`、全形空白及半形空白＋全形空白的替換位元組，依 CodePage 選擇 GBK 或 Big5 對應值；原始 Shift-JIS 偵測條件與寫入長度不變。
- 初始化 Log 及 params 掃描摘要加入實際 CodePage。

## 未修改項目

- `kaguya_scan.h`、五個 params 欄位掃描順序、ReaderWrap 組語、FLS、ExtraSites、IAT Hook、InitOnce、版本 API 匯出與字型 Hook 均保留。
- `EncodingEnabled=0` 仍不安裝文字轉碼 Hook、不掃描 params、不套用 ReaderWrap／ExtraSites／標點位元組修補；字型 Hook 繼續啟用並保留遊戲原始 `lfCharSet`。
- 不修改原始 EXE 與 `params.dat`；不在 DLL 內轉碼 `message.dat`。

## 驗證狀態

- 已完成原始碼層級的替換項目與設定檢查。
- 尚未完成 Windows x86 編譯、GBK 936 遊戲回歸測試或 Big5 950 遊戲實測。請勿將此版描述為已通過 Big5 實機驗證。
