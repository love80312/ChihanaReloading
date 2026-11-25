
# -*- coding: utf-8 -*-
"""Kaguya_YaneKit GUI wrapper.

以 tkinter 建立 Kaguya_YaneKit.exe 的圖形操作介面。
所有格式處理均交由原始 EXE 的 CLI 子命令執行；本檔不重寫任何格式解析器。
需求：Windows + Python 3.10+（僅使用 Python 標準函式庫）。
"""
from __future__ import annotations

import codecs
import hashlib
import json
import os
import queue
import re
import subprocess
import tempfile
import threading
import time
import tkinter as tk
from pathlib import Path
from tkinter import filedialog, messagebox, ttk
from typing import Any

APP_TITLE = "Kaguya_YaneKit 控制中心"
ENCODINGS = [
    ("GBK / CP936", "gbk"),
    ("UTF-8", "utf-8"),
    ("Shift-JIS / CP932", "cp932"),
    ("Big5 / CP950", "big5"),
    ("UTF-16 LE", "utf-16le"),
    ("Windows-1252", "windows-1252"),
]
MAX_LOG_LINES = 8000
STDOUT_READ_SIZE = 64 * 1024


def field(key: str, label: str, kind: str = "text", required: bool = True,
          default: str = "", choices: list[tuple[str, str]] | None = None,
          extensions: list[tuple[str, str]] | None = None,
          help_text: str = "") -> dict[str, Any]:
    return {
        "key": key, "label": label, "kind": kind, "required": required,
        "default": default, "choices": choices or [],
        "extensions": extensions or [("所有檔案", "*.*")], "help": help_text,
    }


def op(category: str, title: str, command: list[str], description: str,
       fields: list[dict[str, Any]] | None = None,
       positional: list[str] | None = None,
       options: list[tuple[str, str]] | None = None,
       flags: list[tuple[str, str]] | None = None,
       choice_flags: list[tuple[str, dict[str, list[str]]]] | None = None,
       steps: list[dict[str, Any]] | None = None) -> dict[str, Any]:
    """建立單一 CLI 操作，或由多個既有 CLI 命令組成的順序操作。"""
    return {
        "category": category, "title": title, "command": command,
        "description": description, "fields": fields or [],
        "positional": positional or [], "options": options or [],
        "flags": flags or [], "choice_flags": choice_flags or [],
        "steps": steps or [],
    }


FILE_ARC = [("ARC 封包", "*.arc"), ("所有檔案", "*.*")]
FILE_DAT = [("DAT 檔案", "*.dat"), ("所有檔案", "*.*")]
FILE_SCR = [("SCR 腳本", "*.scr"), ("所有檔案", "*.*")]
FILE_TXT = [("文字檔", "*.txt"), ("所有檔案", "*.*")]
FILE_JSON = [("JSON", "*.json"), ("所有檔案", "*.*")]
FILE_EXE = [("執行檔", "*.exe"), ("所有檔案", "*.*")]

OPS: list[dict[str, Any]] = []

# ---------------------------------------------------------------------------
# 總覽
# ---------------------------------------------------------------------------
OPS += [
    op("總覽", "執行核心安全性自我測試", ["--self-test"],
       "執行 SafePath 路徑驗證、LINK／AF01 解包路徑委派、暫存檔原子提交失敗保護，以及 Restorer 還原／補齊、建立時間與最後寫入時間測試。"
       "測試會在系統暫存目錄建立獨立測試資料，不會解包或修改遊戲資源。"),
    op("總覽", "顯示命令說明", ["--help"],
       "呼叫 Kaguya_YaneKit.exe --help，將完整 CLI 說明輸出到下方紀錄。"),
]

# ---------------------------------------------------------------------------
# Archive / LINK
# ---------------------------------------------------------------------------
OPS += [
    op("封包", "自動辨識 ARC 解包", ["archive_unpack"],
       "自動辨識 AF01 或 LINK 封包並解包。若 LINK 有加密項目，可在上方指定 params.dat 或遊戲根目錄。",
       [field("src", "輸入 ARC", "file", extensions=FILE_ARC),
        field("dst", "解包輸出資料夾", "save_dir"),
        field("extract_mode", "解包模式", "choice", True, "normal", [("正常解密（預設）", "normal"), ("不解密／保留加密資料", "raw")]),
        field("overwrite", "允許覆蓋輸出資料夾內的同名檔案（預設不覆蓋）", "check", False)], ["src", "dst"],
       flags=[("overwrite", "--overwrite")],
       choice_flags=[("extract_mode", {"normal": [], "raw": ["--raw"]})]),
    op("封包", "依 Manifest 回封 ARC", ["archive_pack"],
       "依 _archive_manifest.json 或 _link_manifest.json 回封。壓縮／加密選項會傳給 EXE。",
       [field("src", "輸入資料夾", "dir"), field("manifest", "Manifest JSON", "file", extensions=FILE_JSON),
        field("dst", "輸出 ARC", "save_file", extensions=FILE_ARC),
        field("compress", "壓縮設定", "choice", True, "auto", [("依 Manifest／預設", "auto"), ("啟用壓縮", "compress"), ("停用壓縮", "no-compress")]),
        field("encrypt", "加密設定", "choice", True, "auto", [("依 Manifest／預設", "auto"), ("啟用加密", "encrypt"), ("停用加密", "no-encrypt")])], ["src", "manifest", "dst"],
       choice_flags=[("compress", {"auto": [], "compress": ["--compress"], "no-compress": ["--no-compress"]}),
                     ("encrypt", {"auto": [], "encrypt": ["--encrypt"], "no-encrypt": ["--no-encrypt"]})]),
    op("封包", "LINK 列出項目", ["link", "list"], "列出 LINK 封包內的檔案項目、偏移、大小與旗標。",
       [field("src", "輸入 ARC", "file", extensions=FILE_ARC)], ["src"]),
    op("封包", "LINK 解包", ["link", "extract"], "解包 LINK 封包；加密項目可使用 params.dat 解密。",
       [field("src", "輸入 ARC", "file", extensions=FILE_ARC), field("dst", "輸出資料夾", "save_dir"),
        field("extract_mode", "解包模式", "choice", True, "normal", [("正常解密（預設）", "normal"), ("不解密／保留加密資料", "raw")])], ["src", "dst"],
       choice_flags=[("extract_mode", {"normal": [], "raw": ["--raw"]})]),
    op("封包", "LINK 驗證", ["link", "verify"], "檢查 LINK 封包的 chunk 佈局與終止標記。",
       [field("src", "輸入 ARC", "file", extensions=FILE_ARC)], ["src"]),
    op("封包", "LINK6 打包", ["link", "pack6"], "由資料夾建立 LINK6 封包。",
       [field("src", "輸入資料夾", "dir"), field("dst", "輸出 ARC", "save_file", extensions=FILE_ARC),
        field("name", "封包名稱（可留空）", "text", False), field("flags", "Flags（可留空，預設 0）", "text", False),
        field("recursive", "遞迴納入子資料夾", "check", False)], ["src", "dst"],
       options=[("name", "--name"), ("flags", "--flags")], flags=[("recursive", "--recursive")]),
    op("封包", "LINK6 依 Manifest 回封", ["link", "repack6"], "依 _link_manifest.json 回封 LINK6；含加密項目時需提供 params.dat，或停用加密。",
       [field("src", "輸入資料夾", "dir"), field("manifest", "_link_manifest.json", "file", extensions=FILE_JSON),
        field("dst", "輸出 ARC", "save_file", extensions=FILE_ARC),
        field("compress", "壓縮設定", "choice", True, "auto", [("依 Manifest／預設", "auto"), ("啟用壓縮", "compress"), ("停用壓縮", "no-compress")]),
        field("encrypt", "加密設定", "choice", True, "auto", [("依 Manifest／預設", "auto"), ("啟用加密", "encrypt"), ("停用加密", "no-encrypt")])], ["src", "manifest", "dst"],
       choice_flags=[("compress", {"auto": [], "compress": ["--compress"], "no-compress": ["--no-compress"]}),
                     ("encrypt", {"auto": [], "encrypt": ["--encrypt"], "no-encrypt": ["--no-encrypt"]})]),
]

# ---------------------------------------------------------------------------
# message.dat
# ---------------------------------------------------------------------------
ini_field = field("ini", "message_config.ini（可留空，使用 EXE 預設）", "file", False,
                  extensions=[("INI 設定", "*.ini"), ("所有檔案", "*.*")])
read_enc = field("read_encoding", "讀取 原message.dat 之編碼", "choice", True, "gbk",
                 [(label, value) for label, value in ENCODINGS],
                 help_text="控制二進位 message.dat 文字欄位的解碼方式。匯出的 TXT 固定為 UTF-8。")
write_enc = field("write_encoding", "回寫 新message.dat 之編碼", "choice", True, "gbk",
                  [(label, value) for label, value in ENCODINGS],
                  help_text="匯入時將修改後文字寫回 message.dat 所使用的編碼；TXT 本身仍以 UTF-8 讀取。")
msg_workflow = [field("no_workflow", "略過自動文字工作流程（長度修正／占位符處理）", "check", False)]

OPS += [
    op("訊息", "message.dat 匯出 TXT", ["msg", "export"],
       "讀取 message.dat 並匯出 UTF-8 TXT。可直接在 GUI 選擇 GBK、UTF-8、Shift-JIS 或 Big5。",
       [field("src", "輸入 message.dat", "file", extensions=FILE_DAT), field("dst", "輸出 TXT（UTF-8）", "save_file", extensions=FILE_TXT),
        read_enc, ini_field, msg_workflow[0]], ["src", "dst"],
       options=[("read_encoding", "--read-encoding"), ("ini", "--ini")], flags=[("no_workflow", "--no-workflow")]),
    op("訊息", "message.dat 匯入 TXT", ["msg", "import"],
       "將 UTF-8 TXT 匯入原始 message.dat 並輸出新 DAT。輸出路徑必須與原檔分開；啟用自動工作流程時，CLI 也可能在翻譯 TXT 旁產生 message_fix.txt。",
       [field("src", "原始 message.dat", "file", extensions=FILE_DAT), field("txt", "翻譯 TXT（UTF-8）", "file", extensions=FILE_TXT),
        field("dst", "輸出 message.dat", "save_file", extensions=FILE_DAT), read_enc, write_enc, ini_field,
        field("encrypt", "加密設定", "choice", True, "ini", [("依 INI／原檔預設", "ini"), ("啟用加密", "true"), ("停用加密", "false")]),
        field("xor_key", "XOR Key（十六進位，可留空使用預設）", "text", False),
        field("no_workflow", "略過自動文字工作流程", "check", False)], ["src", "txt", "dst"],
       options=[("read_encoding", "--read-encoding"), ("write_encoding", "--write-encoding"), ("ini", "--ini"), ("xor_key", "--xor-key")],
       flags=[("no_workflow", "--no-workflow")],
       choice_flags=[("encrypt", {"ini": [], "true": ["--encrypt", "true"], "false": ["--encrypt", "false"]})]),
    op("訊息", "message.dat 二進位驗證", ["msg", "verify"], "檢查 message.dat 是否能完成二進位 byte-for-byte 回環。",
       [field("src", "輸入 message.dat", "file", extensions=FILE_DAT), read_enc, write_enc, ini_field], ["src"],
       options=[("read_encoding", "--read-encoding"), ("write_encoding", "--write-encoding"), ("ini", "--ini")]),
    op("訊息", "message.dat 文字回環驗證", ["msg", "verify-text"], "檢查 message.dat 的文字匯出／匯入回環。",
       [field("src", "輸入 message.dat", "file", extensions=FILE_DAT), read_enc, write_enc, ini_field], ["src"],
       options=[("read_encoding", "--read-encoding"), ("write_encoding", "--write-encoding"), ("ini", "--ini")]),
    op("訊息", "message.dat 結構摘要", ["msg", "dump"], "輸出 message.dat 的格式、版本與結構摘要。",
       [field("src", "輸入 message.dat", "file", extensions=FILE_DAT), read_enc, ini_field], ["src"],
       options=[("read_encoding", "--read-encoding"), ("ini", "--ini")]),
    op("訊息", "建立 message／SCR 對照 JSON", ["msg", "map"], "建立 message.dat 與 .scr 腳本之間的引用對照。",
       [field("src", "輸入 message.dat", "file", extensions=FILE_DAT), field("scr_dir", "SCR 資料夾", "dir"),
        field("dst", "輸出對照 JSON", "save_file", extensions=FILE_JSON), read_enc, ini_field,
        field("no_workflow", "略過文字工作流程", "check", False)], ["src", "scr_dir", "dst"],
       options=[("read_encoding", "--read-encoding"), ("ini", "--ini")], flags=[("no_workflow", "--no-workflow")]),
    op("訊息", "依 SCR 拆分 message 文字", ["msg", "split"], "依 .scr 腳本引用情況拆分訊息，輸出共享、孤立與個別腳本文字。",
       [field("src", "輸入 message.dat", "file", extensions=FILE_DAT), field("scr_dir", "SCR 資料夾", "dir"),
        field("dst", "輸出資料夾", "save_dir"), read_enc, ini_field,
        field("no_workflow", "略過文字工作流程", "check", False)], ["src", "scr_dir", "dst"],
       options=[("read_encoding", "--read-encoding"), ("ini", "--ini")], flags=[("no_workflow", "--no-workflow")]),
    op("訊息", "合併拆分後的 message TXT", ["msg", "merge"], "將 base-message.txt 與拆分資料夾合併成完整 message TXT。",
       [field("base", "基礎 message TXT", "file", extensions=FILE_TXT), field("split_dir", "拆分資料夾", "dir"),
        field("dst", "輸出合併 TXT", "save_file", extensions=FILE_TXT)], ["base", "split_dir", "dst"]),
]

