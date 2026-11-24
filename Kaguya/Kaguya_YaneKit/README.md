
# Kaguya_YaneKit byＡＩ

> `Kaguya_YaneKit` 是面向 `アトリエかぐや` 引擎資源的工具。
>
> 本專案以可擴充的格式工具箱為目標，涵蓋 `params.dat`、LINK／AF01 封包、腳本、文字、圖片及 CG/SP 合成，逐步支援不同 `[SCR-*]` 版本與作品樣本。

## 1. 工具資訊

| 項目 | 內容 |
| --- | --- |
| 工具名稱 | `Kaguya_YaneKit` |
| 目標引擎 | `アトリエかぐや` 使用的 Yane／Kaguya 系資源格式 |
| 目前版本 | Ver1.0.0 |
| 主要測試範圍 | BARE＆BUNNY 分社樣本及其他 AF01 樣本 |
| 執行環境 | Windows x64、.NET 8 |
| 主要功能 | LINK／AF01 解包與回封、`params.dat` 解析與回封、`message.dat` 匯入匯出、`.scr` HLS 高階解析與回編、低階 SCRASM、TBLSTR／TBL 文字處理、圖片轉換與回封、CG/SP 合成、PE 字串處理 |

### 格式文件

- [格式總覽](docs/README_formats.md)
- [程式架構](docs/architecture.md)
- [檔案格式總覽](docs/file/README.md)
- [params.dat](docs/file/params/README_params_dat.md)
- [LINK 封包](docs/archive/README_link.md)
- [AF01 封包](docs/archive/README_af01.md)
- [測試分級與回歸測試](docs/testing.md)
- [SCR 腳本](docs/file/params/README_scr.md)
- [HLS 高階腳本](docs/file/params/README_hls.md)
- [message.dat](docs/file/params/README_message_dat.md)
- [TBLSTR](docs/file/tblstr/README.md)
- [AP 圖片](docs/pic/README_ap.md)
- [ANM 動畫](docs/pic/README_anm.md)
- [PLT 圖片動畫](docs/pic/README_plt.md)
- [APS 格式記錄](docs/pic/README_aps.md)
- [BMP 格式記錄](docs/pic/README_bmp.md)
- [PE／DLL 逆向記錄](docs/PE/README_dll.md)

## 2. 使用方式

### 2.1 啟動方式

不帶任何參數啟動時，會進入互動式選單：

```powershell
Kaguya_YaneKit.exe
```

也可以直接使用命令列子命令：

```powershell
Kaguya_YaneKit.exe [global options] <command> [args]
```

全域參數必須放在子命令之前：

| 參數 | 說明 |
| --- | --- |
| `--game-root <dir>` | 指定遊戲根目錄，工具會嘗試自動尋找 `params.dat` |
| `--workdir <dir>` | 指定工作目錄，未指定時預設為 `<工具目錄>/workplace` |
| `--params <params.dat>` | 明確指定 `params.dat`，優先於自動搜尋 |
| `--help` | 顯示命令列說明 |
| `--self-test` | 執行核心安全路徑、封包寫入及圖片還原自檢 |

例如：

```powershell
Kaguya_YaneKit.exe --game-root "D:\Game" --workdir "D:\Work" archive_unpack "D:\Game\arc\scr.arc" "D:\Work\scr"
```

#### GUI 啟動

專案附有 `gui__V5.pyW`，它是命令列 EXE 的 GUI wrapper，格式解析及封包處理由 `Kaguya_YaneKit.exe` 執行，不會另外實作一套格式解析器。需要 Windows、Python 3.10+ 與 Tkinter；在專案目錄可用下列命令啟動：

```powershell
py -3.10 .\gui__V5.pyW
```

請確認 GUI 與對應版本的 `Kaguya_YaneKit.exe` 位於預期位置。GUI 操作仍使用相同命令列選項與工作目錄規則。

### 2.2 統一封包命令

統一入口會依照封包標頭自動判斷 AF01 或 LINK 格式。

解包：

```powershell
Kaguya_YaneKit.exe archive_unpack <archive.arc> <output-dir> [--params params.dat] [--no-decrypt|--raw] [--overwrite]
```

回封：

```powershell
Kaguya_YaneKit.exe archive_pack <input-dir> <manifest.json> <output.arc> [--compress|--no-compress] [--encrypt|--no-encrypt] [--params params.dat]
```

支援格式：

