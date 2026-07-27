/* dng.c -- DNG/TIFF reader + writer, ported from dng.cs. */
#include "dng.h"
#include <stdlib.h>
#include <string.h>

char dng_error[512];
static void set_err(const char *msg, const char *arg) { snprintf(dng_error, sizeof dng_error, "%s%s", arg ? arg : "", msg); }

int tiff_type_size(int t) {
    switch (t) {
        case 1: case 2: case 6: case 7: return 1;
        case 3: case 8: return 2;
        case 4: case 9: case 11: return 4;
        case 5: case 10: case 12: return 8;
        default: return 1;
    }
}

/* ---- tag lists ------------------------------------------------------------------------------------------- */
void tl_init(TagList *t) { t->items = NULL; t->count = 0; t->cap = 0; }
void tl_free(TagList *t) {
    int i; for (i = 0; i < t->count; i++) free(t->items[i].Data);
    free(t->items); t->items = NULL; t->count = t->cap = 0;
}
static void tl_reserve(TagList *t, int n) {
    if (t->cap >= n) return;
    int c = t->cap ? t->cap * 2 : 8; while (c < n) c *= 2;
    t->items = (TiffTag *)realloc(t->items, c * sizeof(TiffTag)); t->cap = c;
}
static TiffTag tag_dup(const TiffTag *s) {
    TiffTag t = *s; t.Data = (unsigned char *)malloc(s->DataLen ? s->DataLen : 1);
    memcpy(t.Data, s->Data, s->DataLen); return t;
}
static void tl_add_owned(TagList *t, TiffTag tag) { tl_reserve(t, t->count + 1); t->items[t->count++] = tag; }
void tl_copy(TagList *dst, const TagList *src) {
    int i; tl_init(dst); tl_reserve(dst, src->count);
    for (i = 0; i < src->count; i++) tl_add_owned(dst, tag_dup(&src->items[i]));
}
TiffTag *tl_get(TagList *t, int tag) {
    int i; for (i = 0; i < t->count; i++) if (t->items[i].Tag == tag) return &t->items[i];
    return NULL;
}
TiffTag tag_make(int tag, int type, const unsigned char *data, int len) {
    TiffTag t; t.Tag = (unsigned short)tag; t.Type = (unsigned short)type;
    t.Count = (unsigned)(len / tiff_type_size(type)); t.DataLen = len;
    t.Data = (unsigned char *)malloc(len ? len : 1); memcpy(t.Data, data, len); return t;
}
static int cmp_tag(const void *a, const void *b) { return (int)((const TiffTag *)a)->Tag - (int)((const TiffTag *)b)->Tag; }
void tl_put(TagList *t, TiffTag tag) {
    int i; for (i = 0; i < t->count; i++) if (t->items[i].Tag == tag.Tag) { free(t->items[i].Data); t->items[i] = tag; return; }
    tl_add_owned(t, tag);
    qsort(t->items, t->count, sizeof(TiffTag), cmp_tag);
}
void tl_drop(TagList *t, int tag) {
    int i; for (i = 0; i < t->count; i++) if (t->items[i].Tag == tag) {
        free(t->items[i].Data);
        memmove(&t->items[i], &t->items[i + 1], (t->count - i - 1) * sizeof(TiffTag)); t->count--; return;
    }
}

/* ---- primitive reads ------------------------------------------------------------------------------------- */
static unsigned U16(const Dng *d, long o) {
    return d->Le ? (unsigned)(d->Raw[o] | (d->Raw[o + 1] << 8))
                 : (unsigned)((d->Raw[o] << 8) | d->Raw[o + 1]);
}
static unsigned U32(const Dng *d, long o) {
    return d->Le ? (unsigned)(d->Raw[o] | (d->Raw[o + 1] << 8) | (d->Raw[o + 2] << 16) | ((unsigned)d->Raw[o + 3] << 24))
                 : (unsigned)(((unsigned)d->Raw[o] << 24) | (d->Raw[o + 1] << 16) | (d->Raw[o + 2] << 8) | d->Raw[o + 3]);
}