# ---------------------------------------------------------------------------
# SCR
# ---------------------------------------------------------------------------
enc_read_scr = field("read_encoding", "讀取文字編碼", "choice", True, "cp932", [(a, b) for a, b in ENCODINGS])
enc_write_scr = field("write_encoding", "寫入文字編碼", "choice", True, "cp932", [(a, b) for a, b in ENCODINGS])
OPS += [
    op("腳本", "SCR → HLS 高階反編譯", ["scr", "decompile"], "將 .scr 反編譯為 HLS 高階文字格式。",
       [field("src", "輸入 SCR", "file", extensions=FILE_SCR), field("dst", "輸出 HLS TXT", "save_file", extensions=FILE_TXT),
        enc_read_scr, field("params_json", "params.json（可留空）", "file", False, extensions=FILE_JSON)], ["src", "dst"],
       options=[("read_encoding", "--read-encoding"), ("params_json", "--params-json")]),
    op("腳本", "HLS TXT → SCR 回編", ["scr", "hls-asm"], "將 HLS 高階文字格式重新組譯為 .scr。",
       [field("src", "輸入 HLS TXT", "file", extensions=FILE_TXT), field("dst", "輸出 SCR", "save_file", extensions=FILE_SCR), enc_write_scr], ["src", "dst"],
       options=[("write_encoding", "--write-encoding")]),
    op("腳本", "SCR HLS 回環驗證", ["scr", "verify-hls"], "將 .scr 經 HLS 反編譯與回編後檢查結果。",
       [field("src", "輸入 SCR", "file", extensions=FILE_SCR), enc_read_scr, enc_write_scr,
        field("params_json", "params.json（可留空）", "file", False, extensions=FILE_JSON)], ["src"],
       options=[("read_encoding", "--read-encoding"), ("write_encoding", "--write-encoding"), ("params_json", "--params-json")]),
    op("腳本", "SCR 低階反組譯", ["scr", "disasm"], "以低階 SCRASM 格式反組譯 .scr，供指令層級檢視。",
       [field("src", "輸入 SCR", "file", extensions=FILE_SCR), field("dst", "輸出反組譯 TXT", "save_file", extensions=FILE_TXT), enc_read_scr], ["src"],
       options=[("read_encoding", "--read-encoding")]),
    op("腳本", "SCRASM TXT → SCR", ["scr", "asm"], "將低階 SCRASM 文字重新組譯為 .scr。",
       [field("src", "輸入 SCRASM TXT", "file", extensions=FILE_TXT), field("dst", "輸出 SCR", "save_file", extensions=FILE_SCR), enc_write_scr], ["src"],
       options=[("write_encoding", "--write-encoding")]),
    op("腳本", "匯出 SCR Opcode Schema", ["scr", "opcodes"], "匯出集中管理的 SCR opcode 規格；輸出檔可留空，將使用工具預設行為。",
       [field("dst", "輸出 Markdown（可留空）", "save_file", False, extensions=[("Markdown", "*.md"), ("所有檔案", "*.*")])], ["dst"]),
    op("腳本", "掃描未知 SCR Opcode", ["scr", "scan-opcodes"], "掃描單一 SCR 或資料夾，列出未知 opcode／schema 衝突。",
       [field("src", "輸入 SCR 或資料夾", "any_path"), field("dst", "輸出報告 TXT（可留空）", "save_file", False, extensions=FILE_TXT)], ["src", "dst"]),
    op("腳本", "SCR 二進位驗證", ["scr", "verify"], "執行 SCR 二進位回環驗證。",
       [field("src", "輸入 SCR", "file", extensions=FILE_SCR)], ["src"]),
    op("腳本", "SCR 文字回環驗證", ["scr", "verify-text"], "驗證 SCR 文字解碼／編碼回環。",
       [field("src", "輸入 SCR", "file", extensions=FILE_SCR), enc_read_scr, enc_write_scr], ["src"],
       options=[("read_encoding", "--read-encoding"), ("write_encoding", "--write-encoding")]),
    op("腳本", "SCR 指令摘要", ["scr", "dump"], "輸出 .scr 可讀的 opcode 清單。",
       [field("src", "輸入 SCR", "file", extensions=FILE_SCR)], ["src"]),
]

# ---------------------------------------------------------------------------
# params.dat
# ---------------------------------------------------------------------------
OPS += [
    op("參數", "params.dat 結構摘要", ["params", "dump"], "輸出 params.dat 的版本與主要結構摘要。",
       [field("src", "輸入 params.dat", "file", extensions=FILE_DAT)], ["src"]),
    op("參數", "params.dat 匯出 JSON", ["params", "export-json"], "將 params.dat 匯出為可編輯 JSON。",
       [field("src", "輸入 params.dat", "file", extensions=FILE_DAT), field("dst", "輸出 JSON", "save_file", extensions=FILE_JSON),
        enc_read_scr, enc_write_scr], ["src", "dst"], options=[("read_encoding", "--read-encoding"), ("write_encoding", "--write-encoding")]),
    op("參數", "JSON 回封 params.dat", ["params", "import-json"], "將修改後的 JSON 回封為 params.dat。",
       [field("src", "輸入 JSON", "file", extensions=FILE_JSON), field("dst", "輸出 params.dat", "save_file", extensions=FILE_DAT),
        enc_read_scr, enc_write_scr], ["src", "dst"], options=[("read_encoding", "--read-encoding"), ("write_encoding", "--write-encoding")]),
    op("參數", "params.dat 二進位驗證", ["params", "verify"], "檢查 params.dat 是否可 byte-for-byte 回環。",
       [field("src", "輸入 params.dat", "file", extensions=FILE_DAT), enc_read_scr, enc_write_scr], ["src"],
       options=[("read_encoding", "--read-encoding"), ("write_encoding", "--write-encoding")]),
    op("參數", "params.dat JSON 回環驗證", ["params", "verify-json"], "驗證 params.dat 的 JSON 匯出／匯入回環。",
       [field("src", "輸入 params.dat", "file", extensions=FILE_DAT), enc_read_scr, enc_write_scr], ["src"],
       options=[("read_encoding", "--read-encoding"), ("write_encoding", "--write-encoding")]),
    op("參數", "提取 params.dat Raw Blob", ["params", "extract-raw"], "從 params.dat 擷取 Raw Blob。",
       [field("src", "輸入 params.dat", "file", extensions=FILE_DAT), field("dst", "輸出 RAW BIN", "save_file", extensions=[("BIN", "*.bin"), ("所有檔案", "*.*")]), enc_read_scr], ["src", "dst"],
       options=[("read_encoding", "--read-encoding")]),
    op("參數", "替換 params.dat Raw Blob", ["params", "replace-raw"], "使用外部 RAW BIN 替換 params.dat 的 Raw Blob。",
       [field("src", "原始 params.dat", "file", extensions=FILE_DAT), field("raw", "替換用 RAW BIN", "file", extensions=[("BIN", "*.bin"), ("所有檔案", "*.*")]),
        field("dst", "輸出 params.dat", "save_file", extensions=FILE_DAT), enc_read_scr, enc_write_scr], ["src", "raw", "dst"],
       options=[("read_encoding", "--read-encoding"), ("write_encoding", "--write-encoding")]),
]

# ---------------------------------------------------------------------------
# 圖片與角色合成
# ---------------------------------------------------------------------------
PICTURE_WORK_HELP = "選擇 pic 根目錄（再處理資料夾），不要選 ap2、bmp 等單一格式資料夾。"
RESTORE_TARGET_HELP = (
    "請選擇 archive_pack 根目錄，而不是 archive_pack 下的單一 ARC 子資料夾。"
    "metadata 會記錄原始 ARC 名稱與檔案相對路徑，程式會依此建立對應子資料夾。"
)