| 格式 | 解包 | 回封 | 說明 |
| --- | --- | --- | --- |
| AF01 | 支援 | 支援 | 使用 `_archive_manifest.json` 保存封包資訊 |
| LINK6 | 支援 | 支援 | 使用 `_link_manifest.json` 保存封包資訊 |
| UF01 | 僅識別 | 不由此命令處理 | UF01 是 TBLSTR 文字資源包，請使用 `tblstr` 命令 |

解包注意事項：

- 預設不覆蓋已存在的輸出檔案。
- `--overwrite` 允許覆蓋既有輸出檔案。
- LINK 預設嘗試解密；需要從 `params.dat` 取得 XOR 金鑰。
- `--no-decrypt` 或 `--raw` 可保留加密資料，不執行解密。
- `--params <params.dat>` 可指定 LINK 解密所使用的參數檔。
- LINK 清單、驗證及 raw 解包不需要解析 `params.dat` 的內容；只有實際解密或重新加密時才會讀取金鑰。
- LINK 明確指定 `--params` 時，該檔案的金鑰優先，不會沿用另一個遊戲預先載入的金鑰。

回封注意事項：

- AF01 依照 manifest 中原有的壓縮標記決定哪些項目需要重新壓縮。
- LINK 依照 manifest 中原有的壓縮與加密標記處理項目。
- `--compress`／`--no-compress` 可明確指定是否重新壓縮。
- LINK 的 `--encrypt`／`--no-encrypt` 可明確指定是否重新加密。
- LINK 重新加密需要對應遊戲的 `params.dat`。
- AF01 不使用 LINK 加密參數。
- 未明確指定選項時，互動式執行可能會詢問是否壓縮或加密；批次執行則依命令實作使用預設處理方式。

### 2.3 LINK 封包命令

除了統一入口，也保留原本的 LINK 專用命令：

```powershell
Kaguya_YaneKit.exe link list <archive.arc>

Kaguya_YaneKit.exe link extract <archive.arc> <output-dir> [--params params.dat] [--no-decrypt|--raw]

Kaguya_YaneKit.exe link verify <archive.arc>

Kaguya_YaneKit.exe link pack6 <input-dir> <output.arc> [--name archiveName] [--flags 0] [--recursive]

Kaguya_YaneKit.exe link repack6 <input-dir> <_link_manifest.json> <output.arc> [--compress|--no-compress] [--encrypt|--no-encrypt] [--params params.dat]
```

功能：

- `list`：列出封包項目的名稱、偏移、大小、旗標及時間戳。
- `extract`：解包 LINK 封包，可選擇是否解密。
- `verify`：檢查封包 chunk 配置及結尾標記。
- `pack6`：從一般目錄建立 LINK6 封包。
- `repack6`：依照 `_link_manifest.json` 重新建立 LINK6 封包。

`--keep-encryption-flags` 保留為舊參數相容選項，現在代表重新加密並保留加密旗標，而不是單純保留旗標。

### 2.4 SCR 腳本

```powershell
Kaguya_YaneKit.exe scr decompile <input.scr> <output.hls.txt> [--read-encoding cp932] [--params-json params.json]

Kaguya_YaneKit.exe scr hls-asm <input.hls.txt> <output.scr> [--write-encoding cp932]

Kaguya_YaneKit.exe scr verify-hls <input.scr> [--read-encoding cp932] [--write-encoding cp932] [--params-json params.json]

Kaguya_YaneKit.exe scr disasm <input.scr> <output.disasm.txt> [--read-encoding cp932]

Kaguya_YaneKit.exe scr asm <input.disasm.txt> <output.scr> [--write-encoding cp932]

Kaguya_YaneKit.exe scr verify <input.scr>

Kaguya_YaneKit.exe scr verify-text <input.scr> [--read-encoding cp932] [--write-encoding cp932]

Kaguya_YaneKit.exe scr dump <input.scr>

Kaguya_YaneKit.exe scr opcodes [output.md]

Kaguya_YaneKit.exe scr scan-opcodes <input.scr|directory> [output.txt]
```

功能：

