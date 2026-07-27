/* ice_front.c -- the front-half builders. Ported from IceFront.cs. Composites in `double` (bit-exact). */
#include "ice.h"

void ice_front_weight(float *weightOut, const float *gate, int pad, int width, int use3TapMin,
                      float irref, float bias, float slope, float ramp, float floorv, float cap) {
    /* Composited in `double` (verified bit-exact with the original under -mfpmath=387). `long double` here
     * re-introduces the 33-pixel front-half residual, so `double` is the correct x87 model for this stage. */
    double refLevel = (double)irref + bias;
    double w = (refLevel - gate[0]) * slope + ramp;   /* first column */
    if (w <= cap) { weightOut[pad] = (float)w; if (w < floorv) weightOut[pad] = floorv; } else weightOut[pad] = 1.0f;
    int wp = pad + 1, last = width - 1, col;
    if (use3TapMin) {
        for (col = 1; col < last; col++) {
            float g = gate[col - 1], t = gate[col]; if (t < g) g = t;
            t = gate[col + 1]; if (t < g) g = t;
            w = (refLevel - g) * slope + ramp;
            if (w <= cap) { weightOut[wp] = (float)w; if (w < floorv) weightOut[wp] = floorv; } else weightOut[wp] = 1.0f;
            wp++;
        }
    } else {
        for (col = 1; col < last; col++) {
            w = (refLevel - gate[col]) * slope + ramp;
            if (w <= cap) { weightOut[wp] = (float)w; if (w < floorv) weightOut[wp] = floorv; } else weightOut[wp] = 1.0f;
            wp++;
        }
    }
    w = (refLevel - gate[width - 1]) * slope + ramp;  /* last column */
    if (w <= cap) { weightOut[wp] = (float)w; if (w < floorv) weightOut[wp] = floorv; } else weightOut[wp] = 1.0f;
}

void ice_front_ingest(float *planeR, float *planeG, float *planeB, int planeOff, float *gate,
                      const float *convR, const float *convG, const float *convB, const float *convIR,
                      int width, float irCrosstalk, float crosstalkScale, float one, float gateBias, float irref) {
    float crosstalkScaled = irCrosstalk * crosstalkScale;
    int i;
    if (crosstalkScaled == one) {
        for (i = 0; i < width; i++) { planeR[planeOff + i] = convR[i]; planeG[planeOff + i] = convG[i]; planeB[planeOff + i] = convB[i]; gate[i] = irref; }
    } else {
        double gain = (double)one / ((double)one - crosstalkScaled);
        for (i = 0; i < width; i++) {
            planeR[planeOff + i] = convR[i]; planeG[planeOff + i] = convG[i]; planeB[planeOff + i] = convB[i];
            gate[i] = (float)(((double)convR[i] * -(double)crosstalkScaled + convIR[i]) * gain - gateBias);
        }
    }
}

void ice_front_gatehist(float *hist, const float *gate, int pad, int width) {
    int i, w = 0;
    for (i = 0; i < pad; i++)   hist[w++] = gate[0];
    for (i = 0; i < width; i++) hist[w++] = gate[i];
    for (i = 0; i < pad; i++)   hist[w++] = gate[width - 1];
}

void ice_front_transform(float *stg, const float *iR, const float *iG, const float *iB, const float *w, int ecc) {
    int c;
    for (c = 0; c < ecc; c++) {
        stg[c * 3 + 0] = iR[c] * w[c];
        stg[c * 3 + 1] = iG[c] * w[c];
        stg[c * 3 + 2] = iB[c] * w[c];
    }
}

