/* chihana_gbk.c  --  version.dll 代理 DLL：讓「原版」A000.exe 以簡中區域(ACP 936)顯示 GBK
 *
 * 放在 A000.exe 同資料夾，命名為 version.dll（exe 匯入 VERSION.dll，且它不在 KnownDLLs，所以會先載入同資料夾的這個）。
 * 它把真正的 version.dll 轉接出去，並在載入時做和 A001_zhCN.exe + patch_A000_GBK.py 相同的事：
 *   1. CreateFontIndirectA：字元集 / 字型名 / 粗細 / 字高（可由 ChihanaGBK.ini 調整），含 Lib\Graphics.dll
 *   2. WideCharToMultiByte(CP_THREAD_ACP)：預設 932（資源名維持 Shift-JIS），params.dat 的顯示欄位讀取期間改 936
 *   3. 】【 改成 GBK 碼
 * 所有修改只在記憶體中進行，原版 exe 不會被改動。
 * params 讀取點與 】【 位址由 kaguya_scan.h 在載入時自動掃描（已驗證 A000 / B000(4328) / h000）；掃不到時只略過這部分。
 * 版本與說明見 chihana_version.h / chihana_gbk.rc（編譯進 DLL 的版本資源）。
 */
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <stdio.h>
#include <string.h>
#include <stdlib.h>
#include <stdarg.h>
#include "kaguya_scan.h"
#include "chihana_version.h"

/* ---------------- 設定（可由 ChihanaGBK.ini 覆蓋） ---------------- */
static char g_face[LF_FACESIZE] = "SimHei";
static int  g_charset = 134;      /* GB2312_CHARSET */
static int  g_weight  = 0;        /* 0 = 不改；>0：lfWeight < 400 的字型改成此值 */
static int  g_hscale  = 100;      /* 字高縮放百分比 */
static int  g_debug   = 0;
static char g_logpath[MAX_PATH];

__attribute__((used)) volatile LONG g_gbkDepth = 0;   /* >0：目前正在讀 params 的顯示欄位 */
__attribute__((used)) DWORD g_savedRet = 0;
__attribute__((used)) DWORD g_realReader = 0; /* params string16 reader（載入時掃描得到） */

static void logf_(const char *fmt, ...) {
    if (!g_debug) return;
    FILE *f = fopen(g_logpath, "a"); if (!f) return;
    va_list ap; va_start(ap, fmt); vfprintf(f, fmt, ap); va_end(ap); fputc('\n', f); fclose(f);
}

/* ---------------- 真正的 version.dll 轉接 ---------------- */
static HMODULE g_realVer;
typedef BOOL  (WINAPI *PFN_GFVI)(LPCSTR, DWORD, DWORD, LPVOID);
typedef DWORD (WINAPI *PFN_GFVIS)(LPCSTR, LPDWORD);
typedef BOOL  (WINAPI *PFN_VQV)(LPCVOID, LPCSTR, LPVOID *, PUINT);
static PFN_GFVI  p_GFVI;  static PFN_GFVIS p_GFVIS;  static PFN_VQV p_VQV;

__declspec(dllexport) BOOL WINAPI GetFileVersionInfoA(LPCSTR a, DWORD b, DWORD c, LPVOID d) { return p_GFVI ? p_GFVI(a, b, c, d) : FALSE; }
__declspec(dllexport) DWORD WINAPI GetFileVersionInfoSizeA(LPCSTR a, LPDWORD b) { return p_GFVIS ? p_GFVIS(a, b) : 0; }
__declspec(dllexport) BOOL WINAPI VerQueryValueA(LPCVOID a, LPCSTR b, LPVOID *c, PUINT d) { return p_VQV ? p_VQV(a, b, c, d) : FALSE; }

static void load_real_version(void) {
    char p[MAX_PATH]; GetSystemDirectoryA(p, MAX_PATH); lstrcatA(p, "\\version.dll");
    g_realVer = LoadLibraryA(p);
    if (!g_realVer) return;
    p_GFVI  = (PFN_GFVI)  GetProcAddress(g_realVer, "GetFileVersionInfoA");
    p_GFVIS = (PFN_GFVIS) GetProcAddress(g_realVer, "GetFileVersionInfoSizeA");
    p_VQV   = (PFN_VQV)   GetProcAddress(g_realVer, "VerQueryValueA");
}

