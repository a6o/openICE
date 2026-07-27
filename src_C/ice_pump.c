/* ice_pump.c -- the per-row streaming driver. Ported from IcePump.cs. */
#include "ice.h"
#include <stdlib.h>
#include <stdio.h>
#include <string.h>
#include <math.h>

/* density->16-bit output converter -- the EXACT segmented fixed-point inverse of the density LUT.
 *   n   = trunc(density + 0.5) clamped to [0,65535]                     (round-half-up)
 *   out = ((int64)t1[(n>>8)&0xff] * (uint)t2[n&0xff] >> 20) - 1   (u16)  (flag=1 => 64-bit product)
 * with t1[hi]=round(16*2^(hi*256/k2)), t2[lo]=round(65536*2^(lo/k2)), k2=65535/16=4095.9375.
 * const_A=0.5, maxval=65535, flag=1. The analytic tables here are verified byte-identical to the original's
 * inverse tables (256/256 both, 65536/65536 end-to-end). This replaces an earlier strict-lower-bracket
 * approximation (which lost +-1 near brackets). */
static void build_conv_tables(int *t1, int *t2) {
    double k2 = 65535.0 / 16.0; int i;
    for (i = 0; i < 256; i++) {
        t2[i] = (int)(65536.0 * pow(2.0, (double)i / k2) + 0.5);
        t1[i] = (int)(16.0    * pow(2.0, (double)(i * 256) / k2) + 0.5);
    }
}
static int inv16(float d, const int *t1, const int *t2) {
    int n = (int)((double)d + 0.5);   /* add const_A=0.5 as a double, then truncate toward zero */
    if (n < 0) n = 0; else if (n > 65535) n = 65535;
    { long long prod = (long long)t1[(n >> 8) & 0xff] * (unsigned)t2[n & 0xff];
      return (int)((prod >> 20) - 1) & 0xffff; }
}

/* DIAGNOSTIC (de-risk the per-row updater): optional per-INPUT-row overrides for IRref (config 0xf2c) and
 * crosstalk (config 0x34). When set by `openice -irtrace <csv>`, the pump feeds supplied per-row calibration
 * instead of the fixed low-res scalars -- to measure whether the per-row updater is the whole remaining
 * end-to-end gap. NULL (default) = normal fixed-scalar behavior. */
const float *ice_dbg_irref_rows = 0;
const float *ice_dbg_ct_rows = 0;

extern int ice_kind;   /* reconstruction target (ice_setup.c); kind 7 biases the reconstruct-time IRref by -1.0 */