static void read_ifd(const Dng *d, long ifd, TagList *out) {
    tl_init(out);
    int n = (int)U16(d, ifd), i;
    for (i = 0; i < n; i++) {
        long e = ifd + 2 + (long)i * 12;
        TiffTag t; t.Tag = (unsigned short)U16(d, e); t.Type = (unsigned short)U16(d, e + 2); t.Count = U32(d, e + 4);
        int sz = tiff_type_size(t.Type) * (int)t.Count; t.DataLen = sz;
        long off = (sz <= 4) ? e + 8 : (long)U32(d, e + 8);
        t.Data = (unsigned char *)malloc(sz ? sz : 1);
        if (off + sz <= d->RawLen) memcpy(t.Data, d->Raw + off, sz); else memset(t.Data, 0, sz);
        tl_add_owned(out, t);
    }
}

/* value(s) of a tag as longs, honouring the file's byte order */
static long *tag_longs(const Dng *d, const TiffTag *t, int *n) {
    int i, ts = tiff_type_size(t->Type); *n = (int)t->Count;
    long *v = (long *)malloc((t->Count ? t->Count : 1) * sizeof(long));
    for (i = 0; i < (int)t->Count; i++) {
        int o = i * ts; const unsigned char *p = t->Data + o;
        if (t->Type == 3) v[i] = d->Le ? (unsigned short)(p[0] | (p[1] << 8)) : (unsigned short)((p[0] << 8) | p[1]);
        else v[i] = d->Le ? (long)(unsigned)(p[0] | (p[1] << 8) | (p[2] << 16) | ((unsigned)p[3] << 24))
                          : (long)(unsigned)(((unsigned)p[0] << 24) | (p[1] << 16) | (p[2] << 8) | p[3]);
    }
    return v;
}

long tl_scalar(TagList *tags, int tag, long dflt) {
    TiffTag *t = tl_get(tags, tag);
    if (!t || t->Count == 0) return dflt;
    const unsigned char *p = t->Data;
    /* byte order is the file's; the caller reads only from files opened little/big consistently */
    if (t->Type == 3) return (unsigned short)(p[0] | (p[1] << 8));       /* assumes LE; overwritten below for BE files */
    if (t->Type == 4) return (long)(unsigned)(p[0] | (p[1] << 8) | (p[2] << 16) | ((unsigned)p[3] << 24));
    if (t->Type == 1) return p[0];
    return dflt;
}
/* endian-aware scalar (used internally where the Dng is known) */
static long scalar_e(const Dng *d, TagList *tags, int tag, long dflt) {
    TiffTag *t = tl_get(tags, tag);
    if (!t || t->Count == 0) return dflt;
    int n; long *v = tag_longs(d, t, &n); long r = n > 0 ? v[0] : dflt; free(v); return r;
}

/* ---- open ------------------------------------------------------------------------------------------------ */
static void long_push(long **a, int *n, int *cap, long v) {
    int i; for (i = 0; i < *n; i++) if ((*a)[i] == v) return;   /* dedup */
    if (*n >= *cap) { *cap = *cap ? *cap * 2 : 16; *a = (long *)realloc(*a, *cap * sizeof(long)); }
    (*a)[(*n)++] = v;
}

