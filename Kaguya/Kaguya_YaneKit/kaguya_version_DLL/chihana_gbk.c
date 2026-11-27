/* chihana_gbk.c -- version.dll 代理 DLL：依 CodePage 支援 Kaguya GBK／Big5 顯示
 *
 * ChihanaGBK v2.4.1.7
 *
 * 保留 v2.4.1.6 的既有初始化、Debug Log、IAT Hook、FLS、ReaderWrap、
 * 五個 params 欄位掃描與字型 Hook。此版只把 ReaderWrap 內的目標碼頁
 * 及對應括號／空白位元組改為由 INI 的 CodePage=936／950 選擇。
 *
 * 一般 ReaderWrap 外的日文文字仍使用 CP932；message.dat 必須先由
 * Kaguya_YaneKit 轉成指定碼頁，DLL 不會重新編碼 message.dat。
 */
#ifndef _WIN32_WINNT
#define _WIN32_WINNT 0x0600
#endif

#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <stdio.h>
#include <string.h>
#include <stdlib.h>
#include <stdarg.h>

#include "kaguya_scan.h"
#include "chihana_version.h"

/* ---------------- 設定 ---------------- */

static char g_face[LF_FACESIZE] = "SimHei";
static int  g_charset = 134;
static int  g_weight = 0;
static int  g_hscale = 100;
static int  g_debug = 0;
static int  g_encoding_enabled = 1;
static UINT g_codepage = 936;
static char g_logpath[MAX_PATH];

#define READER_MAX_DEPTH 128

typedef struct {
    DWORD depth;
    DWORD returnAddress[READER_MAX_DEPTH];
} ReaderThreadState;

static DWORD g_readerFls = FLS_OUT_OF_INDEXES;

__attribute__((used))
volatile DWORD g_realReader = 0;


/* ---------------- UTF-8 Log ---------------- */

/*
 * 所有 Log 統一使用 UTF-8。
 *
 * 新檔案：
 *   寫入 UTF-8 BOM
 *
 * 每行：
 *   [PID=xxxxx TID=xxxxx] 訊息
 *
 * 注意：
 *   舊版 ChihanaGBK.log 可能混有舊編碼內容。
 *   第一次使用 v2.4.1.6 前，請先刪除舊 Log。
 */
static void logf_(const char *fmt, ...) {
    if (!g_debug) return;

    char line[4096];

    int prefix = snprintf(
        line,
        sizeof(line),
        "[PID=%lu TID=%lu] ",
        (unsigned long)GetCurrentProcessId(),
        (unsigned long)GetCurrentThreadId()
    );

    if (prefix < 0 || prefix >= (int)sizeof(line) - 3)
        return;

    va_list ap;
    va_start(ap, fmt);

    int body_cap = (int)sizeof(line) - prefix - 3;

    int body_len = vsnprintf(
        line + prefix,
        (size_t)body_cap + 1,
        fmt,
        ap
    );

    va_end(ap);

    if (body_len < 0)
        return;

    if (body_len > body_cap)
        body_len = body_cap;

    size_t total = (size_t)prefix + (size_t)body_len;

    line[total++] = '\r';
    line[total++] = '\n';

    FILE *f = fopen(g_logpath, "ab+");

    if (!f)
        return;

    /* 新檔案才寫入 BOM，不覆蓋舊檔案。 */
    if (fseek(f, 0, SEEK_END) == 0 && ftell(f) == 0) {
        static const unsigned char bom[3] = {
            0xEF, 0xBB, 0xBF
        };

        fwrite(bom, 1, sizeof(bom), f);
    }

    fwrite(line, 1, total, f);

    fclose(f);
}


/*
 * 將 ANSI 字串轉成 UTF-8。
 *
 * 優先使用目前執行緒 ANSI Code Page，
 * 再以系統 ANSI Code Page 嘗試。
 *
 * 使用 flags=0，避免一般舊式 ANSI Code Page
 * 不支援 MB_ERR_INVALID_CHARS 而轉換失敗。
 */
