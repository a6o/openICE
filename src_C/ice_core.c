/* ice_core.c -- the reconstruction core + dither/LCG. Ported from IceCore.cs. Accumulates in `real_t`
 * (80-bit long double by default): the x87-faithful model of the original's registers, rounding to float at
 * each store, operation order kept verbatim. */
#include "ice.h"
#include <math.h>

int ice_no_dither = 0;   /* TEST hook; 0 in normal use */
int ice_dbg_col = -1;    /* TEST hook: print reconstruct intermediates for this column */
int ice_dbg_active = 0;  /* set while processing ice_dbg_col */
#include <stdio.h>
static unsigned fbits(float f){ unsigned b; memcpy(&b,&f,4); return b; }
static unsigned long long dbits(double d){ unsigned long long b; memcpy(&b,&d,8); return b; }

/* Round to 32-bit float, defeating x87 excess precision (gcc -fexcess-precision=fast keeps 80-bit across a plain
 * float assignment; the volatile store forces the round). Matches the original storing intermediates as float. */
static float force_f32(float x) { volatile float v = x; return v; }

/* LCG dither noise; advances the LCG once. */
static real_t lcg_noise(IceState *s, float baseVal, float scale) {
    unsigned int next = s->ditherLcg * 0x7du + 1u;
    s->ditherLcg = next - (next & 0xff000000u);
    int v = (int)s->ditherLcg + 1;
    real_t f = (real_t)v;
    if (v < 0) f += (real_t)s->LcgNegFixup;
    return (real_t)baseVal + (f * (real_t)s->LcgScale + (real_t)s->LcgBias) * (real_t)scale;
}

/* per-channel dither amount for a reconstructed density `value`. */
static real_t dither_delta(IceState *s, float value, float amount) {
    double result = s->DitherFloor;   /* the original keeps result/noise in DOUBLE stack slots, not float */
    if (ice_no_dither) return (real_t)result;
    { unsigned short maxIdx = cfg_u16(s, Cfg_MaxDensityIdx);
      unsigned int cap = (unsigned int)maxIdx, idx;
      unsigned short anchorIdx;
      float bandHi, bandLo; double envScale;

      /* The original sets the x87 rounding mode to ROUND-TOWARD-ZERO before converting maxIdx*anchor to the band
       * index -- i.e. it TRUNCATES, it does not round-to-nearest. Truncation makes bandHi's index one lower at
       * the .65 fractional boundary, dropping bandHi by ~1 density unit; getting this wrong flips the dither band
       * membership at knife-edge saturated pixels and desyncs the process-global LCG. (long)(...) truncates
       * toward zero, matching the original. */
      anchorIdx = (unsigned short)(long)((float)maxIdx * s->DitherBandHiAnchor);
      idx = (unsigned int)anchorIdx; if (cap < anchorIdx) idx = cap;
      bandHi = s->densityLut[idx];

      anchorIdx = (unsigned short)(long)((float)maxIdx * s->DitherBandLoAnchor);
      idx = (unsigned int)anchorIdx; if (cap < anchorIdx) idx = cap;
      bandLo = s->densityLut[idx];

      /* envScale = DitherEnvScale / (bandHi-bandLo)^2 -- computed in 80-bit, then STORED TO DOUBLE. */
      { long double diff = (long double)bandHi - (long double)bandLo;
        envScale = (double)((long double)s->DitherEnvScale / (diff * diff)); }
      if ((value < bandHi) && (bandLo < value)) {
          /* The original evaluates, in this exact order & rounding:
           *   envelope = ( (bandHi-value)*(value-bandLo) ) * envScale   -> stored to DOUBLE
           *   noise    = envelope * lcgNoise                            -> stored to DOUBLE
           *   result   = noise (kept DOUBLE, returned as long double). */
          double envelope = (double)((((long double)bandHi - (long double)value) * ((long double)value - (long double)bandLo)) * (long double)envScale);
          /* The original rounds the LCG scale (amount*value) TO FLOAT before the noise multiply.
           * force_f32() breaks gcc's excess precision so the round happens even under -fexcess-precision=fast. */
          real_t lcgn = lcg_noise(s, 0.0f, force_f32(amount * value));
          /* noise stays in the 80-bit register (a DOUBLE copy is stored but the register is kept); the perturbed
           * test uses the 80-bit value, while the stored RESULT is the double copy. Using the double for perturbed
           * flips the in-band test at knife-edge pixels. */
          long double noise80 = (long double)envelope * lcgn;
          double noise = (double)noise80;
          { long double perturbed = (long double)value + noise80;
            if ((perturbed < (long double)bandHi) && ((long double)bandLo < perturbed)) result = noise; }
      }
      return (real_t)result; }
}