- `decompile`：將 `.scr` 轉換為 HLS 高階文字表示。
- `hls-asm`：將 HLS 文字重新組譯為 `.scr`。
- `verify-hls`：測試 `.scr` 經過 HLS 解析與回編後的結果。
- `disasm`：低階 SCRASM 反組譯，主要供除錯與分析使用。
- `asm`：低階 SCRASM 組譯。
- `verify`：檢查 `.scr` 二進位結構。
- `verify-text`：檢查低階文字往返結果。
- `dump`：輸出可閱讀的 opcode 列表。
- `opcodes`：匯出集中管理的 opcode 定義。
- `scan-opcodes`：掃描未知 opcode 與定義衝突。

HLS 是預設的高階編輯流程；SCRASM 則保留作為低階分析與除錯用途。

### 2.5 message.dat 文字處理

```powershell
Kaguya_YaneKit.exe msg export <message.dat> <message.txt> [--read-encoding cp932] [--ini config.ini] [--no-workflow]

Kaguya_YaneKit.exe msg import <message.dat> <message.txt> <output.dat> [--read-encoding cp932] [--write-encoding cp932] [--ini config.ini] [--encrypt true|false] [--xor-key FF] [--no-workflow]

Kaguya_YaneKit.exe msg verify <message.dat> [--read-encoding cp932] [--write-encoding cp932] [--ini config.ini]

Kaguya_YaneKit.exe msg verify-text <message.dat> [--read-encoding cp932] [--write-encoding cp932] [--ini config.ini]

Kaguya_YaneKit.exe msg dump <message.dat> [--read-encoding cp932] [--ini config.ini]

Kaguya_YaneKit.exe msg map <message.dat> <scr-dir> <output.json> [--read-encoding cp932] [--ini config.ini] [--no-workflow]

Kaguya_YaneKit.exe msg split <message.dat> <scr-dir> <output-dir> [--read-encoding cp932] [--ini config.ini] [--no-workflow]

Kaguya_YaneKit.exe msg merge <base-message.txt> <split-dir> <output-message.txt>
```

功能：

- `export`：匯出 message.dat 文字。
- `import`：將編輯後的文字匯入並重建 message.dat。
- `verify`：檢查 message.dat 結構與往返結果。
- `verify-text`：檢查文字匯出、重新匯入流程。
- `dump`：輸出內容摘要。
- `map`：建立 message 與 `.scr` 腳本的對應資料。
- `split`：依照腳本使用狀況拆分文字。
- `merge`：將拆分後的文字合併回完整文本。

`--read-encoding` 與 `--write-encoding` 分別控制讀取及寫入編碼；`--ini` 可指定 `message_config.ini`；`--no-workflow` 可略過工作流程中的長度修正及占位符處理。

### 2.6 TBLSTR 文字處理

```powershell
Kaguya_YaneKit.exe tblstr export <tblstr.arc> <output-dir> [--json] [--scr scr-dir] [--ini tblstr_config.ini]

Kaguya_YaneKit.exe tblstr import <tblstr.arc> <tblstr.txt> <output.arc> [--ini tblstr_config.ini]

Kaguya_YaneKit.exe tblstr split <tblstr.arc> <scr-dir> <output-dir> [--json] [--ini tblstr_config.ini]

Kaguya_YaneKit.exe tblstr merge <base-tblstr.txt> <split-dir> <output-tblstr.txt>

Kaguya_YaneKit.exe tblstr verify-text <tblstr.arc> [--ini tblstr_config.ini]
```

功能：

- `export`：匯出 TBLSTR 文字資源。
- `import`：將編輯後的文字重新匯入並建立 ARC。
- `split`：依照 `.scr` 使用狀況拆分文字。
- `merge`：合併拆分後的文字。
- `verify-text`：檢查 TBLSTR 文字往返流程。
- `--json`：額外輸出 JSON 格式資料。
- `--scr`：提供腳本目錄，以建立文字索引對應。
- `--ini`：指定 `tblstr_config.ini`。

注意：`TBLSTR.ARC` 通常屬於 UF01 格式，不是 AF01。請使用 `tblstr` 功能處理，不要直接使用 AF01／LINK 回封流程。

### 2.7 TBL 支援表格

```powershell
Kaguya_YaneKit.exe tbl export <tbl-file|tbl-dir> <output-dir> [--json]

Kaguya_YaneKit.exe tbl import <tbl-json-file|tbl-json-dir> <output-dir>

Kaguya_YaneKit.exe tbl verify <tbl-file|tbl-dir>
```

功能：

- `export`：匯出支援的 TBL 表格。
- `import`：將 JSON 表格資料重新匯入。
- `verify`：檢查支援的 TBL 表格。