/* ---------------- IAT 修補 ---------------- */
static BOOL patch_iat(HMODULE mod, const char *dll, const char *func, void *hook) {
    BYTE *b = (BYTE *)mod;
    IMAGE_DOS_HEADER *dh = (IMAGE_DOS_HEADER *)b;
    if (dh->e_magic != IMAGE_DOS_SIGNATURE) return FALSE;
    IMAGE_NT_HEADERS *nt = (IMAGE_NT_HEADERS *)(b + dh->e_lfanew);
    if (nt->Signature != IMAGE_NT_SIGNATURE) return FALSE;
    DWORD rva = nt->OptionalHeader.DataDirectory[IMAGE_DIRECTORY_ENTRY_IMPORT].VirtualAddress;
    if (!rva) return FALSE;
    for (IMAGE_IMPORT_DESCRIPTOR *d = (IMAGE_IMPORT_DESCRIPTOR *)(b + rva); d->Name; d++) {
        if (lstrcmpiA((const char *)(b + d->Name), dll)) continue;
        IMAGE_THUNK_DATA *oft = (IMAGE_THUNK_DATA *)(b + (d->OriginalFirstThunk ? d->OriginalFirstThunk : d->FirstThunk));
        IMAGE_THUNK_DATA *ft = (IMAGE_THUNK_DATA *)(b + d->FirstThunk);
        for (; oft->u1.AddressOfData; oft++, ft++) {
            if (IMAGE_SNAP_BY_ORDINAL32(oft->u1.Ordinal)) continue;
            IMAGE_IMPORT_BY_NAME *n = (IMAGE_IMPORT_BY_NAME *)(b + oft->u1.AddressOfData);
            if (strcmp((const char *)n->Name, func)) continue;
            DWORD old;
            if (!VirtualProtect(&ft->u1.Function, 4, PAGE_READWRITE, &old)) return FALSE;
            ft->u1.Function = (DWORD)hook;
            VirtualProtect(&ft->u1.Function, 4, old, &old);
            return TRUE;
        }
    }
    return FALSE;
}

/* ---------------- 追蹤（Debug=2） ---------------- */
static void hex_(char *dst, size_t n, const unsigned char *p, int len) {
    size_t o = 0; for (int i = 0; i < len && o + 4 < n; i++) o += wsprintfA(dst + o, "%02X ", p[i]); dst[o] = 0;
}

/* ---------------- hooks ---------------- */
typedef HFONT (WINAPI *PFN_CFIA)(const LOGFONTA *);
typedef int   (WINAPI *PFN_WCTMB)(UINT, DWORD, LPCWSTR, int, LPSTR, int, LPCSTR, LPBOOL);
typedef HMODULE (WINAPI *PFN_LLA)(LPCSTR);
static PFN_CFIA real_CFIA; static PFN_WCTMB real_WCTMB; static PFN_LLA real_LLA;

static HFONT WINAPI My_CFIA(const LOGFONTA *in) {
    if (!in) return real_CFIA(in);
    LOGFONTA lf = *in;                         /* 不修改呼叫者的結構 */
    lf.lfCharSet = (BYTE)g_charset;
    if (g_face[0]) { memset(lf.lfFaceName, 0, LF_FACESIZE); lstrcpynA(lf.lfFaceName, g_face, LF_FACESIZE); }
    if (g_weight > 0 && lf.lfWeight < 400) lf.lfWeight = g_weight;
    if (g_hscale != 100) {
        long h = lf.lfHeight * g_hscale;
        lf.lfHeight = (LONG)((h < 0) ? (h - 50) / 100 : (h + 50) / 100);
    }
    return real_CFIA(&lf);
}

static int WINAPI My_WCTMB(UINT cp, DWORD fl, LPCWSTR ws, int wl, LPSTR s, int sl, LPCSTR dc, LPBOOL ud) {
    UINT in = cp;
    if (cp == CP_THREAD_ACP) cp = (g_gbkDepth > 0) ? 936 : 932;
    int r = real_WCTMB(cp, fl, ws, wl, s, sl, dc, ud);
    if (g_debug >= 2 && g_gbkDepth > 0 && ws) {
        char w[300], o[300]; int n = wl;
        if (n < 0) { n = 0; while (n < 64 && ws[n]) n++; n++; if (n > 64) n = 64; }   /* 含結尾 NUL */
        if (n > 40) n = 40;
        hex_(w, sizeof w, (const unsigned char *)ws, n * 2);
        o[0] = 0; if (s && sl > 0) hex_(o, sizeof o, (const unsigned char *)s, sl > 48 ? 48 : sl);
        logf_("WCTMB in=%u->cp=%u wl=%d sl=%d r=%d | UTF16: %s| out: %s", in, cp, wl, sl, r, w, o);
    }
    return r;
}

