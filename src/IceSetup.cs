using System;

// IceSetup.cs -- setup: build a fully-populated IceState (scalar config, density LUT, and the fixed constants
// the core reads) from the image geometry.
//
// Each value is either [DERIVED] from geometry / a formula, or [FIXED] -- a target-8 constant written as its
// exact 32-bit pattern. The [FIXED] values are scanner-profile coefficients that are invariant for target 8;
// they are embedded as exact bit patterns so the arithmetic downstream is reproducible.
//
// The ring / plane / staging buffers are sized from W and owned separately (see IceBuffers); this file only
// fills the scalar config, the density LUT, and the constants. Config fields are written by NAME (Cfg.*)
// where one exists; the remaining bare-hex offsets are structural (ring seeds, intermediate stores).

public static class IceSetup
{
    // ICE quality: false = Normal (default), true = Fine. Set by openice.cs's -fine. (See docs/parameters.md.)
    public static bool Fine = false;

    // Reconstruction target ("kind"): which scanner reconstruction profile to use. 8 = LS-5000 (default), 7/9 = the
    // two other targets. Only the ReconCoefBase matrix and BandLookaheadRows differ between kinds. Set by
    // openice.cs's -kind. (This C# build is the readable reference; use the C build when byte-exactness matters.)
    public static int Kind = 8;

    // [kind index 0=7,1=8,2=9][channel R,G,B][IR-slope k + 3 bands x {lo,hi}] -- exact 32-bit float patterns.
    static readonly uint[][][] ReconCoefByKind = {
        new[] {  // kind 7
            new uint[]{0x3F8CCCCD,0x3FAE147B,0x3FA8F5C3,0x3FAF5C29,0x3FA66666,0x3FAB851F,0x3FA00000},
            new uint[]{0x3F8CCCCD,0x3FAF5C29,0x3FA66666,0x3FACCCCD,0x3FA51EB8,0x3FA66666,0x3F9EB852},
            new uint[]{0x3F8CCCCD,0x3FAB851F,0x3FA00000,0x3FA8F5C3,0x3FA00000,0x3FA00000,0x3F9AE148} },
        new[] {  // kind 8 (LS-5000, default)
            new uint[]{0x3F8CCCCD,0x3F9AE148,0x3F8B851F,0x3F95C28F,0x3F8A3D71,0x3F851EB8,0x3F75C28F},
            new uint[]{0x3F8CCCCD,0x3F9D70A4,0x3F90A3D7,0x3F91EB85,0x3F866666,0x3F6E147B,0x3F570A3D},
            new uint[]{0x3F8CCCCD,0x3F90A3D7,0x3F851EB8,0x3F8A3D71,0x3F828F5C,0x3F7851EC,0x3F63D70A} },
        new[] {  // kind 9
            new uint[]{0x3F800000,0x400D70A4,0x4005C28F,0x400AE148,0x40051EB8,0x40028F5C,0x3FFAE148},
            new uint[]{0x3F800000,0x400EB852,0x400851EC,0x4008F5C3,0x40033333,0x3FF70A3D,0x3FEB851F},
            new uint[]{0x3F800000,0x400851EC,0x40028F5C,0x40051EB8,0x400147AE,0x3FFC28F6,0x3FF1EB85} }
    };
    static readonly int[] BandLookaheadByKind = { 950, 1600, 2500 };   // kinds 7, 8, 9
    static int KindIndex() { int i = Kind - 7; return (i < 0 || i > 2) ? 1 : i; }

    static void WriteI   (IceState s, int off, int v)      { BitConverter.GetBytes(v).CopyTo(s.config, off); }
    static void WriteBits(IceState s, int off, uint bits)  { BitConverter.GetBytes(bits).CopyTo(s.config, off); }   // exact float bits
    static void WriteF   (IceState s, int off, float v)    { BitConverter.GetBytes(v).CopyTo(s.config, off); }
    static void WriteU16 (IceState s, int off, ushort v)   { BitConverter.GetBytes(v).CopyTo(s.config, off); }
    static float FloatFromBits(uint bits)                  { return BitConverter.ToSingle(BitConverter.GetBytes(bits), 0); }
    static uint  BitsOf(float f)                           { return BitConverter.ToUInt32(BitConverter.GetBytes(f), 0); }

    // density-anchor helpers. `(int)` truncates toward zero. All three clamp the index to maxIdx.
    static int Idx(float anchor, int maxIdx) { int i = (int)((double)anchor * (double)maxIdx); return i > maxIdx ? maxIdx : i; }
    // LUT[min(max, trunc(anchor*max))] - LUT[max]  (density difference; subtract kept in double)
    static float LutSub(IceState s, float anchor, int maxIdx) { return (float)((double)s.densityLut[Idx(anchor, maxIdx)] - (double)s.densityLut[maxIdx]); }
    // LUT[min(max, trunc(anchor*max))]  (the dust-floor density itself, no subtract)
    static float LutAt (IceState s, float anchor, int maxIdx) { return s.densityLut[Idx(anchor, maxIdx)]; }
    // IRref bias: the density-difference branch on the region anchor vs the fixed statics.
    static float BiasParam(IceState s, float fv1, int maxIdx, double a0, double a4)
    {
        const double a9c = 2.0;
        if (fv1 <= a0) return (a4 < fv1) ? LutSub(s, fv1, maxIdx) : 0f;
        if (a9c <= fv1) return (a4 < fv1) ? LutSub(s, fv1, maxIdx) : 0f;
        double g = a9c - (double)fv1;
        return (float)((double)s.densityLut[maxIdx] - (double)s.densityLut[Idx((float)g, maxIdx)]);   // reversed subtract
    }

