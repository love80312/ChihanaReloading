# ChihanaGBK v2.4.1.7 — CodePage 936 / 950

`version.dll` 代理 DLL，適用於既有 ChihanaGBK v2.4.1.6 專案。此版把原本固定的 GBK 目標碼頁改為 INI 的 `CodePage` 選項，讓同一份 DLL 可選擇 GBK 或 Big5。

## 設定

GBK：

```ini
[Options]
EncodingEnabled=1
CodePage=936
```

Big5：

```ini
[Options]
EncodingEnabled=1
CodePage=950
```

只修改字型、不介入文字編碼：

```ini
[Options]
EncodingEnabled=0

[Font]
Face=LXGW Wenkai TC Medium
Weight=0
HeightScale=100
```

`CodePage` 只接受 `936` 或 `950`；其他值會在 Log 留下警告並回退到 `936`。`Charset` 不再由 INI 手動指定，而是自動對應：936 → `lfCharSet=134`，950 → `lfCharSet=136`。當 `EncodingEnabled=0` 時仍保留遊戲傳入的原始 `lfCharSet`。

## 編碼路由

- ReaderWrap 內自動識別的五個 params 欄位（GameTitle、DisplayTitle、Brand、DefaultFirst、DefaultSecond）使用選定的目標碼頁。
- ReaderWrap 外一般日文文字維持 CP932，不會因選 Big5 而全域切換。
- `params.dat` 保持遊戲原始日文資料；DLL 只在執行時轉換上述五個欄位，不改寫整份檔案。
- `message.dat` 必須先由 Kaguya_YaneKit 寫成目標編碼（936 或 950）。DLL 不會重新編碼 `message.dat`。
- 原始遊戲 EXE 與原始 `params.dat` 不需修改。

## 位元組替換

原始 EXE 中掃描到的 Shift-JIS 字串仍以原始位元組作為比對條件，替換長度不變：

| 字元 | 原始 Shift-JIS | GBK 936 | Big5 950 |
|---|---|---|---|
| `】` | `81 7A` | `A1 BF` | `A1 6A` |
| `【` | `81 79` | `A1 BE` | `A1 69` |
| 全形空白 | `81 40` | `A1 A1` | `A1 40` |
| 半形空白＋全形空白 | `20 81 40` | `20 A1 A1` | `20 A1 40` |

## 保留項目

本版保留既有 `EncodingEnabled` 行為、五個 params 掃描欄位、ReaderWrap 組語、FLS、ExtraSites、版本 API 匯出、延後初始化、字型 Hook，以及 Debug Log 寫入／LastError 保護流程。`kaguya_scan.h` 不需修改，請沿用目前 `_ai_fix` 分支原檔。

## 安裝與建置

將編譯後的 x86 `version.dll`、`ChihanaGBK.ini` 放在遊戲 `Start.exe` 同一個資料夾。沿用專案既有 Windows x86／MinGW 建置流程；三個 ANSI 版本 API 匯出維持不變：`GetFileVersionInfoA`、`GetFileVersionInfoSizeA`、`VerQueryValueA`。

**驗證狀態：**本檔案由 v2.4.1.6 原始碼進行靜態修改；此工作環境沒有 Windows x86／MinGW 編譯器，尚未完成 DLL 編譯，也尚未完成遊戲內 GBK 936 與 Big5 950 實測。請先以 `CodePage=936` 做既有 GBK 回歸測試，再測 `CodePage=950`；不能把 Big5 視為已驗證成功。
