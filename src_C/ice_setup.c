/* ice_setup.c -- Phase 0: build a fully-populated IceState. Ported from IceSetup.cs. Derivations use `double`,
 * which is bit-exact for setup. */
#include "ice.h"
#include <stdlib.h>
#include <math.h>

/* ICE quality: 0 = Normal (default), 1 = Fine. Set by openice.c's -fine. (See docs/parameters.md.) */
int ice_fine = 0;

/* Reconstruction target ("kind"): which scanner reconstruction profile to use. 8 = LS-5000 (default,
 * byte-exact); 7 and 9 are the two other targets. Set by openice.c's -kind. Only the ReconCoefBase matrix and
 * BandLookaheadRows differ between kinds; BandLookaheadRows is output-inert at the LS-5000's 4000/285 dpi
 * (dpi-vs-threshold resolves the same for all). */
int ice_kind = 8;

/* [kind index 0=7,1=8,2=9][channel R,G,B][IR-slope k + 3 bands x {loCoef,hiCoef}] -- exact 32-bit float patterns */
static const unsigned int recon_coef_by_kind[3][3][7] = {
  { /* kind 7 */
    {0x3F8CCCCDu,0x3FAE147Bu,0x3FA8F5C3u,0x3FAF5C29u,0x3FA66666u,0x3FAB851Fu,0x3FA00000u},
    {0x3F8CCCCDu,0x3FAF5C29u,0x3FA66666u,0x3FACCCCDu,0x3FA51EB8u,0x3FA66666u,0x3F9EB852u},
    {0x3F8CCCCDu,0x3FAB851Fu,0x3FA00000u,0x3FA8F5C3u,0x3FA00000u,0x3FA00000u,0x3F9AE148u} },
  { /* kind 8 (LS-5000, default) */
    {0x3F8CCCCDu,0x3F9AE148u,0x3F8B851Fu,0x3F95C28Fu,0x3F8A3D71u,0x3F851EB8u,0x3F75C28Fu},
    {0x3F8CCCCDu,0x3F9D70A4u,0x3F90A3D7u,0x3F91EB85u,0x3F866666u,0x3F6E147Bu,0x3F570A3Du},
    {0x3F8CCCCDu,0x3F90A3D7u,0x3F851EB8u,0x3F8A3D71u,0x3F828F5Cu,0x3F7851ECu,0x3F63D70Au} },
  { /* kind 9 */
    {0x3F800000u,0x400D70A4u,0x4005C28Fu,0x400AE148u,0x40051EB8u,0x40028F5Cu,0x3FFAE148u},
    {0x3F800000u,0x400EB852u,0x400851ECu,0x4008F5C3u,0x40033333u,0x3FF70A3Du,0x3FEB851Fu},
    {0x3F800000u,0x400851ECu,0x40028F5Cu,0x40051EB8u,0x400147AEu,0x3FFC28F6u,0x3FF1EB85u} }
};
static const int band_lookahead_by_kind[3] = { 950, 1600, 2500 };   /* kinds 7, 8, 9 */

/* map ice_kind {7,8,9} -> table index {0,1,2}; anything else -> 1 (kind 8 default) */
static int kind_index(void) { int i = ice_kind - 7; return (i < 0 || i > 2) ? 1 : i; }

static int    idx_of(float anchor, int maxIdx) { int i = (int)((double)anchor * (double)maxIdx); return i > maxIdx ? maxIdx : i; }
static float  lut_sub(const IceState *s, float anchor, int maxIdx) { return (float)((double)s->densityLut[idx_of(anchor, maxIdx)] - (double)s->densityLut[maxIdx]); }
static float  lut_at (const IceState *s, float anchor, int maxIdx) { return s->densityLut[idx_of(anchor, maxIdx)]; }
static float  bias_param(const IceState *s, float fv1, int maxIdx, double a0, double a4) {
    const double a9c = 2.0;
    if (fv1 <= a0) return (a4 < fv1) ? lut_sub(s, fv1, maxIdx) : 0.0f;
    if (a9c <= fv1) return (a4 < fv1) ? lut_sub(s, fv1, maxIdx) : 0.0f;
    { double g = a9c - (double)fv1;
      return (float)((double)s->densityLut[maxIdx] - (double)s->densityLut[idx_of((float)g, maxIdx)]); }
}

