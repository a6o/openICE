/* ice_back.c -- back-half driver + builders. Ported from IceBack.cs. The reconstruction ladders (VBuild,
 * LBuild) accumulate in `real_t` (80-bit long double by default) -- this is the x87 region where the C#
 * `double` model leaves its last-bit gap. Trigger / RunRow / OutputConvert are control + copy (no precision). */
#include "ice.h"
#include <stdlib.h>

void ice_back_vbuild(IceState *s, int col) {
    int start = col, last, c;
    if (col <= s->buildCursor) start = s->buildCursor + 1;
    last = col + 8;
    s->buildCursor = last;
    if (start > last) return;

    float *rowCenter = s->productStage[cfg_i(s, 0xeb0)], *rowUp1 = s->productStage[cfg_i(s, 0xeac)], *rowDown1 = s->productStage[cfg_i(s, 0xeb4)];
    float *rowUp2 = s->productStage[cfg_i(s, 0xea8)], *rowDown2 = s->productStage[cfg_i(s, 0xeb8)];
    float *rowUp3 = s->productStage[cfg_i(s, 0xea4)], *rowDown3 = s->productStage[cfg_i(s, 0xebc)];
    float *rowUp4 = s->productStage[cfg_i(s, 0xea0)], *rowDown4 = s->productStage[cfg_i(s, 0xec0)];

    for (c = start; c <= last; c++) {
        int b = c * 3;
        real_t accR = (real_t)rowCenter[b + 0] + rowUp1[b + 0] + rowDown1[b + 0];
        real_t accG = (real_t)rowCenter[b + 1] + rowUp1[b + 1] + rowDown1[b + 1];
        real_t accB = (real_t)rowCenter[b + 2] + rowUp1[b + 2] + rowDown1[b + 2];
        s->vLadder0[b + 0] = (float)accR; s->vLadder0[b + 1] = (float)accG; s->vLadder0[b + 2] = (float)accB;
        accR = (real_t)rowUp2[b + 0] + accR + rowDown2[b + 0];
        accG = (real_t)rowUp2[b + 1] + accG + rowDown2[b + 1];
        accB = (real_t)rowUp2[b + 2] + accB + rowDown2[b + 2];
        s->vLadder1[b + 0] = (float)accR; s->vLadder1[b + 1] = (float)accG; s->vLadder1[b + 2] = (float)accB;
        accR = (real_t)rowUp3[b + 0] + accR + rowDown3[b + 0];
        accG = (real_t)rowUp3[b + 1] + accG + rowDown3[b + 1];
        accB = (real_t)rowUp3[b + 2] + accB + rowDown3[b + 2];
        s->vLadder2[b + 0] = (float)accR; s->vLadder2[b + 1] = (float)accG; s->vLadder2[b + 2] = (float)accB;
        s->vLadder3[b + 1] = (float)((real_t)rowUp4[b + 1] + accG + rowDown4[b + 1]);
        s->vLadder3[b + 2] = (float)((real_t)rowUp4[b + 2] + accB + rowDown4[b + 2]);
        s->vLadder3[b + 0] = (float)((real_t)rowUp4[b + 0] + accR + rowDown4[b + 0]);
    }
}

