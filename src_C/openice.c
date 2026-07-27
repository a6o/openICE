/* openice.c -- the openICE command-line tool (C port): RGBI DNG in, clean RGB DNG out.
 * Same CLI as the C# tool. Ported from openice.cs. */
#include "ice.h"
#include "dng.h"
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <math.h>

/* raw-IR "clear film" gate for the analysis regressions (8847.226) */
#define RAW_IR_GATE_BITS 0x460A3CE7u

/* DIAGNOSTIC: per-row IRref/crosstalk override arrays (see ice_pump.c). Set by -irtrace. */
extern const float *ice_dbg_irref_rows;
extern const float *ice_dbg_ct_rows;

/* load frame3_ir.csv (row,irref,ct2c,ct34,...) into per-INPUT-row arrays of length H (index = row, clamped).
 * shift>0 pulls the value from an earlier logged row (pipeline lag). Returns 0 on success. */
static int load_irtrace(const char *path, int H, int shift, float **irrefOut, float **ctOut) {
    FILE *f = fopen(path, "rb"); if (!f) return 1;
    float *ir = (float *)malloc((size_t)H * sizeof(float));
    float *ct = (float *)malloc((size_t)H * sizeof(float));
    int n = 0; char line[512];
    float *tir = (float *)malloc((size_t)(H + 16) * sizeof(float));
    float *tct = (float *)malloc((size_t)(H + 16) * sizeof(float));
    if (!fgets(line, sizeof line, f)) { fclose(f); return 1; }   /* header */
    while (fgets(line, sizeof line, f) && n < H + 16) {
        int row; float irref, ct2c, ct34;
        if (sscanf(line, "%d,%f,%f,%f", &row, &irref, &ct2c, &ct34) == 4) { tir[n] = irref; tct[n] = ct34; n++; }
    }
    fclose(f);
    { int k; for (k = 0; k < H; k++) { int idx = k - shift; if (idx < 0) idx = 0; if (idx >= n) idx = n - 1;
        ir[k] = tir[idx]; ct[k] = tct[idx]; } }
    free(tir); free(tct);
    *irrefOut = ir; *ctOut = ct;
    printf("  irtrace        : %s (%d rows, shift %d) -> per-row IRref+crosstalk override ACTIVE\n", path, n, shift);
    return 0;
}

/* ---- LUT (matches setup / analyze) ----------------------------------------------------------------------- */
static float *build_lut(void) {
    float *lut = (float *)malloc(65536 * sizeof(float));
    double k = 65535.0 / (16.0 * log(2.0)); int i;
    for (i = 0; i <= 65535; i++) lut[i] = (float)(k * log((double)i + 1.0));
    return lut;
}

static IceCalib analyze_image(const unsigned short *img, int W, int H) {
    float *lut = build_lut();
    IceCalib c = ice_analyze_run(img, W, H, lut, 65535, float_from_bits(RAW_IR_GATE_BITS));
    free(lut); return c;
}

/* load a low-res RGBI scan into an interleaved 16-bit buffer */
static unsigned short *load_rgbi(const Dng *d, const int map[4], int W, int H) {
    unsigned short *img = (unsigned short *)malloc((size_t)W * H * 4 * sizeof(unsigned short));
    short *row = (short *)malloc((size_t)W * 4 * sizeof(short)); int r, i;
    for (r = 0; r < H; r++) { dng_load_row(d, r, map, row); for (i = 0; i < W * 4; i++) img[(size_t)r * W * 4 + i] = (unsigned short)row[i]; }
    free(row); return img;
}

/* down-sample the main RGBI scan by box-averaging f x f blocks */
static unsigned short *downsample_main(const Dng *src, const int map[4], int W, int H, int *lw_out, int *lh_out, int *f_out) {
    int f = (int)(W / 281.0 + 0.5); if (f < 1) f = 1;
    int lw = W / f, lh = H / f, ry, rx, c, k;
    unsigned short *img = (unsigned short *)malloc((size_t)lw * lh * 4 * sizeof(unsigned short));
    short **rows = (short **)malloc(f * sizeof(short *));
    for (k = 0; k < f; k++) rows[k] = (short *)malloc((size_t)W * 4 * sizeof(short));
    for (ry = 0; ry < lh; ry++) {
        for (k = 0; k < f; k++) dng_load_row(src, ry * f + k, map, rows[k]);
        for (rx = 0; rx < lw; rx++)
            for (c = 0; c < 4; c++) {
                long sum = 0; int bx = rx * f, ky, kx;
                for (ky = 0; ky < f; ky++) for (kx = 0; kx < f; kx++) sum += (unsigned short)rows[ky][(bx + kx) * 4 + c];
                img[((size_t)ry * lw + rx) * 4 + c] = (unsigned short)(sum / (f * f));
            }
    }
    for (k = 0; k < f; k++) free(rows[k]); free(rows);
    *lw_out = lw; *lh_out = lh; *f_out = f; return img;
}

