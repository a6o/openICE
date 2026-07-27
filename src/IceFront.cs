using System;

// IceFront.cs -- the front half of the pipeline: the builders that turn one scan row into the
// reconstruction driver's inputs -- the weight ramp, the gate*weight transform, the two horizontal-sum
// ladders, and the normalized-convolution confidence mask + gate pyramid.
//
// These are pure functions over flat float[] buffers: ring/window indirection is resolved to the concrete
// per-row buffers by the caller (IcePump), so nothing here touches the ring machinery.
//
// Composite float expressions accumulate in `double` (a portable stand-in for 80-bit intermediates) and
// round to `float` only at the store, keeping operation ORDER verbatim -- the same regime as IceCore.cs.
// Everything indexes flat float[] buffers; each expression stays legible.

public static class IceFront
{
    // Weight: IR gate -> clamped-linear weight ring.
    //   weightOut[pad .. pad+width-1] = clamp( (refLevel - g) * slope + ramp, floor, 1.0 )
    //   where refLevel = irref + bias, and g is gate[col] (or the 3-tap min of gate when the lookahead
    //   window is open). Composite expressions run in `double` and round to `float` only at the store; the
    //   comparisons read the un-stored (double) value, matching the original. Operation ORDER is verbatim.
    public static void Weight(float[] weightOut, float[] gate, int pad, int width, bool use3TapMin,
                              float irref, float bias, float slope, float ramp, float floor, float cap)
    {
        double refLevel = (double)irref + bias;
        double w = (refLevel - gate[0]) * slope + ramp;      // first column
        if (w <= cap) { weightOut[pad] = (float)w; if (w < floor) weightOut[pad] = floor; } else weightOut[pad] = 1.0f;
        int wp = pad + 1;
        int last = width - 1;
        if (use3TapMin)
        {
            for (int col = 1; col < last; col++)
            {
                float g = gate[col - 1], t = gate[col]; if (t < g) g = t;
                t = gate[col + 1]; if (t < g) g = t;
                w = (refLevel - g) * slope + ramp;
                if (w <= cap) { weightOut[wp] = (float)w; if (w < floor) weightOut[wp] = floor; } else weightOut[wp] = 1.0f;
                wp++;
            }
        }
        else
        {
            for (int col = 1; col < last; col++)
            {
                w = (refLevel - gate[col]) * slope + ramp;
                if (w <= cap) { weightOut[wp] = (float)w; if (w < floor) weightOut[wp] = floor; } else weightOut[wp] = 1.0f;
                wp++;
            }
        }
        w = (refLevel - gate[width - 1]) * slope + ramp;     // last column
        if (w <= cap) { weightOut[wp] = (float)w; if (w < floor) weightOut[wp] = floor; } else weightOut[wp] = 1.0f;
    }

    // Ingest (interior rows): copy the density-converted RGB into the input planes and compute the IR gate.
    // The converter that produces conv* is a pure density lookup, conv[c][i] = LUT[raw16[c][i]]. The gate is
    // the raw log-IR signal, (convR*-crosstalk + convIR)*gain - bias, isolating IR/dust from the visible dye.
    public static void Ingest(float[] planeR, float[] planeG, float[] planeB, float[] gate,
                              float[] convR, float[] convG, float[] convB, float[] convIR,
                              int width, float irCrosstalk, float crosstalkScale, float one, float gateBias, float irref)
    { Ingest(planeR, planeG, planeB, 0, gate, convR, convG, convB, convIR, width, irCrosstalk, crosstalkScale, one, gateBias, irref); }