static BOOL ansi_to_utf8(
    const char *src,
    char *dst,
    int dst_cap
) {
    if (!src || !dst || dst_cap <= 0)
        return FALSE;

    dst[0] = 0;

    const UINT codepages[2] = {
        CP_THREAD_ACP,
        CP_ACP
    };

    for (int i = 0; i < 2; i++) {

        if (i == 1 && codepages[1] == codepages[0])
            continue;

        int wide_count = MultiByteToWideChar(
            codepages[i],
            0,
            src,
            -1,
            NULL,
            0
        );

        if (wide_count <= 0)
            continue;

        WCHAR *wide = (WCHAR *)HeapAlloc(
            GetProcessHeap(),
            0,
            (SIZE_T)wide_count * sizeof(WCHAR)
        );

        if (!wide)
            return FALSE;

        BOOL ok = FALSE;

        if (MultiByteToWideChar(
                codepages[i],
                0,
                src,
                -1,
                wide,
                wide_count
            ) == wide_count) {

            int utf8_count = WideCharToMultiByte(
                CP_UTF8,
                0,
                wide,
                -1,
                NULL,
                0,
                NULL,
                NULL
            );

            if (utf8_count > 0 && utf8_count <= dst_cap) {

                int written = WideCharToMultiByte(
                    CP_UTF8,
                    0,
                    wide,
                    -1,
                    dst,
                    dst_cap,
                    NULL,
                    NULL
                );

                ok = (written == utf8_count);
            }
        }

        HeapFree(GetProcessHeap(), 0, wide);

        if (ok)
            return TRUE;

        dst[0] = 0;
    }

    return FALSE;
}


/* ---------------- 真正的 version.dll 轉接 ---------------- */

static HMODULE g_realVer;

typedef BOOL (WINAPI *PFN_GFVI)(
    LPCSTR, DWORD, DWORD, LPVOID
);

typedef DWORD (WINAPI *PFN_GFVIS)(
    LPCSTR, LPDWORD
);

typedef BOOL (WINAPI *PFN_VQV)(
    LPCVOID, LPCSTR, LPVOID *, PUINT
);

static PFN_GFVI p_GFVI;
static PFN_GFVIS p_GFVIS;
static PFN_VQV p_VQV;

static BOOL ensure_initialized(void);


__declspec(dllexport)
BOOL WINAPI GetFileVersionInfoA(
    LPCSTR a,
    DWORD b,
    DWORD c,
    LPVOID d
) {
    DWORD saved = GetLastError();

    if (!ensure_initialized()) {
        SetLastError(saved);
        return FALSE;
    }

    SetLastError(saved);

    return p_GFVI ? p_GFVI(a, b, c, d) : FALSE;
}


__declspec(dllexport)
DWORD WINAPI GetFileVersionInfoSizeA(
    LPCSTR a,
    LPDWORD b
) {
    DWORD saved = GetLastError();

    if (!ensure_initialized()) {
        SetLastError(saved);
        return 0;
    }

    SetLastError(saved);

    return p_GFVIS ? p_GFVIS(a, b) : 0;
}


__declspec(dllexport)
BOOL WINAPI VerQueryValueA(
    LPCVOID a,
    LPCSTR b,
    LPVOID *c,
    PUINT d
) {
    DWORD saved = GetLastError();

    if (!ensure_initialized()) {
        SetLastError(saved);
        return FALSE;
    }

    SetLastError(saved);

    return p_VQV ? p_VQV(a, b, c, d) : FALSE;
}


static void load_real_version(void) {
    char p[MAX_PATH];

    UINT n = GetSystemDirectoryA(p, MAX_PATH);

    static const char suffix[] = "\\version.dll";

    if (!n ||
        n >= MAX_PATH ||
        n + sizeof(suffix) > MAX_PATH)
        return;

    memcpy(p + n, suffix, sizeof(suffix));

    g_realVer = LoadLibraryA(p);

    if (!g_realVer)
        return;

    p_GFVI = (PFN_GFVI)GetProcAddress(
        g_realVer,
        "GetFileVersionInfoA"
    );

    p_GFVIS = (PFN_GFVIS)GetProcAddress(
        g_realVer,
        "GetFileVersionInfoSizeA"
    );

    p_VQV = (PFN_VQV)GetProcAddress(
        g_realVer,
        "VerQueryValueA"
    );
}


/* ---------------- IAT 修補 ---------------- */

