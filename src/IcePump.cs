using System;

// IcePump.cs -- the streaming row driver: it wires the setup, buffers, and per-row stages into one loop and
// emits clean output rows. The driver is counter-based -- it advances the ring window every input row and
// starts emitting once the vertical lookahead has filled.
//
// Per input row:
//   SlotAdvance                                    advance the ring-index fields
//   Ingest                                         density-convert the row into the input planes + IR gate
//   Weight, GateHist, Products, MaskPyr, Transform the front-half builders (in this order)
//   RunRow                                         the back-half reconstruction driver
//   emit (once warmed up)                          copy the output rings out, convert density -> 16-bit
//
// Each builder reads its per-row window as a gather from the rings by the SlotAdvance field values, so there
// is no separate window-setup step. IRref (Cfg.IRref) is a per-image input (the clear-film IR reference); it
// is supplied by the caller (from IceAnalyze) rather than computed here.
//
// Build (x64 -- managed): see openice.cs.

public static class IcePump
{
    static void  WriteI(IceState s, int off, int bits) { BitConverter.GetBytes(bits).CopyTo(s.config, off); }
    static float FloatFromBits(uint bits)              { return BitConverter.ToSingle(BitConverter.GetBytes(bits), 0); }
    static float FloatAt(IceState s, int off)          { return BitConverter.ToSingle(BitConverter.GetBytes(s.ReadI(off)), 0); }

    public delegate void LoadRow(int row, short[] rgbi);       // fills W*4 shorts, RGBI order
    public delegate void EmitRow(int row, byte[] rgb, int n);  // W*6 bytes, 3x16-bit LE

    // Optional test hooks. Probe fires per emitted output row BEFORE reconstruction (pristine back-half inputs);
    // ProbeOut fires per emitted output row AFTER reconstruction (s.outR/outG/outB = reconstructed density,
    // s.L0..L3 = the row's L-planes, s.inR/inG/inB = input planes). Both null in normal operation.
    public static Action<int, IceState> Probe;
    public static Action<int, IceState> ProbeOut;
    public static uint SeedOverride = 0x3045u;   // dither LCG seed; override only for dither-variance tests

    // Optional give-up sink: when non-null, fires once per emitted output row with that row's true per-column
    // give-up decision (giveUp[0..W): 1 = gave up / copied the input through, 0 = reconstructed). This is the
    // engine's ground-truth "did I touch this pixel" record -- no output-vs-input diffing needed. Null normally.
    public static Action<int, byte[]> GiveUpSink;

    public static int Run(LoadRow load, EmitRow emit, int W, int H, int dpi, float irref, float irCrosstalk, int emitRows)
    { return Run(load, emit, W, H, dpi, irref, irCrosstalk, emitRows, null, null); }

