using System;

// IceBuffers.cs -- the streaming engine's ring / plane / staging buffers, owned by openICE so the front and
// back half run entirely against managed memory.
//
// Each ring is a single block sliced into `count` sub-buffers of `width * elemFloats` floats
// (ecc = width + 8 header slop; W = image width). The dimensions per ring are listed inline below.
//
// Ring model: each ring is float[slots][len]. Per row, IceRow.SlotAdvance picks which slot each of the ~12
// rows in the working window maps to (mod the slot count); the builders read/write those slots.

public sealed class IceBuffers
{
    public readonly int W, ecc, ec8;

    // object rings (float[slot][col]), shown as (count, width, elemFloats):
    public readonly float[][] inR, inG, inB;   // (12, ecc, 1)  density-converted input planes
    public readonly float[][] gateHist;        // (11, ecc, 1)  edge-padded gate history
    public readonly float[][] weight;          // (11, ecc, 1)  clamped-linear weight ring
    public readonly float[][] gateCur;         // ( 3, W,   1)  current IR gate from ingest
    public readonly float[][] mask;            // ( 2, W,   4)  confidence mask, 4 floats/col
    public readonly float[][] pyr;             // ( 3, W+2, 4)  gate pyramid, 4 floats/col
    public readonly float[][] outR, outG, outB;// ( 3, ecc, 1)  reconstructed output rows
    public readonly float[][] L0, L1, L2, L3;  // ( 1, W, 3)  reconstruction L-planes

    // static staging (separate from the object rings): pyramid staging 12 slots, product staging 11 slots,
    // plus the two 5-tall horizontal-sum ladders (weight partials and product partials).
    public readonly float[][] pyrStage;        // 12 x 36000 floats (3/col)
    public readonly float[][] prodStage;       // 11 x 12000 floats
    public readonly float[][] weightLadder, productLadder;  // the two 5-tall horizontal-sum ladders (5 x ecc each)

    // one ring: `count` slots, each `width * elemFloats` floats.
    static float[][] Ring(int count, int width, int elemFloats)
    {
        var a = new float[count][];
        for (int i = 0; i < count; i++) a[i] = new float[width * elemFloats];
        return a;
    }

    public IceBuffers(int w)
    {
        W = w; ecc = w + 8; ec8 = 4;
        // --- the 15 object rings (count, width, elemFloats) ---
        inR = Ring(12, ecc, 1); inG = Ring(12, ecc, 1); inB = Ring(12, ecc, 1);
        gateHist = Ring(11, ecc, 1);
        weight   = Ring(11, ecc, 1);
        gateCur  = Ring(3, W, 1);
        mask     = Ring(2, W, 4);
        pyr      = Ring(3, W + 2, 4);
        outR = Ring(3, ecc, 1); outG = Ring(3, ecc, 1); outB = Ring(3, ecc, 1);
        L0 = Ring(1, W, 3); L1 = Ring(1, W, 3); L2 = Ring(1, W, 3); L3 = Ring(1, W, 3);
        // --- static staging (separate alloc site) ---
        pyrStage = Ring(12, 36000, 1);
        prodStage = Ring(11, 12000, 1);
        weightLadder = Ring(5, ecc, 1); productLadder = Ring(5, ecc, 1);
    }

    public long TotalBytes()
    {
        long t = 0;
        Action<float[][]> add = r => { foreach (var s in r) t += (long)s.Length * 4; };
        add(inR); add(inG); add(inB); add(gateHist); add(weight); add(gateCur); add(mask); add(pyr);
        add(outR); add(outG); add(outB); add(L0); add(L1); add(L2); add(L3);
        add(pyrStage); add(prodStage); add(weightLadder); add(productLadder);
        return t;
    }
}