void ice_back_lbuild(IceState *s, int col) {
    int m = col * 4;
    float confB = s->mask[m + 2], confG = s->mask[m + 1], confR = s->mask[m + 0];

    int a2 = (col + 2) * 3, a6 = (col + 6) * 3, b3 = (col + 3) * 3, b4 = (col + 4) * 3, b5 = (col + 5) * 3;
    real_t L1r = ((real_t)s->vLadder0[a6 + 0] + s->vLadder0[a2 + 0] + s->vLadder1[b3 + 0] + s->vLadder1[b4 + 0] + s->vLadder1[b5 + 0]) * (real_t)s->LBuildL1Scale;
    real_t L1g = ((real_t)s->vLadder0[a6 + 1] + s->vLadder0[a2 + 1] + s->vLadder1[b3 + 1] + s->vLadder1[b4 + 1] + s->vLadder1[b5 + 1]) * (real_t)s->LBuildL1Scale;
    real_t L1b = ((real_t)s->vLadder0[a6 + 2] + s->vLadder0[a2 + 2] + s->vLadder1[b3 + 2] + s->vLadder1[b4 + 2] + s->vLadder1[b5 + 2]) * (real_t)s->LBuildL1Scale;

    int c0 = col * 3, c8 = (col + 8) * 3, c1 = (col + 1) * 3, c7 = (col + 7) * 3;
    int d2 = (col + 2) * 3, d3 = (col + 3) * 3, d4 = (col + 4) * 3, d5 = (col + 5) * 3, d6 = (col + 6) * 3;
    real_t L0r = ((real_t)s->vLadder1[c8 + 0] + s->vLadder1[c0 + 0] + s->vLadder2[c1 + 0] + s->vLadder2[c7 + 0] + s->vLadder3[d2 + 0] + s->vLadder3[d3 + 0] + s->vLadder3[d4 + 0] + s->vLadder3[d5 + 0] + s->vLadder3[d6 + 0]) * (real_t)s->LBuildL0Scale;
    real_t L0g = ((real_t)s->vLadder1[c8 + 1] + s->vLadder1[c0 + 1] + s->vLadder2[c1 + 1] + s->vLadder2[c7 + 1] + s->vLadder3[d2 + 1] + s->vLadder3[d3 + 1] + s->vLadder3[d4 + 1] + s->vLadder3[d5 + 1] + s->vLadder3[d6 + 1]) * (real_t)s->LBuildL0Scale;
    real_t L0b = ((real_t)s->vLadder1[c8 + 2] + s->vLadder1[c0 + 2] + s->vLadder2[c1 + 2] + s->vLadder2[c7 + 2] + s->vLadder3[d2 + 2] + s->vLadder3[d3 + 2] + s->vLadder3[d4 + 2] + s->vLadder3[d5 + 2] + s->vLadder3[d6 + 2]) * (real_t)s->LBuildL0Scale;

    int lp = col * 3;
    if (confG <= s->LBuildGateThresh) { s->L1[lp + 0] = (float)L1r; s->L1[lp + 1] = (float)L1g; s->L1[lp + 2] = (float)L1b; }
    else { real_t q = (real_t)s->LBuildNormNum / confG; s->L1[lp + 0] = (float)(L1r * q); s->L1[lp + 1] = (float)(L1g * q); s->L1[lp + 2] = (float)(q * L1b); }

    if (confR <= s->LBuildGateThresh) { s->L0[lp + 0] = (float)L0r; s->L0[lp + 1] = (float)L0g; s->L0[lp + 2] = (float)L0b; }
    else { real_t q = (real_t)s->LBuildNormNum / confR; s->L0[lp + 0] = (float)(L0r * q); s->L0[lp + 1] = (float)(L0g * q); s->L0[lp + 2] = (float)(q * L0b); }

    { float *stageCenter = s->productStage[cfg_i(s, 0xeb0)], *stageUp = s->productStage[cfg_i(s, 0xeac)], *stageDown = s->productStage[cfg_i(s, 0xeb4)];
      int e3 = (col + 3) * 3, e4 = (col + 4) * 3, e5 = (col + 5) * 3;
      real_t cR = (real_t)stageCenter[e4 + 0] + stageCenter[e3 + 0] + stageCenter[e5 + 0] + stageCenter[e4 + 0];
      real_t cG = (real_t)stageCenter[e4 + 1] + stageCenter[e3 + 1] + stageCenter[e5 + 1] + stageCenter[e4 + 1];
      real_t cB = (real_t)stageCenter[e4 + 2] + stageCenter[e3 + 2] + stageCenter[e5 + 2] + stageCenter[e4 + 2];
      real_t L2r = ((real_t)stageDown[e4 + 0] + stageDown[e3 + 0] + stageDown[e5 + 0] + cR + stageUp[e4 + 0] + stageUp[e3 + 0] + stageUp[e5 + 0] + stageUp[e4 + 0] + cR + stageDown[e4 + 0]) * (real_t)s->LBuildL2Scale;
      real_t L2g = ((real_t)stageDown[e4 + 1] + stageDown[e3 + 1] + stageDown[e5 + 1] + cG + stageUp[e4 + 1] + stageUp[e3 + 1] + stageUp[e5 + 1] + stageUp[e4 + 1] + cG + stageDown[e4 + 1]) * (real_t)s->LBuildL2Scale;
      real_t L2b = (real_t)s->LBuildL2Scale * ((real_t)stageDown[e4 + 2] + stageDown[e3 + 2] + stageDown[e5 + 2] + cB + stageUp[e4 + 2] + stageUp[e3 + 2] + stageUp[e5 + 2] + stageUp[e4 + 2] + cB + stageDown[e4 + 2]);
      if (confB <= s->LBuildGateThresh) { s->L2[lp + 0] = (float)L2r; s->L2[lp + 1] = (float)L2g; s->L2[lp + 2] = (float)L2b; }
      else { real_t q = (real_t)s->LBuildNormNum / confB; s->L2[lp + 0] = (float)(L2r * q); s->L2[lp + 1] = (float)(L2g * q); s->L2[lp + 2] = (float)(q * L2b); } }

    s->L3[lp + 0] = s->inR[col + 4]; s->L3[lp + 1] = s->inG[col + 4]; s->L3[lp + 2] = s->inB[col + 4];
}