### 2.8 params.dat

```powershell
Kaguya_YaneKit.exe params dump <params.dat>

Kaguya_YaneKit.exe params export-json <params.dat> <output.json> [--read-encoding cp932] [--write-encoding cp932]

Kaguya_YaneKit.exe params import-json <input.json> <output.dat> [--read-encoding cp932] [--write-encoding cp932]

Kaguya_YaneKit.exe params verify <params.dat> [--read-encoding cp932] [--write-encoding cp932]

Kaguya_YaneKit.exe params verify-json <params.dat> [--read-encoding cp932] [--write-encoding cp932]

Kaguya_YaneKit.exe params extract-raw <params.dat> <raw.bin> [--read-encoding cp932]

Kaguya_YaneKit.exe params replace-raw <params.dat> <raw.bin> <output.dat> [--read-encoding cp932] [--write-encoding cp932]
```

功能：

- `dump`：顯示 params.dat 摘要。
- `export-json`：匯出 JSON。
- `import-json`：從 JSON 重建 params.dat。
- `verify`：檢查二進位資料往返結果。
- `verify-json`：檢查 JSON 匯出／匯入結果。
- `extract-raw`：匯出原始二進位區塊。
- `replace-raw`：替換原始二進位區塊並重建 params.dat。

### 2.9 圖片處理

```powershell
Kaguya_YaneKit.exe pic sort <source-dir> <work-dir>

Kaguya_YaneKit.exe pic convert <work-dir>

Kaguya_YaneKit.exe pic repack <work-dir>

Kaguya_YaneKit.exe pic repack-png <work-dir>

Kaguya_YaneKit.exe pic repack-fix <fix-dir>

Kaguya_YaneKit.exe pic restore <work-dir> <output-dir>

Kaguya_YaneKit.exe pic restore-with-replenish <work-dir> <output-dir> [-exclude bmp,ap2]

Kaguya_YaneKit.exe pic export-game <game-root> <work-dir>
```

功能：

- `sort`：依照圖片格式分類來源檔案。
- `convert`：將支援的原始圖片轉換為 PNG。
- `repack`：將修改後的 PNG 回封為原格式。
- `repack-png`：測試直接將 PNG 回封至 `new` 目錄。
- `repack-fix`：使用修正資料重新回封。
- `restore`：依照工作目錄還原圖片。
- `restore-with-replenish`：還原並補充來源資料。
- `export-game`：從遊戲目錄匯出圖片封包，再執行分類與轉換。

### 2.10 CG/SP 合成

```powershell
Kaguya_YaneKit.exe character compose <pic-dir> [output-dir]
```

依照遊戲的 Pattern 等資料合成 CG 與立繪，輸出至 `character/cg` 與 `character/sp`，或指定的輸出目錄。

### 2.11 PE 執行檔字串處理

匯出：

```powershell
Kaguya_YaneKit.exe pe string-dump <input.exe> <output.json> [--encoding cp932] [--min-bytes 4] [--sections .rdata,.data,_RDATA,.rsrc] [--include-text] [--include-ascii] [--include-unreferenced] [--include-diagnostics]
```

匯入：

```powershell
Kaguya_YaneKit.exe pe string-import <input.exe> <strings.json> <output.exe> [--encoding cp932] [--section .yktxt] [--allow-empty]
```

功能：

- `string-dump`：匯出 PE 中可編輯的日文／全形字串及其參照資訊。
- `string-import`：加入翻譯字串區段，並修補記錄中的參照位置。
- `--encoding`：指定字串編碼。
- `--min-bytes`：設定最小字串長度。
- `--sections`：指定掃描區段。
- `--include-text`：包含其他文字內容。
- `--include-ascii`：包含 ASCII 字串。
- `--include-unreferenced`：包含沒有參照位置的字串。
- `--include-diagnostics`：輸出額外診斷資訊。
- `--allow-empty`：允許匯入空白翻譯。

## 3. workplace 工作目錄

`--workdir` 指定工具執行期間的工作目錄。

未指定時，預設使用：

```text
<Kaguya_YaneKit.exe 所在目錄>/workplace/
```

目前工作目錄的主要結構：

