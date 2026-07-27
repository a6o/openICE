using System;

// IceBack.cs -- the back half of the pipeline: the reconstruction driver and its per-column builders
// that the front half feeds into:
//   RunRow  the per-row driver: build-cursor setup + the column loop
//   Trigger decide per column: reconstruct, or give up (copy the input through)
//   VBuild  vertical build: cumulative 3/5/7/9-tap column sums over the staging window
//   LBuild  L-planes: separable horizontal convolution of the ladders -> pyramid + input plane
// The core and give-up copy live in IceCore.cs (ReconstructColumn / GiveUp).
//
// Output stage:
//   OutputConvert  copy the reconstructed output rings into the caller's row buffers.
//
// Arithmetic follows the same regime as IceFront/IceCore: accumulators run in `double` (a portable
// stand-in for 80-bit intermediates) and round to `float` only at each store, with operation ORDER kept
// verbatim. Ring/pointer indirection is resolved to concrete managed arrays before the driver runs. Buffer
// indices keep the byte arithmetic (`>> 2`) so each expression stays legible.

public static class IceBack
{
    // VBUILD. For each column in the horizontal build-ahead window [start .. col+8], sum the 9-row vertical
    // staging window into four cumulative ladders (3/5/7/9 rows). productStage[ring][col*3+ch] is the
    // front-half Transform output (input-plane x weight) for the image row held in that ring slot. The ring
    // indices are the object's concentric-window fields (center eb0, then eac/eb4, ea8/eb8, ea4/ebc, ea0/ec0).
    public static void VBuild(IceState s, int col)
    {
        int start = col;
        if (col <= s.buildCursor) start = s.buildCursor + 1;
        int last = col + 8;
        s.buildCursor = last;
        if (start > last) return;

        float[] rowCenter = s.productStage[s.ReadI(0xeb0)], rowUp1 = s.productStage[s.ReadI(0xeac)], rowDown1 = s.productStage[s.ReadI(0xeb4)]; // level 0 (3 rows)
        float[] rowUp2 = s.productStage[s.ReadI(0xea8)], rowDown2 = s.productStage[s.ReadI(0xeb8)];                                             // +/- 2
        float[] rowUp3 = s.productStage[s.ReadI(0xea4)], rowDown3 = s.productStage[s.ReadI(0xebc)];                                             // +/- 3
        float[] rowUp4 = s.productStage[s.ReadI(0xea0)], rowDown4 = s.productStage[s.ReadI(0xec0)];                                             // +/- 4

        for (int c = start; c <= last; c++)
        {
            int b = c * 3;
            // level 0: center 3 rows (order: eb0 + eac + eb4, verbatim)
            double accR = (double)rowCenter[b + 0] + rowUp1[b + 0] + rowDown1[b + 0];
            double accG = (double)rowCenter[b + 1] + rowUp1[b + 1] + rowDown1[b + 1];
            double accB = (double)rowCenter[b + 2] + rowUp1[b + 2] + rowDown1[b + 2];
            s.vLadder0[b + 0] = (float)accR; s.vLadder0[b + 1] = (float)accG; s.vLadder0[b + 2] = (float)accB;
            // level 1: + rows ea8, eb8 (5 rows)
            accR = (double)rowUp2[b + 0] + accR + rowDown2[b + 0];
            accG = (double)rowUp2[b + 1] + accG + rowDown2[b + 1];
            accB = (double)rowUp2[b + 2] + accB + rowDown2[b + 2];
            s.vLadder1[b + 0] = (float)accR; s.vLadder1[b + 1] = (float)accG; s.vLadder1[b + 2] = (float)accB;
            // level 2: + rows ea4, ebc (7 rows)
            accR = (double)rowUp3[b + 0] + accR + rowDown3[b + 0];
            accG = (double)rowUp3[b + 1] + accG + rowDown3[b + 1];
            accB = (double)rowUp3[b + 2] + accB + rowDown3[b + 2];
            s.vLadder2[b + 0] = (float)accR; s.vLadder2[b + 1] = (float)accG; s.vLadder2[b + 2] = (float)accB;
            // level 3: + rows ea0, ec0 (9 rows)
            s.vLadder3[b + 1] = (float)((double)rowUp4[b + 1] + accG + rowDown4[b + 1]);
            s.vLadder3[b + 2] = (float)((double)rowUp4[b + 2] + accB + rowDown4[b + 2]);
            s.vLadder3[b + 0] = (float)((double)rowUp4[b + 0] + accR + rowDown4[b + 0]);
        }
    }