    // planeOff: the input-plane rings carry a pad-wide LEFT PAD -- the planes are written starting at float
    // index `pad`, so the padded plane shares weight's / gate-history's column system and Transform's iR[c]*w[c]
    // aligns column-for-column. The streaming pump owns the whole ecc-wide ring and passes planeOff=pad; the
    // gate ring (gateCur) has NO pad and is written at index 0. The pad columns stay 0 (never written).
    public static void Ingest(float[] planeR, float[] planeG, float[] planeB, int planeOff, float[] gate,
                              float[] convR, float[] convG, float[] convB, float[] convIR,
                              int width, float irCrosstalk, float crosstalkScale, float one, float gateBias, float irref)
    {
        float crosstalkScaled = irCrosstalk * crosstalkScale;
        if (crosstalkScaled == one)
        {
            // degenerate: crosstalk cancels -- copy planes and set the gate flat to irref.
            for (int i = 0; i < width; i++) { planeR[planeOff + i] = convR[i]; planeG[planeOff + i] = convG[i]; planeB[planeOff + i] = convB[i]; gate[i] = irref; }
        }
        else
        {
            double gain = (double)one / ((double)one - crosstalkScaled);   // crosstalk-correction gain
            for (int i = 0; i < width; i++)
            {
                planeR[planeOff + i] = convR[i]; planeG[planeOff + i] = convG[i]; planeB[planeOff + i] = convB[i];
                gate[i] = (float)(((double)convR[i] * -(double)crosstalkScaled + convIR[i]) * gain - gateBias);
            }
        }
    }

    // GateHist (interior rows): copy the current gate (W wide) into the gate-HISTORY ring (ecc wide) with
    // pad-wide edge replication on both sides, so Products' 9-row vertical window can read past the image
    // edges. (The very first row broadcasts into all history slots; the streaming loop only needs the
    // interior copy modelled here.)
    public static void GateHist(float[] hist, float[] gate, int pad, int width)
    {
        int w = 0;
        for (int i = 0; i < pad; i++)   hist[w++] = gate[0];           // left edge
        for (int i = 0; i < width; i++) hist[w++] = gate[i];           // the row
        for (int i = 0; i < pad; i++)   hist[w++] = gate[width - 1];   // right edge
    }

    // Transform: input planes x weight -> product staging (3 interleaved floats/col).
    public static void Transform(float[] stg, float[] iR, float[] iG, float[] iB, float[] w, int ecc)
    {
        for (int c = 0; c < ecc; c++)
        {
            stg[c * 3 + 0] = iR[c] * w[c];
            stg[c * 3 + 1] = iG[c] * w[c];
            stg[c * 3 + 2] = iB[c] * w[c];
        }
    }

    // Products: gate*weight -> gateWeight, then two cumulative horizontal-sum ladders (3/center/5/7/9-tap) over
    // the 9-row window -- one over the weights, one over the products.
    //   w9[0..8]        = the 9 weight buffers (window center = w9[4])
    //   gateCur         = the current gate buffer;  gateWeight[i] = gateCur[i]*w9[0][i]
    //   prevProds[0..7] = the 8 previous product buffers;  window p = { gateWeight, prevProds0..7 }, center p[4]
    //   weightLadder[0..4] = weight partial sums;  productLadder[0..4] = product partial sums (same taps)
    public static void Products(float[] gateWeight, float[][] w9, float[] gateCur, float[][] prevProds,
                                float[][] weightLadder, float[][] productLadder, int ecc, float centerScale)
    {
        for (int i = 0; i < ecc; i++) gateWeight[i] = gateCur[i] * w9[0][i];

        for (int i = 0; i < ecc; i++)
        {
            double center = w9[4][i];
            double left   = w9[3][i] + center;
            double right  = w9[5][i];
            double sum3   = left + right;
            weightLadder[0][i] = (float)sum3;                                      // 3-tap
            weightLadder[1][i] = (float)((right + center + left) * centerScale);   // center-weighted
            double run = sum3 + w9[2][i] + w9[6][i]; weightLadder[2][i] = (float)run;   // 5-tap
            run = run + w9[1][i] + w9[7][i];         weightLadder[3][i] = (float)run;   // 7-tap
            weightLadder[4][i] = (float)(run + w9[0][i] + w9[8][i]);                    // 9-tap
        }

        for (int i = 0; i < ecc; i++)
        {
            float p0 = gateWeight[i], p1 = prevProds[0][i], p2 = prevProds[1][i], p3 = prevProds[2][i], p4 = prevProds[3][i];
            float p5 = prevProds[4][i], p6 = prevProds[5][i], p7 = prevProds[6][i], p8 = prevProds[7][i];
            double center = p4;
            double left   = p3 + center;
            double right  = p5;
            double sum3   = left + right;
            productLadder[0][i] = (float)sum3;
            productLadder[1][i] = (float)((right + center + left) * centerScale);
            double run = sum3 + p2 + p6; productLadder[2][i] = (float)run;
            run = run + p1 + p7;         productLadder[3][i] = (float)run;
            productLadder[4][i] = (float)(run + p0 + p8);
        }
    }