/* ---- pump callbacks -------------------------------------------------------------------------------------- */
typedef struct { const Dng *img; int map[4]; } LoadCtx;
typedef struct { DngWriter *dw; int emitRows; } EmitCtx;
typedef struct { unsigned char *guMap; int W; } GuCtx;

static void load_cb(void *ctx, int row, short *rgbi) { LoadCtx *c = (LoadCtx *)ctx; dng_load_row(c->img, row, c->map, rgbi); }
static void emit_cb(void *ctx, int row, unsigned char *rgb, int n) {
    EmitCtx *c = (EmitCtx *)ctx; dngw_write_data(c->dw, rgb, n);
    if (((row + 1) & 255) == 0) { printf("\r  rows %d/%d", row + 1, c->emitRows); fflush(stdout); }
}
static void giveup_cb(void *ctx, int row, const unsigned char *gu) {
    GuCtx *c = (GuCtx *)ctx; long b = (long)row * c->W; int x;
    for (x = 0; x < c->W; x++) c->guMap[b + x] = gu[x] == 0 ? (unsigned char)255 : (unsigned char)0;
}

/* ---- PGM + DNG output ------------------------------------------------------------------------------------ */
static void write_pgm(const char *path, int W, int H, const unsigned char *data) {
    FILE *f = fopen(path, "wb"); if (!f) return;
    fprintf(f, "P5\n%d %d\n255\n", W, H);
    fwrite(data, 1, (size_t)W * H, f);
    fclose(f);
}

static void pack_u16(unsigned char *b, int le, int i, int v) {
    if (le) { b[i * 2] = v & 0xFF; b[i * 2 + 1] = (v >> 8) & 0xFF; } else { b[i * 2] = (v >> 8) & 0xFF; b[i * 2 + 1] = v & 0xFF; }
}
static void pack_u32(unsigned char *b, int le, int i, long v) {
    int k; for (k = 0; k < 4; k++) b[i * 4 + (le ? k : 3 - k)] = (unsigned char)((v >> (k * 8)) & 0xFF);
}