Dng *dng_open(const char *path) {
    FILE *f = fopen(path, "rb");
    if (!f) { set_err(": cannot open file", path); return NULL; }
    fseek(f, 0, SEEK_END); long len = ftell(f); fseek(f, 0, SEEK_SET);
    Dng *d = (Dng *)calloc(1, sizeof(Dng));
    d->Raw = (unsigned char *)malloc(len > 0 ? len : 1); d->RawLen = len;
    if (fread(d->Raw, 1, len, f) != (size_t)len) { fclose(f); dng_free(d); set_err(": read failed", path); return NULL; }
    fclose(f);
    d->Dpi = 4000;

    if (len < 8) { dng_free(d); set_err(": not a TIFF/DNG (too short)", path); return NULL; }
    if (d->Raw[0] == 0x49 && d->Raw[1] == 0x49) d->Le = 1;
    else if (d->Raw[0] == 0x4D && d->Raw[1] == 0x4D) d->Le = 0;
    else { dng_free(d); set_err(": not a TIFF/DNG (bad byte-order mark)", path); return NULL; }
    if (U16(d, 2) != 42) { dng_free(d); set_err(": not a TIFF/DNG (bad magic)", path); return NULL; }

    d->Ifd0 = U32(d, 4);
    read_ifd(d, d->Ifd0, &d->Ifd0Tags);

    long *ifds = NULL; int nifd = 0, capifd = 0;
    long p = d->Ifd0;
    while (p != 0 && p + 2 < d->RawLen) { long_push(&ifds, &nifd, &capifd, p); int n = (int)U16(d, p); p = U32(d, p + 2 + (long)n * 12); }
    { int i; for (i = 0; i < nifd; i++) {
        TagList tags; read_ifd(d, ifds[i], &tags);
        TiffTag *sub = tl_get(&tags, DNG_SUBIFDS);
        if (sub) { int m, j; long *sl = tag_longs(d, sub, &m); for (j = 0; j < m; j++) if (sl[j] > 0 && sl[j] + 2 < d->RawLen) long_push(&ifds, &nifd, &capifd, sl[j]); free(sl); }
        tl_free(&tags);
    } }

    long img = -1; int i;
    for (i = 0; i < nifd; i++) {
        TagList tags; read_ifd(d, ifds[i], &tags);
        long spp = scalar_e(d, &tags, DNG_SAMPLES, 1), comp = scalar_e(d, &tags, DNG_COMPRESSION, 1), plan = scalar_e(d, &tags, DNG_PLANAR, 1);
        TiffTag *bt = tl_get(&tags, DNG_BITS); int all16 = 0;
        if (bt) { int m, j; long *bps = tag_longs(d, bt, &m); all16 = m > 0; for (j = 0; j < m; j++) if (bps[j] != 16) all16 = 0; free(bps); }
        if (spp >= 3 && all16 && comp == 1 && plan == 1) { img = ifds[i]; tl_copy(&d->ImageTags, &tags); tl_free(&tags); break; }
        tl_free(&tags);
    }
    free(ifds);
    if (img < 0) { dng_free(d); set_err(": no uncompressed 16-bit chunky image found", path); return NULL; }

    d->ImageIfd = img;
    d->W = (int)scalar_e(d, &d->ImageTags, DNG_WIDTH, 0);
    d->H = (int)scalar_e(d, &d->ImageTags, DNG_LENGTH, 0);
    d->Samples = (int)scalar_e(d, &d->ImageTags, DNG_SAMPLES, 4);
    d->RowsPerStrip = (int)scalar_e(d, &d->ImageTags, DNG_ROWS_PER_STRIP, d->H);
    if (d->RowsPerStrip <= 0) d->RowsPerStrip = d->H;
    d->rowBytes = d->W * d->Samples * 2;
    { TiffTag *so = tl_get(&d->ImageTags, DNG_STRIP_OFFSETS); d->stripOff = tag_longs(d, so, &d->stripCount); }

    /* XResolution rational -> dpi */
    TiffTag *xr = tl_get(&d->ImageTags, DNG_XRES); if (!xr) xr = tl_get(&d->Ifd0Tags, DNG_XRES);
    if (xr && xr->Count >= 1) {
        const unsigned char *b = xr->Data;
        unsigned num = d->Le ? (unsigned)(b[0] | (b[1] << 8) | (b[2] << 16) | ((unsigned)b[3] << 24)) : (unsigned)(((unsigned)b[0] << 24) | (b[1] << 16) | (b[2] << 8) | b[3]);
        unsigned den = d->Le ? (unsigned)(b[4] | (b[5] << 8) | (b[6] << 16) | ((unsigned)b[7] << 24)) : (unsigned)(((unsigned)b[4] << 24) | (b[5] << 16) | (b[6] << 8) | b[7]);
        if (den != 0) { d->Dpi = (int)((double)num / den + 0.5); d->DpiFromFile = 1; }
    }
    snprintf(d->Info, sizeof d->Info, "%dx%d, %d samples x 16-bit, chunky, %s-endian, %d strip(s) of %d row(s), %d dpi%s",
             d->W, d->H, d->Samples, d->Le ? "little" : "big", d->stripCount, d->RowsPerStrip, d->Dpi,
             d->DpiFromFile ? "" : " (ASSUMED -- no XResolution; use -dpi)");
    return d;
}

