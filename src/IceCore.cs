using System;
using System.Runtime.CompilerServices;

// IceCore.cs -- the dust-removal CORE and its helpers: the per-column reconstruction that decides
// whether to rebuild a pixel and, where it does, computes the new value from its neighbours in the
// density domain plus a matched dither.
//
// This file also defines the two things the whole pipeline shares:
//   * Cfg      -- named offsets into the flat scalar-config byte region (so the code reads by NAME,
//                 not by magic hex). The offsets are the layout of the core's config object; the
//                 meaning of each is documented, not guessed.
//   * IceState -- the core's working state (planes, rings-resolved-to-rows, constants, config region).
//
// Two implementation notes shape the arithmetic here:
//   * The algorithm was designed around 80-bit intermediates. C# has no 80-bit type, so this port
//     accumulates composite expressions in `double` (the closest portable stand-in) and rounds to
//     `float` only at each store, keeping operation order verbatim. Any residual difference is at
//     most the last mantissa bit -- a few 16-bit levels, visually identical.
//   * All plane/ring indirection is resolved to managed float[] references on the IceState object
//     before the core runs, rather than raw pointer arithmetic.

// Named offsets into the scalar-config byte region (IceState.config). Reading `s.ReadF(Cfg.DustFloor)`
// documents intent where `s.ReadF(0xf4c)` did not. Every value is the byte offset of that field inside
// the core object; the per-channel reconstruction coefficients form a regular block, so they are
// addressed with IceState.ReconCoef(channel, k) instead of individual names.
public static class Cfg
{
    // geometry / counters
    public const int Dpi              = 0x00C;   // source resolution (also selects lo/hi-res code paths)
    public const int ChannelCount     = 0x3AC;   // active channel count (2 for target 8)
    public const int BandCount        = 0x3B0;   // band count -> last mod-3 gate-pyramid slot
    public const int TargetId         = 0x3B4;   // profile id (8); also the give-up sample cap in Trigger
    public const int RowCounter       = 0xE10;   // per-frame row counter (counts DOWN in SlotAdvance)
    public const int WarmupRows       = 0xE14;   // vertical-lookahead lead: rows before the first emit (7)
    public const int ImageWidth       = 0xED4;   // image width in pixels
    public const int MaxDensityIdx    = 0xD70;   // last density-LUT index (65535), read as u16

    // reconstruction control flags (enable each contribution)
    public const int ReconIrRefAdjust = 0x39C;   // enable the IR-reference density slope on the base planes
    public const int ReconBand0       = 0x3A0;   // enable detail band 0 (L0<-L1)
    public const int ReconBand1       = 0x3A4;   // enable detail band 1 (L1<-L2)
    public const int ReconBand2       = 0x3A8;   // enable detail band 2 (L2<-L3)
    public const int BandLookaheadRows= 0x450;   // BandRange: dpi <= this => use the single-column band
    public const int WeightMinRows    = 0x454;   // Weight: this < dpi => use the 3-tap gate minimum

    // ingest / calibration
    public const int IRref            = 0xF2C;   // clear-film IR reference (per image; written per row by the pump)
    public const int CrosstalkScale   = 0x3C0;   // dye->IR crosstalk scale (1.0)
    public const int GateBias         = 0xF78;   // bias subtracted when forming the IR gate (1.0)

    // weight ramp (clamped-linear IR gate -> weight)
    public const int WeightSlope      = 0xF38;
    public const int WeightRamp       = 0xF3C;
    public const int WeightBias       = 0xF40;
    public const int MaxDensitySum    = 0xF44;   // chans*maxIdx; the give-up blue level when channelCount != 2
    public const int DustFloor        = 0xF4C;   // Trigger's per-sample confidence floor
    public const int WeightFloor      = 0x3C8;   // weight clamp floor (0.02)

    // output / gate
    public const int ClampToL3Enable  = 0xF54;   // 1 for target 8: clamp reconstruction up to the raw input (L3)
    public const int ClampToL3Flag    = 0xF90;   // second half of the clamp/gate enable
    public const int DrainFlag        = 0xF8C;   // set while draining the tail (not modelled in the streaming loop)

    // per-channel dither amounts
    public const int DitherAmtR       = 0xF58;
    public const int DitherAmtG       = 0xF5C;
    public const int DitherAmtB       = 0xF60;