```text
{workdir}/
  analysis/
    params/
      params.json       params.dat 匯出的 JSON
    scr/
      *.scr             從 scr.arc 解包的腳本
    scr_hls/
      *.hls.txt         HLS 高階解析結果
    scr_disasm/
      *.disasm.txt      低階 SCRASM 反組譯結果
    scr_asm/
      *.scr             重新組譯的腳本
    pe/
      PE 字串分析資料

  archive_unpack/       封包解包輸出
  archive_pack/         封包回封輸入及工作區

  pic/                  圖片分類、轉換、修圖及回封工作區
  character/            CG/SP 合成輸出

  msg/
    _split_out/         依照腳本拆分的文字
```

注意：

- `workplace` 是執行期間使用的工作目錄，不是編譯時必須存在的資料夾。
- 不同遊戲或不同版本的樣本，建議指定不同的 `--workdir`。
- 工作目錄內可能包含中間產物、分析資料及回封輸入檔案，請勿在未備份的情況下任意刪除。

### 3.1 互動模式的既有資料保護

互動模式啟動時會嘗試分析遊戲資源。

若發現以下資料已存在，會跳過對應的自動產生步驟，以避免覆蓋手動修改的內容：

- `analysis/params/params.json`
- `analysis/scr/*.scr`
- `analysis/scr_hls/*.hls.txt`

因此，重新啟動工具時，已存在的分析成果與手動修改檔案會保留下來。

## 4. 格式支援範圍

本表區分「已確認的格式線索」與「目前工具實作及驗證程度」。有格式讀取程式，不代表所有版本都已完成真實遊戲樣本的完整回封驗證。

| 檔案／格式 | 已確認版本或標記 | 工具支援範圍 | 目前狀態 |
| --- | --- | --- | --- |
| `params.dat` | `[SCR-PARAMS]v02`、`v03`、`v04`、`v05`、`v05.1`、`v05.3`～`v05.8` | 解析、JSON 匯出／匯入、二進位回封、raw blob 替換 | 已完成多種版本的 `verify-json` 閉環測試 |
| `.scr` | `[SCR-Ver5]`、`[SCR-Ver5.1]`、`[SCR-Ver5.2]`、`[SCR-Ver5.3]` | HLS 高階解析／回編、SCRASM 低階反組譯／組譯、verify、dump | Ver5、Ver5.1、Ver5.3 已實測閉環；Ver5.2 依同構容器預期相容 |
| `message.dat` | `[SCR-MESSAGE]ver4.0`、舊式 `ver2`／`ver3` | 文字匯出／匯入、校驗、腳本映射、拆分／合併 | 已支援多種版本，並完成部分字節及文字往返測試 |
| LINK archive | LINK3、LINK4、LINK5、LINK6 | 列表、解包、校驗、LINK6 打包與 manifest 回封 | LINK6 為主要支援與驗證目標；LINK3／4／5 仍需更多真實樣本驗證 |
| AF01 archive | AF01 | 解包、manifest 回封、AF01-LZ 壓縮 | 已有實作及樣本回封驗證紀錄 |
| UF01／TBLSTR | UF01 | TBLSTR 文字匯出／匯入、拆分／合併、校驗 | 透過 `tblstr` 功能處理，不屬於 AF01 |
| TBL | TBLSTR 家族支援表格 | 匯出、JSON 匯入、校驗 | 依目前支援的表格結構處理 |
| AP 圖片 | `AP`、`AP-0`、`AP-2`、`AP-3` | 提取、PNG 轉換、回封 | 已有實作；依各格式文件所列範圍使用 |
| BMP 圖片 | BMP | 一般圖片轉換及還原流程 | 支援 |
| ANM 動畫 | `AN00`、`AN01`、`AN20`、`AN21` | 提取、圖片處理、回封 | 支援；`AN20` 編輯後回封不保證 payload 字節一致 |
| PLT 圖片動畫 | `PL00`、`PL10` | 逐幀 PNG 匯出及回封 | 支援；`PL10` 回封會重新產生後續影格 diff 與 RLE |
| APS | APS | 格式分析紀錄 | 尚無足夠獨立樣本，不列為完整支援格式 |
| DLL／EXE | `Graphics.dll`、`RenderDX.dll`、`Sound.dll`、`Start.exe` | 引擎鏈路、字串、hook 點及執行時行為分析 | 作為逆向研究文件維護，不屬於一般資源封包回封功能 |

### 4.1 params.dat 版本細節

目前已有 `verify-json` 閉環紀錄的版本：