    // LBUILD. Separable horizontal convolution of the four vertical ladders into the L-pyramid for column
    // `col`, gated + normalized by the mask confidences; then L3 = the raw (density-converted) input plane.
    // Sums run in double so the divide sees the full-width numerator/denominator.
    public static void LBuild(IceState s, int col)
    {
        int m = col * 4;                                   // mask is 4 floats/col
        float confB = s.mask[m + 2];                       // gates L2
        float confG = s.mask[m + 1];                       // gates L1
        float confR = s.mask[m + 0];                       // gates L0

        // L1 numerators: vLadder0 at cols col+2,col+6 ; vLadder1 at cols col+3,col+4,col+5  (* LBuildL1Scale)
        int a2 = (col + 2) * 3, a6 = (col + 6) * 3, b3 = (col + 3) * 3, b4 = (col + 4) * 3, b5 = (col + 5) * 3;
        double L1r = ((double)s.vLadder0[a6 + 0] + s.vLadder0[a2 + 0] + s.vLadder1[b3 + 0] + s.vLadder1[b4 + 0] + s.vLadder1[b5 + 0]) * (double)s.LBuildL1Scale;
        double L1g = ((double)s.vLadder0[a6 + 1] + s.vLadder0[a2 + 1] + s.vLadder1[b3 + 1] + s.vLadder1[b4 + 1] + s.vLadder1[b5 + 1]) * (double)s.LBuildL1Scale;
        double L1b = ((double)s.vLadder0[a6 + 2] + s.vLadder0[a2 + 2] + s.vLadder1[b3 + 2] + s.vLadder1[b4 + 2] + s.vLadder1[b5 + 2]) * (double)s.LBuildL1Scale;

        // L0 numerators: vLadder1 at col,col+8 ; vLadder2 at col+1,col+7 ; vLadder3 at col+2..col+6  (* LBuildL0Scale)
        int c0 = col * 3, c8 = (col + 8) * 3, c1 = (col + 1) * 3, c7 = (col + 7) * 3;
        int d2 = (col + 2) * 3, d3 = (col + 3) * 3, d4 = (col + 4) * 3, d5 = (col + 5) * 3, d6 = (col + 6) * 3;
        double L0r = ((double)s.vLadder1[c8 + 0] + s.vLadder1[c0 + 0] + s.vLadder2[c1 + 0] + s.vLadder2[c7 + 0] + s.vLadder3[d2 + 0] + s.vLadder3[d3 + 0] + s.vLadder3[d4 + 0] + s.vLadder3[d5 + 0] + s.vLadder3[d6 + 0]) * (double)s.LBuildL0Scale;
        double L0g = ((double)s.vLadder1[c8 + 1] + s.vLadder1[c0 + 1] + s.vLadder2[c1 + 1] + s.vLadder2[c7 + 1] + s.vLadder3[d2 + 1] + s.vLadder3[d3 + 1] + s.vLadder3[d4 + 1] + s.vLadder3[d5 + 1] + s.vLadder3[d6 + 1]) * (double)s.LBuildL0Scale;
        double L0b = ((double)s.vLadder1[c8 + 2] + s.vLadder1[c0 + 2] + s.vLadder2[c1 + 2] + s.vLadder2[c7 + 2] + s.vLadder3[d2 + 2] + s.vLadder3[d3 + 2] + s.vLadder3[d4 + 2] + s.vLadder3[d5 + 2] + s.vLadder3[d6 + 2]) * (double)s.LBuildL0Scale;

        int lp = col * 3;
        if (confG <= s.LBuildGateThresh) { s.L1[lp + 0] = (float)L1r; s.L1[lp + 1] = (float)L1g; s.L1[lp + 2] = (float)L1b; }
        else { double q = (double)s.LBuildNormNum / confG; s.L1[lp + 0] = (float)(L1r * q); s.L1[lp + 1] = (float)(L1g * q); s.L1[lp + 2] = (float)(q * L1b); }

        if (confR <= s.LBuildGateThresh) { s.L0[lp + 0] = (float)L0r; s.L0[lp + 1] = (float)L0g; s.L0[lp + 2] = (float)L0b; }
        else { double q = (double)s.LBuildNormNum / confR; s.L0[lp + 0] = (float)(L0r * q); s.L0[lp + 1] = (float)(L0g * q); s.L0[lp + 2] = (float)(q * L0b); }

        // L2: horizontal 3-tap (cols col+3,col+4,col+5, center weighted twice) over the center staging row (eb0),
        // plus the same over its two vertical neighbours (eac, eb4) with the eb0 partial folded in.  (* LBuildL2Scale)
        float[] stageCenter = s.productStage[s.ReadI(0xeb0)], stageUp = s.productStage[s.ReadI(0xeac)], stageDown = s.productStage[s.ReadI(0xeb4)];
        int e3 = (col + 3) * 3, e4 = (col + 4) * 3, e5 = (col + 5) * 3;
        double cR = (double)stageCenter[e4 + 0] + stageCenter[e3 + 0] + stageCenter[e5 + 0] + stageCenter[e4 + 0];
        double cG = (double)stageCenter[e4 + 1] + stageCenter[e3 + 1] + stageCenter[e5 + 1] + stageCenter[e4 + 1];
        double cB = (double)stageCenter[e4 + 2] + stageCenter[e3 + 2] + stageCenter[e5 + 2] + stageCenter[e4 + 2];
        double L2r = ((double)stageDown[e4 + 0] + stageDown[e3 + 0] + stageDown[e5 + 0] + cR + stageUp[e4 + 0] + stageUp[e3 + 0] + stageUp[e5 + 0] + stageUp[e4 + 0] + cR + stageDown[e4 + 0]) * (double)s.LBuildL2Scale;
        double L2g = ((double)stageDown[e4 + 1] + stageDown[e3 + 1] + stageDown[e5 + 1] + cG + stageUp[e4 + 1] + stageUp[e3 + 1] + stageUp[e5 + 1] + stageUp[e4 + 1] + cG + stageDown[e4 + 1]) * (double)s.LBuildL2Scale;
        double L2b = (double)s.LBuildL2Scale * ((double)stageDown[e4 + 2] + stageDown[e3 + 2] + stageDown[e5 + 2] + cB + stageUp[e4 + 2] + stageUp[e3 + 2] + stageUp[e5 + 2] + stageUp[e4 + 2] + cB + stageDown[e4 + 2]);
        if (confB <= s.LBuildGateThresh) { s.L2[lp + 0] = (float)L2r; s.L2[lp + 1] = (float)L2g; s.L2[lp + 2] = (float)L2b; }
        else { double q = (double)s.LBuildNormNum / confB; s.L2[lp + 0] = (float)(L2r * q); s.L2[lp + 1] = (float)(L2g * q); s.L2[lp + 2] = (float)(q * L2b); }

        // L3 = the raw density-converted input planes at this column (slot eb0, header +4 => index col+4).
        s.L3[lp + 0] = s.inR[col + 4]; s.L3[lp + 1] = s.inG[col + 4]; s.L3[lp + 2] = s.inB[col + 4];
    }

