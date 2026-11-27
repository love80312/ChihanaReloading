/* kaguya_scan.h -- 在「已載入（或模擬載入）的 exe 映像」中自動定位 Kaguya 引擎 params 讀取點。
 * 純 C，只用 string.h / stdint.h，方便在沒有 Windows 的環境測試。img = 映像起點，位址以 RVA 計。
 * 作法對應 Kaguya_YaneKit docs/PE/README_dll.md §8.6：
 *   1) 依函式形狀找 params string16 reader（用兩次 byte 讀取組成 u16 長度；前面是 SEH 序言），取呼叫者最多者
 *   2) 反掃所有 call reader，找 GameSystem 欄位：呼叫前不遠處 lea reg,[base+0x1c/0x20/0x24/0x30/0x34] 且其後 push reg
 *   3) 在同一函式附近（0x400 位元組內）取欄位種類最多的一群
 */
#ifndef KAGUYA_SCAN_H
#define KAGUYA_SCAN_H
#include <stdint.h>
#include <string.h>

typedef struct {
    uint32_t reader;          /* RVA */
    int      reader_callers;
    uint32_t site[5];         /* RVA，順序：gameTitle, displayTitle, brand, defaultFirst, defaultSecond；0 = 未找到 */
    int      nsite;
    uint32_t br_close[4], br_open[4]; int n_close, n_open;   /* 「】」「【」字串的 RVA（被引用者） */
    uint32_t sp1[4], sp2[4]; int n_sp1, n_sp2;                /* 「　」(81 40，視窗標題分隔) 與「 　」(20 81 40，人名組字) 字串的 RVA */
} KScan;

static uint32_t ks_u32(const uint8_t *p) { uint32_t v; memcpy(&v, p, 4); return v; }
static uint16_t ks_u16(const uint8_t *p) { uint16_t v; memcpy(&v, p, 2); return v; }

static int ks_sections(const uint8_t *img, int idx, uint32_t *rva, uint32_t *size, uint32_t *chr) {
    uint32_t pe = ks_u32(img + 0x3c); int n = ks_u16(img + pe + 6); int osz = ks_u16(img + pe + 20);
    if (idx >= n) return 0;
    const uint8_t *s = img + pe + 24 + osz + 40 * idx;
    *size = ks_u32(s + 8); *rva = ks_u32(s + 12); *chr = ks_u32(s + 36); return 1;
}

static int ks_call_target_is(const uint8_t *t, uint32_t tsz, uint32_t trva, uint32_t p, uint32_t target_rva) {
    if (t[p] != 0xE8 || p + 5 > tsz) return 0;
    return (uint32_t)(trva + p + 5 + (int32_t)ks_u32(t + p + 1)) == target_rva;
}