/* local dynamic range (min/max) of one gate-pyramid band over the 5-point cross */
static void band_range(IceState *s, int level, float *lo, float *hi, int gateCol) {
    int b = gateCol * 0x10, off = level * 4;
    float center = s->gateCenter[(b + off + 4) >> 2] - s->gateCenter[(b + off) >> 2];
    float right  = s->gateCenter[(b + off + 0x14) >> 2] - s->gateCenter[(b + off + 0x10) >> 2];
    float left   = s->gateCenter[(b + off - 0xc) >> 2] - s->gateCenter[(b + off - 0x10) >> 2];
    float down   = s->gateDown  [(b + off + 4) >> 2] - s->gateDown  [(b + off) >> 2];
    float up     = s->gateUp    [(b + off + 4) >> 2] - s->gateUp    [(b + off) >> 2];
    if (cfg_i(s, Cfg_Dpi) <= cfg_i(s, Cfg_BandLookaheadRows)) { *hi = center; *lo = center; return; }
    { float mx = center; if (center < right) mx = right; if (mx < left) mx = left; if (mx < down) mx = down; if (mx < up) mx = up; *hi = mx; }
    { float mn = center; if (right < mn) mn = right; if (left < mn) mn = left; if (down < mn) mn = down; if (up < mn) mn = up; *lo = mn; }
}

void ice_core_giveup(IceState *s, int outCol) {
    if (cfg_i(s, Cfg_ChannelCount) != 2) { s->outR[outCol] = 0.0f; s->outG[outCol] = 0.0f; s->outB[outCol] = cfg_f(s, Cfg_MaxDensitySum); return; }
    s->outR[outCol] = s->inR[outCol]; s->outG[outCol] = s->inG[outCol]; s->outB[outCol] = s->inB[outCol];
}

/* add one detail band's soft-thresholded contribution to a channel accumulator.
 * Faithful to the original's x87 (MSVC, FLT_EVAL_METHOD=2): the accumulator is DOUBLE; the coefficient PRODUCTS
 * and the accumulate SUM are evaluated in 80-bit (long double) and rounded to float ONLY where the original
 * stores to a float variable (resid), then to double at the store. */
static double add_band_detail(double acc, long double detail, float loCoef, float hiCoef, float bandLo, float bandHi, float conf, float zero) {
    if ((bandHi < zero) && (bandLo < zero)) { float t = hiCoef; hiCoef = loCoef; loCoef = t; }
    { long double resid = (long double)zero;
      if (detail <= (long double)hiCoef * (long double)bandHi) {
          if (detail < (long double)loCoef * (long double)bandLo)
              resid = detail - (long double)loCoef * (long double)bandLo;
      } else {
          resid = detail - (long double)hiCoef * (long double)bandHi;
      }
      /* x87: the accumulator is a DOUBLE stack slot -- load, add in 80-bit, store rounding to double per band.
       * detail/resid/products stay in 80-bit registers. */
      return (double)((long double)acc + (long double)conf * resid); }
}