static void write_dng(DngWriter *dw, Dng *src, int W, int H, int outRow, long dataPos) {
    int le = src->Le; long dataLen = (long)outRow * H;
    unsigned char buf[16]; TagList im, i0;
    int thumbLen = 0; unsigned char *thumb; long thumbPos = 0, subPos, ifd0;
    (void)W;

    tl_copy(&im, &src->ImageTags);
    pack_u16(buf, le, 0, 16); pack_u16(buf, le, 1, 16); pack_u16(buf, le, 2, 16); tl_put(&im, tag_make(DNG_BITS, 3, buf, 6));
    pack_u16(buf, le, 0, 3); tl_put(&im, tag_make(DNG_SAMPLES, 3, buf, 2));
    pack_u16(buf, le, 0, H); tl_put(&im, tag_make(DNG_ROWS_PER_STRIP, 3, buf, 2));
    pack_u32(buf, le, 0, dataPos); tl_put(&im, tag_make(DNG_STRIP_OFFSETS, 4, buf, 4));
    pack_u32(buf, le, 0, dataLen); tl_put(&im, tag_make(DNG_STRIP_COUNTS, 4, buf, 4));
    pack_u16(buf, le, 0, H); tl_put(&im, tag_make(DNG_LENGTH, 3, buf, 2));
    if (tl_get(&im, DNG_MIN_SAMPLE)) { pack_u16(buf, le, 0, 0); pack_u16(buf, le, 1, 0); pack_u16(buf, le, 2, 0); tl_put(&im, tag_make(DNG_MIN_SAMPLE, 3, buf, 6)); }
    if (tl_get(&im, DNG_MAX_SAMPLE)) { pack_u16(buf, le, 0, 65535); pack_u16(buf, le, 1, 65535); pack_u16(buf, le, 2, 65535); tl_put(&im, tag_make(DNG_MAX_SAMPLE, 3, buf, 6)); }

    thumb = dng_thumbnail(src, &thumbLen);
    if (thumb) thumbPos = dngw_write_blob(dw, thumb, thumbLen);
    subPos = dngw_write_ifd(dw, &im, 0);

    tl_copy(&i0, &src->Ifd0Tags);
    pack_u32(buf, le, 0, subPos); tl_put(&i0, tag_make(DNG_SUBIFDS, 4, buf, 4));
    if (thumb) {
        pack_u32(buf, le, 0, thumbPos); tl_put(&i0, tag_make(DNG_STRIP_OFFSETS, 4, buf, 4));
        pack_u32(buf, le, 0, thumbLen); tl_put(&i0, tag_make(DNG_STRIP_COUNTS, 4, buf, 4));
        pack_u16(buf, le, 0, (int)tl_scalar(&src->Ifd0Tags, DNG_LENGTH, 0)); tl_put(&i0, tag_make(DNG_ROWS_PER_STRIP, 3, buf, 2));
    }
    { TiffTag *cm = tl_get(&i0, DNG_COLOR_MATRIX1);
      if (cm && cm->Count == 12) { unsigned char d[9 * 8]; memcpy(d, cm->Data, 9 * 8); tl_put(&i0, tag_make(DNG_COLOR_MATRIX1, cm->Type, d, 9 * 8)); } }
    { TiffTag *fm = tl_get(&i0, DNG_FORWARD_MATRIX1);
      if (fm && fm->Count == 12) { unsigned char d[9 * 8]; int r; for (r = 0; r < 3; r++) memcpy(d + (r * 3) * 8, fm->Data + (r * 4) * 8, 3 * 8); tl_put(&i0, tag_make(DNG_FORWARD_MATRIX1, fm->Type, d, 9 * 8)); } }
    { const char *sw = "openICE (open reimplementation)"; tl_put(&i0, tag_make(DNG_SOFTWARE, 2, (const unsigned char *)sw, (int)strlen(sw) + 1)); }
    tl_drop(&i0, DNG_EXIF_IFD);

    ifd0 = dngw_write_ifd(dw, &i0, 0); dngw_set_ifd0(dw, ifd0);
    free(thumb); tl_free(&im); tl_free(&i0);
}

/* ---- CLI ------------------------------------------------------------------------------------------------- */
static void usage(void) {
    printf("openice -- Digital ICE, reimplemented.  RGBI DNG in, clean RGB DNG out.\n\n");
    printf("  openice <in.dng> [out.dng] [options]\n");
    printf("  -o <out.dng>        output path (default <in>.openice.dng; a bare 2nd arg works too)\n");
    printf("  -order <RGBI>       channel order of the source samples (default RGBI; 4th letter = IR)\n");
    printf("  -dpi <N>            override the source resolution\n");
    printf("  -lowres <file>      low-res scan for the calibration; if omitted, the main scan is down-sampled\n");
    printf("  -lowres_fields <V>  the three analysis fields directly: irCrosstalk,Rref,irRefRaw\n");
    printf("  -giveup <file.pgm>  also write the give-up map (white = reconstructed, black = gave up)\n");
    printf("  -fine               ICE Fine (default is ICE Normal): stage gains 1.0 not 1.25, L3 clamp off\n");
    printf("  -kind <7|8|9>       scanner reconstruction target (8 = LS-5000 default, 7 = LS-9000; all byte-exact)\n");
}