static int ks_scan(const uint8_t *img, uint32_t image_base, KScan *r) {
        static const uint8_t OFFS[5] = {0x1c,0x20,0x24,0x30,0x34};
    memset(r, 0, sizeof *r);
    uint32_t trva = 0, tsz = 0, ch; int i;
    for (i = 0; ks_sections(img, i, &trva, &tsz, &ch); i++) if (ch & 0x20000000) break;   /* 第一個可執行區段 = .text */
    if (!tsz) return 0;
    const uint8_t *t = img + trva; const uint32_t text_rva = trva;
    /* 1. reader：用兩次 byte 讀取組成 u16 長度（movzx r,[eax+edx+1]; movzx eax,[eax+edx]; shl r,8; add ...）。
     *    暫存器配置依編譯器而異（A000: ebx / h000: ecx），所以只固定位元組結構，暫存器欄位用遮罩比對。 */
    for (uint32_t p = 0; p + 14 <= tsz; p++) {
        if (t[p] != 0x0F || t[p+1] != 0xB6 || (t[p+2] & 0xC7) != 0x44 || t[p+3] != 0x10 || t[p+4] != 0x01) continue;
        int reg = (t[p+2] >> 3) & 7;
        if (t[p+5] != 0x0F || t[p+6] != 0xB6 || t[p+7] != 0x04 || t[p+8] != 0x10) continue;
        if (t[p+9] != 0xC1 || t[p+10] != (0xE0 | reg) || t[p+11] != 0x08 || t[p+12] != 0x03) continue;
        if (t[p+13] != (0xC0 | (reg << 3)) && t[p+13] != (0xC0 | reg)) continue;
        uint32_t cand = 0;
        for (uint32_t q = p; q + 0x70 >= p && q + 3 <= tsz; q--) { if (t[q] == 0x55 && t[q+1] == 0x8B && t[q+2] == 0xEC) { cand = q; break; } if (q == 0) break; }
        if (!cand) continue;
        int nc = 0; for (uint32_t c = 0; c + 5 <= tsz; c++) if (t[c] == 0xE8 && ks_call_target_is(t, tsz, trva, c, trva + cand)) nc++;
        if (nc > r->reader_callers) { r->reader_callers = nc; r->reader = trva + cand; }
    }
    if (r->reader_callers < 10) return 0;
    /* 2. 欄位呼叫點 */
    struct { int k; uint32_t p; } rec[256]; int nrec = 0;
    for (uint32_t p = 0; p + 5 <= tsz && nrec < 256; p++) {
        if (!ks_call_target_is(t, tsz, trva, p, r->reader)) continue;
        for (int64_t q = (int64_t)p - 3; q >= 0 && q >= (int64_t)p - 0x40; q--) {
            if (t[q] != 0x8D || (t[q+1] & 0xC0) != 0x40 || (t[q+1] & 7) == 4) continue;
            int k = -1; for (int o = 0; o < 5; o++) if (t[q+2] == OFFS[o]) k = o;
            if (k < 0) continue;
            int reg = (t[q+1] >> 3) & 7, pushed = 0;
            for (uint32_t x = (uint32_t)q + 3; x < p; x++) if (t[x] == 0x50 + reg) pushed = 1;
            if (pushed) { rec[nrec].k = k; rec[nrec].p = p; nrec++; break;   /* p = .text 內偏移 */ }
        }
    }
    int best = 0; uint32_t bestp = 0;
    for (int a = 0; a < nrec; a++) {
        int mask = 0; for (int b = 0; b < nrec; b++) if ((int64_t)rec[b].p - rec[a].p <= 0x400 && (int64_t)rec[a].p - rec[b].p <= 0x400) mask |= 1 << rec[b].k;
        int c = __builtin_popcount(mask); if (c > best) { best = c; bestp = rec[a].p; }
    }
    for (int b = 0; b < nrec; b++)
        if ((int64_t)rec[b].p - bestp <= 0x400 && (int64_t)bestp - rec[b].p <= 0x400 && !r->site[rec[b].k]) { r->site[rec[b].k] = text_rva + rec[b].p; r->nsite++; }
    /* 3. 「】」「【」「　」「 　」：獨立 SJIS 字串（前後為 NUL）且有被引用，位於非可執行區段 */
    uint32_t total = ks_u32(img + ks_u32(img + 0x3c) + 24 + 56);   /* SizeOfImage */
    for (i = 0; ks_sections(img, i, &trva, &tsz, &ch); i++) {
        if (ch & 0x20000000) continue;
        const uint8_t *d = img + trva;
        for (uint32_t p = 1; p + 4 <= tsz; p++) {
            int kind = 0;   /* 1=】 2=【 3=全形空白 4=半形空白+全形空白 */
            if (d[p-1] == 0 && d[p] == 0x81 && d[p+2] == 0) { if (d[p+1] == 0x7A) kind = 1; else if (d[p+1] == 0x79) kind = 2; else if (d[p+1] == 0x40) kind = 3; }
            else if (d[p-1] == 0 && d[p] == 0x20 && d[p+1] == 0x81 && d[p+2] == 0x40 && d[p+3] == 0) kind = 4;
            if (!kind) continue;
            uint32_t va = image_base + trva + p; int refs = 0;
            for (uint32_t x = 0; x + 4 <= total && !refs; x++) if (ks_u32(img + x) == va) refs = 1;
            if (!refs) continue;
            if (kind == 1 && r->n_close < 4) r->br_close[r->n_close++] = trva + p;
            if (kind == 2 && r->n_open  < 4) r->br_open[r->n_open++]  = trva + p;
            if (kind == 3 && r->n_sp1   < 4) r->sp1[r->n_sp1++] = trva + p;
            if (kind == 4 && r->n_sp2   < 4) r->sp2[r->n_sp2++] = trva + p;
        }
    }
    return r->nsite >= 4;
}
#endif