    // per-channel mask overrides (0 => derive the confidence from the mask; nonzero => use directly)
    public const int MaskOverrideR    = 0xF64;
    public const int MaskOverrideG    = 0xF68;
    public const int MaskOverrideB    = 0xF6C;

    // trigger horizontal offsets (u16) and the alternate edge-test mode
    public const int TriggerOffsetL   = 0xF70;
    public const int TriggerOffsetR   = 0xF72;
    public const int TriggerEdgeMode  = 0x398;

    // exposure-scaled per-band stage gains (band k uses StageGain[k])
    public const int StageGain0       = 0xF88;   // band 0 gain (1.0)
    public const int StageGain1       = 0xF84;   // band 1 gain (1.25)
    public const int StageGain2       = 0xF80;   // band 2 gain (1.25)

    // per-channel reconstruction coefficient block: 3 channels x 7 floats, stride 0x1C.
    public const int ReconCoefBase    = 0xED8;
}

// The core's working state. Planes are managed arrays; ring/pointer indirection is resolved to
// named rows before the core runs (gateCenter/gateUp/gateDown are the three gate-pyramid rows of the
// vertical cross; mask/L*/out*/in* are the current row). (`Sdc` = the reconstruction core's scratch state.)
public sealed class IceState
{
    public byte[]  config;                       // scalar config region (>=0x1200 bytes), addressed by Cfg offsets
    public float[] densityLut;                   // density LUT, LUT[i] = 65535*log2(i+1)/16
    public float[] gateCenter, gateUp, gateDown; // gate-pyramid rows (center/up/down of the vertical cross), 4/px
    public float[] mask;                         // confidence mask row, 4 floats/px
    public float[] L0, L1, L2, L3;               // RGB working planes (the L-pyramid), 3 floats/px
    public float[] outR, outG, outB;             // reconstructed output rows, 1 float/px
    public float[] inR, inG, inB;                // input density rows, copied through on the give-up path
    public uint    ditherLcg;                    // dither LCG state

    // --- back-half working state (see IceBack.cs) ---
    // Product staging: the front-half Transform outputs, one ring slot per image row (3 floats/col).
    // VBuild reads the 9 vertical-window slots by ring index as productStage[slot][col*3+ch].
    public float[][] productStage;               // 12 ring slots x ecc*3
    public float[] vLadder0, vLadder1, vLadder2, vLadder3; // VBuild's four cumulative vertical ladders, 3 floats/col
    public int   buildCursor;                    // horizontal build-ahead cursor
    public float[][] confWindow;                 // Trigger's 9-row confidence window
    public float[] gateHistA, gateHistB;         // Trigger's two gate-history rows, ecc wide
    public byte[] giveUpRecord;                  // TEST ONLY (null in normal use): RunRow records give-up(1)/reconstruct(0) per column

    // fixed constants read by the core (supplied by setup). The comments give the target-8 value.
    public float Zero;                // 0.0
    public float RConfGain;           // 2.0   gain applied to the R confidence before its clamp
    public float MaskClampHi;         // 1.0   R-confidence clamp threshold
    public float DitherFloor;         // 0.0   dither result when the band gate does not fire
    public float DitherEnvScale;      // 4.0   parabolic-envelope numerator
    public float DitherBandLoAnchor;  // 0.01  lower density-band anchor (fraction of maxIdx)
    public float DitherBandHiAnchor;  // 0.99  upper density-band anchor
    public float LcgScale;            // 2^-24 LCG output scale
    public float LcgBias;             // -0.5  LCG output bias
    public float LcgNegFixup;         // 0.0   fixup added when the LCG value goes negative (unused; value < 2^24)
    public float IrGiveUpThresh;      // 1.0   driver give-up threshold on the IR-mask confidence
    public float LBuildL0Scale;       // L-build L0 numerator scale
    public float LBuildL1Scale;       // L-build L1 numerator scale
    public float LBuildL2Scale;       // 0.0625 L-build L2 numerator scale
    public float LBuildNormNum;       // 1.0   L-build normalization numerator
    public float LBuildGateThresh;    // 0.0   L-build confidence gate threshold
    public float TriggerEdgeThresh;   // 1.0   Trigger edge-branch threshold