int main(int argc, char **argv) {
    /* x87 precision control. The original 2003 MSVC build runs the FPU at 53-bit mantissa (control word 0x027F,
     * the MSVCRT default), NOT 64-bit extended. Matching that is what makes the x87 evaluation bit-identical.
     * ICE_FPUCW overrides for testing (hex, e.g. 037F for 64-bit extended). */
    { unsigned short cw = 0x027F; const char *e = getenv("ICE_FPUCW"); if (e) cw = (unsigned short)strtol(e, 0, 16);
      __asm__ __volatile__("fldcw %0" :: "m"(cw)); }

    if (argc < 2) { usage(); return 2; }
    {
        const char *path = argv[1], *outp = NULL, *order = NULL, *lowresPath = NULL, *giveupPath = NULL, *irtracePath = NULL;
        float lowresFields[3]; int haveFields = 0, argDpi = 0, irshift = 0, irmode = 0, i;
        for (i = 2; i < argc; i++) {
            const char *a = argv[i];
            if (!strcmp(a, "-o")) outp = argv[++i];
            else if (!strcmp(a, "-order")) order = argv[++i];
            else if (!strcmp(a, "-dpi")) argDpi = atoi(argv[++i]);
            else if (!strcmp(a, "-lowres")) lowresPath = argv[++i];
            else if (!strcmp(a, "-irtrace")) irtracePath = argv[++i];
            else if (!strcmp(a, "-irshift")) irshift = atoi(argv[++i]);
            else if (!strcmp(a, "-irmode")) irmode = atoi(argv[++i]);   /* 0=both 1=IRref-only 2=crosstalk-only */
            else if (!strcmp(a, "-giveup")) giveupPath = argv[++i];
            else if (!strcmp(a, "-fine")) { extern int ice_fine; ice_fine = 1; }   /* ICE Fine */
            else if (!strcmp(a, "-kind")) { extern int ice_kind; ice_kind = atoi(argv[++i]);   /* reconstruction target */
                if (ice_kind != 7 && ice_kind != 8 && ice_kind != 9) { fprintf(stderr, "ERROR: -kind must be 7, 8, or 9 (8 = LS-5000, default)\n"); return 2; }
                if (ice_kind == 7) fprintf(stderr, "note: -kind 7 (LS-9000) reconstruction is byte-exact. On a REAL 9000 scan\n"
                                                   "      via -lowres, the analysis-pass calibration is ~200 ULPs off (imperceptible);\n"
                                                   "      supply -lowres_fields for a bit-exact reconstruction.\n"); }
            else if (!strcmp(a, "-lowres_fields")) {
                char *s = argv[++i], *tok; int n = 0;
                for (tok = strtok(s, ","); tok && n < 3; tok = strtok(NULL, ",")) lowresFields[n++] = (float)atof(tok);
                if (n != 3) { fprintf(stderr, "ERROR: -lowres_fields needs 3 comma-separated values: irCrosstalk,Rref,irRefRaw\n"); return 2; }
                haveFields = 1;
            } else if (!outp) outp = a;
        }

        {
            Dng *img = dng_open(path);
            if (!img) { fprintf(stderr, "ERROR: %s\n", dng_error); return 2; }
            int W = img->W, H = img->H, map[4], dpi;
            if (dng_make_map(img, order, map) != 0) { fprintf(stderr, "ERROR: %s\n", dng_error); dng_free(img); return 2; }
            dpi = argDpi > 0 ? argDpi : img->Dpi;
            if (map[3] < 0) { fprintf(stderr, "ERROR: %s: no IR channel (%d ch). Digital ICE needs one.\n", path, img->Samples); dng_free(img); return 2; }

            IceCalib cal; char calSrc[400]; float irCrosstalk, irref;
            if (haveFields) {
                cal.crosstalk = lowresFields[0]; cal.coeff2 = 1.5f * lowresFields[0]; cal.rref = lowresFields[1]; cal.irRefRaw = lowresFields[2];
                snprintf(calSrc, sizeof calSrc, "-lowres_fields");
            } else if (lowresPath) {
                Dng *low = dng_open(lowresPath);
                if (!low) { fprintf(stderr, "ERROR: %s\n", dng_error); dng_free(img); return 2; }
                int lmap[4]; if (dng_make_map(low, order, lmap) != 0) { fprintf(stderr, "ERROR: %s\n", dng_error); dng_free(low); dng_free(img); return 2; }
                { unsigned short *limg = load_rgbi(low, lmap, low->W, low->H);
                  cal = analyze_image(limg, low->W, low->H); free(limg); }
                snprintf(calSrc, sizeof calSrc, "-lowres %s (%dx%d, analyzed, bit-exact)", lowresPath, low->W, low->H);
                dng_free(low);
            } else {
                int lw, lh, f; unsigned short *limg = downsample_main(img, map, W, H, &lw, &lh, &f);
                cal = analyze_image(limg, lw, lh); free(limg);
                snprintf(calSrc, sizeof calSrc, "down-sampled main scan 1/%d -> %dx%d (single-scan approximation)", f, lw, lh);
            }
            ice_analyze_install(cal, &irCrosstalk, &irref);

            char defOut[1024];
            if (!outp) {
                const char *dot = strrchr(path, '.'); size_t n = dot ? (size_t)(dot - path) : strlen(path);
                if (n > sizeof defOut - 20) n = sizeof defOut - 20;
                memcpy(defOut, path, n); memcpy(defOut + n, ".openice.dng", 13); outp = defOut;
            }

            printf("openICE          : %s\n", path);
            printf("source           : %s\n", img->Info);
            printf("geometry         : %d x %d @ %d dpi\n", W, H, dpi);
            printf("calibration      : %s\n", calSrc);
            printf("  fields         : irCrosstalk=%.9g  Rref=%.9g  irRefRaw=%.9g\n", cal.crosstalk, cal.rref, cal.irRefRaw);
            printf("  installed      : irCrosstalk=%.9g  IRref=%.9g\n", irCrosstalk, irref);

            unsigned char *guMap = NULL; GuCtx guctx; guctx.guMap = NULL; guctx.W = W;
            if (giveupPath) { guMap = (unsigned char *)malloc((size_t)W * H); guctx.guMap = guMap; }

            { float *irRows = NULL, *ctRows = NULL;
              if (irmode == 3) {   /* CONTROL: fill per-row arrays with the FIXED scalars -> must == baseline */
                  int k; irRows = (float *)malloc((size_t)H * sizeof(float)); ctRows = (float *)malloc((size_t)H * sizeof(float));
                  for (k = 0; k < H; k++) { irRows[k] = irref; ctRows[k] = irCrosstalk; }
                  ice_dbg_irref_rows = irRows; ice_dbg_ct_rows = ctRows;
                  printf("  irtrace mode   : CONTROL (per-row arrays = fixed scalars; must reproduce baseline)\n");
              } else if (irtracePath) {
                  if (load_irtrace(irtracePath, H, irshift, &irRows, &ctRows) != 0) { fprintf(stderr, "ERROR: cannot read -irtrace %s\n", irtracePath); dng_free(img); free(guMap); return 2; }
                  ice_dbg_irref_rows = irRows; ice_dbg_ct_rows = ctRows;
                  if (irmode == 1) { ice_dbg_ct_rows = NULL; printf("  irtrace mode   : IRref-only (crosstalk stays fixed)\n"); }
                  else if (irmode == 2) { ice_dbg_irref_rows = NULL; printf("  irtrace mode   : crosstalk-only (IRref stays fixed)\n"); }
              } }

            {
                DngWriter *dw = dngw_create(outp, img->Le);
                if (!dw) { fprintf(stderr, "ERROR: cannot create %s\n", outp); dng_free(img); free(guMap); return 2; }
                long dataPos = dw->DataPos;
                LoadCtx loadctx; loadctx.img = img; memcpy(loadctx.map, map, sizeof map);
                EmitCtx emitctx; emitctx.dw = dw; emitctx.emitRows = H;
                int rowsOut = ice_pump_run(load_cb, &loadctx, emit_cb, &emitctx, W, H, dpi, irref, irCrosstalk, H,
                                           giveupPath ? giveup_cb : NULL, &guctx);
                printf("\r  rows %d/%d        \n", rowsOut, H);

                write_dng(dw, img, W, rowsOut, W * 6, dataPos);
                dngw_dispose(dw);
                printf("-> %s   (3-ch linear RGB DNG, %d x %d)\n", outp, W, rowsOut);

                if (guMap) {
                    long edited = 0, tot = (long)W * rowsOut, k;
                    for (k = 0; k < tot; k++) if (guMap[k]) edited++;
                    write_pgm(giveupPath, W, rowsOut, guMap);
                    { double pct = 100.0 * edited / tot;
                      printf("-> %s   give-up map (%d x %d), reconstructed %.1f%%  (gave up %.1f%%)\n", giveupPath, W, rowsOut, pct, 100.0 - pct); }
                }
                dng_free(img); free(guMap);
                return rowsOut == H ? 0 : 1;
            }
        }
    }
}