static BOOL patch_iat(
    HMODULE mod,
    const char *dll,
    const char *func,
    void *hook
) {
    BYTE *b = (BYTE *)mod;

    IMAGE_DOS_HEADER *dh =
        (IMAGE_DOS_HEADER *)b;

    if (dh->e_magic != IMAGE_DOS_SIGNATURE)
        return FALSE;

    IMAGE_NT_HEADERS *nt =
        (IMAGE_NT_HEADERS *)(b + dh->e_lfanew);

    if (nt->Signature != IMAGE_NT_SIGNATURE)
        return FALSE;

    DWORD rva =
        nt->OptionalHeader.DataDirectory[
            IMAGE_DIRECTORY_ENTRY_IMPORT
        ].VirtualAddress;

    if (!rva)
        return FALSE;

    for (
        IMAGE_IMPORT_DESCRIPTOR *d =
            (IMAGE_IMPORT_DESCRIPTOR *)(b + rva);
        d->Name;
        d++
    ) {
        if (lstrcmpiA(
                (const char *)(b + d->Name),
                dll
            ))
            continue;

        IMAGE_THUNK_DATA *oft =
            (IMAGE_THUNK_DATA *)(
                b + (
                    d->OriginalFirstThunk
                    ? d->OriginalFirstThunk
                    : d->FirstThunk
                )
            );

        IMAGE_THUNK_DATA *ft =
            (IMAGE_THUNK_DATA *)(b + d->FirstThunk);

        for (; oft->u1.AddressOfData; oft++, ft++) {

            if (IMAGE_SNAP_BY_ORDINAL32(oft->u1.Ordinal))
                continue;

            IMAGE_IMPORT_BY_NAME *n =
                (IMAGE_IMPORT_BY_NAME *)(
                    b + oft->u1.AddressOfData
                );

            if (strcmp((const char *)n->Name, func))
                continue;

            DWORD old;

            if (!VirtualProtect(
                    &ft->u1.Function,
                    4,
                    PAGE_READWRITE,
                    &old
                ))
                return FALSE;

            ft->u1.Function = (DWORD)hook;

            VirtualProtect(
                &ft->u1.Function,
                4,
                old,
                &old
            );

            return TRUE;
        }
    }

    return FALSE;
}


/* ---------------- HEX Log ---------------- */

static void hex_(
    char *dst,
    size_t n,
    const unsigned char *p,
    int len
) {
    size_t o = 0;

    for (int i = 0; i < len && o + 4 < n; i++) {
        o += wsprintfA(
            dst + o,
            "%02X ",
            p[i]
        );
    }

    dst[o] = 0;
}


/* ---------------- hooks ---------------- */

typedef HFONT (WINAPI *PFN_CFIA)(
    const LOGFONTA *
);

typedef int (WINAPI *PFN_WCTMB)(
    UINT,
    DWORD,
    LPCWSTR,
    int,
    LPSTR,
    int,
    LPCSTR,
    LPBOOL
);

typedef HMODULE (WINAPI *PFN_LLA)(
    LPCSTR
);

static PFN_CFIA real_CFIA;
static PFN_WCTMB real_WCTMB;
static PFN_LLA real_LLA;


/* ---------------- 字型 Hook ---------------- */

static HFONT WINAPI My_CFIA(
    const LOGFONTA *in
) {
    if (!in)
        return real_CFIA(in);

    LOGFONTA lf = *in;

    /*
     * EncodingEnabled=0：
     * 保留遊戲原始 lfCharSet，只替換字型名稱。
     *
     * EncodingEnabled=1：
     * 使用依 CodePage 自動對應的字型 Charset。
     */
    if (g_encoding_enabled)
        lf.lfCharSet = (BYTE)g_charset;

    if (g_face[0]) {
        memset(lf.lfFaceName, 0, LF_FACESIZE);

        lstrcpynA(
            lf.lfFaceName,
            g_face,
            LF_FACESIZE
        );
    }

    if (g_weight > 0 && lf.lfWeight < 400)
        lf.lfWeight = g_weight;

    if (g_hscale != 100) {
        long h = lf.lfHeight * g_hscale;

        lf.lfHeight = (LONG)(
            (h < 0)
            ? (h - 50) / 100
            : (h + 50) / 100
        );
    }

    return real_CFIA(&lf);
}


/* ---------------- FLS 狀態 ---------------- */

static VOID CALLBACK reader_state_cleanup(PVOID data) {
    if (data)
        HeapFree(GetProcessHeap(), 0, data);
}