void dng_free(Dng *d) {
    if (!d) return;
    tl_free(&d->Ifd0Tags); tl_free(&d->ImageTags);
    free(d->stripOff); free(d->Raw); free(d);
}

int dng_has_ir(const Dng *d) { return d->Samples >= 4; }

int dng_make_map(const Dng *d, const char *order, int map[4]) {
    char buf[8]; const char *rgb = "RGB"; int c, i, ln;
    if (!order || !order[0]) order = dng_has_ir(d) ? "RGBI" : "RGB";
    ln = (int)strlen(order); if (ln > 7) ln = 7;
    for (i = 0; i < ln; i++) { char ch = order[i]; if (ch >= 'a' && ch <= 'z') ch -= 32; buf[i] = ch; } buf[ln] = 0;
    if (ln != 3 && ln != 4) { snprintf(dng_error, sizeof dng_error, "order must be a permutation of RGB or RGBI, got: %s", order); return -1; }
    for (c = 0; c < 3; c++) {
        const char *pos = strchr(buf, rgb[c]);
        if (!pos) { snprintf(dng_error, sizeof dng_error, "order must be a permutation of RGB or RGBI, got: %s", order); return -1; }
        i = (int)(pos - buf);
        if (i >= d->Samples) { snprintf(dng_error, sizeof dng_error, "order %s needs more channels than the file has (%d)", order, d->Samples); return -1; }
        map[c] = i;
    }
    { const char *pI = strchr(buf, 'I'); map[3] = pI ? (int)(pI - buf) : -1; }
    if (map[3] >= d->Samples) { snprintf(dng_error, sizeof dng_error, "order %s asks for an IR channel, but the file has only %d channels", order, d->Samples); return -1; }
    return 0;
}

void dng_load_row(const Dng *d, int row, const int map[4], short *dst) {
    int strip = row / d->RowsPerStrip, x, c;
    if (strip >= d->stripCount) { memset(dst, 0, (size_t)d->W * 4 * sizeof(short)); return; }
    long baseOff = d->stripOff[strip] + (long)(row % d->RowsPerStrip) * d->rowBytes;
    for (x = 0; x < d->W; x++)
        for (c = 0; c < 4; c++) {
            if (map[c] < 0) { dst[x * 4 + c] = 0; continue; }
            long o = baseOff + ((long)x * d->Samples + map[c]) * 2;
            dst[x * 4 + c] = (o + 1 < d->RawLen)
                ? (short)(d->Le ? (unsigned short)(d->Raw[o] | (d->Raw[o + 1] << 8)) : (unsigned short)((d->Raw[o] << 8) | d->Raw[o + 1]))
                : (short)0;
        }
}

unsigned char *dng_thumbnail(const Dng *d, int *outLen) {
    TagList *t = (TagList *)&d->Ifd0Tags;
    TiffTag *so = tl_get(t, DNG_STRIP_OFFSETS), *sc = tl_get(t, DNG_STRIP_COUNTS);
    if (!so || !sc) { *outLen = 0; return NULL; }
    int no, nc, i; long *offs = tag_longs(d, so, &no), *cnts = tag_longs(d, sc, &nc);
    long total = 0; for (i = 0; i < nc; i++) total += cnts[i];
    unsigned char *out = (unsigned char *)malloc(total ? total : 1); long q = 0;
    for (i = 0; i < no; i++) {
        if (offs[i] + cnts[i] > d->RawLen) { free(offs); free(cnts); free(out); *outLen = 0; return NULL; }
        memcpy(out + q, d->Raw + offs[i], cnts[i]); q += cnts[i];
    }
    free(offs); free(cnts); *outLen = (int)total; return out;
}