    [MethodImpl(MethodImplOptions.AggressiveInlining)] public int    ReadI  (int off) { return BitConverter.ToInt32 (config, off); }
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public float  ReadF  (int off) { return BitConverter.ToSingle(config, off); }
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public ushort ReadU16(int off) { return BitConverter.ToUInt16(config, off); }

    // one per-channel reconstruction coefficient: channel (R/G/B = 0/1/2), k in 0..6.
    // k=0 is the IR-reference slope; (k=1,2) band 0; (k=3,4) band 1; (k=5,6) band 2.
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public float ReconCoef(int channel, int k) { return ReadF(Cfg.ReconCoefBase + channel * 0x1C + k * 4); }

    // --- decoded reconstruction constants (SPEED). The per-column core read all of these from the byte config
    // on every one of the tens of millions of columns, though none of them changes during a row (only IRref
    // changes, per row). CacheRecon() decodes them once per row (from IceBack.RunRow) into these typed fields;
    // the core then reads the fields. Same bytes, same values -> bit-identical output, far fewer BitConverter calls.
    public int rcIrRefAdjust, rcBand0, rcBand1, rcBand2, rcClampEnable, rcClampFlag;
    public float rcIRref, rcMaskOvR, rcMaskOvG, rcMaskOvB, rcGain0, rcGain1, rcGain2, rcDithR, rcDithG, rcDithB;
    public bool rcDpiLEBand;
    public float[] rcCoef;
    public int rcTrigEdgeMode, rcTargetId, rcTrigOffL, rcTrigOffR, rcMaxIdx;
    public float rcDustFloor;

    public void CacheRecon()
    {
        rcIrRefAdjust = ReadI(Cfg.ReconIrRefAdjust);
        rcBand0 = ReadI(Cfg.ReconBand0); rcBand1 = ReadI(Cfg.ReconBand1); rcBand2 = ReadI(Cfg.ReconBand2);
        rcClampEnable = ReadI(Cfg.ClampToL3Enable); rcClampFlag = ReadI(Cfg.ClampToL3Flag);
        rcIRref = ReadF(Cfg.IRref);
        rcMaskOvR = ReadF(Cfg.MaskOverrideR); rcMaskOvG = ReadF(Cfg.MaskOverrideG); rcMaskOvB = ReadF(Cfg.MaskOverrideB);
        rcGain0 = ReadF(Cfg.StageGain0); rcGain1 = ReadF(Cfg.StageGain1); rcGain2 = ReadF(Cfg.StageGain2);
        rcDithR = ReadF(Cfg.DitherAmtR); rcDithG = ReadF(Cfg.DitherAmtG); rcDithB = ReadF(Cfg.DitherAmtB);
        rcDpiLEBand = ReadI(Cfg.Dpi) <= ReadI(Cfg.BandLookaheadRows);
        rcTrigEdgeMode = ReadI(Cfg.TriggerEdgeMode); rcDustFloor = ReadF(Cfg.DustFloor); rcTargetId = ReadI(Cfg.TargetId);
        rcTrigOffL = ReadU16(Cfg.TriggerOffsetL); rcTrigOffR = ReadU16(Cfg.TriggerOffsetR);
        rcMaxIdx = ReadU16(Cfg.MaxDensityIdx);
        if (rcCoef == null) rcCoef = new float[21];
        for (int c = 0; c < 3; c++) for (int k = 0; k < 7; k++) rcCoef[c * 7 + k] = ReconCoef(c, k);
    }
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public float RC(int channel, int k) { return rcCoef[channel * 7 + k]; }
}

public static class IceCore
{
    // LCG dither noise (double stands in for the original's 80-bit intermediate). Advances the LCG once and
    // returns baseVal + (scaled LCG in [-0.5, 0.5)) * scale.
    static double LcgNoise(IceState s, float baseVal, float scale)
    {
        uint next = s.ditherLcg * 0x7du + 1u;
        s.ditherLcg = next - (next & 0xff000000u);
        int v = (int)s.ditherLcg + 1;
        double f = (double)v;
        if (v < 0) f += (double)s.LcgNegFixup;
        return (double)baseVal + (f * (double)s.LcgScale + (double)s.LcgBias) * (double)scale;
    }