void ice_front_products(float *gateWeight, float *const *w9, const float *gateCur, float *const *prevProds,
                        float **weightLadder, float **productLadder, int ecc, float centerScale) {
    int i;
    for (i = 0; i < ecc; i++) gateWeight[i] = gateCur[i] * w9[0][i];

    for (i = 0; i < ecc; i++) {
        double center = w9[4][i];
        double left   = w9[3][i] + center;
        double right  = w9[5][i];
        double sum3   = left + right;
        weightLadder[0][i] = (float)sum3;
        weightLadder[1][i] = (float)((right + center + left) * centerScale);
        { double run = sum3 + w9[2][i] + w9[6][i]; weightLadder[2][i] = (float)run;
          run = run + w9[1][i] + w9[7][i];         weightLadder[3][i] = (float)run;
          weightLadder[4][i] = (float)(run + w9[0][i] + w9[8][i]); }
    }

    for (i = 0; i < ecc; i++) {
        float p0 = gateWeight[i], p1 = prevProds[0][i], p2 = prevProds[1][i], p3 = prevProds[2][i], p4 = prevProds[3][i];
        float p5 = prevProds[4][i], p6 = prevProds[5][i], p7 = prevProds[6][i], p8 = prevProds[7][i];
        double center = p4;
        double left   = p3 + center;
        double right  = p5;
        double sum3   = left + right;
        productLadder[0][i] = (float)sum3;
        productLadder[1][i] = (float)((right + center + left) * centerScale);
        { double run = sum3 + p2 + p6; productLadder[2][i] = (float)run;
          run = run + p1 + p7;         productLadder[3][i] = (float)run;
          productLadder[4][i] = (float)(run + p0 + p8); }
    }
}

void ice_front_maskpyr(float *mask, float *pyr, float *const *weightLadder, float *const *productLadder,
                       const float *weightCenter, const float *irRatio, int width,
                       float scale5, float confFloor, float scale9, float irref) {
    int i;
    for (i = 0; i < width; i++) {
        double conf = ((double)weightLadder[3][i + 1] + weightLadder[2][i] + weightLadder[4][i + 2] + weightLadder[4][i + 3] + weightLadder[4][i + 4]
                    + weightLadder[4][i + 5] + weightLadder[4][i + 6] + weightLadder[3][i + 7] + weightLadder[2][i + 8]) * scale9;
        mask[i * 4 + 0] = (float)conf;
        if (conf <= confFloor) pyr[(i + 1) * 4 + 0] = irref;
        else pyr[(i + 1) * 4 + 0] = (float)((((double)productLadder[3][i + 1] + productLadder[2][i] + productLadder[4][i + 2] + productLadder[4][i + 3] + productLadder[4][i + 4]
                                    + productLadder[4][i + 5] + productLadder[4][i + 6] + productLadder[3][i + 7] + productLadder[2][i + 8]) * scale9) / conf);

        conf = ((double)weightLadder[2][i + 3] + weightLadder[0][i + 2] + weightLadder[2][i + 4] + weightLadder[2][i + 5] + weightLadder[0][i + 6]) * scale5;
        mask[i * 4 + 1] = (float)conf;
        if (conf <= confFloor) pyr[(i + 1) * 4 + 1] = irref;
        else pyr[(i + 1) * 4 + 1] = (float)((((double)productLadder[2][i + 3] + productLadder[0][i + 2] + productLadder[2][i + 4] + productLadder[2][i + 5] + productLadder[0][i + 6]) * scale5) / conf);

        conf = (double)weightLadder[1][i + 4] + weightLadder[1][i + 3] + weightLadder[1][i + 5] + weightLadder[1][i + 4];
        mask[i * 4 + 2] = (float)conf;
        if (conf <= confFloor) pyr[(i + 1) * 4 + 2] = irref;
        else pyr[(i + 1) * 4 + 2] = (float)(((double)productLadder[1][i + 4] + productLadder[1][i + 3] + productLadder[1][i + 5] + productLadder[1][i + 4]) / conf);

        { float irWeight = weightCenter[i + 4];
          mask[i * 4 + 3] = irWeight;
          if (irWeight <= confFloor) pyr[(i + 1) * 4 + 3] = irref;
          else pyr[(i + 1) * 4 + 3] = irRatio[i + 4]; }
    }
}