/* ---- writer ---------------------------------------------------------------------------------------------- */
static void WU16(DngWriter *w, int v) {
    if (w->le) { fputc(v & 0xFF, w->fs); fputc((v >> 8) & 0xFF, w->fs); }
    else { fputc((v >> 8) & 0xFF, w->fs); fputc(v & 0xFF, w->fs); }
}
static void WU32(DngWriter *w, long v) {
    if (w->le) { fputc(v & 0xFF, w->fs); fputc((v >> 8) & 0xFF, w->fs); fputc((v >> 16) & 0xFF, w->fs); fputc((v >> 24) & 0xFF, w->fs); }
    else { fputc((v >> 24) & 0xFF, w->fs); fputc((v >> 16) & 0xFF, w->fs); fputc((v >> 8) & 0xFF, w->fs); fputc(v & 0xFF, w->fs); }
}
DngWriter *dngw_create(const char *path, int le) {
    DngWriter *w = (DngWriter *)calloc(1, sizeof(DngWriter));
    w->fs = fopen(path, "wb"); w->le = le;
    if (!w->fs) { free(w); return NULL; }
    fputc(le ? 0x49 : 0x4D, w->fs); fputc(le ? 0x49 : 0x4D, w->fs);
    WU16(w, 42);
    w->ifd0PtrPos = ftell(w->fs);
    WU32(w, 0);
    w->DataPos = ftell(w->fs);
    return w;
}
void dngw_write_data(DngWriter *w, const unsigned char *b, int len) { fwrite(b, 1, len, w->fs); w->DataLen += len; }
long dngw_position(DngWriter *w) { return ftell(w->fs); }
void dngw_align(DngWriter *w) { if (ftell(w->fs) & 1) fputc(0, w->fs); }
long dngw_write_blob(DngWriter *w, const unsigned char *b, int len) { dngw_align(w); long p = ftell(w->fs); fwrite(b, 1, len, w->fs); return p; }

long dngw_write_ifd(DngWriter *w, TagList *tags, long nextIfd) {
    dngw_align(w);
    long ifdPos = ftell(w->fs);
    int n = tags->count, i;
    long valuePos = ifdPos + 2 + (long)n * 12 + 4;
    if (valuePos & 1) valuePos++;
    long *offs = (long *)malloc((n ? n : 1) * sizeof(long));
    long vp = valuePos;
    for (i = 0; i < n; i++) {
        int sz = tags->items[i].DataLen;
        if (sz > 4) { offs[i] = vp; vp += sz; if (vp & 1) vp++; } else offs[i] = -1;
    }
    WU16(w, n);
    for (i = 0; i < n; i++) {
        TiffTag *t = &tags->items[i];
        WU16(w, t->Tag); WU16(w, t->Type); WU32(w, t->Count);
        if (offs[i] < 0) {
            unsigned char pad[4] = {0,0,0,0}; int c = t->DataLen < 4 ? t->DataLen : 4;
            memcpy(pad, t->Data, c); fwrite(pad, 1, 4, w->fs);
        } else WU32(w, offs[i]);
    }
    WU32(w, nextIfd);
    for (i = 0; i < n; i++)
        if (offs[i] >= 0) {
            if (ftell(w->fs) < offs[i]) fputc(0, w->fs);
            fwrite(tags->items[i].Data, 1, tags->items[i].DataLen, w->fs);
        }
    free(offs);
    return ifdPos;
}
void dngw_set_ifd0(DngWriter *w, long pos) { long save = ftell(w->fs); fseek(w->fs, w->ifd0PtrPos, SEEK_SET); WU32(w, pos); fseek(w->fs, save, SEEK_SET); }
void dngw_dispose(DngWriter *w) { if (w) { if (w->fs) fclose(w->fs); free(w); } }