```text
v02
v02_2
v03
v03_2
v04
v05
v05.1
v05.1_2
v05.3
v05.4
v05.4_2
v05.4_3
v05.5
v05.5_2
v05.6
v05.6_2
v05.7
v05.8
```

另外：

- v02／v03／v04 的 `GameSystem`、`Pattern`、`SceneLabel` 已支援結構化寫回。
- `--read-encoding` 與 `--write-encoding` 只作用於 `GameTitle`、`DisplayTitle`、`Brand`、`StaffName1/2`。
- 其他 legacy 資源名稱維持 CP932。
- 各版本欄位差異請參閱 [README_params_dat.md](docs/file/params/README_params_dat.md)。

### 4.2 SCR 腳本驗證紀錄

目前已記錄：

- `[SCR-Ver5]`、`[SCR-Ver5.1]`、`[SCR-Ver5.3]` 已完成實測閉環。
- `[SCR-Ver5.2]` 依照同構容器預期相容，仍應以實際樣本確認。
- v5.8 全量 156 個 HLS 已通過 `HLS → SCR → HLS → SCR` 字節閉環。
- `func/v2/scr` 的 87 個與 `func/v3/scr` 的 91 個 `[SCR-Ver5]`，已完成低階 `scr verify-text` 測試。

## 5. 安全性與自我測試

### 5.1 解包路徑驗證

LINK／AF01 解包使用共用的 `SafePath` 檢查輸出路徑，包含：

- 拒絕絕對路徑及磁碟機路徑。
- 拒絕 `.`、`..` 等不合法路徑段。
- 拒絕 Windows 無效檔名字元。
- 拒絕檔名尾端空白及句點。
- 拒絕 Windows 保留裝置名稱，例如 `CON`、`NUL`、`COM1`、`LPT1`。
- 檢查輸出根目錄及既有路徑元件中的 Reparse Point，例如 junction 與 symlink。

### 5.2 暫存檔與原子提交

LINK／AF01 解包的檔案及 manifest 使用暫存檔流程：

1. 先驗證輸出路徑。
2. 在目的目錄建立唯一暫存檔。
3. 將資料寫入暫存檔。
4. 完成寫入並 Flush。
5. 再次驗證輸出路徑。
6. 將暫存檔提交至正式目標。

當寫入或解碼發生錯誤時，不會在寫入尚未完成前先截斷原本存在的同名檔案。

圖片還原也使用暫存檔與原子替換流程，並嘗試保留來源檔案的建立、最後存取及最後寫入時間。

### 5.3 執行自我測試

```powershell
Kaguya_YaneKit.exe --self-test
```

目前自我測試包含：

| 測試項目 | 驗證內容 |
| --- | --- |
| SafePath | 路徑穿越、磁碟機路徑、Windows 無效名稱與保留裝置名稱 |
| Reparse Point | 檢查既有 symlink 等路徑元件 |
| LINK／AF01 路徑委派 | 確認兩種封包的解包路徑均經過 SafePath |
| 原子寫入失敗保護 | 故意使暫存檔寫入失敗，確認原有檔案不被破壞 |
| 暫存檔清理 | 確認失敗或完成後不殘留暫存檔 |
| Runtime Context 延遲解析 | 只保留 params.dat 路徑，不讀取或解析內容 |
| LINK 金鑰選擇 | 明確指定 `--params` 時，不沿用 context 中的快取金鑰 |
| Restorer | 測試圖片還原、補充、原子替換及時間戳 |

**測試範圍說明：**

`--self-test` 是核心安全性、Runtime Context 路徑選擇及檔案操作測試，不是完整的遊戲封包整合測試。LINK 金鑰選擇測試驗證的是參數優先順序，不代表已使用商業遊戲的加密資料完成解密測試。

通過自我測試不代表已經完成所有真實 LINK／AF01 樣本的解包、修改、回封、重新解包及遊戲內載入驗證。實際操作仍應使用備份封包進行閉環比對。

### 5.4 測試分級

專案的測試結果應依 [測試分級與回歸測試](docs/testing.md) 標示 T1～T5。除非有對應的測試輸出或記錄，不應將「已識別版本／格式」寫成「已完整測試」。

## 6. 樣本測試紀錄

### 6.1 AF01 樣本

依照 [AF01 格式紀錄](docs/archive/README_af01.md) 中的測試紀錄：