    // TRIGGER. Returns 1 (give up) if too many samples in any of four 9-tap windows fall below the confidence
    // floor (Cfg.DustFloor); else 0 (reconstruct). Windows: the confidence ring confWindow[] read at two
    // horizontal offsets (col - Cfg.TriggerOffsetL and col + Cfg.TriggerOffsetR) and the two gate-history rows
    // gateHistA/gateHistB read straight. The Cfg.TriggerEdgeMode!=0 alternate branch is a small fixed-tap edge
    // test used only for that configuration.
    public static int Trigger(IceState s, int col)
    {
        if (s.rcTrigEdgeMode == 0)
        {
            float floor = s.rcDustFloor;
            int maxBelow = s.rcTargetId;

            int leftCol = col - s.rcTrigOffL;
            int below = 0; for (int k = 0; k < 9; k++) if (s.confWindow[k][leftCol + 4] < floor) below++;
            if (maxBelow < below) return 1;

            below = 0; for (int k = 0; k < 9; k++) if (s.gateHistA[col + k] < floor) below++;
            if (maxBelow < below) return 1;

            int rightCol = s.rcTrigOffR + col;
            below = 0; for (int k = 0; k < 9; k++) if (s.confWindow[k][rightCol + 4] < floor) below++;
            if (maxBelow < below) return 1;

            below = 0; for (int k = 0; k < 9; k++) if (s.gateHistB[col + k] < floor) below++;
            if (maxBelow < below) return 1;

            return 0;
        }
        // edge mode: keep (return 0) only when all four fixed taps clear the edge threshold, else give up.
        if ((s.TriggerEdgeThresh <= s.confWindow[4][col + 2]) && (s.TriggerEdgeThresh <= s.confWindow[6][col + 4]) &&
            (s.TriggerEdgeThresh <= s.confWindow[4][col + 6]) && (s.TriggerEdgeThresh <= s.confWindow[2][col + 4]))
            return 0;
        return 1;
    }