static ReaderThreadState *reader_state_get(BOOL create) {
    if (g_readerFls == FLS_OUT_OF_INDEXES)
        return NULL;

    ReaderThreadState *state =
        (ReaderThreadState *)FlsGetValue(g_readerFls);

    if (!state && create) {

        HANDLE heap = GetProcessHeap();

        state = (ReaderThreadState *)HeapAlloc(
            heap,
            HEAP_ZERO_MEMORY,
            sizeof(*state)
        );

        if (!state)
            return NULL;

        if (!FlsSetValue(g_readerFls, state)) {
            HeapFree(heap, 0, state);
            return NULL;
        }
    }

    return state;
}


static BOOL reader_active(void) {
    DWORD saved = GetLastError();

    ReaderThreadState *state =
        reader_state_get(FALSE);

    BOOL active =
        (state && state->depth != 0);

    SetLastError(saved);

    return active;
}


/* 由 ReaderWrap 組語呼叫。 */

BOOL __attribute__((noinline, used))
reader_enter(DWORD return_address) {
    DWORD saved = GetLastError();

    ReaderThreadState *state =
        reader_state_get(TRUE);

    BOOL ok = FALSE;

    if (state && state->depth < READER_MAX_DEPTH) {
        state->returnAddress[state->depth++] =
            return_address;

        ok = TRUE;
    }

    SetLastError(saved);

    return ok;
}


/* 由 ReaderWrap 組語呼叫。 */

DWORD __attribute__((noinline, used))
reader_leave(void) {
    DWORD saved = GetLastError();

    ReaderThreadState *state =
        reader_state_get(FALSE);

    DWORD return_address = 0;

    if (state && state->depth != 0)
        return_address =
            state->returnAddress[--state->depth];

    SetLastError(saved);

    return return_address;
}


/* ---------------- WideCharToMultiByte Hook ---------------- */

static int WINAPI My_WCTMB(
    UINT cp,
    DWORD fl,
    LPCWSTR ws,
    int wl,
    LPSTR s,
    int sl,
    LPCSTR dc,
    LPBOOL ud
) {
    UINT in = cp;

    BOOL in_reader = reader_active();

    /*
     * ReaderWrap 內五個 params 欄位使用 INI 指定的目標碼頁；
     * ReaderWrap 外的一般日文文字維持 CP932。
     * message.dat 由 Kaguya_YaneKit 預先轉碼，DLL 不處理該檔案。
     */
    if (cp == CP_THREAD_ACP)
        cp = in_reader ? g_codepage : 932;

    int r = real_WCTMB(
        cp,
        fl,
        ws,
        wl,
        s,
        sl,
        dc,
        ud
    );

    /*
     * 必須在任何 Debug 操作前取得 API 的錯誤碼。
     */
    DWORD api_error = GetLastError();

    if (g_debug >= 2 && in_reader && ws) {

        char w[300];
        char o[300];

        int n = wl;

        if (n < 0) {
            n = 0;

            while (n < 64 && ws[n])
                n++;

            n++;

            if (n > 64)
                n = 64;
        }

        if (n > 40)
            n = 40;

        hex_(
            w,
            sizeof(w),
            (const unsigned char *)ws,
            n * 2
        );

        /*
         * 只有 API 回傳成功時才記錄有效輸出長度。
         * r=0 時不把容量不足的緩衝區內容誤認為完整結果。
         */
        if (r > 0 && s && sl > 0) {

            int out_len = r;

            if (out_len > sl)
                out_len = sl;

            if (out_len > 48)
                out_len = 48;

            hex_(
                o,
                sizeof(o),
                (const unsigned char *)s,
                out_len
            );

        } else {
            lstrcpynA(
                o,
                "<none>",
                sizeof(o)
            );
        }

        logf_(
            "WCTMB in=%u->cp=%u wl=%d sl=%d r=%d err=%lu | UTF16: %s| out: %s",
            in,
            cp,
            wl,
            sl,
            r,
            (unsigned long)(r == 0 ? api_error : 0),
            w,
            o
        );
    }

    /*
     * Debug 寫檔不得改變原本 API 的 LastError。
     */
    SetLastError(api_error);

    return r;
}


/* ---------------- 視窗標題追蹤 ---------------- */

typedef HWND (WINAPI *PFN_CWEA)(
    DWORD,
    LPCSTR,
    LPCSTR,
    DWORD,
    int,
    int,
    int,
    int,
    HWND,
    HMENU,
    HINSTANCE,
    LPVOID
);

