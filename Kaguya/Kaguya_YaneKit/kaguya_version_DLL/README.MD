# ChihanaGBK v2.4.1.8

Kaguya 引擎用 x86 `version.dll` 代理 DLL。

本版以 v2.4.1.6 的 GBK 專用程式碼為基礎，固定使用 CP936，不提供 Big5 950 或 CodePage 切換。

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
Debug=0

[Font]
Face=LXGW Wenkai TC Medium
Charset=134
Weight=0
HeightScale=100

[Params]
ExtraSites=
```


EncodingEnabled
- 1：啟用 CP932／CP936 文字處理、五個 params ReaderWrap、ExtraSites 與 GBK 位元組修補。
- 0：停用文字轉碼、params 掃描、ReaderWrap、ExtraSites 與文字位元組修補，但仍保留字型名稱修改。
Charset
Charset=134 是 Windows LOGFONT.lfCharSet 字型字集代碼，不是轉碼頁。
- 128：日文字集
- 136：繁體中文字集
- 134：簡體中文字集
它只控制字型字集；文字轉碼目標固定為 CP936。
保留功能
本版保留：
- UTF-8 Debug Log
- PID／TID
- LastError 保護
- IAT Hook
- FLS
- ReaderWrap
- 五個 params 欄位掃描
- ExtraSites
- GBK 標點與空白修補
- 字型 Hook
- 動態模組字型 Hook
- InitOnce
- 三個 ANSI 版本 API 匯出
kaguya_scan.h 不在本次修改範圍。
WCTMB Log 判讀
當 WideCharToMultiByte 使用 lpWideCharStrLen=-1 且輸出指標為 NULL 查詢需求長度時，回傳長度包含結尾 NUL。

如果後續實際轉換提供的輸出容量比需求少 1 byte，Windows 會回傳 0，並設定錯誤碼 122（ERROR_INSUFFICIENT_BUFFER）。

本版不擅自擴張或替換遊戲提供的緩衝區，也不改變 API 回傳語意。這些 Log 必須配合遊戲實際顯示結果判斷，不能單獨視為 DLL 編碼失敗。
建置與測試
目標為 Windows x86 Release。

請使用專案既有 GitHub Actions 工作流程建置，並在目標遊戲實測 params、message、字型及標點顯示。

本原始碼尚未經本次 GitHub Actions 編譯及目標遊戲實機測試。