typedef HWND (WINAPI *PFN_CWEA)(DWORD, LPCSTR, LPCSTR, DWORD, int, int, int, int, HWND, HMENU, HINSTANCE, LPVOID);
typedef BOOL (WINAPI *PFN_SWTA)(HWND, LPCSTR);
static PFN_CWEA real_CWEA; static PFN_SWTA real_SWTA;
static void log_title_(const char *api, LPCSTR t) {
    if (!t || (((ULONG_PTR)t) >> 16) == 0) return;           /* NULL 或 atom */
    int n = lstrlenA(t); char h[400]; hex_(h, sizeof h, (const unsigned char *)t, n > 60 ? 60 : n);
    logf_("%s title(%d bytes): %s", api, n, h);
}
static HWND WINAPI My_CWEA(DWORD ex, LPCSTR cls, LPCSTR name, DWORD st, int x, int y, int w, int h, HWND p, HMENU m, HINSTANCE i, LPVOID lp) {
    if (g_debug >= 2) log_title_("CreateWindowExA", name);
    return real_CWEA(ex, cls, name, st, x, y, w, h, p, m, i, lp);
}
static BOOL WINAPI My_SWTA(HWND h, LPCSTR t) {
    if (g_debug >= 2) log_title_("SetWindowTextA", t);
    return real_SWTA(h, t);
}

static void hook_module_fonts(HMODULE m) { patch_iat(m, "GDI32.dll", "CreateFontIndirectA", My_CFIA); }

static HMODULE WINAPI My_LLA(LPCSTR name) {
    HMODULE h = real_LLA(name);
    if (h) { hook_module_fonts(h); logf_("LoadLibraryA(%s) -> font hook applied", name ? name : "?"); }
    return h;
}

/* params string16 reader 的包裝：呼叫期間 g_gbkDepth>0 */
__asm__(
    ".text\n.globl _ReaderWrap\n_ReaderWrap:\n"
    "  popl _g_savedRet\n"
    "  incl _g_gbkDepth\n"
    "  call *_g_realReader\n"
    "  decl _g_gbkDepth\n"
    "  pushl _g_savedRet\n"
    "  ret\n");
extern void ReaderWrap(void);

static BOOL patch_reader_site(DWORD va) {
    BYTE *p = (BYTE *)va; DWORD old;
    if (IsBadReadPtr(p, 5) || p[0] != 0xE8) return FALSE;
    if ((DWORD)(p + 5 + *(int *)(p + 1)) != g_realReader) return FALSE;
    if (!VirtualProtect(p, 5, PAGE_EXECUTE_READWRITE, &old)) return FALSE;
    *(int *)(p + 1) = (int)((BYTE *)ReaderWrap - (p + 5));
    VirtualProtect(p, 5, old, &old); FlushInstructionCache(GetCurrentProcess(), p, 5);
    return TRUE;
}

static BOOL patch_bytes(DWORD va, const BYTE *expect, const BYTE *repl, int n) {
    BYTE *p = (BYTE *)va; DWORD old;
    if (IsBadReadPtr(p, n) || memcmp(p, expect, n)) return FALSE;
    if (!VirtualProtect(p, n, PAGE_READWRITE, &old)) return FALSE;
    memcpy(p, repl, n); VirtualProtect(p, n, old, &old); return TRUE;
}

/* ---------------- 初始化 ---------------- */
static void read_ini(void) {
    char exe[MAX_PATH], ini[MAX_PATH];
    GetModuleFileNameA(NULL, exe, MAX_PATH);
    char *s = strrchr(exe, '\\'); if (s) *s = 0;
    wsprintfA(ini, "%s\\ChihanaGBK.ini", exe); wsprintfA(g_logpath, "%s\\ChihanaGBK.log", exe);
    g_debug   = GetPrivateProfileIntA("Options", "Debug", 0, ini);
    GetPrivateProfileStringA("Font", "Face", "SimHei", g_face, LF_FACESIZE, ini);
    g_charset = GetPrivateProfileIntA("Font", "Charset", 134, ini);
    g_weight  = GetPrivateProfileIntA("Font", "Weight", 0, ini);
    g_hscale  = GetPrivateProfileIntA("Font", "HeightScale", 100, ini);
    if (g_hscale < 10 || g_hscale > 1000) g_hscale = 100;
}