/* one detail band's difference (hires - lores) * gain, kept in 80-bit (the original never rounds it to float). */
#define DETAIL(hires, lores, gain) (((long double)(hires) - (long double)(lores)) * (long double)(gain))

void ice_core_reconstruct(IceState *s, int outCol, int gateCol, int col) {
    int lp = col * 0xc, mp = col * 0x10;
    /* base = (double)L0. Accumulators are DOUBLE. */
    double baseR = (double)s->L0[lp >> 2], baseG = (double)s->L0[(lp + 4) >> 2], baseB = (double)s->L0[(lp + 8) >> 2];
    if (cfg_i(s, Cfg_ReconIrRefAdjust) != 0) {
        /* irDelta = IRref - gateCenter kept 80-bit (fld DWORD IRref, fsub DWORD gateCenter), reused across R,G,B;
         * base(double) + coef*irDelta rounded to double (fstp QWORD), NOT to float. */
        long double irDelta = (long double)cfg_f(s, Cfg_IRref) - (long double)s->gateCenter[(gateCol * 0x10) >> 2];
        baseR = (double)((long double)baseR + (long double)recon_coef(s, 0, 0) * irDelta);
        baseG = (double)((long double)baseG + (long double)recon_coef(s, 1, 0) * irDelta);
        baseB = (double)((long double)baseB + (long double)recon_coef(s, 2, 0) * irDelta);
    }
    { double accR = baseR, accG = baseG, accB = baseB;   /* DOUBLE accumulators */
      float bandLo, bandHi;

      if (cfg_i(s, Cfg_ReconBand0) != 0) {
          float zero = s->Zero, conf, gain;
          band_range(s, 0, &bandLo, &bandHi, gateCol);
          conf = cfg_f(s, Cfg_MaskOverrideR);
          if (conf == zero) {
              s->mask[(mp + 4) >> 2] = s->RConfGain * s->mask[(mp + 4) >> 2];
              conf = s->mask[(mp + 4) >> 2];
              if (conf <= s->MaskClampHi) { if (conf < zero) { s->mask[(mp + 4) >> 2] = 0.0f; conf = s->mask[(mp + 4) >> 2]; } }
              else { s->mask[(mp + 4) >> 2] = 1.0f; conf = s->mask[(mp + 4) >> 2]; }
          }
          gain = cfg_f(s, Cfg_StageGain0);
          accR = add_band_detail(accR, DETAIL(s->L1[lp >> 2],       s->L0[lp >> 2],       gain), recon_coef(s,0,2), recon_coef(s,0,1), bandLo, bandHi, conf, zero);
          accG = add_band_detail(accG, DETAIL(s->L1[(lp + 4) >> 2], s->L0[(lp + 4) >> 2], gain), recon_coef(s,1,2), recon_coef(s,1,1), bandLo, bandHi, conf, zero);
          accB = add_band_detail(accB, DETAIL(s->L1[(lp + 8) >> 2], s->L0[(lp + 8) >> 2], gain), recon_coef(s,2,2), recon_coef(s,2,1), bandLo, bandHi, conf, zero);
      }

      if (cfg_i(s, Cfg_ReconBand1) != 0) {
          float zero = s->Zero, conf, gain;
          band_range(s, 1, &bandLo, &bandHi, gateCol);
          conf = cfg_f(s, Cfg_MaskOverrideG);
          if (conf == zero) { conf = s->mask[(mp + 8) >> 2]; if (conf < zero) { s->mask[(mp + 8) >> 2] = 0.0f; conf = s->mask[(mp + 8) >> 2]; } }
          gain = cfg_f(s, Cfg_StageGain1);
          accR = add_band_detail(accR, DETAIL(s->L2[lp >> 2],       s->L1[lp >> 2],       gain), recon_coef(s,0,4), recon_coef(s,0,3), bandLo, bandHi, conf, zero);
          accG = add_band_detail(accG, DETAIL(s->L2[(lp + 4) >> 2], s->L1[(lp + 4) >> 2], gain), recon_coef(s,1,4), recon_coef(s,1,3), bandLo, bandHi, conf, zero);
          accB = add_band_detail(accB, DETAIL(s->L2[(lp + 8) >> 2], s->L1[(lp + 8) >> 2], gain), recon_coef(s,2,4), recon_coef(s,2,3), bandLo, bandHi, conf, zero);
      }

      if (cfg_i(s, Cfg_ReconBand2) != 0) {
          float zero = s->Zero, conf, gain;
          band_range(s, 2, &bandLo, &bandHi, gateCol);
          conf = cfg_f(s, Cfg_MaskOverrideB);
          if (conf == zero) { conf = s->mask[(mp + 0xc) >> 2]; conf = conf * conf; }
          gain = cfg_f(s, Cfg_StageGain2);
          accR = add_band_detail(accR, DETAIL(s->L3[lp >> 2],       s->L2[lp >> 2],       gain), recon_coef(s,0,6), recon_coef(s,0,5), bandLo, bandHi, conf, zero);
          accG = add_band_detail(accG, DETAIL(s->L3[(lp + 4) >> 2], s->L2[(lp + 4) >> 2], gain), recon_coef(s,1,6), recon_coef(s,1,5), bandLo, bandHi, conf, zero);
          accB = add_band_detail(accB, DETAIL(s->L3[(lp + 8) >> 2], s->L2[(lp + 8) >> 2], gain), recon_coef(s,2,6), recon_coef(s,2,5), bandLo, bandHi, conf, zero);
      }

      if ((s->Zero < (float)accR) && (s->Zero < (float)accG) && (s->Zero < (float)accB)) {
          if ((cfg_i(s, Cfg_ClampToL3Enable) == 0) && (cfg_i(s, Cfg_ClampToL3Flag) == 0)) {
              s->outR[outCol] = (float)(dither_delta(s, (float)accR, cfg_f(s, Cfg_DitherAmtR)) + accR);
              s->outG[outCol] = (float)(dither_delta(s, (float)accG, cfg_f(s, Cfg_DitherAmtG)) + accG);
              s->outB[outCol] = (float)(dither_delta(s, (float)accB, cfg_f(s, Cfg_DitherAmtB)) + accB);
              return;
          }
          { real_t dith = dither_delta(s, (float)accR, cfg_f(s, Cfg_DitherAmtR));
            real_t outV = (real_t)s->L3[lp >> 2];
            if (outV < dith + accR) outV = dither_delta(s, (float)accR, cfg_f(s, Cfg_DitherAmtR)) + accR;
            s->outR[outCol] = (float)outV;
            dith = dither_delta(s, (float)accG, cfg_f(s, Cfg_DitherAmtG));
            outV = (real_t)s->L3[(lp + 4) >> 2];
            if (outV < dith + accG) outV = dither_delta(s, (float)accG, cfg_f(s, Cfg_DitherAmtG)) + accG;
            s->outG[outCol] = (float)outV;
            dith = dither_delta(s, (float)accB, cfg_f(s, Cfg_DitherAmtB));
            outV = (real_t)s->L3[(lp + 8) >> 2];
            { real_t sumB = dith + accB; int recon = (outV < sumB);
              if (recon) outV = dither_delta(s, (float)accB, cfg_f(s, Cfg_DitherAmtB)) + accB;
              s->outB[outCol] = (float)outV;
              if (col == ice_dbg_col)
                printf("[dbg col %d B] accB=%.10g (f=%08X)  L3B=%.10g  dithB=%.10g  sumB=%.10g  recon=%d  outVB=%.10g  outB=%08X\n",
                       col, (double)accB, fbits((float)accB), (double)s->L3[(lp+8)>>2], (double)dith, (double)sumB, recon, (double)outV, fbits((float)outV)); } }
          return;
      }
      ice_core_giveup(s, outCol); }
}

int ice_acc_mode = 0;