typedef BOOL (WINAPI *PFN_SWTA)(
    HWND,
    LPCSTR
);

static PFN_CWEA real_CWEA;
static PFN_SWTA real_SWTA;


static void log_title_(
    const char *api,
    LPCSTR t
) {
    if (!t || (((ULONG_PTR)t) >> 16) == 0)
        return;

    int n = lstrlenA(t);

    char h[400];

    hex_(
        h,
        sizeof(h),
        (const unsigned char *)t,
        n > 60 ? 60 : n
    );

    logf_(
        "%s title(%d bytes): %s",
        api,
        n,
        h
    );
}


static HWND WINAPI My_CWEA(
    DWORD ex,
    LPCSTR cls,
    LPCSTR name,
    DWORD st,
    int x,
    int y,
    int w,
    int h,
    HWND p,
    HMENU m,
    HINSTANCE i,
    LPVOID lp
) {
    if (g_debug >= 2)
        log_title_("CreateWindowExA", name);

    return real_CWEA(
        ex,
        cls,
        name,
        st,
        x,
        y,
        w,
        h,
        p,
        m,
        i,
        lp
    );
}


static BOOL WINAPI My_SWTA(
    HWND h,
    LPCSTR t
) {
    if (g_debug >= 2)
        log_title_("SetWindowTextA", t);

    return real_SWTA(h, t);
}


/* ---------------- 動態模組字型 Hook ---------------- */

static void hook_module_fonts(HMODULE m) {
    patch_iat(
        m,
        "GDI32.dll",
        "CreateFontIndirectA",
        My_CFIA
    );
}


static HMODULE WINAPI My_LLA(LPCSTR name) {

    HMODULE h = real_LLA(name);

    /*
     * 先保存真正 LoadLibraryA 的 LastError。
     */
    DWORD api_error = GetLastError();

    if (h) {

        hook_module_fonts(h);

        if (name) {

            char path_utf8[4096];

            if (ansi_to_utf8(
                    name,
                    path_utf8,
                    sizeof(path_utf8)
                )) {

                logf_(
                    "LoadLibraryA(%s) -> font hook applied",
                    path_utf8
                );

            } else {

                /*
                 * 轉換失敗時保留原始位元組 HEX，
                 * 不將非 UTF-8 ANSI 路徑直接寫入 UTF-8 Log。
                 */
                char raw[256];

                int raw_len = lstrlenA(name);

                if (raw_len > 60)
                    raw_len = 60;

                hex_(
                    raw,
                    sizeof(raw),
                    (const unsigned char *)name,
                    raw_len
                );

                logf_(
                    "LoadLibraryA(<ANSI-to-UTF-8 failed; raw bytes: %s>) -> font hook applied",
                    raw
                );
            }

        } else {
            logf_(
                "LoadLibraryA(?) -> font hook applied"
            );
        }
    }

    /*
     * 字型 IAT 修補及 Debug 寫檔不得改變
     * 原始 LoadLibraryA 的 LastError。
     */
    SetLastError(api_error);

    return h;
}


/* ---------------- ReaderWrap ---------------- */

/*
 * params string16 reader 的包裝。
 *
 * 原返回位址以參數交給 reader_enter，
 * 存放在執行緒專屬 FLS 堆疊。
 *
 * FLS 配置失敗或巢狀深度超過上限時，
 * 直接 tail-jump 原 reader。
 */

__asm__(
    ".text\n.globl _ReaderWrap\n_ReaderWrap:\n"

    "  pushl %eax\n"
    "  pushl %ecx\n"
    "  pushl %edx\n"

    "  movl 12(%esp), %eax\n"
    "  pushl %eax\n"

    "  call _reader_enter\n"

    "  testl %eax, %eax\n"
    "  jz 1f\n"

    "  addl $4, %esp\n"

    "  popl %edx\n"
    "  popl %ecx\n"
    "  popl %eax\n"

    "  addl $4, %esp\n"

    "  call *_g_realReader\n"

    "  pushl %edx\n"
    "  pushl %eax\n"

    "  call _reader_leave\n"

    "  pushl %eax\n"

    "  movl 4(%esp), %eax\n"
    "  movl 8(%esp), %edx\n"

    "  ret $8\n"

    "1:\n"

    "  addl $4, %esp\n"

    "  popl %edx\n"
    "  popl %ecx\n"
    "  popl %eax\n"

    "  jmp *_g_realReader\n"
);