OPS += [
    op("圖片", "圖片分類", ["pic", "sort"],
       "掃描 ARC 解包資料夾內的圖片，依檔案格式分類到再處理資料夾；原始圖片會複製到各格式的 orig，並建立 metadata JSON。"
       "若來源含 _link_manifest.json，會依 ARC 名稱建立對應子資料夾。",
       [field("src", "來源資料夾（ARC 解包資料夾）", "dir",
              help_text="選擇已完成 ARC 解包的資料夾，例如 archive_unpack\\N18。請勿選擇 ARC 檔本身。"),
        field("dst", "工作資料夾（再處理資料夾）", "save_dir",
              help_text="建議選擇 pic 根目錄。若來源有 Manifest，程式會自動在此建立 ARC 名稱子資料夾。")], ["src", "dst"]),
    op("圖片", "原檔轉 PNG", ["pic", "convert"],
       "讀取再處理資料夾中各格式資料夾的 orig 原始圖片，轉換為可編輯 PNG，輸出至對應的 png 資料夾。"
       "本操作不會直接掃描 ARC 解包資料夾；必須先執行「圖片分類」。",
       [field("src", "工作資料夾（再處理資料夾）", "dir", help_text=PICTURE_WORK_HELP)], ["src"]),
    op("圖片", "分類並轉換 PNG（Sort + Convert）", ["pic"],
       "依序執行「圖片分類」及「原檔轉 PNG」：先從 ARC 解包資料夾辨識並分類原始圖片，再轉成可編輯 PNG。"
       "這是兩個既有 CLI 命令的快捷操作，不會改寫或取代原本的分類／轉換功能。",
       [field("src", "來源資料夾（ARC 解包資料夾）", "dir",
              help_text="選擇已完成 ARC 解包的資料夾，例如 archive_unpack\\N18。"),
        field("dst", "輸出資料夾（再處理資料夾）", "save_dir",
              help_text="建議選擇 pic 根目錄。完成後可在其下各 ARC／格式資料夾的 png 目錄編輯圖片。")],
       steps=[
           {"command": ["pic", "sort"], "positional": ["src", "dst"]},
           {"command": ["pic", "convert"], "positional": ["dst"]},
       ]),
    op("圖片", "FIX 夾內圖片回封", ["pic", "repack"],
       "自動搜尋再處理資料夾內各格式的 fix 子資料夾，將其中的 PNG 依 metadata 回封為原始格式，輸出至各格式的 new。"
       "只處理 fix 內的圖片；沒有放入 fix 的 PNG 不會回封。",
       [field("src", "工作資料夾（再處理資料夾）", "dir", help_text=PICTURE_WORK_HELP)], ["src"]),
    op("圖片", "各格式 NEW 回填至待打包資料夾", ["pic", "restore"],
       "將再處理資料夾中各格式 new 內的回封檔案，依 metadata 記錄的原始路徑複製到待打包資料夾。"
       "這是複製，不會刪除或移動 new 內的檔案；也不會自動補回未處理的原始圖片或其他非圖片檔案。",
       [field("src", "工作資料夾（再處理資料夾）", "dir", help_text=PICTURE_WORK_HELP),
        field("dst", "輸出資料夾（待打包資料夾根目錄）", "save_dir", help_text=RESTORE_TARGET_HELP)], ["src", "dst"]),
    op("圖片", "回填 NEW 並補齊原始圖片", ["pic", "restore-with-replenish"],
       "先將各格式 new 的回封檔案複製到待打包資料夾，再從 orig 複製尚未回封且目標不存在的原始圖片。"
       "new 中的回封結果優先；排除格式只影響 orig 補齊，不會排除 new。"
       "此功能只補圖片，不會補回 ARC 中其他非圖片檔案。",
       [field("src", "工作資料夾（再處理資料夾）", "dir", help_text=PICTURE_WORK_HELP),
        field("dst", "輸出資料夾（待打包資料夾根目錄）", "save_dir", help_text=RESTORE_TARGET_HELP),
        field("exclude", "補齊時排除格式（選填，逗號分隔）", "text", False, "",
              help_text="例如 ap2,bmp。只排除從 orig 補回的格式；new 中已回封的檔案仍會照常回填。留空代表不排除任何格式。")], ["src", "dst"],
       options=[("exclude", "-exclude")]),
    op("圖片進階", "指定 FIX 資料夾回封", ["pic", "repack-fix"],
       "只回封使用者指定的一個 fix 資料夾，不會搜尋或處理其他格式／ARC 的 fix。"
       "指定資料夾名稱必須是 fix，上一層必須是格式資料夾；例如 pic\\N18\\ap2\\fix。回封結果寫入同一格式資料夾的 new。",
       [field("src", "指定 FIX 資料夾", "dir",
              help_text="請選擇實際名稱為 fix 的資料夾，例如 pic\\N18\\ap2\\fix，而不是 PNG 檔或 ap2 資料夾。")], ["src"]),
    op("圖片進階", "PNG 全量回封｜測試", ["pic", "repack-png"],
       "將再處理資料夾內各格式 png 子資料夾的所有 PNG，不論是否修改過，全部回封為原始格式並輸出至對應的 new。"
       "會實際產生回封檔案；不會執行回填，也不會打包 ARC。",
       [field("src", "工作資料夾（再處理資料夾）", "dir", help_text=PICTURE_WORK_HELP)], ["src"]),
    op("圖片進階", "自動匯出遊戲圖片工作區", ["pic", "export-game"],
       "依所選遊戲根目錄中的 params.dat 安裝表自動尋找遊戲圖片 ARC，逐一解包、分類並轉換 PNG。"
       "本操作會以這裡選取的遊戲根目錄及輸出工作目錄分別同步設定 CLI 的 --game-root 與 --workdir；"
       "與上方全域路徑衝突時，以本操作欄位為準，避免誤用其他遊戲的安裝表。上方明確指定的 params.dat 仍具有優先權。"
       "輸出會建立在指定工作目錄下的 picture_arc 與 picture。",
       [field("game", "遊戲根目錄", "dir",
              help_text="此欄位是本操作實際使用的 --game-root。未另外指定全域 params.dat 時，_ai_fix 只會從此目錄第一層尋找 params.dat。"),
        field("work", "輸出工作目錄", "save_dir",
              help_text="輸出會建立 picture_arc 與 picture。請勿選在遊戲根目錄內，也不要與遊戲根目錄互相包含。")], ["game", "work"]),
    op("角色", "CG／SP 角色圖合成", ["character", "compose"], "依 pattern 資料合成 CG 與立繪，輸出 character/cg 與 character/sp。",
       [field("src", "圖片資料夾", "dir"), field("dst", "輸出資料夾（可留空，使用預設）", "save_dir", False)], ["src", "dst"]),
]

# ---------------------------------------------------------------------------
# TBLSTR / TBL
# ---------------------------------------------------------------------------
OPS += [
    op("TBLSTR／TBL", "TBLSTR 匯出 TXT", ["tblstr", "export"], "讀取 TBLSTR ARC 並匯出 UTF-8 tblstr.txt，可選擇輸出 JSON 與 SCR 對照。",
       [field("src", "輸入 TBLSTR ARC", "file", extensions=FILE_ARC), field("dst", "輸出資料夾", "save_dir"),
        field("json", "同時輸出 JSON", "check", False), field("scr_dir", "SCR 資料夾（可留空以略過映射）", "dir", False),
        field("ini", "tblstr_config.ini（可留空）", "file", False, extensions=[("INI", "*.ini"), ("所有檔案", "*.*")])], ["src", "dst"],
       options=[("scr_dir", "--scr"), ("ini", "--ini")], flags=[("json", "--json")]),
    op("TBLSTR／TBL", "TBLSTR 匯入回封", ["tblstr", "import"], "將編輯後的 tblstr.txt 匯入原 ARC，輸出新的 TBLSTR ARC。",
       [field("src", "原始 TBLSTR ARC", "file", extensions=FILE_ARC), field("txt", "編輯後 tblstr.txt", "file", extensions=FILE_TXT),
        field("dst", "輸出 ARC", "save_file", extensions=FILE_ARC), field("ini", "tblstr_config.ini（可留空）", "file", False, extensions=[("INI", "*.ini"), ("所有檔案", "*.*")])],
       ["src", "txt", "dst"], options=[("ini", "--ini")]),
    op("TBLSTR／TBL", "TBLSTR 依 SCR 拆分", ["tblstr", "split"], "依 .scr 的引用情況拆分 TBLSTR 文字。",
       [field("src", "輸入 TBLSTR ARC", "file", extensions=FILE_ARC), field("scr_dir", "SCR 資料夾", "dir"), field("dst", "輸出資料夾", "save_dir"),
        field("json", "同時輸出映射 JSON", "check", False), field("ini", "tblstr_config.ini（可留空）", "file", False, extensions=[("INI", "*.ini"), ("所有檔案", "*.*")])],
       ["src", "scr_dir", "dst"], options=[("ini", "--ini")], flags=[("json", "--json")]),
    op("TBLSTR／TBL", "合併 TBLSTR 拆分文字", ["tblstr", "merge"], "將 base-tblstr.txt 與拆分資料夾合併。",
       [field("base", "基礎 TBLSTR TXT", "file", extensions=FILE_TXT), field("split", "拆分資料夾", "dir"), field("dst", "輸出合併 TXT", "save_file", extensions=FILE_TXT)],
       ["base", "split", "dst"]),
    op("TBLSTR／TBL", "TBLSTR 文字回環驗證", ["tblstr", "verify-text"], "檢查 TBLSTR 文字匯出／匯入是否 byte-for-byte 一致。",
       [field("src", "輸入 TBLSTR ARC", "file", extensions=FILE_ARC), field("ini", "tblstr_config.ini（可留空）", "file", False, extensions=[("INI", "*.ini"), ("所有檔案", "*.*")])],
       ["src"], options=[("ini", "--ini")]),
    op("TBLSTR／TBL", "TBL 支援表匯出", ["tbl", "export"], "匯出單一 TBL 或資料夾內支援的 TBL 表為 TXT，可選擇 JSON。",
       [field("src", "輸入 TBL 檔案或資料夾", "any_path"), field("dst", "輸出資料夾", "save_dir"), field("json", "同時輸出 JSON", "check", False)],
       ["src", "dst"], flags=[("json", "--json")]),
    op("TBLSTR／TBL", "TBL JSON 匯入", ["tbl", "import"], "將單一 TBL JSON 或資料夾內 tbl_*.json 回封為 TBL。",
       [field("src", "輸入 TBL JSON 或資料夾", "any_path"), field("dst", "輸出資料夾", "save_dir")], ["src", "dst"]),
    op("TBLSTR／TBL", "TBL 二進位驗證", ["tbl", "verify"], "驗證單一 TBL 或資料夾內支援表的 byte-for-byte 回環。",
       [field("src", "輸入 TBL 檔案或資料夾", "any_path")], ["src"]),
]

# ---------------------------------------------------------------------------
# PE
# ---------------------------------------------------------------------------
OPS += [
    op("PE", "PE 字串與指標匯出", ["pe", "string-dump"], "分析 EXE 的 TBLSTR 字串及指標引用，輸出 JSON。",
       [field("src", "輸入 EXE", "file", extensions=FILE_EXE), field("dst", "輸出 JSON", "save_file", extensions=FILE_JSON),
        field("encoding", "字串編碼", "choice", True, "cp932", [(a, b) for a, b in ENCODINGS]),
        field("min_bytes", "最小位元組長度", "text", True, "4"),
        field("sections", "掃描區段（逗號分隔）", "text", False, ".rdata,.data,_RDATA,.rsrc"),
        field("include_text", "包含文字資訊", "check", False), field("include_ascii", "包含 ASCII 字串", "check", False),
        field("include_unreferenced", "包含未被引用字串", "check", False), field("include_diagnostics", "包含診斷資訊", "check", False)],
       ["src", "dst"], options=[("encoding", "--encoding"), ("min_bytes", "--min-bytes"), ("sections", "--sections")],
       flags=[("include_text", "--include-text"), ("include_ascii", "--include-ascii"), ("include_unreferenced", "--include-unreferenced"), ("include_diagnostics", "--include-diagnostics")]),
    op("PE", "PE 字串 JSON 匯入", ["pe", "string-import"], "將翻譯後的字串 JSON 新增為自訂 section，並修補記錄的指標。",
       [field("src", "原始 EXE", "file", extensions=FILE_EXE), field("json", "編輯後 strings.json", "file", extensions=FILE_JSON),
        field("dst", "輸出 EXE", "save_file", extensions=FILE_EXE), field("encoding", "字串編碼", "choice", True, "cp932", [(a, b) for a, b in ENCODINGS]),
        field("section", "新增區段名稱", "text", True, ".yktxt"), field("allow_empty", "允許空字串", "check", False)],
       ["src", "json", "dst"], options=[("encoding", "--encoding"), ("section", "--section")], flags=[("allow_empty", "--allow-empty")]),
]