static void init(void) {
    read_ini();
    logf_("== ChihanaGBK %s (%s) init, exe base %p, face=%s charset=%d weight=%d hscale=%d", CHIHANA_VER_STR, CHIHANA_BUILD_DATE, GetModuleHandleA(NULL), g_face, g_charset, g_weight, g_hscale);
    HMODULE exe = GetModuleHandleA(NULL);
    HMODULE gdi = GetModuleHandleA("gdi32.dll"), k32 = GetModuleHandleA("kernel32.dll");
    real_CFIA = (PFN_CFIA)GetProcAddress(gdi, "CreateFontIndirectA");
    real_WCTMB = (PFN_WCTMB)GetProcAddress(k32, "WideCharToMultiByte");
    real_LLA = (PFN_LLA)GetProcAddress(k32, "LoadLibraryA");
    logf_("exe IAT: CreateFontIndirectA=%d WideCharToMultiByte=%d LoadLibraryA=%d",
          patch_iat(exe, "GDI32.dll", "CreateFontIndirectA", My_CFIA),
          patch_iat(exe, "KERNEL32.dll", "WideCharToMultiByte", My_WCTMB),
          patch_iat(exe, "KERNEL32.dll", "LoadLibraryA", My_LLA));
    if (g_debug >= 2) {
        HMODULE u32 = GetModuleHandleA("user32.dll");
        real_CWEA = (PFN_CWEA)GetProcAddress(u32, "CreateWindowExA"); real_SWTA = (PFN_SWTA)GetProcAddress(u32, "SetWindowTextA");
        logf_("trace: CreateWindowExA=%d SetWindowTextA=%d", patch_iat(exe, "USER32.dll", "CreateWindowExA", My_CWEA), patch_iat(exe, "USER32.dll", "SetWindowTextA", My_SWTA));
    }
    /* params 讀取點 / 】【：自動掃描 exe 映像（與 A000.exe 及另一款同引擎遊戲驗證過） */
    KScan sc; int found = ks_scan((const BYTE *)exe, (DWORD)exe, &sc);
    int ok = 0, br = 0;
    if (found) {
        g_realReader = (DWORD)exe + sc.reader;
        for (int i = 0; i < 5; i++) if (sc.site[i]) ok += patch_reader_site((DWORD)exe + sc.site[i]);
        static const BYTE e1[] = {0x81,0x7A}, r1[] = {0xA1,0xBF}, e2[] = {0x81,0x79}, r2[] = {0xA1,0xBE};
        for (int i = 0; i < sc.n_close; i++) br += patch_bytes((DWORD)exe + sc.br_close[i], e1, r1, 2);
        for (int i = 0; i < sc.n_open;  i++) br += patch_bytes((DWORD)exe + sc.br_open[i],  e2, r2, 2);
        /* 全形空白：視窗標題 = GameTitle + 「　」+ DisplayTitle；Shift-JIS 81 40 在 GBK 下會變成「丂」(U+4E02)，改成 GBK 的全形空白 A1 A1 */
        static const BYTE e3[] = {0x81,0x40}, r3[] = {0xA1,0xA1}, e4[] = {0x20,0x81,0x40}, r4[] = {0x20,0xA1,0xA1};
        for (int i = 0; i < sc.n_sp1; i++) br += patch_bytes((DWORD)exe + sc.sp1[i], e3, r3, 2);
        for (int i = 0; i < sc.n_sp2; i++) br += patch_bytes((DWORD)exe + sc.sp2[i], e4, r4, 3);
        logf_("掃描成功: reader=%p (呼叫者 %d), 欄位 %d/5, 】【 %d+%d, 全形空白 %d+%d", (void *)g_realReader, sc.reader_callers, sc.nsite, sc.n_close, sc.n_open, sc.n_sp1, sc.n_sp2);
    } else logf_("掃描不到 params reader（可能不是此引擎版本）：略過標題/括號修補");
    /* 手動補充：ini [Params] ExtraSites=0x...,0x...（reader 的呼叫位址；僅在掃描成功時有意義） */
    if (found) {
        char ini[MAX_PATH], exep[MAX_PATH]; GetModuleFileNameA(NULL, exep, MAX_PATH); char *s2 = strrchr(exep, '\\'); if (s2) *s2 = 0;
        wsprintfA(ini, "%s\\ChihanaGBK.ini", exep);
        char list[256]; GetPrivateProfileStringA("Params", "ExtraSites", "", list, sizeof list, ini);
        for (char *t = strtok(list, ", "); t; t = strtok(NULL, ", ")) ok += patch_reader_site((DWORD)strtoul(t, NULL, 0));
    }
    logf_("params 顯示欄位 hook: %d 處；標點(】【全形空白): %d 處", ok, br);
}

BOOL WINAPI DllMain(HINSTANCE h, DWORD reason, LPVOID r) {
    if (reason == DLL_PROCESS_ATTACH) { DisableThreadLibraryCalls(h); load_real_version(); init(); }
    return TRUE;
}