extern void ReaderWrap(void);


/* ---------------- Reader 呼叫點修補 ---------------- */

static BOOL patch_reader_site(DWORD va) {

    BYTE *p = (BYTE *)va;

    DWORD old;

    if (IsBadReadPtr(p, 5) || p[0] != 0xE8)
        return FALSE;

    if (
        (DWORD)(p + 5 + *(int *)(p + 1))
        != g_realReader
    )
        return FALSE;

    if (!VirtualProtect(
            p,
            5,
            PAGE_EXECUTE_READWRITE,
            &old
        ))
        return FALSE;

    *(int *)(p + 1) =
        (int)((BYTE *)ReaderWrap - (p + 5));

    VirtualProtect(p, 5, old, &old);

    FlushInstructionCache(
        GetCurrentProcess(),
        p,
        5
    );

    return TRUE;
}


/* ---------------- 目標碼頁位元組修補 ---------------- */

static BOOL patch_bytes(
    DWORD va,
    const BYTE *expect,
    const BYTE *repl,
    int n
) {
    BYTE *p = (BYTE *)va;

    DWORD old;

    if (IsBadReadPtr(p, n) ||
        memcmp(p, expect, n))
        return FALSE;

    if (!VirtualProtect(
            p,
            n,
            PAGE_READWRITE,
            &old
        ))
        return FALSE;

    memcpy(p, repl, n);

    VirtualProtect(p, n, old, &old);

    return TRUE;
}


/* ---------------- INI ---------------- */

static void read_ini(void) {

    char exe[MAX_PATH];
    char ini[MAX_PATH];

    GetModuleFileNameA(
        NULL,
        exe,
        MAX_PATH
    );

    char *s = strrchr(exe, '\\');

    if (s)
        *s = 0;

    wsprintfA(
        ini,
        "%s\\ChihanaGBK.ini",
        exe
    );

    wsprintfA(
        g_logpath,
        "%s\\ChihanaGBK.log",
        exe
    );

    g_encoding_enabled =
        GetPrivateProfileIntA(
            "Options",
            "EncodingEnabled",
            1,
            ini
        ) != 0;

    g_debug =
        GetPrivateProfileIntA(
            "Options",
            "Debug",
            0,
            ini
        );

    GetPrivateProfileStringA(
        "Font",
        "Face",
        "SimHei",
        g_face,
        LF_FACESIZE,
        ini
    );

    g_codepage = (UINT)GetPrivateProfileIntA(
        "Options",
        "CodePage",
        936,
        ini
    );

    if (g_codepage != 936 && g_codepage != 950) {
        logf_(
            "WARNING: unsupported CodePage=%u; fallback to 936",
            (unsigned)g_codepage
        );
        g_codepage = 936;
    }

    /* LOGFONT charset 由目標碼頁自動對應，不再獨立設定。 */
    g_charset = (g_codepage == 950) ? 136 : 134;

    g_weight =
        GetPrivateProfileIntA(
            "Font",
            "Weight",
            0,
            ini
        );

    g_hscale =
        GetPrivateProfileIntA(
            "Font",
            "HeightScale",
            100,
            ini
        );

    if (g_hscale < 10 || g_hscale > 1000)
        g_hscale = 100;
}


/* ---------------- 初始化 ---------------- */

