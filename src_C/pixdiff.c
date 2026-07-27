/* pixdiff.c -- list every differing pixel between ref and openICE outputs, with full context.
 * Build: gcc -m32 -O2 -msse2 -mfpmath=sse -o pixdiff.exe pixdiff.c dng.c -lm
 * Usage: pixdiff <postlut> <ref> <oi> */
#include "dng.h"
#include <stdio.h>
#include <stdlib.h>

int main(int argc, char **argv) {
    if (argc < 4) { printf("usage: pixdiff <postlut> <ref> <oi>\n"); return 2; }
    Dng *in = dng_open(argv[1]), *rf = dng_open(argv[2]), *oi = dng_open(argv[3]);
    if (!in || !rf || !oi) { printf("open err\n"); return 2; }
    int mi[4], mc[4], mo[4]; dng_make_map(in, NULL, mi); dng_make_map(rf, NULL, mc); dng_make_map(oi, NULL, mo);
    int W = in->W, H = in->H; if (rf->W < W) W = rf->W; if (oi->W < W) W = oi->W; if (rf->H < H) H = rf->H; if (oi->H < H) H = oi->H;
    short *ri = malloc((size_t)in->W*4*2), *rc = malloc((size_t)rf->W*4*2), *ro = malloc((size_t)oi->W*4*2);
    const char *ch = "RGB";
    printf("row   col    ch  raw    ref     oi    delta\n");
    int n = 0;
    for (int y = 0; y < H; y++) {
        dng_load_row(in, y, mi, ri); dng_load_row(rf, y, mc, rc); dng_load_row(oi, y, mo, ro);
        for (int x = 0; x < W; x++) for (int c = 0; c < 3; c++) {
            int rv = (unsigned short)rc[x*4+c], ov = (unsigned short)ro[x*4+c];
            if (rv != ov) {
                int raw = (unsigned short)ri[x*4+c];
                printf("%5d %5d  %c  %5d  %6d %6d  %+5d\n", y, x, ch[c], raw, rv, ov, ov-rv);
                n++;
            }
        }
    }
    printf("total %d differing pixels\n", n);
    return 0;
}