    // the per-row DRIVER (main branch: dpi > 1 and Cfg.DrainFlag == 0). Reset the build cursor, then for every
    // output column decide give-up vs reconstruct and run the chain. The plane pointer-cache is folded into the
    // resolved arrays on IceState, so this is just the loop.
    public static void RunRow(IceState s)
    {
        if (!((1 < s.ReadI(Cfg.Dpi)) && (s.ReadI(Cfg.DrainFlag) == 0))) return;   // drain branch not modelled (streaming interior)
        s.buildCursor = -1;
        s.CacheRecon();   // decode the reconstruction config ONCE for this row (see IceState.CacheRecon)

        int width = s.ReadI(Cfg.ImageWidth);
        bool irMaskGate = (s.ReadI(Cfg.ClampToL3Enable) != 0) || (s.ReadI(Cfg.ClampToL3Flag) != 0);
        for (int col = 0; col < width; col++)
        {
            // give up if the IR-mask confidence is above the floor, or the trigger says so; else reconstruct.
            bool giveUp = (irMaskGate && s.IrGiveUpThresh <= s.mask[col * 4 + 3]) || (Trigger(s, col) != 0);
            if (s.giveUpRecord != null) s.giveUpRecord[col] = (byte)(giveUp ? 1 : 0);   // TEST hook: record the true decision
            if (giveUp) IceCore.GiveUp(s, col + 4);
            else { VBuild(s, col); LBuild(s, col); IceCore.ReconstructColumn(s, col + 4, col + 1, col); }
        }
    }

    // OUTPUT CONVERT, RGB path: copy the three reconstructed output rings (outR/outG/outB) into the caller's
    // three destination row buffers, `count` (= ecc) columns each. This is a straight element-by-element move.
    public static void OutputConvert(IceState s, float[] dstR, float[] dstG, float[] dstB, int count)
    {
        for (int i = 0; i < count; i++) { dstR[i] = s.outR[i]; dstG[i] = s.outG[i]; dstB[i] = s.outB[i]; }
    }
}
