# 測試分級與回歸測試規範

本文件定義格式支援聲明的最低證據要求。測試等級應按實際完成的最高級別記錄；低階測試通過不代表高階測試已通過。

## T1～T5 測試等級

| 等級 | 名稱 | 測試內容 | 可宣稱的結果 |
| --- | --- | --- | --- |
| T1 | 格式識別／解析 | 驗證 magic、版本欄位、header、index／entry 數量及邊界；解析器不應讀越界 | 已識別或可解析此格式／版本 |
| T2 | 完整解包 | 對完整封包解包，確認條目數、大小、路徑及解碼結果合理，且無未處理錯誤 | 此樣本可完整解包 |
| T3 | 未修改回封閉環 | 解包後不修改內容，依 manifest 回封，再次解包；逐檔比對 SHA256 或明確定義的正規化內容 | 此樣本未修改回封閉環通過 |
| T4 | 修改資源回封 | 修改至少一個代表性資源，回封後重新解包，確認修改內容、檔案結構及旗標仍正確 | 此樣本修改後回封通過；應列出修改的資源類型 |
| T5 | 遊戲載入驗證 | 將回封資源放入遊戲的獨立副本，實際啟動並確認目標場景／資源可載入 | 此遊戲版本的指定資源已完成實際載入驗證 |

## 測試執行原則

1. 原始封包只讀保留，所有解包、修改與回封都在獨立工作目錄進行。
2. 測試記錄遊戲名稱、版本、封包路徑、封包 SHA256、工具 commit、命令列及測試日期。
3. T3 必須對解包後的每個預期輸出檔案比較 SHA256；只比較總檔案數或總大小不足以通過 T3。
4. 若格式的壓縮器或編碼器不保證位元組完全一致，應比較重新解包後的原始 payload，而不是比較整個封包檔 SHA256；報告中必須說明比對範圍。
5. T4 至少保留原始檔、修改檔、回封封包及回封後再解包的輸出，並記錄修改前後的檔案雜湊。
6. T5 使用遊戲副本，不能用原始安裝目錄作為唯一測試環境。記錄實際啟動方式、進入的場景及確認的資源。
7. 失敗、跳過或未執行的階段應明確標示，不得以 T1/T2 結果推定 T3～T5。

## 合成測試與真實樣本的區別

`--self-test` 中的合成資料及路徑安全測試用來驗證程式邏輯，不屬於任何商業遊戲樣本的 T1～T5 報告。CI 編譯成功及 synthetic self-test 通過，也不代表真實封包已完成 T3、T4 或 T5。

LINK 加密測試需要對應遊戲的 `params.dat` 金鑰。請勿將商業遊戲原始 `params.dat`、金鑰或受版權保護的完整資源上傳到公開 issue；可記錄測試環境、匿名化雜湊及可重現的錯誤訊息。

## 建議測試報告範本

```text
Game:
Game version / release:
Archive:
Archive SHA256:
Archive format / version:
Tool commit:
Test date:

T1 format identification: PASS / FAIL / SKIP
  Header / entry summary:
  Notes:

T2 full extraction: PASS / FAIL / SKIP
  Extracted entry count:
  Failed entry count:
  Notes:

T3 unchanged repack closure: PASS / FAIL / SKIP
  Repacked archive:
  Compared file count:
  SHA256 matches / mismatches:
  Comparison scope (raw payload / normalized data / other):

T4 modified-resource repack: PASS / FAIL / SKIP
  Modified resource(s):
  Post-repack extraction result:
  Content verification:

T5 game-load verification: PASS / FAIL / SKIP
  Game copy / launch method:
  Scene or resource verified:
  Observed result:

Environment:
Command lines:
Logs / artifact references:
Known limitations:
```

## CI 結果的解讀

GitHub Actions 的 build 成功，只代表指定工作流程中的編譯步驟成功。若該工作流程執行 `--self-test`，則該次結果另外代表目前 self-test 所涵蓋的合成測試通過。兩者都不能取代真實遊戲樣本的 T2～T5 測試。