| 封包 | 條目數／內容 | 已記錄測試結果 |
| --- | --- | --- |
| `SCR.ARC` | 54 個條目 | `pack --compress → unpack`，SHA256 比對一致 |
| `CGW.ARC` | 73 個壓縮條目 | 曾完成 pack → unpack SHA256 比對 |
| `PARTS.ARC` | AP3、AP 及其他圖片資源 | AP3 182/182、AP 88/88 完成圖片處理流程 |
| `BGM.ARC` | OGG 音訊 | 已確認 AF01 格式及內容類型 |
| `VO1.ARC` | OGG 音訊 | 已確認 AF01 格式及內容類型 |
| `VO2.ARC` | OGG 音訊 | 已確認 AF01 格式及內容類型 |
| `WAV.ARC` | OGG 音訊 | 已確認 AF01 格式及內容類型 |

上述紀錄包含格式確認及既有測試，不代表每個封包都已完成相同程度的回封閉環驗證。

### 6.2 BARE＆BUNNY 樣本

| # | 遊戲名稱 | 版本與格式 | 狀態 |
| --- | --- | --- | --- |
| 01 | `ばくあね ～弟しぼっちゃうぞ！` | `[SCR-PARAMS]v05.4`／`[SCR-Ver5.1]`／`[SCR-MESSAGE]ver4.0`／`LINK6` | 已確認版本／格式；T 等級待補 |
| 02 | `なまイキ ～生粋荘へようこそ！～` | 待補 | 樣本池 |
| 03 | `CHU×ペット` | `[SCR-PARAMS]v05.5`／`[SCR-Ver5.1]`／`[SCR-MESSAGE]ver4.0`／`LINK6` | 已確認版本／格式；T 等級待補 |
| 04 | `ハラミタマ` | 待補 | 樣本池 |
| 05 | `えろゼミ ～エッチにヤルきにABC～` | 待補 | 樣本池 |
| 06 | `ばくあね2 弟いっぱいしぼっちゃうぞ！` | `[SCR-PARAMS]v05.6`／`[SCR-Ver5.1]`／`[SCR-MESSAGE]ver4.0`／`LINK6` | 已確認版本／格式；T 等級待補 |
| 07 | `しごカレ ～エッチな女子大生とドキ×2ラブレッスン!!` | 待補 | 樣本池 |
| 08 | `Love×Holic ～魅惑の乙女と白濁カンケイ～` | 待補 | 樣本池 |
| 09 | `姉ちゃんのススメ ～お姉ちゃんのイタズラ性生活～` | 待補 | 樣本池 |
| 10 | `ままごと ～ままとないしょのえっちしましょ～` | 待補 | 樣本池 |
| 11 | `Mama×Holic ～魅惑のママと甘々カンケイ～` | 待補 | 樣本池 |
| 12 | `ねぇねぇ姉` | 待補 | 樣本池 |
| 13 | `アネトモ` | 待補 | 樣本池 |
| 14 | `Bunny’s ママ代行サービス` | `[SCR-PARAMS]v05.7`／`[SCR-Ver5.3]`／`[SCR-MESSAGE]ver4.0`／`LINK6` | 已確認版本／格式；T 等級待補 |
| 15 | `Pure×Holic～純潔乙女と婚姻カンケイ！？～` | 待補 | 樣本池 |
| 16 | `おっぱいでかいナースとイチャコラエロ×２入院生活！？` | 待補 | 樣本池 |
| 17 | `Hakoniwaハコニワ` | `[SCR-PARAMS]v05.8`／`[SCR-Ver5.3]`／`[SCR-MESSAGE]ver4.0`／`LINK6` | 已確認版本／格式；T 等級待補 |
| 18 | `ママトモ` | 未發布 | 待補 |

## 7. 支援範圍與注意事項

- 格式文件中記錄的版本線索，不應直接視為所有版本都已完成回封驗證。
- 有讀取程式不代表一定能正確重建原始封包。
- LINK3／LINK4／LINK5 仍需更多真實樣本確認，LINK6 是目前主要回封目標。
- AF01 壓縮回封以內容還原為目標，不保證與原始封包的壓縮位元組完全一致。
- ANM／PLT 等圖片動畫格式，編輯後回封可能重新產生內部壓縮資料，不保證與原始 payload 字節完全相同。
- 修改任何遊戲資源前，請先備份原始檔案。
- 建議保留原始封包、解包資料、修改資料及回封結果，以便進行 SHA256 或內容比對。