int ice_back_trigger(IceState *s, int col) {
    int k;
    if (cfg_i(s, Cfg_TriggerEdgeMode) == 0) {
        float floorv = cfg_f(s, Cfg_DustFloor);
        int maxBelow = cfg_i(s, Cfg_TargetId);

        int leftCol = col - cfg_u16(s, Cfg_TriggerOffsetL);
        int below = 0; for (k = 0; k < 9; k++) if (s->confWindow[k][leftCol + 4] < floorv) below++;
        if (maxBelow < below) return 1;

        below = 0; for (k = 0; k < 9; k++) if (s->gateHistA[col + k] < floorv) below++;
        if (maxBelow < below) return 1;

        { int rightCol = cfg_u16(s, Cfg_TriggerOffsetR) + col;
          below = 0; for (k = 0; k < 9; k++) if (s->confWindow[k][rightCol + 4] < floorv) below++;
          if (maxBelow < below) return 1; }

        below = 0; for (k = 0; k < 9; k++) if (s->gateHistB[col + k] < floorv) below++;
        if (maxBelow < below) return 1;

        return 0;
    }
    if ((s->TriggerEdgeThresh <= s->confWindow[4][col + 2]) && (s->TriggerEdgeThresh <= s->confWindow[6][col + 4]) &&
        (s->TriggerEdgeThresh <= s->confWindow[4][col + 6]) && (s->TriggerEdgeThresh <= s->confWindow[2][col + 4]))
        return 0;
    return 1;
}

void ice_back_runrow(IceState *s) {
    if (!((1 < cfg_i(s, Cfg_Dpi)) && (cfg_i(s, Cfg_DrainFlag) == 0))) return;
    s->buildCursor = -1;

    { int width = cfg_i(s, Cfg_ImageWidth), col;
      int irMaskGate = (cfg_i(s, Cfg_ClampToL3Enable) != 0) || (cfg_i(s, Cfg_ClampToL3Flag) != 0);
      for (col = 0; col < width; col++) {
          int giveUp = (irMaskGate && s->IrGiveUpThresh <= s->mask[col * 4 + 3]) || (ice_back_trigger(s, col) != 0);
          if (s->giveUpRecord) s->giveUpRecord[col] = (unsigned char)(giveUp ? 1 : 0);
          if (giveUp) ice_core_giveup(s, col + 4);
          else { ice_back_vbuild(s, col); ice_back_lbuild(s, col); ice_core_reconstruct(s, col + 4, col + 1, col); }
      } }
}

void ice_back_output_convert(IceState *s, float *dstR, float *dstG, float *dstB, int count) {
    int i; for (i = 0; i < count; i++) { dstR[i] = s->outR[i]; dstG[i] = s->outG[i]; dstB[i] = s->outB[i]; }
}
