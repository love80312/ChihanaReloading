轉接系統 version.dll，並在記憶體中修補 Kaguya 引擎遊戲，使其在簡中區域（Locale Emulator 轉簡中）下正確顯示 GBK 文字，不修改 exe 檔案。

功能：
1) 字型強制 GBK 字元集，字型名稱/粗細/字高可由 ChihanaGBK.ini 調整（含 Lib\\Graphics.dll）；
2) params.dat 的標題、品牌、預設姓名走 GBK(936)，資源名維持 Shift-JIS(932)；
3) 】【與全形空白改為 GBK 碼（修正視窗標題中間的亂字）。
載入時自動掃描修補位址。

已驗證遊戲：A000（params v5.8）、B000/4328（v5.7）、h000/Hakoniwa（v5.8 新編譯器）。

需要 日版exe 匯入 VERSION.dll。

# 使用方法：
## 將 version.dll 和 ChihanaGBK.ini 放在遊戲 Start.exe 同一個資料夾即可。
### 依照需求修改 `ini`