    public static IceState Create(int W, int H, int dpi)
    {
        var s = new IceState();
        s.config = new byte[0x1200];

        // ---- geometry / config  [DERIVED from cfg + W] ----
        WriteI(s, Cfg.Dpi, dpi);
        WriteI(s, 0x010, 2);
        WriteI(s, Cfg.TriggerEdgeMode, 0);
        WriteI(s, Cfg.ReconIrRefAdjust, 1); WriteI(s, Cfg.ReconBand0, 1); WriteI(s, Cfg.ReconBand1, 1); WriteI(s, Cfg.ReconBand2, 1);   // per-channel gate coeff flags (IR=1)
        // Cfg.TargetId (0x3B4) is a MISNOMER: NOT the reconstruction kind -- the core reads 8 for ALL kinds 7/8/9.
        // It is the give-up sample cap (maxBelow in the trigger). Writing Kind made -kind 7 give up at >7 / -kind 9
        // at >9, flipping the give-up decision on many pixels. Kind-invariant: always 8.
        WriteI(s, Cfg.ChannelCount, 2); WriteI(s, Cfg.BandCount, 0); WriteI(s, Cfg.TargetId, 8);
        WriteI(s, 0xEC8, 4);              // vertical lookahead margin
        WriteI(s, 0xECC, W + 8);
        WriteI(s, 0xED0, W + 4);
        WriteI(s, Cfg.ImageWidth, W);
        WriteI(s, Cfg.RowCounter, 0);     // image-derived accumulator (seed 0)
        WriteI(s, Cfg.ClampToL3Enable, 1); WriteI(s, Cfg.DrainFlag, 0); WriteI(s, Cfg.ClampToL3Flag, 0);

        // row / ring config (target-8 fixed) [FIXED]
        WriteI(s, Cfg.WarmupRows, 7); WriteI(s, 0xE1C, 6); WriteI(s, 0xE28, 2); WriteI(s, 0xE3C, 3); WriteI(s, 0xE40, 1);
        WriteI(s, 0xE2C, 0); WriteI(s, 0xE30, 1); WriteI(s, 0xE34, 2);   // input channel routing R/G/B -> 0/1/2 (ingest reads these)
        // initial ring slots 0xe44..0xec4 are all 0 -- a zeroed cfg already has them.
        WriteU16(s, Cfg.MaxDensityIdx, 65535); WriteU16(s, 0xD72, 0);   // maxIdx
        WriteI(s, Cfg.BandLookaheadRows, BandLookaheadByKind[KindIndex()]); WriteI(s, Cfg.WeightMinRows, 550); WriteI(s, 0x458, 1);   // band/lookahead window (per reconstruction kind)

        // ---- density LUT: 65536 entries  [FORMULA] LUT[i] = 65535 * log2(i+1) / 16 ----
        // (computed first: the per-scan parameter formulas below read it.)
        s.densityLut = new float[65536];
        double k = 65535.0 / (16.0 * Math.Log(2.0));
        for (int i = 0; i <= 65535; i++)
            s.densityLut[i] = (float)(k * Math.Log(i + 1));

        // ---- scalar constants that are true constants (not scan-derived) ----
        WriteBits(s, 0x3BC, 0x00000000);   // 0.0
        WriteBits(s, Cfg.WeightFloor, 0x3CA3D70A);   // 0.02   weight floor
        WriteBits(s, Cfg.IRref, 0x00000000);         // 0.0    IRref seed (supplied by the pump per-image)
        WriteBits(s, Cfg.MaskOverrideR, 0); WriteBits(s, Cfg.MaskOverrideG, 0); WriteBits(s, Cfg.MaskOverrideB, 0);   // mask overrides off
        WriteU16(s, Cfg.TriggerOffsetL, 4); WriteU16(s, Cfg.TriggerOffsetR, 4); WriteU16(s, 0xF74, 4); WriteU16(s, 0xF76, 4);

        // The three per-channel dither amounts, as exact 32-bit patterns (LS-5000 target-8, scan-invariant).
        WriteBits(s, Cfg.DitherAmtR, 0x3C75C28F);   // 0.015   dither R
        WriteBits(s, Cfg.DitherAmtG, 0x3C75C28F);   // 0.015   dither G
        WriteBits(s, Cfg.DitherAmtB, 0x3CCCCCCD);   // 0.025   dither B

        // Exposure-scaled stage gains (from the host exposure setting, external to the core -- kept fixed).
        WriteBits(s, Cfg.StageGain2, 0x3FA00000);   // 1.25   band 2 gain
        WriteBits(s, Cfg.StageGain1, 0x3FA00000);   // 1.25   band 1 gain
        WriteBits(s, Cfg.StageGain0, 0x3FA00000);   // 1.25   band 0 gain (Normal uses 1.25, not 1.0)
        WriteI(s, Cfg.ClampToL3Enable, 1);          // clamp-to-L3 output path enabled

        // ICE Fine (`-fine`). Between Normal and Fine, the only reconstruction fields that change are the three
        // stage gains (1.25 -> 1.0) and the L3 clamp (on -> off). Setting just these reproduces Fine output
        // byte-exactly. (Fine also zeroes an unread word at 0xF50 and a 192-float table at 0x94; neither affects
        // the reconstruction output.)
        if (Fine || Environment.GetEnvironmentVariable("ICE_FINE") != null)
        {
            WriteBits(s, Cfg.StageGain2, 0x3F800000); WriteBits(s, Cfg.StageGain1, 0x3F800000); WriteBits(s, Cfg.StageGain0, 0x3F800000);   // 1.0
            WriteI(s, Cfg.ClampToL3Enable, 0);
        }

        // ---- per-channel 7-float block at ReconCoefBase (R,G,B): reconstruction coefficients selected by the
        // reconstruction target (Kind, default 8 = LS-5000), at config 0xED8..0xF28; IR-slope k + 3 bands
        // x {loCoef,hiCoef}. Kinds 7/9 are the two other targets (see ReconCoefByKind above).
        uint[][] pc = ReconCoefByKind[KindIndex()];
        for (int c = 0; c < 3; c++)
            for (int i = 0; i < 7; i++)
                WriteBits(s, Cfg.ReconCoefBase + c*0x1C + i*4, pc[c][i]);

        // ---- per-scan parameters: the weight-ramp / bias / dust-floor the pipeline runs on. Each is DERIVED
        // from a scanner-profile anchor (a threshold in [0,1]) via the density LUT. The anchors are the target-8
        // scan config; the truncation `(int)` and the slope kept in double across the store follow the same
        // arithmetic modelling as IceFront/IceBack, so f3c rounds to 1.0 exactly.
        int chans   = 2;            // channel count
        int maxIdx  = 65535;
        double statA = 1.0, statB = 0.0;   // fixed statics (1.0 / 0.0)
        float f30 = LutSub(s, 0.85f,  maxIdx);   // slope region anchor
        // The weight-ramp anchor 0xF30 is per-kind: c4701ac0 (-960.418) for kinds 7/8 but
        // c4702180 (-960.523) for kind 9. WeightSlope=1/f30 follows.
        if (Kind == 9) f30 = FloatFromBits(0xC4702180);
        float f34 = LutSub(s, 1.0f,   maxIdx);   // = LUT[max]-LUT[max] = 0
        float f40 = BiasParam(s, 0.98f, maxIdx, statA, statB);   // IRref bias
        float f4c = LutAt (s, 0.065f, maxIdx);   // dust floor
        WriteBits(s, 0xF30, BitsOf(f30));
        WriteBits(s, 0xF34, BitsOf(f34));
        double fv5 = (double)f30 - (double)f34;                 // kept in double across the store
        if (fv5 == statB) { WriteF(s, Cfg.WeightSlope, -(float)maxIdx); WriteF(s, Cfg.WeightRamp, (float)statA); }
        else { double slope = statA / fv5; WriteF(s, Cfg.WeightSlope, (float)slope); WriteF(s, Cfg.WeightRamp, (float)(slope * (double)f30)); }
        WriteF(s, Cfg.WeightBias, f40);
        WriteF(s, Cfg.MaxDensitySum, (float)((float)chans * (float)maxIdx));     // chans * maxIdx = 131070
        WriteF(s, 0xF48, 0f);                                                    // 0.0
        WriteF(s, Cfg.DustFloor, f4c);

        // ---- fixed constants the core reads directly [exact bits] ----
        s.Zero               = 0f;
        s.RConfGain          = 2f;
        s.MaskClampHi        = 1f;
        s.DitherFloor        = 0f;
        s.DitherEnvScale     = 4f;
        // The dither BAND is per-engine: kind 7 runs the odd reconstruction engine with a narrower band
        // (0.04/0.96) vs 0.01/0.99 for kinds 8/9. Invisible dither-free, but band membership gates the frame-
        // global dither LCG, so the wrong band desyncs the whole dithered frame on the LS-9000.
        if (Kind == 7) {
            s.DitherBandLoAnchor = FloatFromBits(0x3D23D70A);   // 0.04
            s.DitherBandHiAnchor = FloatFromBits(0x3F75C28F);   // 0.96
        } else {
            s.DitherBandLoAnchor = FloatFromBits(0x3C23D70A);   // 0.01
            s.DitherBandHiAnchor = FloatFromBits(0x3F7D70A4);   // 0.99
        }
        s.LcgScale           = FloatFromBits(0x337FFFFF);   // 2^-24
        s.LcgBias            = -0.5f;
        s.LcgNegFixup        = 0f;

        return s;
    }
}