    public static bool NoDither;   // TEST ONLY: skip the random dither draw (keep the deterministic baseline)

    // Per-channel dither amount for a reconstructed density `value`. A zero-mean grain, shaped by a parabolic
    // envelope that peaks mid-band and vanishes at the two density anchors, and applied only when both the value
    // and value+noise stay inside the band. Outside the band it returns the deterministic floor (0).
    static double DitherDelta(IceState s, float value, float amount)
    {
        double result = s.DitherFloor;   // the original keeps the dither result in a DOUBLE stack slot, not float
        if (NoDither) return result;
        ushort maxIdx = (ushort)s.rcMaxIdx;
        uint cap = (uint)maxIdx;

        // TRUNCATE (round-toward-zero): the original sets the x87 rounding mode to round-toward-zero
        // before converting maxIdx*anchor to the band index -- it does NOT round-to-nearest. C#'s (long) cast of a
        // float truncates toward zero, matching. Rounding here shifts bandHi/bandLo, flips dither band membership
        // at saturated pixels, and desyncs the process-global dither LCG for the rest of the image.
        ushort anchorIdx = (ushort)(long)((float)maxIdx * s.DitherBandHiAnchor);
        uint idx = (uint)anchorIdx; if (cap < anchorIdx) idx = cap;
        float bandHi = s.densityLut[idx];

        anchorIdx = (ushort)(long)((float)maxIdx * s.DitherBandLoAnchor);
        idx = (uint)anchorIdx; if (cap < anchorIdx) idx = cap;
        float bandLo = s.densityLut[idx];

        double diff = (double)bandHi - (double)bandLo;
        double envScale = (double)s.DitherEnvScale / (diff * diff);
        if ((value < bandHi) && (bandLo < value))
        {
            // envelope in the original's multiply order: ((bandHi-value)*(value-bandLo)) * envScale, then * lcgNoise.
            // amount*value is float*float in C# (already rounded to float), matching the original's float store on that scale.
            double envelope = (((double)bandHi - (double)value) * ((double)value - (double)bandLo)) * envScale;
            double lcgn = LcgNoise(s, 0f, amount * value);
            double noise = envelope * lcgn;
            double perturbed = (double)value + noise;
            if ((perturbed < (double)bandHi) && ((double)bandLo < perturbed)) result = noise;
        }
        return result;
    }

    // round-to-nearest-even.
    static float RoundHalfEven(float v) { return (float)Math.Round((double)v, MidpointRounding.ToEven); }

    // Local dynamic range (min/max) of one gate-pyramid band over the 5-point cross centred at `gateCol`.
    // `level` selects the band: 0 -> (level1-level0), 1 -> (level2-level1), 2 -> (level3-level2). The cross is
    // center + its left/right columns + up/down rows. At low dpi only the center column is used.
    static void BandRange(IceState s, int level, out float lo, out float hi, int gateCol)
    {
        int b = gateCol * 0x10;
        int off = level * 4;                        // byte offset of the band's low member
        float center = s.gateCenter[(b + off + 4) >> 2] - s.gateCenter[(b + off) >> 2];
        float right  = s.gateCenter[(b + off + 0x14) >> 2] - s.gateCenter[(b + off + 0x10) >> 2];
        float left   = s.gateCenter[(b + off - 0xc) >> 2] - s.gateCenter[(b + off - 0x10) >> 2];
        float down   = s.gateDown  [(b + off + 4) >> 2] - s.gateDown  [(b + off) >> 2];
        float up     = s.gateUp    [(b + off + 4) >> 2] - s.gateUp    [(b + off) >> 2];
        if (s.rcDpiLEBand) { hi = center; lo = center; return; }
        float mx = center; if (center < right) mx = right; if (mx < left) mx = left; if (mx < down) mx = down; if (mx < up) mx = up;
        hi = mx;
        float mn = center; if (right < mn) mn = right; if (left < mn) mn = left; if (down < mn) mn = down; if (up < mn) mn = up;
        lo = mn;
    }

    // give-up copy: pass the input through unchanged. Public so the driver (IceBack) can call it.
    public static void GiveUp(IceState s, int outCol)
    {
        if (s.ReadI(Cfg.ChannelCount) != 2) { s.outR[outCol] = 0f; s.outG[outCol] = 0f; s.outB[outCol] = s.ReadF(Cfg.MaxDensitySum); return; }
        s.outR[outCol] = s.inR[outCol]; s.outG[outCol] = s.inG[outCol]; s.outB[outCol] = s.inB[outCol];
    }