CATEGORIES = list(dict.fromkeys(x["category"] for x in OPS))


class ScrollableFrame(ttk.Frame):
    def __init__(self, master: tk.Widget, **kwargs: Any) -> None:
        super().__init__(master, **kwargs)
        self.canvas = tk.Canvas(self, highlightthickness=0, borderwidth=0)
        self.scrollbar = ttk.Scrollbar(self, orient="vertical", command=self.canvas.yview)
        self.inner = ttk.Frame(self.canvas, padding=(14, 10, 14, 14))
        self.window_id = self.canvas.create_window((0, 0), window=self.inner, anchor="nw")
        self.canvas.configure(yscrollcommand=self.scrollbar.set)
        self.inner.bind("<Configure>", self._on_inner_configure)
        self.canvas.bind("<Configure>", self._on_canvas_configure)
        self.canvas.pack(side="left", fill="both", expand=True)
        self.scrollbar.pack(side="right", fill="y")
        self.canvas.bind_all("<MouseWheel>", self._on_mousewheel, add="+")

    def _on_inner_configure(self, _event: tk.Event) -> None:
        self.canvas.configure(scrollregion=self.canvas.bbox("all"))

    def _on_canvas_configure(self, event: tk.Event) -> None:
        self.canvas.itemconfigure(self.window_id, width=event.width)

    def _on_mousewheel(self, event: tk.Event) -> str | None:
        """只在游標位於本捲動區內時捲動，避免干擾右側執行紀錄。"""
        try:
            if not self.winfo_exists() or not self.winfo_ismapped():
                return
            widget = self.winfo_containing(event.x_root, event.y_root)
            inside = False
            while widget is not None:
                if widget is self.inner or widget is self.canvas:
                    inside = True
                    break
                widget = getattr(widget, "master", None)
            if not inside:
                return

            delta = int(-event.delta / 120)
            if delta == 0 and event.delta:
                delta = -1 if event.delta > 0 else 1
            if delta:
                self.canvas.yview_scroll(delta, "units")
                return "break"
        except tk.TclError:
            return