    // irref/irCrosstalk are the per-image ingest scalars (fixed defaults supplied by the caller). irCrosstalkArr/
    // irrefarr (optional): per-row adaptive values captured from a reference run. When supplied, openICE uses them
    // per row instead of the constants -- these are normally recomputed from each row's IR statistics, which
    // openICE does not yet reproduce, so the fixed constants are the dominant residual (see openice.cs).
    public static int Run(LoadRow load, EmitRow emit, int W, int H, int dpi, float irref, float irCrosstalk, int emitRows,
                          float[] irCrosstalkArr, float[] irrefarr)
    {
        var s = IceSetup.Create(W, H, dpi);
        int ecc = W + 8, pad = 4, width = W;

        WriteI(s, Cfg.CrosstalkScale, unchecked((int)0x3F800000));   // crosstalk scale = 1.0
        // IR-gate bias: kinds 8/9 use 1.0; KIND 7 uses 0, so its gate runs exactly 1.0 higher (the kind-7 gate
        // history is uniformly +1.0). This is the gate itself, not a compensating IRref shift.
        WriteI(s, Cfg.GateBias, IceSetup.Kind == 7 ? 0 : unchecked((int)0x3F800000));   // gate bias
        // back-half constants
        s.IrGiveUpThresh   = 1f;
        s.LBuildL0Scale    = FloatFromBits(0x3C6D7304);
        s.LBuildL1Scale    = FloatFromBits(0x3D430C31);
        s.LBuildL2Scale    = 0.0625f;
        s.LBuildNormNum    = 1f;
        s.LBuildGateThresh = 0f;
        s.TriggerEdgeThresh = 1f;
        // dither LCG seed: the LCG is only ever advanced, never re-seeded, so this initial value is a constant.
        // It must be set here -- leaving it at 0 desyncs the dither from the very first reconstruction.
        s.ditherLcg = SeedOverride;   // = 0x3045 in normal use; overridable for dither-variance tests

        // front-half constants (from cfg). irCrosstalk/irref are per-image inputs applied per row (see the loop).
        float crosstalkScale = FloatAt(s, Cfg.CrosstalkScale), one = 1f, gateBias = FloatAt(s, Cfg.GateBias);
        float weightBias = FloatAt(s, Cfg.WeightBias), weightSlope = FloatAt(s, Cfg.WeightSlope), weightRamp = FloatAt(s, Cfg.WeightRamp), weightFloor = FloatAt(s, Cfg.WeightFloor);
        float weightCap = 1f, centerScale = 0.0625f, scale5 = FloatFromBits(0x3D430C31), confFloor = 0f, scale9 = FloatFromBits(0x3C6D7304);
        bool use3TapMin = s.ReadI(Cfg.WeightMinRows) < s.ReadI(Cfg.Dpi);

        var buf = new IceBuffers(W);
        s.productStage = buf.pyrStage;                // IceBack indexes productStage[] by the mod-12 fields
        // per-row scratch the back half writes/reads within one row
        s.L0 = new float[ecc * 3]; s.L1 = new float[ecc * 3]; s.L2 = new float[ecc * 3]; s.L3 = new float[ecc * 3];
        s.vLadder0 = new float[ecc * 3]; s.vLadder1 = new float[ecc * 3]; s.vLadder2 = new float[ecc * 3]; s.vLadder3 = new float[ecc * 3];
        s.confWindow = new float[9][];
        if (Probe != null || ProbeOut != null || GiveUpSink != null) s.giveUpRecord = new byte[ecc];   // per-column give-up record

        // density->16-bit output converter -- the EXACT segmented fixed-point inverse of the density LUT (see
        // src_C/ice_pump.c). n = trunc(density+0.5); out = ((int64)t1[(n>>8)&0xff]*t2[n&0xff] >> 20) - 1. The
        // two 256-entry tables are the inverse of the forward LUT split hi/lo byte; the analytic construction
        // here is verified byte-identical to the original's tables (256/256, 65536/65536 end-to-end).
        int[] convT1 = new int[256], convT2 = new int[256];
        BuildConvTables(convT1, convT2);

        var rgbi = new short[W * 4];
        var convR = new float[W]; var convG = new float[W]; var convB = new float[W]; var convIR = new float[W];
        var w9 = new float[9][]; var prevProds = new float[8][];
        var dstR = new float[ecc]; var dstG = new float[ecc]; var dstB = new float[ecc];
        var outb = new byte[W * 6];

        // driver row/emit counters: Cfg.WarmupRows = warmup lead (rows before first emit) = the vertical
        // lookahead (~7). Setup seeds the config counters. We stream H rows then drain the tail.
        int warmup = s.ReadI(Cfg.WarmupRows);          // warmup lead (vertical lookahead)
        int rowsProcessed = 0;
        int outRow = 0;

        for (int step = 0; outRow < emitRows; step++)
        {
            // SlotAdvance
            IceRow.SlotAdvance(s);
            rowsProcessed++;

            // --- ingest: read the next input row and density-convert it, then copy into the ring. Clamp to
            //     the last row while draining the tail. ---
            int inRow = step < H ? step : H - 1;
            // per-row adaptive ingest scalars (irCrosstalk/IRref). When supplied, use them (aligned: the scalars are
            // output-row indexed, so input row inRow uses scalars[inRow - (warmup-1)]); else the steady-state const.
            float irCrosstalkRow = irCrosstalk, irrefRow = irref;
            if (irrefarr != null) { int j = inRow - (warmup - 1); if (j < 0) j = 0; if (j >= irrefarr.Length) j = irrefarr.Length - 1; irCrosstalkRow = irCrosstalkArr[j]; irrefRow = irrefarr[j]; }
            WriteI(s, Cfg.IRref, BitConverter.ToInt32(BitConverter.GetBytes(irrefRow), 0));   // the core reads IRref from config
            load(inRow, rgbi);
            for (int x = 0; x < W; x++)
            {
                convR[x] = s.densityLut[(ushort)rgbi[x * 4 + 0]]; convG[x] = s.densityLut[(ushort)rgbi[x * 4 + 1]];
                convB[x] = s.densityLut[(ushort)rgbi[x * 4 + 2]]; convIR[x] = s.densityLut[(ushort)rgbi[x * 4 + 3]];
            }
            IceFront.Ingest(buf.inR[s.ReadI(0xe98)], buf.inG[s.ReadI(0xe98)], buf.inB[s.ReadI(0xe98)], pad, buf.gateCur[s.ReadI(0xe64)],
                            convR, convG, convB, convIR, width, irCrosstalkRow, crosstalkScale, one, gateBias, irrefRow);   // planeOff=pad: input-plane left pad

            // FIRST ROW (RowCounter==-1): edge-replicate the current input planes across the WHOLE vertical
            // ring (inR/inG/inB slot 0xe98 -> all 11 slots 0xe9c..0xec4), so the reconstruction reads row 0 for the
            // above-frame rows at output rows 0-3 instead of unwritten zeros. (see src_C/ice_pump.c)
            if (s.ReadI(Cfg.RowCounter) == -1)
            {
                int[] inSlots = { 0xe9c, 0xea0, 0xea4, 0xea8, 0xeac, 0xeb0, 0xeb4, 0xeb8, 0xebc, 0xec0, 0xec4 };
                float[] r0 = buf.inR[s.ReadI(0xe98)], gi0 = buf.inG[s.ReadI(0xe98)], b0 = buf.inB[s.ReadI(0xe98)];
                foreach (int sl in inSlots)
                {
                    int d = s.ReadI(sl);
                    Array.Copy(r0, buf.inR[d], ecc); Array.Copy(gi0, buf.inG[d], ecc); Array.Copy(b0, buf.inB[d], ecc);
                }
            }

            // --- the front builders, in order: Weight, GateHist, Products, MaskPyr, Transform ---
            IceFront.Weight(buf.weight[s.ReadI(0xe6c)], buf.gateCur[s.ReadI(0xe50)], pad, width, use3TapMin, irrefRow, weightBias, weightSlope, weightRamp, weightFloor, weightCap);
            // Gate history. FIRST ROW: fill the ENTIRE vertical gatehist ring (all 10 slots 0xe6c..0xe90) with the
            // current row's FRESH gate (gateCur slot 0xe64, NOT the pipeline-delayed 0xe50), so the give-up trigger's
            // oldest rows are valid from output row 0 (else false give-up in rows 0-3). Later rows: normal single slot.
            if (s.ReadI(Cfg.RowCounter) == -1)
            {
                int[] ghSlots = { 0xe6c, 0xe70, 0xe74, 0xe78, 0xe7c, 0xe80, 0xe84, 0xe88, 0xe8c, 0xe90 };
                float[] g0 = buf.gateCur[s.ReadI(0xe64)];
                foreach (int sl in ghSlots) IceFront.GateHist(buf.gateHist[s.ReadI(sl)], g0, pad, width);
            }
            else IceFront.GateHist(buf.gateHist[s.ReadI(0xe6c)], buf.gateCur[s.ReadI(0xe50)], pad, width);

            for (int k = 0; k < 9; k++) w9[k] = buf.weight[s.ReadI(0xe6c + k * 4)];
            for (int k = 0; k < 8; k++) prevProds[k] = buf.prodStage[s.ReadI(0xe70 + k * 4)];
            IceFront.Products(buf.prodStage[s.ReadI(0xe6c)], w9, buf.gateHist[s.ReadI(0xe6c)], prevProds, buf.weightLadder, buf.productLadder, ecc, centerScale);

            IceFront.MaskPyr(buf.mask[s.ReadI(0xe44)], buf.pyr[s.ReadI(0xe58)], buf.weightLadder, buf.productLadder,
                             buf.weight[s.ReadI(0xe7c)], buf.gateHist[s.ReadI(0xe7c)], width, scale5, confFloor, scale9, irrefRow);

            IceFront.Transform(buf.pyrStage[s.ReadI(0xe9c)], buf.inR[s.ReadI(0xe9c)], buf.inG[s.ReadI(0xe9c)], buf.inB[s.ReadI(0xe9c)], buf.weight[s.ReadI(0xe6c)], ecc);

            // --- back half: resolve the driver's planes to this step's ring slots, then reconstruct ---
            s.mask = buf.mask[s.ReadI(0xe48)];
            s.gateCenter = buf.pyr[s.ReadI(0xe5c)]; s.gateUp = buf.pyr[s.ReadI(0xe58)]; s.gateDown = buf.pyr[s.ReadI(0xe60)];
            s.inR = buf.inR[s.ReadI(0xeb0)]; s.inG = buf.inG[s.ReadI(0xeb0)]; s.inB = buf.inB[s.ReadI(0xeb0)];
            s.outR = buf.outR[s.ReadI(0xe5c)]; s.outG = buf.outG[s.ReadI(0xe5c)]; s.outB = buf.outB[s.ReadI(0xe5c)];
            for (int k = 0; k < 9; k++) s.confWindow[k] = buf.gateHist[s.ReadI(0xe70 + k * 4)];
            s.gateHistA = buf.gateHist[s.ReadI(0xe90)]; s.gateHistB = buf.gateHist[s.ReadI(0xe70)];
            if (Probe != null && warmup <= rowsProcessed) Probe(outRow, s);   // pristine back-half inputs, before RunRow mutates them
            IceBack.RunRow(s);

            // --- emit once warmed up: copy the reconstructed density rings out and convert density -> linear
            //     16-bit (invTable). ---
            if (warmup <= rowsProcessed)
            {
                if (ProbeOut != null) ProbeOut(outRow, s);   // post-RunRow: reconstructed output + inputs + L-planes
                // copy count = ecc = W+8 (the ecc count), NOT width=W: RunRow writes the
                // reconstructed density at outR[col+4], and emit reads dstR[x+4], so the last 4 output columns
                // live at dstR[W..W+3]; copying only W leaves them at 0 (a 4-px black right edge). ecc covers them.
                IceBack.OutputConvert(s, dstR, dstG, dstB, ecc);
                for (int x = 0; x < W; x++)
                {
                    int r = Inv16(dstR[x + 4], convT1, convT2), g = Inv16(dstG[x + 4], convT1, convT2), b = Inv16(dstB[x + 4], convT1, convT2);
                    outb[x * 6 + 0] = (byte)(r & 0xFF); outb[x * 6 + 1] = (byte)(r >> 8);
                    outb[x * 6 + 2] = (byte)(g & 0xFF); outb[x * 6 + 3] = (byte)(g >> 8);
                    outb[x * 6 + 4] = (byte)(b & 0xFF); outb[x * 6 + 5] = (byte)(b >> 8);
                }
                emit(outRow, outb, W * 6);
                if (GiveUpSink != null) GiveUpSink(outRow, s.giveUpRecord);   // this row's give-up decision (filled by RunRow)
                outRow++;
            }
        }
        return outRow;
    }

    // Build the two inverse tables: t1[hi]=round(16*2^(hi*256/k2)), t2[lo]=round(65536*2^(lo/k2)),
    // k2=65535/16. These are the fixed-point inverse of the forward density LUT, split by hi/lo byte of the
    // density index. Verified byte-identical to the original's inverse tables.
    static void BuildConvTables(int[] t1, int[] t2)
    {
        double k2 = 65535.0 / 16.0;
        for (int i = 0; i < 256; i++)
        {
            t2[i] = (int)(65536.0 * Math.Pow(2.0, i / k2) + 0.5);
            t1[i] = (int)(16.0 * Math.Pow(2.0, (i * 256) / k2) + 0.5);
        }
    }
    static int Inv16(float d, int[] t1, int[] t2)
    {
        int n = (int)((double)d + 0.5);   // add const_A=0.5 as a double, then truncate toward zero
        if (n < 0) n = 0; else if (n > 65535) n = 65535;
        long prod = (long)t1[(n >> 8) & 0xff] * (uint)t2[n & 0xff];
        return (int)((prod >> 20) - 1) & 0xffff;
    }
}