    // MaskPyr -- normalized convolution: horizontal taps over the ladders -> mask (Sigma k*w = confidence)
    // and gate pyramid (Sigma(k*x*w)/Sigma(k*w)). Where confidence <= confFloor, the pyramid gates to irref.
    //   mask[i*4 + {0,1,2,3}]    = R/G/B confidence + IR(=center weight)
    //   pyr[(i+1)*4 + {0,1,2,3}] = R/G/B ratio + IR(=irRatio), or irref when confidence <= confFloor
    public static void MaskPyr(float[] mask, float[] pyr, float[][] weightLadder, float[][] productLadder,
                               float[] weightCenter, float[] irRatio, int width, float scale5, float confFloor, float scale9, float irref)
    {
        for (int i = 0; i < width; i++)
        {
            // R -- 9-tap horizontal over the 9-tap vertical ladder ([4]) plus 7/5-tap shoulders. Sums and the
            // divide run in double so the compare + divide see the full-width numerator/denominator.
            double conf = ((double)weightLadder[3][i + 1] + weightLadder[2][i] + weightLadder[4][i + 2] + weightLadder[4][i + 3] + weightLadder[4][i + 4]
                       + weightLadder[4][i + 5] + weightLadder[4][i + 6] + weightLadder[3][i + 7] + weightLadder[2][i + 8]) * scale9;
            mask[i * 4 + 0] = (float)conf;
            if (conf <= confFloor) pyr[(i + 1) * 4 + 0] = irref;
            else pyr[(i + 1) * 4 + 0] = (float)((((double)productLadder[3][i + 1] + productLadder[2][i] + productLadder[4][i + 2] + productLadder[4][i + 3] + productLadder[4][i + 4]
                                        + productLadder[4][i + 5] + productLadder[4][i + 6] + productLadder[3][i + 7] + productLadder[2][i + 8]) * scale9) / conf);

            // G -- 5-tap horizontal over the 5-tap ([2]) / 3-tap ([0]) ladders
            conf = ((double)weightLadder[2][i + 3] + weightLadder[0][i + 2] + weightLadder[2][i + 4] + weightLadder[2][i + 5] + weightLadder[0][i + 6]) * scale5;
            mask[i * 4 + 1] = (float)conf;
            if (conf <= confFloor) pyr[(i + 1) * 4 + 1] = irref;
            else pyr[(i + 1) * 4 + 1] = (float)((((double)productLadder[2][i + 3] + productLadder[0][i + 2] + productLadder[2][i + 4] + productLadder[2][i + 5] + productLadder[0][i + 6]) * scale5) / conf);

            // B -- weighted 3-tap over the center-weighted ladder ([1]); note the +4 tap counts twice
            conf = (double)weightLadder[1][i + 4] + weightLadder[1][i + 3] + weightLadder[1][i + 5] + weightLadder[1][i + 4];
            mask[i * 4 + 2] = (float)conf;
            if (conf <= confFloor) pyr[(i + 1) * 4 + 2] = irref;
            else pyr[(i + 1) * 4 + 2] = (float)(((double)productLadder[1][i + 4] + productLadder[1][i + 3] + productLadder[1][i + 5] + productLadder[1][i + 4]) / conf);

            // IR (4th) -- mask = center weight; pyramid = irRatio, gated to irref by the same threshold
            float irWeight = weightCenter[i + 4];
            mask[i * 4 + 3] = irWeight;
            if (irWeight <= confFloor) pyr[(i + 1) * 4 + 3] = irref;
            else pyr[(i + 1) * 4 + 3] = irRatio[i + 4];
        }
    }
}