IceState *ice_setup_create(int W, int H, int dpi) {
    IceState *s = (IceState *)calloc(1, sizeof(IceState));
    (void)H;

    /* geometry / config */
    cfg_wi(s, Cfg_Dpi, dpi);
    cfg_wi(s, 0x010, 2);
    cfg_wi(s, Cfg_TriggerEdgeMode, 0);
    cfg_wi(s, Cfg_ReconIrRefAdjust, 1); cfg_wi(s, Cfg_ReconBand0, 1); cfg_wi(s, Cfg_ReconBand1, 1); cfg_wi(s, Cfg_ReconBand2, 1);
    /* Cfg_TargetId (0x3B4) is a MISNOMER: it is NOT the reconstruction kind. It reads 8 for ALL three kinds
     * 7/8/9 -- it is the give-up sample cap read as `maxBelow` in ice_back_trigger. Writing ice_kind here made
     * -kind 7 give up at >7 and -kind 9 at >9, flipping the give-up decision on a large fraction of pixels.
     * It is kind-invariant: always 8. */
    cfg_wi(s, Cfg_ChannelCount, 2); cfg_wi(s, Cfg_BandCount, 0); cfg_wi(s, Cfg_TargetId, 8);
    cfg_wi(s, 0xEC8, 4);
    cfg_wi(s, 0xECC, W + 8);
    cfg_wi(s, 0xED0, W + 4);
    cfg_wi(s, Cfg_ImageWidth, W);
    cfg_wi(s, Cfg_RowCounter, 0);
    cfg_wi(s, Cfg_ClampToL3Enable, 1); cfg_wi(s, Cfg_DrainFlag, 0); cfg_wi(s, Cfg_ClampToL3Flag, 0);

    cfg_wi(s, Cfg_WarmupRows, 7); cfg_wi(s, 0xE1C, 6); cfg_wi(s, 0xE28, 2); cfg_wi(s, 0xE3C, 3); cfg_wi(s, 0xE40, 1);
    cfg_wi(s, 0xE2C, 0); cfg_wi(s, 0xE30, 1); cfg_wi(s, 0xE34, 2);
    cfg_wu16(s, Cfg_MaxDensityIdx, 65535); cfg_wu16(s, 0xD72, 0);
    cfg_wi(s, Cfg_BandLookaheadRows, band_lookahead_by_kind[kind_index()]); cfg_wi(s, Cfg_WeightMinRows, 550); cfg_wi(s, 0x458, 1);

    /* density LUT: LUT[i] = 65535*log2(i+1)/16 */
    s->densityLut = (float *)malloc(65536 * sizeof(float));
    { double k = 65535.0 / (16.0 * log(2.0)); int i;
      for (i = 0; i <= 65535; i++) s->densityLut[i] = (float)(k * log((double)i + 1.0)); }

    /* true constants */
    cfg_wbits(s, 0x3BC, 0x00000000u);
    cfg_wbits(s, Cfg_WeightFloor, 0x3CA3D70Au);   /* 0.02 */
    cfg_wbits(s, Cfg_IRref, 0x00000000u);
    cfg_wbits(s, Cfg_MaskOverrideR, 0); cfg_wbits(s, Cfg_MaskOverrideG, 0); cfg_wbits(s, Cfg_MaskOverrideB, 0);
    /* Trigger probe offsets are 4 for every kind at 0xF70..0xF76. (The core carries a second copy at
     * 0x4A0/0x4A4 that reads 3 for kind 7, but mirroring it here measurably HURTS kind 7 -- not this. ) */
    cfg_wu16(s, Cfg_TriggerOffsetL, 4); cfg_wu16(s, Cfg_TriggerOffsetR, 4); cfg_wu16(s, 0xF74, 4); cfg_wu16(s, 0xF76, 4);

    /* per-channel dither amounts (0.015/0.015/0.025) -- LS-5000 target-8, scan-invariant. */
    cfg_wbits(s, Cfg_DitherAmtR, 0x3C75C28Fu);   /* 0.015 */
    cfg_wbits(s, Cfg_DitherAmtG, 0x3C75C28Fu);   /* 0.015 */
    cfg_wbits(s, Cfg_DitherAmtB, 0x3CCCCCCDu);   /* 0.025 */

    cfg_wbits(s, Cfg_StageGain2, 0x3FA00000u);   /* 1.25 */
    cfg_wbits(s, Cfg_StageGain1, 0x3FA00000u);   /* 1.25 */
    cfg_wbits(s, Cfg_StageGain0, 0x3FA00000u);   /* 1.25 (Normal uses 1.25, not 1.0) */
    cfg_wi(s, Cfg_ClampToL3Enable, 1);

    /* ICE Fine (`-fine`). Between Normal and Fine, the only reconstruction fields that change are the three
     * stage gains (1.25 -> 1.0) and the L3 clamp (on -> off). Setting just these reproduces Fine output
     * byte-exactly (70,542,642/70,542,642, 0 diff). (Fine also zeroes an unread 0xF50 word and a 192-float
     * table at 0x94; neither affects the reconstruction output.) */
    if (ice_fine || getenv("ICE_FINE")) {
        cfg_wbits(s, Cfg_StageGain2, 0x3F800000u); cfg_wbits(s, Cfg_StageGain1, 0x3F800000u); cfg_wbits(s, Cfg_StageGain0, 0x3F800000u);
        cfg_wi(s, Cfg_ClampToL3Enable, 0);
    }

    /* per-channel 7-float reconstruction coefficient block (IR-slope k + 3 bands x {loCoef,hiCoef}), selected
     * by the reconstruction target (ice_kind, default 8 = LS-5000), at config 0xED8..0xF28. */
    { const unsigned int (*pc)[7] = recon_coef_by_kind[kind_index()]; int c, i;
      for (c = 0; c < 3; c++) for (i = 0; i < 7; i++) cfg_wbits(s, Cfg_ReconCoefBase + c * 0x1C + i * 4, pc[c][i]); }

    /* per-scan parameters */
    { int chans = 2, maxIdx = 65535; double statA = 1.0, statB = 0.0;
      float f30 = lut_sub(s, 0.85f, maxIdx);
      /* The weight-ramp anchor is per-kind: config 0xF30 is c4701ac0 (-960.418) for kinds 7/8 but c4702180
       * (-960.523) for kind 9. WeightSlope = 1/f30 follows (k8 ba887952, k9 ba88757c), so overriding f30 here
       * reproduces the whole ramp. */
      if (ice_kind == 9) f30 = float_from_bits(0xc4702180u);
      float f34 = lut_sub(s, 1.0f, maxIdx);
      float f40 = bias_param(s, 0.98f, maxIdx, statA, statB);
      float f4c = lut_at(s, 0.065f, maxIdx);
      cfg_wbits(s, 0xF30, bits_of_float(f30));
      cfg_wbits(s, 0xF34, bits_of_float(f34));
      { double fv5 = (double)f30 - (double)f34;
        if (fv5 == statB) { cfg_wf(s, Cfg_WeightSlope, -(float)maxIdx); cfg_wf(s, Cfg_WeightRamp, (float)statA); }
        else { double slope = statA / fv5; cfg_wf(s, Cfg_WeightSlope, (float)slope); cfg_wf(s, Cfg_WeightRamp, (float)(slope * (double)f30)); } }
      cfg_wf(s, Cfg_WeightBias, f40);
      cfg_wf(s, Cfg_MaxDensitySum, (float)((float)chans * (float)maxIdx));
      cfg_wf(s, 0xF48, 0.0f);
      cfg_wf(s, Cfg_DustFloor, f4c); }

    /* fixed core constants */
    s->Zero               = 0.0f;
    s->RConfGain          = 2.0f;
    s->MaskClampHi        = 1.0f;
    s->DitherFloor        = 0.0f;
    s->DitherEnvScale     = 4.0f;
    /* FIX 5 -- the dither BAND is per-kind. The original carries separate dither constant blocks; the two band
     * anchors differ by kind:
     *     kind 7:      0.04 / 0.96   (the NARROW band)
     *     kinds 8/9:   0.01 / 0.99
     * Floor (0.0) and envScale (4.0) are identical for all. Band membership gates the process-global dither LCG,
     * so this is invisible dither-free (envelope -> 0 at the edges) yet desyncs the whole frame once dither is on. */
    if (ice_kind == 7) {
        s->DitherBandLoAnchor = float_from_bits(0x3D23D70Au);   /* 0.04 */
        s->DitherBandHiAnchor = float_from_bits(0x3F75C28Fu);   /* 0.96 */
    } else {
        s->DitherBandLoAnchor = float_from_bits(0x3C23D70Au);   /* 0.01 */
        s->DitherBandHiAnchor = float_from_bits(0x3F7D70A4u);   /* 0.99 */
    }
    s->LcgScale           = float_from_bits(0x337FFFFFu);   /* 2^-24 */
    s->LcgBias            = -0.5f;
    s->LcgNegFixup        = 0.0f;
    return s;
}

void ice_state_free(IceState *s) { if (s) { free(s->densityLut); free(s); } }