class KaguyaGui:
    def __init__(self, root: tk.Tk) -> None:
        self.root = root
        self.root.title(APP_TITLE)
        # 右側常駐執行紀錄需要一定寬度；視螢幕解析度調整初始尺寸。
        screen_w = self.root.winfo_screenwidth()
        screen_h = self.root.winfo_screenheight()
        min_width = min(900, max(640, screen_w - 40))
        min_height = min(620, max(480, screen_h - 80))
        width = min(1480, max(min_width, screen_w - 60))
        height = min(900, max(min_height, screen_h - 100))
        self.root.geometry(f"{width}x{height}")
        # 初始尺寸不是最小尺寸；允許使用者縮小視窗，讓各欄位重新分配空間。
        self.root.minsize(min_width, min_height)
        self.events: queue.Queue[tuple[str, Any]] = queue.Queue()
        self.worker: threading.Thread | None = None
        self.process: subprocess.Popen[bytes] | None = None
        self._process_lock = threading.Lock()
        self._closing = False
        self.current_operation: dict[str, Any] | None = None
        self.field_vars: dict[str, tk.Variable] = {}
        self.field_widgets: dict[str, tk.Widget] = {}
        self.field_labels: dict[str, ttk.Label] = {}
        self.field_defs: dict[str, dict[str, Any]] = {}
        self.help_labels: list[ttk.Label] = []
        self._last_wraplengths: dict[str, int] = {}
        self._wrap_update_pending = False
        # v05+ params.dat 固定 UTF-16LE；切換版本時保留使用者選過的舊版編碼。
        self._params_encoding_fixed_keys: set[str] = set()
        self._params_encoding_saved_values: dict[str, str] = {}
        self._params_encoding_preferences: dict[str, str] = {}
        self.category_var = tk.StringVar(value=CATEGORIES[0])
        self.operation_var = tk.StringVar()
        self.exe_var = tk.StringVar(value=str(self._find_exe() or ""))
        self.game_root_var = tk.StringVar()
        self.workdir_var = tk.StringVar()
        self.params_var = tk.StringVar()
        self.params_hint_var = tk.StringVar(value="params.dat 尚未檢查。")
        self.command_var = tk.StringVar(value="尚未選擇操作")
        self.status_var = tk.StringVar(value="就緒")
        self.game_root_var.trace_add("write", lambda *_: self._on_global_path_change())
        self.workdir_var.trace_add("write", lambda *_: self._on_global_path_change())
        self.params_var.trace_add("write", lambda *_: self._on_global_path_change())
        self.exe_var.trace_add("write", lambda *_: self._on_global_path_change())
        self._build_ui()
        self._update_params_hint()
        self._populate_operations()
        self._poll_events()
        self.root.protocol("WM_DELETE_WINDOW", self._on_close)

    def _find_exe(self) -> Path | None:
        here = Path(__file__).resolve().parent
        candidates = [here / "Kaguya_YaneKit.exe", here / "publish" / "Kaguya_YaneKit.exe",
                      here / "Kaguya_YaneKit" / "Kaguya_YaneKit.exe"]
        for path in candidates:
            if path.is_file():
                return path
        return None

    def _build_ui(self) -> None:
        style = ttk.Style()
        try:
            style.theme_use("vista")
        except tk.TclError:
            try:
                style.theme_use("clam")
            except tk.TclError:
                pass
        style.configure("Header.TLabel", font=("Microsoft JhengHei UI", 15, "bold"))
        style.configure("Section.TLabel", font=("Microsoft JhengHei UI", 10, "bold"))
        style.configure("Run.TButton", font=("Microsoft JhengHei UI", 10, "bold"), padding=(18, 7))

        outer = ttk.Frame(self.root, padding=10)
        outer.pack(fill="both", expand=True)

        header = ttk.Frame(outer)
        header.pack(fill="x", pady=(0, 7))
        ttk.Label(header, text="Kaguya_YaneKit", style="Header.TLabel").pack(side="left")
        ttk.Label(header, text="GUI 前端｜所有格式處理均由原始 EXE 執行",
                  foreground="#666666").pack(side="left", padx=14, pady=(5, 0))

        exe_frame = ttk.LabelFrame(outer, text="執行檔", padding=7)
        exe_frame.pack(fill="x", pady=(0, 6))
        ttk.Entry(exe_frame, textvariable=self.exe_var).pack(side="left", fill="x", expand=True)
        ttk.Button(exe_frame, text="瀏覽 EXE", command=self._browse_exe).pack(side="left", padx=(6, 0))

        global_frame = ttk.LabelFrame(outer, text="全域路徑（可留空）", padding=7)
        global_frame.pack(fill="x", pady=(0, 8))
        self._path_row(global_frame, 0, "遊戲根目錄", self.game_root_var, "dir")
        self._path_row(global_frame, 1, "工作目錄", self.workdir_var, "save_dir")
        self._path_row(global_frame, 2, "params.dat", self.params_var, "file", FILE_DAT)
        self.params_hint_label = ttk.Label(
            global_frame, textvariable=self.params_hint_var, foreground="#52677a",
            wraplength=1100, justify="left")
        self.params_hint_label.grid(
            row=3, column=1, columnspan=2, sticky="ew", pady=(4, 1))
        global_frame.bind("<Configure>", self._schedule_wrap_update, add="+")

        # 主操作區與執行紀錄採左右分割；紀錄面板不再被擠到視窗底部。
        main_panes = ttk.Panedwindow(outer, orient="horizontal")
        main_panes.pack(fill="both", expand=True)

        work_area = ttk.Frame(main_panes)
        log_frame = ttk.LabelFrame(main_panes, text="執行紀錄", padding=6)
        main_panes.add(work_area, weight=5)
        main_panes.add(log_frame, weight=2)

        def set_initial_log_width() -> None:
            if not main_panes.winfo_exists():
                return
            total_width = main_panes.winfo_width()
            if total_width <= 1:
                self.root.after(100, set_initial_log_width)
                return
            # 讓紀錄區維持約四分之一寬度，避免中央操作表單被壓縮。
            log_width = min(400, max(270, int(total_width * 0.26)))
            sash_position = max(1, total_width - log_width)
            main_panes.sashpos(0, sash_position)

        self.root.after_idle(set_initial_log_width)

        # 左側：功能導覽與操作欄位。
        work_panes = ttk.Panedwindow(work_area, orient="horizontal")
        work_panes.pack(fill="both", expand=True)
        nav = ttk.Frame(work_panes, padding=(0, 0, 8, 0), width=190)
        content = ttk.Frame(work_panes)
        work_panes.add(nav, weight=0)
        work_panes.add(content, weight=1)
        nav.configure(width=190)

        ttk.Label(nav, text="功能分類", style="Section.TLabel").pack(anchor="w", pady=(2, 5))
        self.category_list = tk.Listbox(
            nav, height=10, exportselection=False, activestyle="dotbox",
            font=("Microsoft JhengHei UI", 10), relief="solid", borderwidth=1)
        for name in CATEGORIES:
            self.category_list.insert("end", name)
        self.category_list.pack(fill="x")
        self.category_list.bind("<<ListboxSelect>>", self._on_category_select)

        ttk.Label(nav, text="操作項目", style="Section.TLabel").pack(anchor="w", pady=(12, 5))
        self.operation_list = tk.Listbox(
            nav, exportselection=False, activestyle="dotbox",
            font=("Microsoft JhengHei UI", 10), relief="solid", borderwidth=1)
        self.operation_list.pack(fill="both", expand=True)
        self.operation_list.bind("<<ListboxSelect>>", self._on_operation_select)

        ttk.Label(content, text="操作設定", style="Section.TLabel").pack(anchor="w", pady=(0, 4))
        self.form_scroll = ScrollableFrame(content)
        self.form_scroll.pack(fill="both", expand=True)
        self.form_title = ttk.Label(self.form_scroll.inner, text="請選擇左側功能", style="Header.TLabel")
        self.form_title.pack(anchor="w", pady=(4, 8))
        self.description_label = ttk.Label(
            self.form_scroll.inner, text="", wraplength=700, foreground="#964909",font=("Microsoft JhengHei UI", 11))
        self.description_label.pack(anchor="w", fill="x", pady=(0, 12))
        self.form_area = ttk.Frame(self.form_scroll.inner)
        self.form_area.pack(fill="x", expand=True)
        self.form_scroll.bind("<Configure>", self._schedule_wrap_update, add="+")
        self.form_scroll.inner.bind("<Configure>", self._schedule_wrap_update, add="+")
        self.form_area.bind("<Configure>", self._schedule_wrap_update, add="+")

        command_frame = ttk.LabelFrame(work_area, text="即將執行的命令", padding=6)
        command_frame.pack(fill="x", pady=(8, 6))
        command_text_frame = ttk.Frame(command_frame)
        command_text_frame.pack(fill="x", expand=True)
        self.command_text = tk.Text(
            command_text_frame, height=3, wrap="word", font=("Consolas", 10),
            foreground="#555555", selectbackground="#365a7a",
            relief="solid", borderwidth=1, padx=6, pady=4,
            undo=False, state="disabled")
        command_scroll = ttk.Scrollbar(
            command_text_frame, orient="vertical", command=self.command_text.yview)
        self.command_text.configure(yscrollcommand=command_scroll.set)
        self.command_text.pack(side="left", fill="both", expand=True)
        command_scroll.pack(side="right", fill="y")
        self.command_text.bind("<Control-a>", self._select_all_command)
        self.command_text.bind("<Control-A>", self._select_all_command)

        buttons = ttk.Frame(work_area)
        buttons.pack(fill="x", pady=(0, 2))
        self.run_button = ttk.Button(
            buttons, text="執行操作", style="Run.TButton", command=self._run_operation)
        self.run_button.pack(side="left")
        self.stop_button = ttk.Button(
            buttons, text="強制停止", command=self._stop_process, state="disabled")
        self.stop_button.pack(side="left", padx=6)
        ttk.Button(buttons, text="複製命令", command=self._copy_command).pack(side="left")
        self.progress = ttk.Progressbar(buttons, mode="indeterminate", length=130)
        self.progress.pack(side="right", padx=(8, 0))
        ttk.Label(buttons, textvariable=self.status_var).pack(side="right", padx=5)

        # 右側：獨立紀錄面板，附垂直捲軸與完整紀錄複製功能。
        log_toolbar = ttk.Frame(log_frame)
        log_toolbar.pack(fill="x", pady=(0, 5))
        ttk.Button(log_toolbar, text="複製紀錄", command=self._copy_log).pack(side="left")
        ttk.Button(log_toolbar, text="清除紀錄", command=self._clear_log).pack(side="right")

        log_text_frame = ttk.Frame(log_frame)
        log_text_frame.pack(fill="both", expand=True)
        self.log = tk.Text(
            log_text_frame, width=50, height=20, wrap="word", font=("Consolas", 12),
            background="#171a1f", foreground="#d9e0e8", insertbackground="white",
            selectbackground="#365a7a", relief="flat", padx=7, pady=6,
            undo=False, state="normal")
        log_scroll = ttk.Scrollbar(log_text_frame, orient="vertical", command=self.log.yview)
        self.log.configure(yscrollcommand=log_scroll.set)
        self.log.pack(side="left", fill="both", expand=True)
        log_scroll.pack(side="right", fill="y")
        self.log.tag_configure("success", foreground="#8fd18f")
        self.log.tag_configure("error", foreground="#ff8585")
        self.log.tag_configure("info", foreground="#8ecbff")
        self.log.bind("<Control-a>", self._select_all_log)
        self.log.bind("<Control-A>", self._select_all_log)
        self._log("GUI 已啟動。選擇左側功能後，設定參數並執行。", "info")
        self._schedule_wrap_update()

    def _schedule_wrap_update(self, _event: tk.Event | None = None) -> None:
        """將換行寬度更新合併到 idle，避免 Configure 事件反覆重入。"""
        if self._wrap_update_pending:
            return
        self._wrap_update_pending = True
        try:
            self.root.after_idle(self._update_wraplengths)
        except tk.TclError:
            self._wrap_update_pending = False

    def _update_wraplengths(self) -> None:
        self._wrap_update_pending = False
        targets: list[tuple[str, tk.Widget, int]] = []
        widgets: list[tuple[str, tk.Widget]] = [
            ("params_hint", getattr(self, "params_hint_label", None)),
            ("description", getattr(self, "description_label", None)),
        ]
        widgets.extend((f"help_{index}", label) for index, label in enumerate(self.help_labels))
        for key, widget in widgets:
            if widget is None:
                continue
            try:
                if not widget.winfo_exists():
                    continue
                width = widget.winfo_width()
                if width <= 1:
                    continue
                # 留出內距及可能的捲軸寬度；不讓小視窗的提示文字被硬性 700/1100px 限制。
                wraplength = max(80, min(1400, width - 18))
                targets.append((key, widget, wraplength))
            except tk.TclError:
                continue

        for key, widget, wraplength in targets:
            if self._last_wraplengths.get(key) == wraplength:
                continue
            try:
                widget.configure(wraplength=wraplength)
                self._last_wraplengths[key] = wraplength
            except tk.TclError:
                self._last_wraplengths.pop(key, None)

    def _set_command_preview(self, text: str) -> None:
        """同步保存命令字串並更新可選取的多行預覽框。"""
        self.command_var.set(text)
        if not hasattr(self, "command_text"):
            return
        try:
            self.command_text.configure(state="normal")
            self.command_text.delete("1.0", "end")
            self.command_text.insert("1.0", text)
            self.command_text.configure(state="disabled")
            self.command_text.yview_moveto(0)
        except tk.TclError:
            pass

    def _select_all_command(self, _event: tk.Event | None = None) -> str:
        try:
            self.command_text.configure(state="normal")
            self.command_text.tag_add("sel", "1.0", "end-1c")
            self.command_text.mark_set("insert", "1.0")
            self.command_text.see("insert")
            self.command_text.configure(state="disabled")
        except tk.TclError:
            pass
        return "break"

    def _on_global_path_change(self) -> None:
        self._update_params_hint()
        self._refresh_command_preview()

    def _on_operation_field_change(self) -> None:
        self._update_params_encoding_controls()
        self._update_params_hint()
        self._refresh_command_preview()

    @staticmethod
    def _params_encoding_mode_from_header(header: Any) -> str | None:
        """依 params.dat Header 判斷舊 ANSI 或 v05+ UTF-16LE 字串格式。"""
        if not isinstance(header, str):
            return None
        match = re.match(r"^\[SCR-PARAMS\]v(02|03|04|05(?:\.\d+)?)", header.strip())
        if not match:
            return None
        version = match.group(1)
        if version in {"02", "03", "04"}:
            return "legacy"
        if version == "05" or version.startswith("05."):
            return "utf16"
        return None

    def _detect_params_encoding_mode(self) -> str | None:
        """讀取 params 操作來源的版本標頭；import-json 則讀取 JSON 的 Header。"""
        operation = self.current_operation
        if not operation or operation.get("command", [])[:1] != ["params"]:
            return None
        if not ({"read_encoding", "write_encoding"} & set(self.field_vars)):
            return None

        source_var = self.field_vars.get("src")
        source_text = str(source_var.get()).strip() if source_var is not None else ""
        if not source_text:
            return None

        source_path = Path(source_text).expanduser()
        if not source_path.is_file():
            return None

        try:
            command = operation.get("command", [])
            if command[:2] == ["params", "import-json"]:
                with source_path.open("r", encoding="utf-8-sig") as stream:
                    document = json.load(stream)
                if not isinstance(document, dict):
                    return None
                return self._params_encoding_mode_from_header(document.get("Header"))

            with source_path.open("rb") as stream:
                header_text = stream.read(32).decode("ascii", errors="ignore")
            return self._params_encoding_mode_from_header(header_text)
        except (OSError, UnicodeError, json.JSONDecodeError, ValueError):
            return None

    def _remember_params_encoding_preferences(self) -> None:
        """記住目前舊版 params 操作的選擇，供切換操作後恢復。"""
        operation = self.current_operation
        if not operation or operation.get("command", [])[:1] != ["params"]:
            return
        for key in ("read_encoding", "write_encoding"):
            if key not in self.field_vars or key in self._params_encoding_fixed_keys:
                continue
            try:
                value = str(self._field_value(key))
            except (KeyError, tk.TclError):
                continue
            if value:
                self._params_encoding_preferences[key] = value

    def _update_params_encoding_controls(self) -> None:
        """v05+ 固定 UTF-16LE；v02-v04 恢復可選編碼並保留原選擇。"""
        mode = self._detect_params_encoding_mode()
        fixed = mode == "utf16"

        for key in ("read_encoding", "write_encoding"):
            if key not in self.field_vars or key not in self.field_widgets:
                continue

            variable = self.field_vars[key]
            widget = self.field_widgets[key]
            spec = self.field_defs[key]
            label_widget = self.field_labels.get(key)

            if fixed:
                if key not in self._params_encoding_fixed_keys:
                    current_value = str(self._field_value(key))
                    if current_value:
                        self._params_encoding_preferences[key] = current_value
                    self._params_encoding_saved_values[key] = current_value
                    self._params_encoding_fixed_keys.add(key)

                widget.configure(
                    values=["固定 UTF-16LE（不適用）"],
                    state="disabled",
                )
                variable.set("固定 UTF-16LE（不適用）")
                if label_widget is not None:
                    label_widget.configure(text=f"{spec['label']}（固定 UTF-16LE）")
                continue

            if key in self._params_encoding_fixed_keys:
                choices = spec.get("choices", [])
                values = [f"{name}  [{value}]" for name, value in choices]
                saved_value = self._params_encoding_preferences.get(
                    key,
                    self._params_encoding_saved_values.get(key, spec.get("default", "")),
                )
                shown_value = next(
                    (f"{name}  [{value}]" for name, value in choices if value == saved_value),
                    next(
                        (
                            f"{name}  [{value}]"
                            for name, value in choices
                            if value == spec.get("default")
                        ),
                        values[0] if values else "",
                    ),
                )
                widget.configure(values=values, state="readonly")
                variable.set(shown_value)
                self._params_encoding_fixed_keys.discard(key)
                self._params_encoding_saved_values.pop(key, None)

            if label_widget is not None:
                label_widget.configure(text=spec["label"])

    def _update_params_hint(self) -> None:
        """顯示可預期的 params.dat 搜尋路徑；不冒充 CLI 已成功載入。"""
        explicit = self.params_var.get().strip()
        if explicit:
            path = Path(explicit).expanduser()
            if path.is_file():
                self.params_hint_var.set(
                    f"已指定 params.dat：{path}（執行命令會附加 --params；優先於自動搜尋）")
            else:
                self.params_hint_var.set(f"指定的 params.dat 不存在：{path}")
            return

        exe_text = self.exe_var.get().strip()
        tool_params: Path | None = None
        if exe_text:
            try:
                tool_params = Path(exe_text).expanduser().resolve().parent / "params.dat"
            except OSError:
                tool_params = None

        operation = getattr(self, "current_operation", None)
        if (operation is not None
                and operation.get("command") == ["pic", "export-game"]
                and "game" in self.field_vars):
            operation_game = str(self.field_vars["game"].get()).strip()
            if not operation_game:
                self.params_hint_var.set(
                    "圖片自動匯出：請先選擇操作欄位中的遊戲根目錄；此欄位會成為 CLI 的 --game-root。")
                return

            operation_root = Path(operation_game).expanduser()
            if not operation_root.is_dir():
                self.params_hint_var.set(f"圖片自動匯出的遊戲根目錄不存在：{operation_root}")
                return

            operation_params = operation_root / "params.dat"
            if operation_params.is_file():
                self.params_hint_var.set(
                    f"圖片自動匯出將使用操作欄位指定的遊戲根目錄：{operation_root}。"
                    f"未指定全域 params.dat 時，CLI 會從此目錄載入 {operation_params}。")
            else:
                self.params_hint_var.set(
                    f"圖片自動匯出的遊戲根目錄沒有第一層 params.dat：{operation_params}。"
                    "目前 _ai_fix 不會回退至工具目錄；請在上方明確指定正確的 params.dat。")
            return

        game_root = self.game_root_var.get().strip()
        if not game_root:
            if tool_params is not None and tool_params.is_file():
                self.params_hint_var.set(
                    f"params.dat 留空：GUI 不傳入 --params。此 GUI 以 EXE 所在目錄作為子程序工作目錄；目前 _ai_fix 會依序搜尋工作目錄與 EXE 目錄，可能載入 {tool_params}。")
            else:
                self.params_hint_var.set(
                    "params.dat 留空且未設定遊戲根目錄：GUI 不傳入 --params 或 --game-root；目前 _ai_fix 會依序搜尋 CLI 工作目錄與 EXE 目錄。")
            return

        root_path = Path(game_root).expanduser()
        if not root_path.is_dir():
            self.params_hint_var.set(f"遊戲根目錄不存在：{root_path}")
            return

        candidate = root_path / "params.dat"
        if candidate.is_file():
            self.params_hint_var.set(
                f"遊戲根目錄第一層找到：{candidate}。GUI 只傳入 --game-root，不會附加 --params；實際載入仍由 EXE 決定。")
            return

        if tool_params is not None and tool_params.is_file():
            self.params_hint_var.set(
                f"遊戲根目錄第一層沒有 params.dat：{candidate}。目前 _ai_fix 的規則是：明確指定 --game-root 後只搜尋該目錄第一層，不會回退使用工具目錄的 {tool_params}。"
                "如需使用其他位置的 params.dat，請在上方明確指定。")
        else:
            self.params_hint_var.set(
                f"遊戲根目錄第一層沒有 params.dat：{candidate}。目前 _ai_fix 不會回退搜尋其他目錄；如 params.dat 位於其他位置，請在上方明確指定。")

    def _path_row(self, parent: ttk.LabelFrame, row: int, label: str, variable: tk.StringVar,
                  kind: str, extensions: list[tuple[str, str]] | None = None) -> None:
        ttk.Label(parent, text=label, width=13).grid(row=row, column=0, sticky="w", padx=(0, 5), pady=2)
        ttk.Entry(parent, textvariable=variable).grid(row=row, column=1, sticky="ew", pady=2)
        ttk.Button(parent, text="瀏覽", width=8,
                   command=lambda v=variable, k=kind, e=extensions: self._browse_into(v, k, e)).grid(row=row, column=2, padx=(6, 0), pady=2)
        parent.columnconfigure(1, weight=1)

    def _browse_exe(self) -> None:
        path = filedialog.askopenfilename(title="選擇 Kaguya_YaneKit.exe", filetypes=[("執行檔", "*.exe"), ("所有檔案", "*.*")])
        if path:
            self.exe_var.set(path)
            self._refresh_command_preview()

    def _browse_into(self, variable: tk.StringVar, kind: str, extensions: list[tuple[str, str]] | None = None) -> None:
        value = variable.get().strip()
        candidate = Path(value).expanduser() if value else None
        if kind in ("dir", "save_dir") and candidate is not None and candidate.is_dir():
            initial = str(candidate)
        elif candidate is not None and candidate.parent.is_dir():
            initial = str(candidate.parent)
        else:
            initial = os.getcwd()
        if kind in ("dir", "save_dir"):
            path = filedialog.askdirectory(title="選擇資料夾", initialdir=initial, mustexist=(kind == "dir"))
        elif kind == "save_file":
            path = filedialog.asksaveasfilename(title="指定輸出檔案", initialdir=initial,
                                                initialfile=Path(value).name if value else "",
                                                filetypes=extensions or [("所有檔案", "*.*")])
        else:
            path = filedialog.askopenfilename(title="選擇檔案", initialdir=initial,
                                              filetypes=extensions or [("所有檔案", "*.*")])
        if path:
            variable.set(path)
            self._refresh_command_preview()

    def _populate_operations(self) -> None:
        self.category_list.selection_clear(0, "end")
        index = CATEGORIES.index(self.category_var.get()) if self.category_var.get() in CATEGORIES else 0
        self.category_list.selection_set(index)
        self.category_list.activate(index)
        self._set_operation_list()

    def _set_operation_list(self) -> None:
        category = self.category_var.get()
        self.category_ops = [x for x in OPS if x["category"] == category]
        self.operation_list.delete(0, "end")
        for item in self.category_ops:
            self.operation_list.insert("end", item["title"])
        if self.category_ops:
            self.operation_list.selection_set(0)
            self.operation_list.activate(0)
            self._show_operation(self.category_ops[0])

    def _on_category_select(self, _event: tk.Event | None = None) -> None:
        selected = self.category_list.curselection()
        if not selected:
            return
        self.category_var.set(CATEGORIES[selected[0]])
        self._set_operation_list()

    def _on_operation_select(self, _event: tk.Event | None = None) -> None:
        selected = self.operation_list.curselection()
        if not selected:
            return
        self._show_operation(self.category_ops[selected[0]])

    def _show_operation(self, operation: dict[str, Any]) -> None:
        self._remember_params_encoding_preferences()
        self.current_operation = operation
        self.form_title.configure(text=operation["title"])
        self.description_label.configure(text=operation["description"])
        for child in self.form_area.winfo_children():
            child.destroy()
        self.field_vars.clear()
        self.field_widgets.clear()
        self.field_labels.clear()
        self.help_labels.clear()
        self._last_wraplengths = {
            key: value for key, value in self._last_wraplengths.items()
            if key in {"params_hint", "description"}
        }
        self._params_encoding_fixed_keys.clear()
        self._params_encoding_saved_values.clear()
        self.field_defs = {f["key"]: f for f in operation["fields"]}
        row = 0
        for spec in operation["fields"]:
            row = self._create_field(row, spec) + 1
        self._update_params_encoding_controls()
        self._update_params_hint()
        self._refresh_command_preview()
        self._schedule_wrap_update()

    def _create_field(self, row: int, spec: dict[str, Any]) -> int:
        key, label, kind = spec["key"], spec["label"], spec["kind"]
        var: tk.Variable
        if kind == "check":
            var = tk.BooleanVar(value=bool(spec["default"]))
            widget = ttk.Checkbutton(self.form_area, text=label, variable=var, command=self._on_operation_field_change)
            widget.grid(row=row, column=0, columnspan=3, sticky="w", pady=(5, 2))
            self.field_vars[key] = var
            self.field_widgets[key] = widget
            return row

        label_widget = ttk.Label(self.form_area, text=label, width=31)
        label_widget.grid(row=row, column=0, sticky="w", padx=(0, 8), pady=5)
        self.field_labels[key] = label_widget
        default = spec["default"]
        if (self.current_operation is not None
                and self.current_operation.get("command", [])[:1] == ["params"]
                and key in self._params_encoding_preferences):
            default = self._params_encoding_preferences[key]
        if kind == "choice":
            var = tk.StringVar(value=default)
            values = [f"{name}  [{value}]" for name, value in spec["choices"]]
            shown_default = next((f"{name}  [{value}]" for name, value in spec["choices"] if value == default), values[0] if values else "")
            var.set(shown_default)
            widget = ttk.Combobox(self.form_area, textvariable=var, values=values, state="readonly")
            widget.grid(row=row, column=1, columnspan=2, sticky="ew", pady=4)
            widget.bind("<<ComboboxSelected>>", lambda _e: self._on_operation_field_change())
        else:
            var = tk.StringVar(value=default)
            widget = ttk.Entry(self.form_area, textvariable=var)
            widget.grid(row=row, column=1, sticky="ew", pady=4)
            var.trace_add("write", lambda *_: self._on_operation_field_change())
            if kind in ("file", "dir", "save_file", "save_dir", "any_path"):
                browse_kind = kind
                if kind == "any_path":
                    browse = ttk.Button(self.form_area, text="選擇檔案／資料夾", command=lambda v=var: self._browse_any(v))
                else:
                    browse = ttk.Button(self.form_area, text="瀏覽", command=lambda v=var, k=browse_kind, e=spec.get("extensions"): self._browse_into(v, k, e))
                browse.grid(row=row, column=2, padx=(6, 0), pady=4)
        self.field_vars[key] = var
        self.field_widgets[key] = widget
        if spec.get("help"):
            help_label = ttk.Label(self.form_area, text=spec["help"], foreground="#777777", wraplength=700, font=("Microsoft JhengHei UI", 14))
            help_label.grid(row=row + 1, column=1, columnspan=2, sticky="ew", pady=(0, 3))
            self.help_labels.append(help_label)
            self.form_area.columnconfigure(1, weight=1)
            return row + 1
        self.form_area.columnconfigure(1, weight=1)
        return row

    def _browse_any(self, variable: tk.StringVar) -> None:
        choice = messagebox.askyesnocancel("選擇輸入類型", "要選擇資料夾嗎？\n\n是：資料夾\n否：檔案\n取消：不變更")
        if choice is True:
            path = filedialog.askdirectory(title="選擇資料夾")
        elif choice is False:
            path = filedialog.askopenfilename(title="選擇檔案", filetypes=[("所有檔案", "*.*")])
        else:
            return
        if path:
            variable.set(path)
            self._refresh_command_preview()

    def _field_value(self, key: str) -> Any:
        # v05+ 的編碼欄位固定 UTF-16LE，略過必填欄位檢查。
        if key in self._params_encoding_fixed_keys:
            return ""
        variable = self.field_vars[key]
        value = variable.get()
        spec = self.field_defs[key]
        if spec["kind"] == "choice":
            selected = str(value)
            match = re.search(r"\[([^\]]+)\]\s*$", selected)
            return match.group(1) if match else spec["default"]
        return value

    @staticmethod
    def _absolute_path(value: str) -> Path:
        return Path(value).expanduser().resolve(strict=False)

    @staticmethod
    def _path_is_within(path: Path, parent: Path) -> bool:
        try:
            path_text = os.path.normcase(str(path))
            parent_text = os.path.normcase(str(parent))
            return os.path.commonpath((path_text, parent_text)) == parent_text
        except (ValueError, OSError):
            return False

    @staticmethod
    def _same_path(left: Path, right: Path) -> bool:
        try:
            if left.exists() and right.exists() and os.path.samefile(left, right):
                return True
        except OSError:
            pass
        return os.path.normcase(str(left)) == os.path.normcase(str(right))

    def _check_output_collisions(self, input_paths: list[Path], output_paths: list[tuple[Path, str]]) -> None:
        for output, output_kind in output_paths:
            for source in input_paths:
                if self._same_path(output, source):
                    raise ValueError(f"輸出路徑與輸入路徑相同，為避免破壞原始資料已阻止執行：\n{output}")
                if output_kind == "save_dir":
                    if self._path_is_within(output, source) or self._path_is_within(source, output):
                        raise ValueError(
                            "輸出資料夾與輸入路徑重疊，為避免覆蓋或混入原始資料已阻止執行：\n"
                            f"輸入：{source}\n輸出：{output}"
                        )
                elif source.is_dir() and self._path_is_within(output, source):
                    raise ValueError(
                        "輸出檔案不可放在輸入資料夾內，避免回封或批次處理時混入輸出檔案：\n"
                        f"輸入資料夾：{source}\n輸出檔案：{output}"
                    )

    def _validate_path_fields(self, operation: dict[str, Any], values: dict[str, Any]) -> tuple[list[Path], list[tuple[Path, str]]]:
        input_paths: list[Path] = []
        output_paths: list[tuple[Path, str]] = []

        for spec in operation["fields"]:
            key = spec["key"]
            kind = spec["kind"]
            # v05+ 的編碼欄位固定 UTF-16LE，略過必填欄位檢查。
            if key in self._params_encoding_fixed_keys:
                continue
            raw_value = str(values.get(key, "")).strip()
            if kind == "check":
                continue
            if spec["required"] and not raw_value:
                raise ValueError(f"請填寫「{spec['label']}」。")
            if not raw_value or kind not in ("file", "dir", "any_path", "save_file", "save_dir"):
                continue

            path = self._absolute_path(raw_value)
            values[key] = str(path)
            if kind == "file":
                if not path.is_file():
                    raise FileNotFoundError(f"找不到檔案：{path}")
                input_paths.append(path)
            elif kind == "dir":
                if not path.is_dir():
                    raise NotADirectoryError(f"找不到資料夾：{path}")
                input_paths.append(path)
            elif kind == "any_path":
                if not (path.is_file() or path.is_dir()):
                    raise FileNotFoundError(f"找不到檔案或資料夾：{path}")
                input_paths.append(path)
            elif kind == "save_file":
                if path.exists() and path.is_dir():
                    raise IsADirectoryError(f"輸出檔案路徑目前是資料夾：{path}")
                if not path.parent.is_dir():
                    raise FileNotFoundError(f"輸出檔案的上層資料夾不存在：{path.parent}")
                output_paths.append((path, kind))
            elif kind == "save_dir":
                if path.exists() and not path.is_dir():
                    raise NotADirectoryError(f"輸出資料夾路徑目前是檔案：{path}")
                output_paths.append((path, kind))

        # 輸出不能指向輸入檔案，也不能把輸出資料夾放進輸入樹或反過來包住輸入。
        self._check_output_collisions(input_paths, output_paths)

        return input_paths, output_paths

    def _make_encoding_override_ini(
        self,
        source: Path,
        read_encoding: str,
        write_encoding: str,
        override_read: bool,
        override_write: bool,
    ) -> str:
        """建立 GUI 專用 INI，將明確選取的編碼寫入作用中的 Profile。"""
        source = source.expanduser().resolve(strict=True)
        original_bytes = source.read_bytes()
        original = original_bytes.decode("utf-8-sig").splitlines()

        active_id = "0"
        for line in original:
            match = re.match(r"\s*ConfigurationID\s*=\s*(.*?)\s*$", line, re.IGNORECASE)
            if match:
                active_id = match.group(1).strip() or "0"
                break

        active_prefix = f"■[{active_id}]["
        if not any(line.strip().startswith(active_prefix) for line in original):
            active_id = "0"
            active_prefix = "■[0]["

        overrides: dict[str, str] = {}
        if override_read:
            overrides["readingencoding"] = f"ReadingEncoding = {read_encoding}"
        if override_write:
            overrides["writingencoding"] = f"WritingEncoding = {write_encoding}"

        # 只修改作用中 Profile；其他 Profile、全域設定及占位符資料都保留。
        output: list[str] = []
        emitted: set[str] = set()
        in_active = False
        active_found = False

        def append_missing_overrides() -> None:
            for key, line in overrides.items():
                if key not in emitted:
                    output.append(line)
                    emitted.add(key)

        for line in original:
            stripped = line.strip()
            if stripped.startswith("■["):
                if in_active:
                    append_missing_overrides()
                in_active = stripped.startswith(active_prefix)
                active_found = active_found or in_active
                output.append(line)
                continue

            if in_active and "=" in stripped and not stripped.startswith(";"):
                key = stripped.split("=", 1)[0].strip().lower()
                if key in overrides:
                    if key not in emitted:
                        output.append(overrides[key])
                        emitted.add(key)
                    # 重複的同名設定一併移除，避免後續值覆蓋 GUI 選取值。
                    continue

            output.append(line)

        if in_active:
            append_missing_overrides()

        # 原始 INI 沒有可用的 [0] Profile 時，建立一個最小 Profile，供 CLI 正常讀取覆寫值。
        if not active_found:
            if output and output[-1].strip():
                output.append("")
            output.append("■[0][GUI encoding overrides]")
            append_missing_overrides()

        signature = b"\0".join((
            str(source).encode("utf-8"),
            hashlib.sha256(original_bytes).hexdigest().encode("ascii"),
            read_encoding.encode("utf-8"),
            write_encoding.encode("utf-8"),
            str(override_read).encode("ascii"),
            str(override_write).encode("ascii"),
        ))
        filename = hashlib.sha256(signature).hexdigest() + ".ini"
        base = Path(os.environ.get("LOCALAPPDATA") or os.environ.get("APPDATA") or tempfile.gettempdir())
        folder = base / "Kaguya_YaneKit_GUI" / "encoding_overrides"
        folder.mkdir(parents=True, exist_ok=True)
        destination = folder / filename
        generated_text = "\n".join(output) + "\n"
        if not destination.is_file() or destination.read_text(encoding="utf-8") != generated_text:
            destination.write_text(generated_text, encoding="utf-8")
        return str(destination.resolve())

    def _build_commands(self) -> list[list[str]]:
        """建立命令清單。Sort + Convert 會依序執行兩個原始 CLI 子命令。"""
        operation = self.current_operation
        if operation is None:
            raise ValueError("請先選擇要執行的操作。")

        exe_text = self.exe_var.get().strip()
        if not exe_text:
            raise FileNotFoundError("請先指定 Kaguya_YaneKit.exe。")
        exe = self._absolute_path(exe_text)
        if not exe.is_file():
            raise FileNotFoundError(f"找不到 Kaguya_YaneKit.exe：{exe}")

        values: dict[str, Any] = {key: self._field_value(key) for key in self.field_vars}
        input_paths, output_paths = self._validate_path_fields(operation, values)
        # EXE 與全域 params.dat 一律視為受保護的輸入，不能被輸出覆蓋。
        input_paths.append(exe)

        game_root = self.game_root_var.get().strip()
        workdir = self.workdir_var.get().strip()
        params = self.params_var.get().strip()

        uses_runtime_context = operation["command"][0] in {
            "scr", "pic", "character", "link", "archive_unpack", "archive_pack"
        }
        is_pic_export_game = operation["command"] == ["pic", "export-game"]
        global_args: list[str] = []

        # pic export-game 自己帶有遊戲根目錄與輸出工作目錄欄位。
        # 必須用這兩個欄位建立 EXE 的 runtime context，否則 CLI 可能先從上方全域路徑、
        # CWD 或工具目錄載入另一款遊戲的 params.dat，之後才執行 export-game。
        if is_pic_export_game:
            game_root_path = self._absolute_path(str(values["game"]))
            workdir_path = self._absolute_path(str(values["work"]))
            if not game_root_path.is_dir():
                raise NotADirectoryError(f"遊戲根目錄不存在：{game_root_path}")
            if self._path_is_within(workdir_path, game_root_path) or self._path_is_within(game_root_path, workdir_path):
                raise ValueError(
                    "圖片匯出工作目錄不可與遊戲根目錄相同、互相包含或放在遊戲根目錄內，"
                    "請使用獨立的工作資料夾。"
                )
            if workdir_path.exists() and not workdir_path.is_dir():
                raise NotADirectoryError(f"圖片匯出工作目錄目前是檔案：{workdir_path}")
            global_args += ["--game-root", str(game_root_path), "--workdir", str(workdir_path)]
        else:
            if game_root:
                game_root_path = self._absolute_path(game_root)
                if not game_root_path.is_dir():
                    raise NotADirectoryError(f"遊戲根目錄不存在：{game_root_path}")
                global_args += ["--game-root", str(game_root_path)]
                if uses_runtime_context:
                    input_paths.append(game_root_path)
            else:
                game_root_path = None

            if workdir:
                workdir_path = self._absolute_path(workdir)
                if game_root_path and uses_runtime_context and self._path_is_within(workdir_path, game_root_path):
                    raise ValueError("工作目錄不可放在遊戲根目錄內，避免工具輸出混入原始遊戲檔案。")
                if workdir_path.exists() and not workdir_path.is_dir():
                    raise NotADirectoryError(f"工作目錄路徑目前是檔案：{workdir_path}")
                global_args += ["--workdir", str(workdir_path)]

        if params:
            params_path = self._absolute_path(params)
            if not params_path.is_file():
                raise FileNotFoundError(f"params.dat 不存在：{params_path}")
            input_paths.append(params_path)
            global_args += ["--params", str(params_path)]
        else:
            # _ai_fix：有明確 --game-root 時只搜尋該遊戲根目錄第一層；不會回退到工具目錄。
            # 未指定 --game-root 時，CLI 依序搜尋子程序工作目錄及 EXE 目錄。
            if uses_runtime_context and game_root_path:
                auto_params = game_root_path / "params.dat"
            else:
                auto_params = exe.parent / "params.dat"
            if auto_params.is_file():
                input_paths.append(auto_params.resolve())

        self._check_output_collisions(input_paths, output_paths)

        # msg CLI 會優先採用 INI 內作用中 Profile 的編碼。
        # 使用持久化的覆寫 INI，讓 GUI 實際執行與複製命令完全一致；原始 INI 不會被修改。
        if operation["command"] and operation["command"][0] == "msg" and "ini" in values:
            if values.get("ini"):
                ini_path = self._absolute_path(str(values["ini"]))
            else:
                candidate = exe.parent / "ini" / "message_config.ini"
                ini_path = candidate if candidate.is_file() else None

            if ini_path is not None:
                if not ini_path.is_file():
                    raise FileNotFoundError(f"message_config.ini 不存在：{ini_path}")
                read_encoding = str(values.get("read_encoding") or "cp932")
                write_encoding = str(values.get("write_encoding") or read_encoding)
                values["ini"] = self._make_encoding_override_ini(
                    ini_path,
                    read_encoding,
                    write_encoding,
                    override_read="read_encoding" in values,
                    override_write="write_encoding" in values,
                )

        steps = operation.get("steps") or [{
            "command": operation["command"],
            "positional": operation["positional"],
        }]
        commands: list[list[str]] = []
        for step in steps:
            args: list[str] = [str(exe), *global_args]
            args += step["command"]
            for key in step.get("positional", []):
                value = values.get(key, "")
                if value:
                    args.append(str(value))

            for key, option in operation["options"]:
                value = values.get(key, "")
                if value:
                    args += [option, str(value)]
            for key, flag in operation["flags"]:
                if bool(values.get(key, False)):
                    args.append(flag)
            for key, mapping in operation["choice_flags"]:
                choice = str(values.get(key, ""))
                spec = self.field_defs.get(key)
                if spec and spec["kind"] == "choice":
                    choice = str(values.get(key, spec["default"]))
                args.extend(mapping.get(choice, []))
            commands.append(args)

        return commands

    def _build_command(self) -> list[str]:
        """相容單命令預覽；複合操作請使用 _build_commands。"""
        return self._build_commands()[0]

    def _confirm_existing_outputs(self, operation: dict[str, Any], output_paths: list[tuple[Path, str]], values: dict[str, Any]) -> bool:
        existing = [path for path, _kind in output_paths if path.exists()]

        # message import 在工作流程啟用且 INI 設定 MsgLengthFix 時，可能額外產生 message_fix.txt。
        # 即使無法僅由 GUI 判定 INI 的 MsgLengthFix 值，也先保護這個固定副產物。
        sidecar: Path | None = None
        if (operation["command"][:2] == ["msg", "import"]
                and not bool(values.get("no_workflow", False))
                and values.get("txt")):
            text_path = self._absolute_path(str(values["txt"]))
            sidecar = text_path.parent / "message_fix.txt"
            if self._same_path(sidecar, text_path):
                raise ValueError(
                    "翻譯 TXT 檔名不可使用 message_fix.txt：CLI 的長度修正流程可能會以此固定名稱輸出預覽，造成原翻譯檔被覆蓋。"
                )
            for output, _kind in output_paths:
                if self._same_path(sidecar, output):
                    raise ValueError("DAT 輸出路徑不可與可能產生的 message_fix.txt 相同。")
            if sidecar.exists():
                existing.append(sidecar)

        if not existing:
            return True

        lines = "\n".join(f"• {path}" for path in existing)
        archive_overwrite = bool(values.get("overwrite", False)) and operation["command"] == ["archive_unpack"]
        if archive_overwrite:
            message = (
                "下列輸出路徑已存在。你已允許 ARC 解包覆蓋同名檔案，請確認這些檔案可以被取代：\n\n"
                f"{lines}\n\n是否繼續？"
            )
        else:
            message = (
                "下列輸出路徑已存在。部分操作可能更新其中的檔案，請先確認已有資料可被覆蓋；"
                "若 CLI 採取不覆蓋策略，仍可能回報檔案已存在：\n\n"
                f"{lines}\n\n是否繼續？"
            )
        return messagebox.askyesno("確認輸出路徑", message)

    def _refresh_command_preview(self) -> None:
        try:
            commands = self._build_commands()
            if len(commands) == 1:
                preview = subprocess.list2cmdline(commands[0])
            else:
                preview = "\n&& ".join(subprocess.list2cmdline(command) for command in commands)
            self._set_command_preview(preview)
        except Exception as exc:
            self._set_command_preview(f"參數尚未完整：{exc}")

    def _run_operation(self) -> None:
        if self.worker and self.worker.is_alive():
            return
        try:
            commands = self._build_commands()
            values = {key: self._field_value(key) for key in self.field_vars}
            _inputs, output_paths = self._validate_path_fields(self.current_operation or {}, values)
            if not self._confirm_existing_outputs(self.current_operation or {}, output_paths, values):
                self.status_var.set("已取消")
                return
            exe_dir = str(Path(commands[0][0]).parent)
            cwd = exe_dir
            if len(commands) == 1:
                self._set_command_preview(subprocess.list2cmdline(commands[0]))
            else:
                self._set_command_preview("\n&& ".join(subprocess.list2cmdline(command) for command in commands))
        except Exception as exc:
            messagebox.showerror("無法執行", str(exc))
            return

        self.run_button.configure(state="disabled")
        self.stop_button.configure(state="disabled")
        self.progress.start(10)
        self.status_var.set("準備啟動…")
        self._log("\n" + "=" * 78, "info")
        for index, command in enumerate(commands, start=1):
            if len(commands) > 1:
                self._log(f"執行步驟 {index}/{len(commands)}：" + subprocess.list2cmdline(command), "info")
            else:
                self._log("執行：" + subprocess.list2cmdline(command), "info")

        def command_option(command: list[str], option: str) -> str | None:
            try:
                index = command.index(option)
                return command[index + 1] if index + 1 < len(command) else None
            except ValueError:
                return None

        effective_game_root = command_option(commands[0], "--game-root")
        effective_workdir = command_option(commands[0], "--workdir")
        effective_params = command_option(commands[0], "--params")
        if effective_game_root:
            self._log(f"CLI 實際遊戲根目錄：{effective_game_root}", "info")
        if effective_workdir:
            self._log(f"CLI 工作目錄：{effective_workdir}", "info")
        if effective_params:
            self._log(f"params.dat 指定路徑：{effective_params}", "info")
        elif effective_game_root:
            self._log(
                "params.dat：未手動指定；目前 _ai_fix 只會從上述遊戲根目錄第一層尋找，不會回退到工具目錄。",
                "info",
            )
        else:
            self._log(
                "params.dat：未手動指定；目前 _ai_fix 會依序搜尋 CLI 工作目錄與 EXE 目錄。",
                "info",
            )

        self.worker = threading.Thread(target=self._execute, args=(commands, cwd), daemon=True)
        self.worker.start()

    def _execute(self, commands: list[list[str]], cwd: str) -> None:
        started = time.time()
        try:
            for index, command in enumerate(commands, start=1):
                process: subprocess.Popen[bytes] | None = None
                with self._process_lock:
                    if self._closing:
                        return
                    creationflags = getattr(subprocess, "CREATE_NO_WINDOW", 0)
                    process = subprocess.Popen(
                        command, cwd=cwd, stdin=subprocess.DEVNULL,
                        stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
                        text=False, bufsize=0,
                        creationflags=creationflags,
                    )
                    self.process = process

                self.events.put(("started", (process.pid, index, len(commands))))
                assert process.stdout is not None

                # 使用二進位管線分塊讀取；UTF-8 增量解碼可處理跨區塊的多位元組字元。
                decoder = codecs.getincrementaldecoder("utf-8")(errors="replace")
                pending_cr = False
                last_output_char = ""

                def normalize_newlines(text: str, final: bool = False) -> str:
                    nonlocal pending_cr

                    # 前一個區塊尾端若是 CR，需與本區塊開頭的 LF 合併。
                    if pending_cr:
                        if text.startswith("\n"):
                            text = text[1:]
                        text = "\n" + text
                        pending_cr = False

                    # 區塊最後一個 CR 先保留，等待下一個區塊判斷是否為 CRLF。
                    if not final and text.endswith("\r"):
                        text = text[:-1]
                        pending_cr = True

                    return text.replace("\r\n", "\n").replace("\r", "\n")

                while True:
                    chunk = os.read(process.stdout.fileno(), STDOUT_READ_SIZE)
                    if not chunk:
                        break

                    decoded = decoder.decode(chunk, final=False)
                    normalized = normalize_newlines(decoded)
                    if normalized:
                        last_output_char = normalized[-1]
                        self.events.put(("output", normalized))

                tail = decoder.decode(b"", final=True)
                normalized_tail = normalize_newlines(tail, final=True)
                if normalized_tail:
                    last_output_char = normalized_tail[-1]
                    self.events.put(("output", normalized_tail))

                # 舊版逐行讀取會為未以換行結尾的最後一行補上換行。
                if last_output_char and last_output_char != "\n":
                    self.events.put(("output", "\n"))

                process.stdout.close()
                code = process.wait()

                with self._process_lock:
                    if self.process is process:
                        self.process = None

                if code != 0:
                    elapsed = time.time() - started
                    self.events.put(("done", (code, elapsed, index, len(commands))))
                    return

            elapsed = time.time() - started
            self.events.put(("done", (0, elapsed, len(commands), len(commands))))

        except Exception as exc:
            self.events.put(("failed", str(exc)))

        finally:
            with self._process_lock:
                self.process = None

    def _poll_events(self) -> None:
        try:
            while True:
                kind, payload = self.events.get_nowait()
                if kind == "started":
                    pid, step_index, step_total = payload
                    self.stop_button.configure(state="normal")
                    if step_total > 1:
                        self.status_var.set(f"執行步驟 {step_index}/{step_total}（PID {pid}）")
                    else:
                        self.status_var.set(f"執行中（PID {pid}）")

                elif kind == "output":
                    self._log_batch(str(payload))

                elif kind == "done":
                    code, elapsed, completed_steps, total_steps = payload
                    self.progress.stop()
                    self.run_button.configure(state="normal")
                    self.stop_button.configure(state="disabled")
                    if code == 0:
                        suffix = f"；完成 {completed_steps} 個步驟" if total_steps > 1 else ""
                        self.status_var.set(f"完成（{elapsed:.1f} 秒）")
                        self._log(f"執行完成{suffix}，Exit code = {code}，耗時 {elapsed:.2f} 秒。", "success")
                    else:
                        self.status_var.set(f"步驟 {completed_steps}/{total_steps} 失敗，Exit code = {code}")
                        self._log(f"執行失敗：步驟 {completed_steps}/{total_steps}，Exit code = {code}。後續步驟已停止。", "error")

                elif kind == "failed":
                    self.progress.stop()
                    self.run_button.configure(state="normal")
                    self.stop_button.configure(state="disabled")
                    self.status_var.set("執行錯誤")
                    self._log("執行錯誤：" + str(payload), "error")

        except queue.Empty:
            pass

        self.root.after(100, self._poll_events)

    def _stop_process(self) -> None:
        with self._process_lock:
            process = self.process
        if process and process.poll() is None:
            try:
                process.terminate()
                self._log("已送出強制終止要求。正在等待 CLI 結束；目前輸出檔案可能不完整。", "error")
                self.status_var.set("正在強制停止…")
                self.stop_button.configure(state="disabled")
            except Exception as exc:
                messagebox.showerror("停止失敗", str(exc))

    def _copy_command(self) -> None:
        try:
            commands = self._build_commands()
            command_text = " && ".join(subprocess.list2cmdline(command) for command in commands)
            self.root.clipboard_clear()
            self.root.clipboard_append(command_text)
            self.status_var.set("命令已複製（編碼覆寫設定檔會保留在本機使用者資料夾）")
        except Exception as exc:
            messagebox.showerror("無法複製命令", str(exc))

    def _clear_log(self) -> None:
        self.log.delete("1.0", "end")

    def _copy_log(self) -> None:
        content = self.log.get("1.0", "end-1c")
        if not content:
            self.status_var.set("沒有可複製的執行紀錄")
            return
        self.root.clipboard_clear()
        self.root.clipboard_append(content)
        self.status_var.set("執行紀錄已複製")

    def _select_all_log(self, _event: tk.Event | None = None) -> str:
        self.log.tag_add("sel", "1.0", "end-1c")
        self.log.mark_set("insert", "1.0")
        self.log.see("insert")
        return "break"

    def _log(self, text: str, tag: str | None = None) -> None:
        self.log.insert("end", text + "\n", tag or ())
        self._trim_log()
        self.log.see("end")

    def _log_batch(self, text: str) -> None:
        """一次插入一批 CLI stdout，避免逐行操作 Tk Text 控制項。"""
        if not text:
            return
        self.log.insert("end", text)
        self._trim_log()
        self.log.see("end")

    def _trim_log(self) -> None:
        try:
            end_line = int(self.log.index("end-1c").split(".", 1)[0])
            if end_line > MAX_LOG_LINES:
                self.log.delete("1.0", f"{end_line - MAX_LOG_LINES}.0")
        except (tk.TclError, ValueError):
            pass

    def _on_close(self) -> None:
        # 先封鎖新的子程序啟動，再讀取目前程序，避免關閉視窗時漏掉剛啟動的 EXE。
        with self._process_lock:
            self._closing = True
            process = self.process

        if process and process.poll() is None:
            if not messagebox.askyesno(
                "結束程式",
                "EXE 仍在執行中。強制終止可能留下不完整的輸出檔案。\n\n確定要強制停止並關閉 GUI 嗎？",
            ):
                with self._process_lock:
                    self._closing = False
                return

        if process and process.poll() is None:
            try:
                process.terminate()
                try:
                    process.wait(timeout=5)
                except subprocess.TimeoutExpired:
                    process.kill()
                    process.wait(timeout=3)
            except Exception:
                pass

        if self.worker and self.worker.is_alive():
            self.worker.join(timeout=1)
        self.root.destroy()


def _enable_windows_dpi_awareness() -> None:
    """在建立 Tk 視窗前設定 DPI 感知，盡可能採用 Per-Monitor V2。"""
    if os.name != "nt":
        return
    try:
        import ctypes
        # Windows 10/11：Per-Monitor V2。若系統或程序已設定，則改用舊 API fallback。
        try:
            if ctypes.windll.user32.SetProcessDpiAwarenessContext(ctypes.c_void_p(-4)):
                return
        except Exception:
            pass
        try:
            ctypes.windll.shcore.SetProcessDpiAwareness(2)
            return
        except Exception:
            pass
        try:
            ctypes.windll.user32.SetProcessDPIAware()
        except Exception:
            pass
    except Exception:
        pass


def main() -> None:
    _enable_windows_dpi_awareness()
    root = tk.Tk()
    KaguyaGui(root)
    root.mainloop()


if __name__ == "__main__":
    main()