    // Add one detail band's soft-thresholded contribution to a channel accumulator. The detail is the
    // difference between the higher- and lower-resolution L-planes (times the stage gain); it is soft-thresholded
    // against the band's local dynamic range (bandLo/bandHi scaled by two per-(channel,band) coefficients), then
    // weighted by the confidence and added to `acc`. When the band is entirely negative the two coefficients swap
    // (so the threshold follows the sign). Verbatim for all 3 channels x 3 bands.
    // detail/resid/acc kept in DOUBLE. The original evaluates these in 80-bit; C# has no long double, so double is the
    // closest model -- but NOT float (which the earlier port used): float here loses ~24 bits of the accumulator,
    // flips the clamp-to-L3 decision, and desyncs the process-global dither LCG for the rest of the image.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static double AddBandDetail(double acc, double detail, float loCoef, float hiCoef, float bandLo, float bandHi, float conf, float zero)
    {
        if ((bandHi < zero) && (bandLo < zero)) { float t = hiCoef; hiCoef = loCoef; loCoef = t; }
        double resid = zero;
        if (detail <= (double)hiCoef * bandHi) { if (detail < (double)loCoef * bandLo) resid = detail - (double)loCoef * bandLo; }
        else resid = detail - (double)hiCoef * bandHi;
        return acc + (double)conf * resid;
    }

    // one detail band: (hires - lores) * gain, computed in double (the original keeps it 80-bit; never round to float).
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static double Detail(float hires, float lores, float gain) { return ((double)hires - (double)lores) * (double)gain; }

