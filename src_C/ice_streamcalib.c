/* ice_streamcalib.c -- the STREAMING per-row calibration estimator, ported bit-exact.
 * Chain: density-LUT each channel -> per-pixel accumulate -> per-8-row-block finalize (rref/irRefRaw window +
 * crosstalk) -> crosstalk finalize.
 * Verified 5959/5959 = 100% bit-exact for {ct,rref,irRefRaw} vs the original engine.
 *
 * Precision model (x87, 80-bit long double where the original keeps values in registers):
 *  - xx/xy/xr accumulate in DOUBLE (80-bit fmul+fadd, store double); NOT float.
 *  - rref/irRefRaw = xx/xr, xy/xr : CUMULATIVE sum, 80-bit divide -> float.
 *  - crosstalk: per-8x8-tile quad_dev regression, per-quad 80-bit (term never rounded to float), round to double
 *    per tile; cumulative denom/numer, ct divide in 80-bit (prevFloat+cur), store float; seed 0.15.
 */
#include "ice.h"
#include <stdlib.h>
#include <string.h>

#define IR_GATE 8847.226f          /* 0x460A3CE7 : raw-IR clear-film gate (integer threshold rawIR>=8848) */
#define CC648   0.0625f            /* quad-sum scale 1/16 */
#define CC658   0.25f              /* mean scale */
#define CT_SC   0.707106769f       /* 0x9cc608 crosstalk val scale */
#define RG_LO   (-0.125f)          /* 0x9cc614 */
#define RG_MID  ( 0.3f)            /* 0x9cc60c */
#define RG_HI   ( 0.425f)          /* 0x9cc610 */

static const int QIDX[4][16] = {
    {0x1a,0x1b,0x18,0x19,0x12,0x13,0x10,0x11,0xa,0xb,0,1,2,3,8,9},
    {0x1e,0x1f,0x1c,0x1d,0x16,0x17,0x14,0x15,0xe,0xf,4,5,6,7,0xc,0xd},
    {0x3a,0x3b,0x38,0x39,0x32,0x33,0x30,0x31,0x2a,0x2b,0x20,0x21,0x22,0x23,0x28,0x29},
    {0x3e,0x3f,0x3c,0x3d,0x36,0x37,0x34,0x35,0x2e,0x2f,0x24,0x25,0x26,0x27,0x2c,0x2d},
};

struct IceStreamCalib {
    int W, nBlocks;
    const float *lut;
    double winXX, winXY, winXR;             /* cumulative regression sums */
    unsigned char *blkClear;                /* per-8px-block all-clear flag (persists across the 8-row tile) */
    float *tileR, *tileIR, *tileW;          /* 8x8 tile density buffers + per-tile IR-sum weight */
    float ctVal;                            /* grid.ct (seed 0.15 until crosstalk fires) */
    float denomSum, numerSum;               /* crosstalk running sums (float-stored) */
};

IceStreamCalib *ice_streamcalib_create(int W, const float *lut) {
    IceStreamCalib *sc = (IceStreamCalib *)calloc(1, sizeof(IceStreamCalib));
    sc->W = W; sc->nBlocks = W / 8; sc->lut = lut;
    sc->blkClear = (unsigned char *)malloc(sc->nBlocks);
    sc->tileR  = (float *)malloc((size_t)sc->nBlocks * 64 * sizeof(float));
    sc->tileIR = (float *)malloc((size_t)sc->nBlocks * 64 * sizeof(float));
    sc->tileW  = (float *)malloc((size_t)sc->nBlocks * sizeof(float));
    sc->ctVal = 0.15f;                      /* setup default d34 */
    return sc;
}

void ice_streamcalib_sums(const IceStreamCalib *sc, double *xx, double *xy, double *xr) {
    *xx = sc->winXX; *xy = sc->winXY; *xr = sc->winXR;
}

void ice_streamcalib_free(IceStreamCalib *sc) {
    if (!sc) return;
    free(sc->blkClear); free(sc->tileR); free(sc->tileIR); free(sc->tileW); free(sc);
}

