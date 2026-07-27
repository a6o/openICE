/* ice_analyze.c -- the low-res calibration pass. Ported from IceAnalyze.cs. Sums in `double`, rounded
 * to `float` where the original stores a float -- bit-exact with the original engine. */
#include "ice.h"
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#define DENS_FIXUP 4294967296.0f
#define CT_GATE_LO (-0.2f)
#define CT_GATE_HI ( 0.2f)

double ice_dbg_xx, ice_dbg_xy, ice_dbg_xr;   /* last ice_analyze_run's raw regression sums (diagnostics) */

static const int QIDX[4][16] = {
    {0x1a,0x1b,0x18,0x19,0x12,0x13,0x10,0x11,0xa,0xb,0,1,2,3,8,9},
    {0x1e,0x1f,0x1c,0x1d,0x16,0x17,0x14,0x15,0xe,0xf,4,5,6,7,0xc,0xd},
    {0x3a,0x3b,0x38,0x39,0x32,0x33,0x30,0x31,0x2a,0x2b,0x20,0x21,0x22,0x23,0x28,0x29},
    {0x3e,0x3f,0x3c,0x3d,0x36,0x37,0x34,0x35,0x2e,0x2f,0x24,0x25,0x26,0x27,0x2c,0x2d},
};

static float dens_at(const float *lut, int maxIdx, int x) { return lut[x > maxIdx ? maxIdx : x]; }

static void quad_dev(const float *plane, float *dev) {
    float q[4]; int i, j;
    for (i = 0; i < 4; i++) { double s = 0.0; for (j = 0; j < 16; j++) s += plane[QIDX[i][j]]; q[i] = (float)(s * 0.0625); }
    { float mean = (float)(((double)q[0] + q[1] + q[2] + q[3]) * 0.25);
      for (i = 0; i < 4; i++) dev[i] = q[i] - mean; }
}

IceCalib ice_analyze_run(const unsigned short *img, int W, int H, const float *lut, int maxIdx, float irGate) {
    /* Tiles: floor(W/8) full 8-wide columns x ceil(H/8) rows, natural positions, bottom-edge row clamp. Was
     * (W-8)/8, one column short for the LS-9000 (1494/8=186 not 185), which cost the analysis ~0.9% of the
     * regression sum. */
    int ncol = W / 8, nrow = (H + 7) / 8, ri, ci, r, c, q;
    double xx = 0.0, xy = 0.0, xr = 0.0;
    float ctDenom = 0.0f, ctNumer = 0.0f;
    float densR[64], densIR[64], devR[4], devIR[4];
    IceCalib o;

    for (ri = 0; ri < nrow; ri++) {
        int by = ri * 8;
        for (ci = 0; ci < ncol; ci++) {
            int bx = ci * 8; float sumIR = 0.0f; int allGated = 1;
            for (r = 0; r < 8; r++) for (c = 0; c < 8; c++) {
                int sy = by + r; if (sy > H - 1) sy = H - 1;
                { int idx = (sy * W + bx + c) * 4;
                  int R = img[idx], IR = img[idx + 3];
                  float dR = dens_at(lut, maxIdx, R), dIR = dens_at(lut, maxIdx, IR);
                  densR[r * 8 + c] = dR; densIR[r * 8 + c] = dIR;
                  sumIR = sumIR + (float)IR;
                  if (irGate < (float)IR) {
                      int irSqInt = (int)((unsigned)IR * (unsigned)IR);
                      float irSq = (float)irSqInt; if (irSqInt < 0) irSq += DENS_FIXUP;
                      xx = irSq * (double)dR  + xx;
                      xy = irSq * (double)dIR + xy;
                      xr = (double)irSq       + xr;
                  } else allGated = 0; }
            }
            if (!allGated) continue;
            quad_dev(densR, devR); quad_dev(densIR, devIR);
            { double accDenom = ctDenom, accNumer = ctNumer, tileWeight = sumIR;
              for (q = 0; q < 4; q++) {
                  double a = devR[q], b = devIR[q], val, ratio, term;
                  if (a != 0.0) { ratio = b / a; val = (ratio >= CT_GATE_LO && ratio <= CT_GATE_HI) ? a : 0.0; }
                  else          { ratio = 0.0; val = 0.0; }
                  term = ((val * val) * tileWeight) * tileWeight;
                  accDenom = accDenom + term;
                  accNumer = accNumer + ratio * term;
              }
              ctDenom = (float)accDenom; ctNumer = (float)accNumer; }
        }
    }
    o.crosstalk = (float)((double)ctNumer / (double)ctDenom);
    o.coeff2    = 1.5f * o.crosstalk;
    o.rref      = (float)(xx / xr);
    o.irRefRaw  = (float)(xy / xr);
    ice_dbg_xx = xx; ice_dbg_xy = xy; ice_dbg_xr = xr;
    return o;
}

void ice_analyze_install(IceCalib c, float *irCrosstalk, float *irref) {
    *irCrosstalk = c.crosstalk;
    /* Install formula, x87: the division result g=1/(1-ct) is KEPT in the x87 register (80-bit) and multiplied
     * through -- NOT rounded to float first. Everything is 80-bit, rounded to float only at the final IRref
     * store. Rounding g to float gives 1 ULP low (.2227); doing it all in float gives 1 ULP high (.2305); the
     * correct value is the middle float (.2266 = 0x4777FB3A). A 1-ULP-wrong IRref feeds refLevel=IRref+bias into
     * every weight, perturbing weight/mask/products/gate-pyramid wherever the film is not clear -- this single
     * ULP was the entire front-half staging divergence. */
    { long double g = 1.0L / (1.0L - (long double)c.crosstalk);
      long double v = ((long double)(-c.rref) * (long double)c.crosstalk + (long double)c.irRefRaw) * g;
      /* KIND 7 installs IRref exactly one gate-bias unit LOWER. Measured, not guessed: sweeping irRefRaw on two
       * independent frames (cfgnormal_f01 and frame3_f01, whose calibrations differ) both peak at the same
       * shift, and that shift equals -(1-ct) in each case (-0.966004 / -0.964613). Since
       * IRref = (irRefRaw - ct*Rref)/(1-ct), a -(1-ct) shift in irRefRaw is exactly -1.0 in IRref -- i.e. the
       * same "-1" the IR gate applies (g = (d_IR - ct*d_R)/(1-ct) - 1, GateBias = 1.0). Kind 7 folds that bias
       * into the reference; kinds 8/9 do not. Dither-free this takes kind 7 from 67% to ~99.5% on both frames. */
      /* KIND 7 installs IRref exactly one gate-bias unit (1.0) lower (swept on two frames; the optimum equals
       * -(1-ct) on irRefRaw, i.e. exactly -1.0 on IRref).
       * Applied GLOBALLY here, i.e. to the front half (ingest/weight/maskpyr) as well as the reconstruct-time
       * cfg 0xF2C. Restricting it to reconstruct only was TESTED and is measurably worse
       * (dither-free 88.24% / 92.53% vs 99.91% / 99.93%), so the whole pipeline sees the biased reference. */
      *irref = (float)v; }
    /* (Kind 7 needs NO bias here. An earlier -1.0 on IRref reached 99.91% only because it CANCELS the gate
     * bias inside the weight -- refLevel-g = (irref-1+b)-(g-1). The real difference is the gate itself: kind 7
     * uses GateBias 0, so its gate runs exactly 1.0 higher. See ice_pump.c. The kind-7 gate history is
     * uniformly +1.0 higher on 8 of 9 rows.) */
}