    // the reconstruction CORE for one column. outCol = output col (col+4), gateCol = gate col (col+1),
    // col = L/mask col. Builds the R/G/B reconstructed density from the base plane plus up to three detail
    // bands, and -- if all three channels came out positive -- writes them (dithered, clamped up to the raw
    // input); otherwise gives up and copies the input through.
    public static void ReconstructColumn(IceState s, int outCol, int gateCol, int col)
    {
        int lp = col * 0xc;                          // L-plane byte base (>>2 gives col*3 + channel)
        int mp = col * 0x10;                         // mask byte base (>>2 gives col*4 + channel)
        double baseR = (double)s.L0[lp >> 2], baseG = (double)s.L0[(lp + 4) >> 2], baseB = (double)s.L0[(lp + 8) >> 2];
        if (s.rcIrRefAdjust != 0)
        {
            double irDelta = (double)s.rcIRref - (double)s.gateCenter[(gateCol * 0x10) >> 2];
            baseR += (double)s.RC(0, 0) * irDelta; baseG += (double)s.RC(1, 0) * irDelta; baseB += (double)s.RC(2, 0) * irDelta;
        }
        double accR = baseR, accG = baseG, accB = baseB;
        float bandLo, bandHi;

        if (s.rcBand0 != 0)
        {
            BandRange(s, 0, out bandLo, out bandHi, gateCol);
            float zero = s.Zero;
            // confidence for this band: mask override, else the R mask slot doubled and clamped to {0,1} or itself.
            float conf = s.rcMaskOvR;
            if (conf == zero)
            {
                s.mask[(mp + 4) >> 2] = s.RConfGain * s.mask[(mp + 4) >> 2];
                conf = s.mask[(mp + 4) >> 2];
                if (conf <= s.MaskClampHi) { if (conf < zero) { s.mask[(mp + 4) >> 2] = 0f; conf = s.mask[(mp + 4) >> 2]; } }
                else { s.mask[(mp + 4) >> 2] = 1f; conf = s.mask[(mp + 4) >> 2]; }
            }
            float gain = s.rcGain0;
            accR = AddBandDetail(accR, Detail(s.L1[lp >> 2],       s.L0[lp >> 2],       gain), s.RC(0, 2), s.RC(0, 1), bandLo, bandHi, conf, zero);
            accG = AddBandDetail(accG, Detail(s.L1[(lp + 4) >> 2], s.L0[(lp + 4) >> 2], gain), s.RC(1, 2), s.RC(1, 1), bandLo, bandHi, conf, zero);
            accB = AddBandDetail(accB, Detail(s.L1[(lp + 8) >> 2], s.L0[(lp + 8) >> 2], gain), s.RC(2, 2), s.RC(2, 1), bandLo, bandHi, conf, zero);
        }

        if (s.rcBand1 != 0)
        {
            BandRange(s, 1, out bandLo, out bandHi, gateCol);
            float zero = s.Zero;
            float conf = s.rcMaskOvG;
            if (conf == zero) { conf = s.mask[(mp + 8) >> 2]; if (conf < zero) { s.mask[(mp + 8) >> 2] = 0f; conf = s.mask[(mp + 8) >> 2]; } }
            float gain = s.rcGain1;
            accR = AddBandDetail(accR, Detail(s.L2[lp >> 2],       s.L1[lp >> 2],       gain), s.RC(0, 4), s.RC(0, 3), bandLo, bandHi, conf, zero);
            accG = AddBandDetail(accG, Detail(s.L2[(lp + 4) >> 2], s.L1[(lp + 4) >> 2], gain), s.RC(1, 4), s.RC(1, 3), bandLo, bandHi, conf, zero);
            accB = AddBandDetail(accB, Detail(s.L2[(lp + 8) >> 2], s.L1[(lp + 8) >> 2], gain), s.RC(2, 4), s.RC(2, 3), bandLo, bandHi, conf, zero);
        }

        if (s.rcBand2 != 0)
        {
            BandRange(s, 2, out bandLo, out bandHi, gateCol);
            float zero = s.Zero;
            float conf = s.rcMaskOvB;
            if (conf == zero) { conf = s.mask[(mp + 0xc) >> 2]; conf = conf * conf; }
            float gain = s.rcGain2;
            accR = AddBandDetail(accR, Detail(s.L3[lp >> 2],       s.L2[lp >> 2],       gain), s.RC(0, 6), s.RC(0, 5), bandLo, bandHi, conf, zero);
            accG = AddBandDetail(accG, Detail(s.L3[(lp + 4) >> 2], s.L2[(lp + 4) >> 2], gain), s.RC(1, 6), s.RC(1, 5), bandLo, bandHi, conf, zero);
            accB = AddBandDetail(accB, Detail(s.L3[(lp + 8) >> 2], s.L2[(lp + 8) >> 2], gain), s.RC(2, 6), s.RC(2, 5), bandLo, bandHi, conf, zero);
        }

        if ((s.Zero < (float)accR) && (s.Zero < (float)accG) && (s.Zero < (float)accB))
        {
            if ((s.rcClampEnable == 0) && (s.rcClampFlag == 0))
            {
                // additive output path (not taken for target 8, where clamp-to-L3 is enabled)
                s.outR[outCol] = (float)(DitherDelta(s, (float)accR, s.rcDithR) + accR);
                s.outG[outCol] = (float)(DitherDelta(s, (float)accG, s.rcDithG) + accG);
                s.outB[outCol] = (float)(DitherDelta(s, (float)accB, s.rcDithB) + accB);
                return;
            }
            // clamp-to-L3 path: keep the raw input unless the dithered reconstruction exceeds it. The dither is
            // drawn TWICE (once for the compare, once for the stored value) -- this advances the LCG twice; verbatim.
            double dith = DitherDelta(s, (float)accR, s.rcDithR);
            double outV = (double)s.L3[lp >> 2];
            if (outV < dith + accR) outV = DitherDelta(s, (float)accR, s.rcDithR) + accR;
            s.outR[outCol] = (float)outV;
            dith = DitherDelta(s, (float)accG, s.rcDithG);
            outV = (double)s.L3[(lp + 4) >> 2];
            if (outV < dith + accG) outV = DitherDelta(s, (float)accG, s.rcDithG) + accG;
            s.outG[outCol] = (float)outV;
            dith = DitherDelta(s, (float)accB, s.rcDithB);
            outV = (double)s.L3[(lp + 8) >> 2];
            if (outV < dith + accB) outV = DitherDelta(s, (float)accB, s.rcDithB) + accB;
            s.outB[outCol] = (float)outV;
            return;
        }
        GiveUp(s, outCol);
    }
}