static void init(void) {

    read_ini();

    /*
     * Face 轉成 UTF-8 後才寫入 Log。
     */
    char face_utf8[LF_FACESIZE * 4];

    if (!ansi_to_utf8(
            g_face,
            face_utf8,
            sizeof(face_utf8)
        )) {

        lstrcpynA(
            face_utf8,
            "<ANSI-to-UTF-8 failed>",
            sizeof(face_utf8)
        );
    }

    logf_(
        "== ChihanaGBK %s (%s) init, exe base %p, encoding=%s, codepage=%u, face=%s charset=%d weight=%d hscale=%d",
        CHIHANA_VER_STR,
        CHIHANA_BUILD_DATE,
        GetModuleHandleA(NULL),
        g_encoding_enabled ? "ON" : "OFF",
        (unsigned)g_codepage,
        face_utf8,
        g_charset,
        g_weight,
        g_hscale
    );

    HMODULE exe = GetModuleHandleA(NULL);

    HMODULE gdi =
        GetModuleHandleA("gdi32.dll");

    HMODULE k32 =
        GetModuleHandleA("kernel32.dll");

    real_CFIA =
        (PFN_CFIA)GetProcAddress(
            gdi,
            "CreateFontIndirectA"
        );

    real_WCTMB =
        (PFN_WCTMB)GetProcAddress(
            k32,
            "WideCharToMultiByte"
        );

    real_LLA =
        (PFN_LLA)GetProcAddress(
            k32,
            "LoadLibraryA"
        );

    logf_(
        "exe IAT: CreateFontIndirectA=%d WideCharToMultiByte=%d LoadLibraryA=%d",

        real_CFIA
            ? patch_iat(
                exe,
                "GDI32.dll",
                "CreateFontIndirectA",
                My_CFIA
            )
            : 0,

        (g_encoding_enabled && real_WCTMB)
            ? patch_iat(
                exe,
                "KERNEL32.dll",
                "WideCharToMultiByte",
                My_WCTMB
            )
            : 0,

        real_LLA
            ? patch_iat(
                exe,
                "KERNEL32.dll",
                "LoadLibraryA",
                My_LLA
            )
            : 0
    );


    if (!g_encoding_enabled) {

        logf_(
            "EncodingEnabled=0: original text behavior; no encoding or text-trace hooks; font hooks remain enabled"
        );
    }


    /* ---------------- Debug 視窗標題追蹤 ---------------- */

    if (g_encoding_enabled && g_debug >= 2) {

        HMODULE u32 =
            GetModuleHandleA("user32.dll");

        real_CWEA =
            (PFN_CWEA)GetProcAddress(
                u32,
                "CreateWindowExA"
            );

        real_SWTA =
            (PFN_SWTA)GetProcAddress(
                u32,
                "SetWindowTextA"
            );

        logf_(
            "trace: CreateWindowExA=%d SetWindowTextA=%d",

            real_CWEA
                ? patch_iat(
                    exe,
                    "USER32.dll",
                    "CreateWindowExA",
                    My_CWEA
                )
                : 0,

            real_SWTA
                ? patch_iat(
                    exe,
                    "USER32.dll",
                    "SetWindowTextA",
                    My_SWTA
                )
                : 0
        );
    }


    /* ---------------- 目標碼頁編碼模式（GBK／Big5） ---------------- */

    if (g_encoding_enabled) {

        /*
         * params 讀取點 / 標點：
         * 自動掃描 exe 映像。
         *
         * kaguya_scan.h 不修改。
         */
        KScan sc;

        int found = ks_scan(
            (const BYTE *)exe,
            (DWORD)exe,
            &sc
        );

        int ok = 0;
        int extra = 0;
        int br = 0;

        if (found) {

            g_realReader =
                (DWORD)exe + sc.reader;

            g_readerFls =
                FlsAlloc(reader_state_cleanup);

            if (g_readerFls != FLS_OUT_OF_INDEXES) {

                /*
                 * 保留五個自動 params 欄位。
                 */
                for (int i = 0; i < 5; i++) {
                    if (sc.site[i]) {
                        ok += patch_reader_site(
                            (DWORD)exe + sc.site[i]
                        );
                    }
                }

            } else {

                logf_(
                    "FLS allocation failed: ReaderWrap hook skipped; other patches remain enabled"
                );
            }


            /* 】：GBK A1 BF；Big5 A1 6A */
            static const BYTE e1[] = {
                0x81, 0x7A
            };
            static const BYTE r1_gbk[] = {
                0xA1, 0xBF
            };
            static const BYTE r1_big5[] = {
                0xA1, 0x6A
            };
            const BYTE *r1 =
                (g_codepage == 950) ? r1_big5 : r1_gbk;


            /* 【：GBK A1 BE；Big5 A1 69 */
            static const BYTE e2[] = {
                0x81, 0x79
            };
            static const BYTE r2_gbk[] = {
                0xA1, 0xBE
            };
            static const BYTE r2_big5[] = {
                0xA1, 0x69
            };
            const BYTE *r2 =
                (g_codepage == 950) ? r2_big5 : r2_gbk;


            for (int i = 0; i < sc.n_close; i++) {

                br += patch_bytes(
                    (DWORD)exe + sc.br_close[i],
                    e1,
                    r1,
                    2
                );
            }


            for (int i = 0; i < sc.n_open; i++) {

                br += patch_bytes(
                    (DWORD)exe + sc.br_open[i],
                    e2,
                    r2,
                    2
                );
            }


            /* 全形空白：GBK A1 A1；Big5 A1 40 */
            static const BYTE e3[] = {
                0x81, 0x40
            };
            static const BYTE r3_gbk[] = {
                0xA1, 0xA1
            };
            static const BYTE r3_big5[] = {
                0xA1, 0x40
            };
            const BYTE *r3 =
                (g_codepage == 950) ? r3_big5 : r3_gbk;


            /* 半形空白 + 全形空白 */
            static const BYTE e4[] = {
                0x20, 0x81, 0x40
            };
            static const BYTE r4_gbk[] = {
                0x20, 0xA1, 0xA1
            };
            static const BYTE r4_big5[] = {
                0x20, 0xA1, 0x40
            };
            const BYTE *r4 =
                (g_codepage == 950) ? r4_big5 : r4_gbk;


            for (int i = 0; i < sc.n_sp1; i++) {

                br += patch_bytes(
                    (DWORD)exe + sc.sp1[i],
                    e3,
                    r3,
                    2
                );
            }


            for (int i = 0; i < sc.n_sp2; i++) {

                br += patch_bytes(
                    (DWORD)exe + sc.sp2[i],
                    e4,
                    r4,
                    3
                );
            }


            logf_(
                "掃描成功: reader=%p (呼叫者 %d), 欄位 %d/5, ReaderWrap CP%u 欄位 %d/5, FLS=%s, 】【 %d+%d, 全形空白 %d+%d",

                (void *)g_realReader,
                sc.reader_callers,
                sc.nsite,
                (unsigned)g_codepage,
                ok,

                g_readerFls != FLS_OUT_OF_INDEXES
                    ? "OK"
                    : "FAILED",

                sc.n_close,
                sc.n_open,
                sc.n_sp1,
                sc.n_sp2
            );

        } else {

            logf_(
                "掃描不到 params reader（可能不是此引擎版本）：略過標題/括號修補"
            );
        }


        /*
         * ExtraSites
         */
        if (
            found &&
            g_readerFls != FLS_OUT_OF_INDEXES
        ) {

            char ini[MAX_PATH];
            char exep[MAX_PATH];

            GetModuleFileNameA(
                NULL,
                exep,
                MAX_PATH
            );

            char *s2 = strrchr(exep, '\\');

            if (s2)
                *s2 = 0;

            wsprintfA(
                ini,
                "%s\\ChihanaGBK.ini",
                exep
            );

            char list[256];

            GetPrivateProfileStringA(
                "Params",
                "ExtraSites",
                "",
                list,
                sizeof(list),
                ini
            );

            for (
                char *t = strtok(list, ", ");
                t;
                t = strtok(NULL, ", ")
            ) {

                extra += patch_reader_site(
                    (DWORD)strtoul(t, NULL, 0)
                );
            }
        }


        logf_(
            "params CP%u hook: 五個自動欄位=%d/5；ExtraSites=%d；標點(】【全形空白): %d 處",
            (unsigned)g_codepage,
            ok,
            extra,
            br
        );

    } else {

        /*
         * 原版文字模式：
         * 不掃描、不掛 ReaderWrap、不套用 ExtraSites，
         * 也不修改目標碼頁文字位元組。
         */
        logf_(
            "EncodingEnabled=0: skipped text conversion hook, params scan, ReaderWrap/ExtraSites and target-codepage byte patches; font hooks remain enabled"
        );
    }
}


/* ---------------- InitOnce ---------------- */

static INIT_ONCE g_initOnce =
    INIT_ONCE_STATIC_INIT;


static BOOL CALLBACK initialize_once(
    PINIT_ONCE once,
    PVOID parameter,
    PVOID *context
) {
    (void)once;
    (void)parameter;
    (void)context;

    load_real_version();

    init();

    return TRUE;
}


static BOOL ensure_initialized(void) {

    return InitOnceExecuteOnce(
        &g_initOnce,
        initialize_once,
        NULL,
        NULL
    );
}


/* ---------------- DllMain ---------------- */

BOOL WINAPI DllMain(
    HINSTANCE h,
    DWORD reason,
    LPVOID reserved
) {
    (void)reserved;

    if (reason == DLL_PROCESS_ATTACH)
        DisableThreadLibraryCalls(h);

    return TRUE;
}