int ice_pump_run(load_row_fn load, void *load_ctx, emit_row_fn emit, void *emit_ctx,
                 int W, int H, int dpi, float irref, float irCrosstalk, int emitRows,
                 giveup_sink_fn giveup, void *giveup_ctx) {
    IceState *s = ice_setup_create(W, H, dpi);
    { extern int ice_no_dither; if (getenv("ICE_NODITHER")) ice_no_dither = 1; }   /* TEST: kill LCG dither */
    /* ICE_ZERODITHER: zero the dither AMOUNTS (0xf58/f5c/f60). This matches a dither-free reference (amount=0 ->
     * noise=0, LCG still advances) -- unlike ICE_NODITHER which returns DitherFloor. Use to validate front+back
     * halves bit-exact without the LCG-phase confound. */
    if (getenv("ICE_ZERODITHER")) { cfg_wf(s, Cfg_DitherAmtR, 0.0f); cfg_wf(s, Cfg_DitherAmtG, 0.0f); cfg_wf(s, Cfg_DitherAmtB, 0.0f); }
    int ecc = W + 8, pad = 4, width = W;
    int step, outRow = 0, rowsProcessed = 0, warmup, x, k;
    IceBuffers *buf;
    int convT1[256], convT2[256];
    short *rgbi; float *convR, *convG, *convB, *convIR;
    float *w9[9]; float *prevProds[8];
    float *dstR, *dstG, *dstB; unsigned char *outb;
    float crosstalkScale, one, gateBias, weightBias, weightSlope, weightRamp, weightFloor, weightCap, centerScale, scale5, confFloor, scale9;
    int use3TapMin;

    cfg_wbits(s, Cfg_CrosstalkScale, 0x3F800000u);   /* 1.0 */
    /* IR-gate bias: g = (d_IR - ct*d_R)/(1-ct) - GateBias. Kinds 8/9 use 1.0; KIND 7 uses 0, so its gate runs
     * exactly 1.0 higher (the kind-7 gate history is uniformly +1.0 higher on 8 of 9 rows).
     * This matters ONLY through absolute-gate comparisons -- the give-up trigger tests gate < DustFloor. In the
     * weight it cancels (refLevel - g), which is why biasing IRref by -1.0 instead also scored 99.91%; that was
     * compensation, not the mechanism. */
    cfg_wbits(s, Cfg_GateBias, (ice_kind == 7) ? 0x00000000u : 0x3F800000u);
    s->IrGiveUpThresh   = 1.0f;
    s->LBuildL0Scale    = float_from_bits(0x3C6D7304u);
    s->LBuildL1Scale    = float_from_bits(0x3D430C31u);
    s->LBuildL2Scale    = 0.0625f;
    s->LBuildNormNum    = 1.0f;
    s->LBuildGateThresh = 0.0f;
    s->TriggerEdgeThresh = 1.0f;
    s->ditherLcg = 0x3045u;

    crosstalkScale = cfg_f(s, Cfg_CrosstalkScale); one = 1.0f; gateBias = cfg_f(s, Cfg_GateBias);
    weightBias = cfg_f(s, Cfg_WeightBias); weightSlope = cfg_f(s, Cfg_WeightSlope); weightRamp = cfg_f(s, Cfg_WeightRamp); weightFloor = cfg_f(s, Cfg_WeightFloor);
    weightCap = 1.0f; centerScale = 0.0625f; scale5 = float_from_bits(0x3D430C31u); confFloor = 0.0f; scale9 = float_from_bits(0x3C6D7304u);
    use3TapMin = cfg_i(s, Cfg_WeightMinRows) < cfg_i(s, Cfg_Dpi);

    buf = ice_buffers_create(W);
    s->productStage = buf->pyrStage;
    s->L0 = (float *)calloc(ecc * 3, sizeof(float)); s->L1 = (float *)calloc(ecc * 3, sizeof(float));
    s->L2 = (float *)calloc(ecc * 3, sizeof(float)); s->L3 = (float *)calloc(ecc * 3, sizeof(float));
    s->vLadder0 = (float *)calloc(ecc * 3, sizeof(float)); s->vLadder1 = (float *)calloc(ecc * 3, sizeof(float));
    s->vLadder2 = (float *)calloc(ecc * 3, sizeof(float)); s->vLadder3 = (float *)calloc(ecc * 3, sizeof(float));
    if (giveup) s->giveUpRecord = (unsigned char *)calloc(ecc, 1);

    build_conv_tables(convT1, convT2);
    rgbi = (short *)malloc((size_t)W * 4 * sizeof(short));
    convR = (float *)malloc(W * sizeof(float)); convG = (float *)malloc(W * sizeof(float));
    convB = (float *)malloc(W * sizeof(float)); convIR = (float *)malloc(W * sizeof(float));
    dstR = (float *)malloc(ecc * sizeof(float)); dstG = (float *)malloc(ecc * sizeof(float)); dstB = (float *)malloc(ecc * sizeof(float));
    outb = (unsigned char *)malloc((size_t)W * 6);

    warmup = cfg_i(s, Cfg_WarmupRows);

    /* TEST split (ICE_WEIGHT_IRREF_FIXED): does the ingest-time weight/maskpyr use a FIXED irref while only the
     * reconstruct-time irDelta (cfg[0xf2c]) uses the per-row value? Probes whether per-row IRref belongs only in
     * reconstruct, not the give-up boundary. */
    int weightIrrefFixed = getenv("ICE_WEIGHT_IRREF_FIXED") ? 1 : 0;

    /* ICE_STREAMCALIB: run the bit-exact per-row estimator IN-PLACE instead of the fixed low-res calibration.
     * Computes grid[1].{ct,rref,irRefRaw} per input row, then the install formula -> per-row (crosstalk, IRref). */
    IceStreamCalib *sc = getenv("ICE_STREAMCALIB") ? ice_streamcalib_create(W, s->densityLut) : 0;

    for (step = 0; outRow < emitRows; step++) {
        int inRow = step < H ? step : H - 1;
        float irCrosstalkRow = ice_dbg_ct_rows ? ice_dbg_ct_rows[inRow] : irCrosstalk;
        float irrefRow = ice_dbg_irref_rows ? ice_dbg_irref_rows[inRow] : irref;
        float wIrref = weightIrrefFixed ? irref : irrefRow;   /* ingest-time (weight/maskpyr) irref */

        ice_row_slot_advance(s);
        rowsProcessed++;

        load(load_ctx, inRow, rgbi);
        if (sc && step < H) {   /* streaming estimator overrides the fixed low-res calibration (in-place) */
            float ct, rref, irRefRaw;
            ice_streamcalib_row(sc, inRow, rgbi, &ct, &rref, &irRefRaw);
            /* install formula: g=1/(1-ct); IRref=(-rref*ct+irRefRaw)*g  (x87 80-bit) */
            float g = (float)((long double)1.0 / ((long double)1.0 - (long double)ct));
            float scIrref = (float)(((long double)(-rref) * (long double)ct + (long double)irRefRaw) * (long double)g);
            if (getenv("ICE_SC_CTONLY")) {      /* per-row ct -> gate/trigger; IRref fixed everywhere */
                irCrosstalkRow = ct;
                irrefRow = irref; wIrref = irref;
            } else if (getenv("ICE_SC_RECONONLY")) {   /* only reconstruct-irDelta per-row; gate/weight stay fixed low-res */
                irrefRow = scIrref;             /* cfg[0xf2c] (reconstruct) */
                irCrosstalkRow = irCrosstalk;   /* gate ct fixed */
                wIrref = irref;                 /* weight/maskpyr irref fixed */
            } else {
                irCrosstalkRow = ct;
                irrefRow = scIrref;
                wIrref = weightIrrefFixed ? irref : irrefRow;
            }
        }
        cfg_wf(s, Cfg_IRref, irrefRow);   /* kind 7's -1.0 bias is applied globally at install (ice_analyze.c) */
        for (x = 0; x < W; x++) {
            convR[x] = s->densityLut[(unsigned short)rgbi[x * 4 + 0]]; convG[x] = s->densityLut[(unsigned short)rgbi[x * 4 + 1]];
            convB[x] = s->densityLut[(unsigned short)rgbi[x * 4 + 2]]; convIR[x] = s->densityLut[(unsigned short)rgbi[x * 4 + 3]];
        }
        ice_front_ingest(buf->inR[cfg_i(s, 0xe98)], buf->inG[cfg_i(s, 0xe98)], buf->inB[cfg_i(s, 0xe98)], pad, buf->gateCur[cfg_i(s, 0xe64)],
                         convR, convG, convB, convIR, width, irCrosstalkRow, crosstalkScale, one, gateBias, wIrref);

        /* FIRST ROW (RowCounter==-1): the ingest edge-replicates the current input planes across the WHOLE
         * vertical ring -- inR/inG/inB (slot 0xe98) copied into all 11 ring slots 0xe9c..0xec4. This makes the
         * reconstruction read row 0 for the above-frame rows at output rows 0-3 instead of unwritten zeros.
         * (Gatehist is likewise replicated below at the gatehist step.) */
        if (cfg_i(s, Cfg_RowCounter) == -1) {
            static const int inSlots[11] = { 0xe9c, 0xea0, 0xea4, 0xea8, 0xeac, 0xeb0, 0xeb4, 0xeb8, 0xebc, 0xec0, 0xec4 };
            float *r0 = buf->inR[cfg_i(s, 0xe98)], *gi0 = buf->inG[cfg_i(s, 0xe98)], *b0 = buf->inB[cfg_i(s, 0xe98)];
            for (k = 0; k < 11; k++) {
                memcpy(buf->inR[cfg_i(s, inSlots[k])], r0,  (size_t)ecc * sizeof(float));
                memcpy(buf->inG[cfg_i(s, inSlots[k])], gi0, (size_t)ecc * sizeof(float));
                memcpy(buf->inB[cfg_i(s, inSlots[k])], b0,  (size_t)ecc * sizeof(float));
            }
        }

        /* ICE_WIRREF_OFF: offset the weight-threshold irref only (refLevel = wIrref+weightBias), to test whether
         * the give-up mask boundary (cfg[0xf2c]) is a table-lookup value != openICE's install-formula irref. */
        { static float wirOff = 0; static int wirInit = 0; if (!wirInit) { wirInit = 1; if (getenv("ICE_WIRREF_OFF")) wirOff = (float)atof(getenv("ICE_WIRREF_OFF")); }
          ice_front_weight(buf->weight[cfg_i(s, 0xe6c)], buf->gateCur[cfg_i(s, 0xe50)], pad, width, use3TapMin, wIrref + wirOff, weightBias, weightSlope, weightRamp, weightFloor, weightCap); }
        /* Gate history. FIRST ROW (RowCounter==-1 after the slot advance): fill the ENTIRE
         * vertical gatehist ring -- all 10 slots 0xe6c..0xe90 -- with the current row's gate, read from gateCur slot
         * 0xe64 (the freshly-ingested gate, NOT the pipeline-delayed 0xe50). This edge-replicates row 0 across the
         * whole vertical window so the give-up trigger's oldest rows (gateHistA=slot 0xe90 etc.) are valid from
         * output row 0 instead of reading unwritten zeros (which caused false give-up in rows 0-3). Later rows use
         * the normal single-slot write with the delayed gate. */
        if (cfg_i(s, Cfg_RowCounter) == -1) {
            static const int ghSlots[10] = { 0xe6c, 0xe70, 0xe74, 0xe78, 0xe7c, 0xe80, 0xe84, 0xe88, 0xe8c, 0xe90 };
            float *g0 = buf->gateCur[cfg_i(s, 0xe64)];
            for (k = 0; k < 10; k++) ice_front_gatehist(buf->gateHist[cfg_i(s, ghSlots[k])], g0, pad, width);
        } else {
            ice_front_gatehist(buf->gateHist[cfg_i(s, 0xe6c)], buf->gateCur[cfg_i(s, 0xe50)], pad, width);
        }

        for (k = 0; k < 9; k++) w9[k] = buf->weight[cfg_i(s, 0xe6c + k * 4)];
        for (k = 0; k < 8; k++) prevProds[k] = buf->prodStage[cfg_i(s, 0xe70 + k * 4)];
        ice_front_products(buf->prodStage[cfg_i(s, 0xe6c)], w9, buf->gateHist[cfg_i(s, 0xe6c)], prevProds, buf->weightLadder, buf->productLadder, ecc, centerScale);

        { int m3s = getenv("ICE_MASK3SLOT") ? (int)strtol(getenv("ICE_MASK3SLOT"), 0, 16) : 0xe7c;
          /* NOTE: the `conf <= confFloor -> pyr = irref` fill is a no-op here (confFloor = 0), so whether that
           * fill carries kind 7's -1.0 bias is unobservable -- tested both ways, bit-identical output. */
          ice_front_maskpyr(buf->mask[cfg_i(s, 0xe44)], buf->pyr[cfg_i(s, 0xe58)], buf->weightLadder, buf->productLadder,
                            buf->weight[cfg_i(s, m3s)], buf->gateHist[cfg_i(s, m3s)], width, scale5, confFloor, scale9, wIrref); }

        ice_front_transform(buf->pyrStage[cfg_i(s, 0xe9c)], buf->inR[cfg_i(s, 0xe9c)], buf->inG[cfg_i(s, 0xe9c)], buf->inB[cfg_i(s, 0xe9c)], buf->weight[cfg_i(s, 0xe6c)], ecc);

        s->mask = buf->mask[cfg_i(s, 0xe48)];   /* e48 confirmed correct (e44 -> 30M diffs); recon uses the delayed slot */
        s->gateCenter = buf->pyr[cfg_i(s, 0xe5c)]; s->gateUp = buf->pyr[cfg_i(s, 0xe58)]; s->gateDown = buf->pyr[cfg_i(s, 0xe60)];
        s->inR = buf->inR[cfg_i(s, 0xeb0)]; s->inG = buf->inG[cfg_i(s, 0xeb0)]; s->inB = buf->inB[cfg_i(s, 0xeb0)];
        s->outR = buf->outR[cfg_i(s, 0xe5c)]; s->outG = buf->outG[cfg_i(s, 0xe5c)]; s->outB = buf->outB[cfg_i(s, 0xe5c)];
        for (k = 0; k < 9; k++) s->confWindow[k] = buf->gateHist[cfg_i(s, 0xe70 + k * 4)];
        s->gateHistA = buf->gateHist[cfg_i(s, 0xe90)]; s->gateHistB = buf->gateHist[cfg_i(s, 0xe70)];

        /* DIAGNOSTIC: dump the back-half inputs at ICE_PUMPDUMP output row, to diff vs a reference back.dump. */
        { const char *dr = getenv("ICE_PUMPDUMP");
          if (dr && warmup <= rowsProcessed && outRow == atoi(dr)) {
              FILE *f = fopen("oipump.dump", "wb"); int pw = (width + 2) * 4;
              fwrite(s->config, 1, CFG_BYTES, f);
              fwrite(&ecc, 4, 1, f); fwrite(s->inR, 4, ecc, f); fwrite(s->inG, 4, ecc, f); fwrite(s->inB, 4, ecc, f);
              fwrite(&pw, 4, 1, f); fwrite(s->gateCenter, 4, pw, f); fwrite(s->gateUp, 4, pw, f); fwrite(s->gateDown, 4, pw, f);
              /* EXTENDED (magic 'GC2'): the give-up inputs -- 9-row confWindow (= per-row padded gateCur) + mask (4/px).
               * Lets a comparison diff openICE's gateCur/mask3 vs a reference's captured confWindow/mask. */
              { int magic = 0x00324347 /* "GC2" */, k, mw = width * 4;
                fwrite(&magic, 4, 1, f);
                for (k = 0; k < 9; k++) fwrite(s->confWindow[k], 4, ecc, f);
                fwrite(&mw, 4, 1, f); fwrite(s->mask, 4, mw, f); }
              /* EXTENDED (magic 'PS3'): the reconstruction's actual VBuild input -- 12 productStage
               * (pyrStage/transform-output) slots -- plus gateHistA/B (the give-up trigger rows). Lets
               * a comparison diff every front-half output a reference captured, not just inR/gateCenter/confWindow/mask3. */
              { int magic2 = 0x00335350 /* "PS3" */, sl;
                fwrite(&magic2, 4, 1, f);
                for (sl = 0; sl < 12; sl++) fwrite(s->productStage[sl], 4, ecc * 3, f);
                fwrite(s->gateHistA, 4, ecc, f); fwrite(s->gateHistB, 4, ecc, f); }
              /* EXTENDED (magic 'WLAD'): openICE's 5 weight ladders (mask/conf inputs). Lets a comparison diff them
               * vs a reference's captured weight ladders to localize the mask1 divergence. */
              { int magic3 = 0x44414C57 /* "WLAD" */, wl;
                fwrite(&magic3, 4, 1, f);
                for (wl = 0; wl < 5; wl++) fwrite(buf->weightLadder[wl], 4, ecc, f); }
              /* EXTENDED (magic 'M44'): the CURRENT maskpyr output (slot 0xe44), aligned with the ladders above
               * (unlike s->mask which is the 1-row-delayed 0xe48 the reconstruction reads). Lets us recompute
               * mask1 from the same-row ladders and pin the divergence. */
              { int magic4 = 0x0034344D /* "M44" */, mw2 = width * 4;
                fwrite(&magic4, 4, 1, f); fwrite(buf->mask[cfg_i(s, 0xe44)], 4, mw2, f); }
              fclose(f);
              printf("[ICE_PUMPDUMP] wrote oipump.dump at outRow %d (ecc=%d pw=%d ct=%.9g irref=%.9g) [+GC2 confWindow/mask]\n",
                     outRow, ecc, pw, (double)irCrosstalkRow, (double)irrefRow);
              if (getenv("ICE_PUMPEXIT")) { fflush(stdout); _exit(0); }
          } }
        /* ICE_INRFP=<outRow0>,<outRow1>: print inR[100..103] fingerprint for each output row in the range,
         * to find which openICE output row matches a reference dump's captured inR. */
        { const char *fp = getenv("ICE_INRFP");
          if (fp && warmup <= rowsProcessed) { int a=0,b2=100000; sscanf(fp,"%d,%d",&a,&b2);
              if (outRow>=a && outRow<=b2) { float *ir = buf->inR[cfg_i(s,0xeb0)]; int q;
                  printf("[INRFP] outRow %d inR[98..108]=", outRow); for(q=98;q<=108;q++) printf(" %.5g",(double)ir[q]); printf("\n"); } } }

        /* DIAGNOSTIC (ICE_GUDUMP=<outRow>): dump the per-column give-up breakdown for one even row, so the
         * mask-branch vs trigger-branch flip can be localized against a reference give-up map. Replicates
         * ice_back_runrow's decision (TriggerEdgeMode==0 path) read-only; gu column is cross-checked vs the PGM. */
        { const char *gd = getenv("ICE_GUDUMP");
          if (gd && warmup <= rowsProcessed && outRow == atoi(gd)) {
              FILE *f = fopen("gudump.csv", "wb");
              int irMaskGate = (cfg_i(s, Cfg_ClampToL3Enable) != 0) || (cfg_i(s, Cfg_ClampToL3Flag) != 0);
              float floorv = cfg_f(s, Cfg_DustFloor); int maxBelow = cfg_i(s, Cfg_TargetId);
              int offL = cfg_u16(s, Cfg_TriggerOffsetL), offR = cfg_u16(s, Cfg_TriggerOffsetR);
              int col2, kk;
              { int aBelow=0,bBelow=0; float aMin=s->gateHistA[8],aMax=s->gateHistA[8];
                for (int i=8;i<ecc-8;i++){ if(s->gateHistA[i]<floorv)aBelow++; if(s->gateHistB[i]<floorv)bBelow++;
                    if(s->gateHistA[i]<aMin)aMin=s->gateHistA[i]; if(s->gateHistA[i]>aMax)aMax=s->gateHistA[i]; }
                fprintf(stderr,"[GUDUMP] gateHistA min=%.6g max=%.6g belowFloor=%d  A[100..104]= %.6g %.6g %.6g %.6g %.6g\n",
                        (double)aMin,(double)aMax,aBelow,(double)s->gateHistA[100],(double)s->gateHistA[101],(double)s->gateHistA[102],(double)s->gateHistA[103],(double)s->gateHistA[104]);
                fprintf(stderr,"[GUDUMP] gateHistB belowFloor=%d  confWindow[0..8]@col108: %.5g %.5g %.5g %.5g %.5g %.5g %.5g %.5g %.5g\n", bBelow,
                        (double)s->confWindow[0][108],(double)s->confWindow[1][108],(double)s->confWindow[2][108],(double)s->confWindow[3][108],(double)s->confWindow[4][108],
                        (double)s->confWindow[5][108],(double)s->confWindow[6][108],(double)s->confWindow[7][108],(double)s->confWindow[8][108]);
                fprintf(stderr,"[GUDUMP] confWindow[k]@col104 (reference ghA@r3=29710 31766 30456 27419 31912):");
                for(int kk2=0;kk2<9;kk2++) fprintf(stderr," [%d]=%.5g", kk2, (double)s->confWindow[kk2][104]); fprintf(stderr,"\n");
                fprintf(stderr,"[GUDUMP] step=%d gateHistA slot 0xe90=%d write 0xe6c=%d  (reference ghA[100..104]=29710 31766 30456 27419 31912)\n",
                        step, cfg_i(s,0xe90), cfg_i(s,0xe6c));
                for(int sl=0;sl<11;sl++) fprintf(stderr,"   gateHist slot %2d [100..104]= %.5g %.5g %.5g %.5g %.5g\n", sl,
                        (double)buf->gateHist[sl][100],(double)buf->gateHist[sl][101],(double)buf->gateHist[sl][102],(double)buf->gateHist[sl][103],(double)buf->gateHist[sl][104]); }
              fprintf(f, "col,gate,mask3,belowL,belowA,belowR,belowB,maskBranch,trigger,giveUp\n");
              for (col2 = 0; col2 < width; col2++) {
                  int leftCol = col2 - offL, rightCol = col2 + offR, bL=0,bA=0,bR=0,bB=0;
                  for (kk = 0; kk < 9; kk++) {
                      if (s->confWindow[kk][leftCol + 4]  < floorv) bL++;
                      if (s->gateHistA[col2 + kk]         < floorv) bA++;
                      if (s->confWindow[kk][rightCol + 4] < floorv) bR++;
                      if (s->gateHistB[col2 + kk]         < floorv) bB++;
                  }
                  int trig = (maxBelow < bL) || (maxBelow < bA) || (maxBelow < bR) || (maxBelow < bB);
                  int mb = irMaskGate && (s->IrGiveUpThresh <= s->mask[col2 * 4 + 3]);
                  fprintf(f, "%d,%.9g,%.9g,%d,%d,%d,%d,%d,%d,%d\n", col2,
                          (double)s->confWindow[4][col2 + 4], (double)s->mask[col2 * 4 + 3],
                          bL, bA, bR, bB, mb, trig, mb || trig);
              }
              fclose(f);
              printf("[ICE_GUDUMP] wrote gudump.csv at outRow %d (irMaskGate=%d edgeMode=%d floor=%.9g maxBelow=%d irref=%.9g)\n",
                     outRow, irMaskGate, cfg_i(s, Cfg_TriggerEdgeMode), (double)floorv, maxBelow, (double)irrefRow);
          } }

        ice_back_runrow(s);

        if (warmup <= rowsProcessed) {
            /* DIAGNOSTIC (ICE_LCGLOG): log the shared dither-LCG AFTER each emitted output row's reconstruction,
             * indexed by outRow, to diff vs a reference and locate the 1st give-up flip. */
            { static FILE *lf = (FILE *)-1; const char *lp = getenv("ICE_LCGLOG");
              if (lp) { if (lf == (FILE *)-1) lf = fopen(lp, "wb"); if (lf) { fprintf(lf, "%d,%u\n", outRow, s->ditherLcg); fflush(lf); } } }
            ice_back_output_convert(s, dstR, dstG, dstB, ecc);
            for (x = 0; x < W; x++) {
                int r = inv16(dstR[x + 4], convT1, convT2), g = inv16(dstG[x + 4], convT1, convT2), b = inv16(dstB[x + 4], convT1, convT2);
                outb[x * 6 + 0] = (unsigned char)(r & 0xFF); outb[x * 6 + 1] = (unsigned char)(r >> 8);
                outb[x * 6 + 2] = (unsigned char)(g & 0xFF); outb[x * 6 + 3] = (unsigned char)(g >> 8);
                outb[x * 6 + 4] = (unsigned char)(b & 0xFF); outb[x * 6 + 5] = (unsigned char)(b >> 8);
            }
            emit(emit_ctx, outRow, outb, W * 6);
            if (giveup) giveup(giveup_ctx, outRow, s->giveUpRecord);
            outRow++;
        }
    }

    free(rgbi); free(convR); free(convG); free(convB); free(convIR);
    free(dstR); free(dstG); free(dstB); free(outb);
    free(s->L0); free(s->L1); free(s->L2); free(s->L3);
    free(s->vLadder0); free(s->vLadder1); free(s->vLadder2); free(s->vLadder3);
    free(s->giveUpRecord);
    ice_streamcalib_free(sc);
    ice_buffers_free(buf);
    ice_state_free(s);
    return outRow;
}