/* quad_dev: dev[i]=q[i]-mean, q[i]=(Sum16 in QIDX order)*0.0625, mean=(Sum q)*0.25. 80-bit sums, float stores. */
static void quad_dev(const float *tile, float *dev) {
    float q[4];
    for (int i = 0; i < 4; i++) {
        long double s = 0;
        for (int j = 0; j < 16; j++) s += (long double)tile[QIDX[i][j]];
        q[i] = (float)(s * (long double)CC648);
    }
    float mean = (float)(((long double)q[0] + q[1] + q[2] + q[3]) * (long double)CC658);
    for (int i = 0; i < 4; i++) dev[i] = q[i] - mean;
}

void ice_streamcalib_row(IceStreamCalib *sc, int y, const short *rgbi, float *ctOut, float *rrefOut, float *irRefRawOut) {
    int nBlocks = sc->nBlocks; const float *lut = sc->lut;
    int rit = y % 8;                        /* row within the 8-row tile */
    if (rit == 0) { memset(sc->blkClear, 1, nBlocks); memset(sc->tileW, 0, (size_t)nBlocks * sizeof(float)); }

    double xx = 0, xy = 0, xr = 0;          /* per-row DOUBLE accumulators */
    for (int b = 0; b < nBlocks; b++) {
        if (!sc->blkClear[b]) continue;
        int clear = 1;
        for (int j = 0; j < 8; j++) {
            if (!clear) break;
            int x = b * 8 + j;
            int rawR  = (unsigned short)rgbi[x * 4 + 0];
            int rawIR = (unsigned short)rgbi[x * 4 + 3];
            float dR = lut[rawR], dIR = lut[rawIR];
            sc->tileR[b * 64 + rit * 8 + j] = dR; sc->tileIR[b * 64 + rit * 8 + j] = dIR;
            if (IR_GATE < (float)rawIR) {
                long double irSq = (long double)((unsigned)rawIR * (unsigned)rawIR);
                xx = (double)((long double)dR  * irSq + (long double)xx);
                xy = (double)((long double)dIR * irSq + (long double)xy);
                xr = (double)(irSq + (long double)xr);
                sc->tileW[b] = (float)((long double)sc->tileW[b] + (long double)rawIR);
            } else { clear = 0; sc->blkClear[b] = 0; }
        }
    }
    /* cumulative window sum -> rref/irRefRaw (80-bit divide) */
    sc->winXX += xx; sc->winXY += xy; sc->winXR += xr;
    float rref = 0, irr = 0;
    if (sc->winXR != 0.0) {
        long double fac = (long double)1.0 / (long double)sc->winXR;
        rref = (float)((long double)sc->winXX * fac);
        irr  = (float)((long double)sc->winXY * fac);
    }

    /* crosstalk every 8 rows over all-clear tiles */
    if (rit == 7) {
        double denomD = 0, numerD = 0;
        for (int b = 0; b < nBlocks; b++) {
            if (!sc->blkClear[b]) continue;
            float devR[4], devIR[4];
            quad_dev(&sc->tileR[b * 64], devR); quad_dev(&sc->tileIR[b * 64], devIR);
            long double w = (long double)sc->tileW[b];
            long double dT = (long double)denomD, nT = (long double)numerD;
            for (int q = 0; q < 4; q++) {
                long double a = (long double)devR[q];
                if (a == 0.0L) continue;
                long double r = (long double)devIR[q] / a;
                long double val;
                if (r < (long double)RG_LO || r > (long double)RG_HI) val = 0;
                else if (r < 0.0L)                 val = a * (long double)CT_SC;
                else if (r <= (long double)RG_MID) val = a;
                else                               val = a * (long double)CT_SC;
                long double term = val * val * w * w;
                dT = dT + term; nT = nT + r * term;
            }
            denomD = (double)dT; numerD = (double)nT;
        }
        float denom = (float)denomD, numer = (float)numerD;
        long double dS = (long double)sc->denomSum + (long double)denom;
        long double nS = (long double)sc->numerSum + (long double)numer;
        if (dS != 0.0L) sc->ctVal = (float)(nS / dS);
        sc->denomSum = (float)dS; sc->numerSum = (float)nS;
    }

    *ctOut = sc->ctVal; *rrefOut = rref; *irRefRawOut = irr;
}
