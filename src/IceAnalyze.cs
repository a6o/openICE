using System;

// IceAnalyze.cs -- the LOW-RES analysis (calibration) pass. The two-pass ICE runs a low-res analysis that primes
// a per-frame calibration buffer, then the full-res reconstruction pass reads it. That buffer's ONLY effect on
// the reconstruction is four fields:
//     calibrated flag = 1, crosstalk, and the two IR-reference stats (float)(xx/xr)=Rref and (float)(xy/xr)=irRefRaw.
// This file computes all four from the low-res RGBI image, BIT-EXACT with the original engine. The managed twin
// of src_C/ice_analyze.c.
//
// x87 FIDELITY. The original runs its x87 in 53-bit (double) precision. So: accumulate sums in DOUBLE and round
// to FLOAT only where the original stores a float (the per-tile crosstalk carriers, the quadrant means/deviations).
// C# double == the original's 53-bit register; C# float == its 32-bit store. Reproduced exactly this way.
//
// GEOMETRY: 8x8 tiles, raster, row-band outer / column-tile inner. ncol=(W-8)/8 column
// tiles (34 for W=281; the short right remainder is dropped); ceil(H/8) row-bands; pixel[r][c] =
// img[min(by+r,H-1)][bx+c] (bottom edge-clamp). Density plane1=LUT[R], plane2=LUT[IR].

public struct IceCalib { public float crosstalk, coeff2, rref, irRefRaw; }

public static class IceAnalyze
{
    const float DENS_FIXUP = 4294967296.0f;   // 2^32, uint->float fixup for IR*IR overflow (IR > 46340)
    const float CT_GATE_LO = -0.2f, CT_GATE_HI = 0.2f;   // ratio gate (path A, target 8)

    // quadrant index orders (stride-8 plane), verbatim for order-faithful summation.
    static readonly int[][] QIDX = new int[][] {
        new int[]{0x1a,0x1b,0x18,0x19,0x12,0x13,0x10,0x11,0xa,0xb,0,1,2,3,8,9},
        new int[]{0x1e,0x1f,0x1c,0x1d,0x16,0x17,0x14,0x15,0xe,0xf,4,5,6,7,0xc,0xd},
        new int[]{0x3a,0x3b,0x38,0x39,0x32,0x33,0x30,0x31,0x2a,0x2b,0x20,0x21,0x22,0x23,0x28,0x29},
        new int[]{0x3e,0x3f,0x3c,0x3d,0x36,0x37,0x34,0x35,0x2e,0x2f,0x24,0x25,0x26,0x27,0x2c,0x2d},
    };

    static float Dens(float[] lut, int maxIdx, int x) { return lut[x > maxIdx ? maxIdx : x]; }

    // d10 (x1/16 quadrant sums, double) + e80 (x1/4 mean) + ea0 (subtract) -> 4 float deviations of an 8x8 plane.
    static void QuadDev(float[] plane, float[] dev)
    {
        float[] quad = new float[4];
        for (int i = 0; i < 4; i++) { double s = 0.0; for (int j = 0; j < 16; j++) s += plane[QIDX[i][j]]; quad[i] = (float)(s * 0.0625); }
        float mean = (float)(((double)quad[0] + quad[1] + quad[2] + quad[3]) * 0.25);
        for (int i = 0; i < 4; i++) dev[i] = quad[i] - mean;
    }

    // Column-tile count = floor(W/8) full 8-wide columns. The tile accumulator sums plain 8x8 tiles at natural
    // positions, e.g. a 186x70 grid for the LS-9000. Was (W-8)/8, one column short for the 9000 (1494/8=186 not
    // 185) -- ~0.9% of the regression sum. Byte-exact for LS-5000 too.
    public static int ColTiles(int W) { return W / 8; }

    // Compute the calibration from the low-res RGBI image (interleaved 16-bit R,G,B,IR), the density LUT
    // (65536 floats, LUT[i]=65535*log2(i+1)/16), and the raw-IR gate (8847.226).
    public static IceCalib Run(ushort[] img, int W, int H, float[] lut, int maxIdx, float irGate)
    {
        int ncol = ColTiles(W), nrow = (H + 7) / 8;
        double xx = 0.0, xy = 0.0, xr = 0.0;          // IR-stat regression (all gated pixels): sum ir^2*densR, ir^2*densIR, ir^2
        float ctDenom = 0.0f, ctNumer = 0.0f;         // crosstalk ratio denominator/numerator (fully-gated tiles), FLOAT per tile
        float[] densR = new float[64], densIR = new float[64], devR = new float[4], devIR = new float[4];

        for (int ri = 0; ri < nrow; ri++) {
            int by = ri * 8;
            for (int ci = 0; ci < ncol; ci++) {
                int bx = ci * 8;
                float sumIR = 0.0f; bool allGated = true;
                for (int r = 0; r < 8; r++) for (int c = 0; c < 8; c++) {
                    int sy = by + r; if (sy > H - 1) sy = H - 1;
                    int idx = (sy * W + bx + c) * 4;
                    int R = img[idx], IR = img[idx + 3];
                    float dR = Dens(lut, maxIdx, R), dIR = Dens(lut, maxIdx, IR);
                    densR[r*8+c] = dR; densIR[r*8+c] = dIR;
                    sumIR = sumIR + (float)IR;
                    if (irGate < (float)IR) {
                        int irSqInt = unchecked((int)((uint)IR * (uint)IR));
                        float irSq = (float)irSqInt; if (irSqInt < 0) irSq += DENS_FIXUP;
                        xx = irSq * (double)dR  + xx;
                        xy = irSq * (double)dIR + xy;
                        xr = (double)irSq       + xr;
                    } else allGated = false;
                }
                if (!allGated) continue;              // crosstalk regresses only fully-gated tiles
                QuadDev(densR, devR); QuadDev(densIR, devIR);
                double accDenom = ctDenom, accNumer = ctNumer, tileWeight = sumIR;
                for (int q = 0; q < 4; q++) {
                    double a = devR[q], b = devIR[q], val, ratio, term;
                    if (a != 0.0) { ratio = b / a; val = (ratio >= CT_GATE_LO && ratio <= CT_GATE_HI) ? a : 0.0; }
                    else          { ratio = 0.0; val = 0.0; }
                    term = ((val * val) * tileWeight) * tileWeight;
                    accDenom = accDenom + term;
                    accNumer = accNumer + ratio * term;
                }
                ctDenom = (float)accDenom; ctNumer = (float)accNumer;   // round to float per tile
            }
        }
        IceCalib o;
        o.crosstalk = (float)((double)ctNumer / (double)ctDenom);
        o.coeff2    = 1.5f * o.crosstalk;
        o.rref      = (float)(xx / xr);
        o.irRefRaw  = (float)(xy / xr);
        return o;
    }

    // Install the calibration into the pump's ingest scalars, exactly as the full-res reconstruction pass does:
    //   irCrosstalk = crosstalk                                            (config 0x44)
    //   IRref       = (irRefRaw - rref*crosstalk) / (1 - crosstalk)         (config 0xf2c, in the original's float op-order)
    // For the two-pass target-8 path the reconstruction runs FROZEN on these values (no per-row adaptation), so
    // this one install is the whole story, and matches the original's frozen scalars bit-exactly. (The LUT-override
    // path does not fire for target 8.)
    public static void Install(IceCalib c, out float irCrosstalk, out float irref)
    {
        irCrosstalk = c.crosstalk;
        // The original keeps g=1/(1-ct) and the products in the x87 80-bit register, rounding to float only at the
        // IRref store. All-float rounds g early and lands ~1 ULP high -- invisible for the LS-5000 but enough to
        // desync the LS-9000's dither. C# has no 80-bit type, so accumulate in double and round to float once (the
        // C build uses long double for the same reason).
        double g = 1.0 / (1.0 - (double)c.crosstalk);
        irref = (float)(((double)(-c.rref) * (double)c.crosstalk + (double)c.irRefRaw) * g);
    }

    // Write the four output-affecting fields into the calibration buffer (rest = the image-independent config base).
    public static void WriteReadFields(byte[] buf, IceCalib c)
    {
        BitConverter.GetBytes(1).CopyTo(buf, 0x40);
        BitConverter.GetBytes(c.crosstalk).CopyTo(buf, 0x44);
        BitConverter.GetBytes(c.coeff2).CopyTo(buf, 0x48);
        BitConverter.GetBytes(c.rref).CopyTo(buf, 0x68);
        BitConverter.GetBytes(c.irRefRaw).CopyTo(buf, 0x6C);
        BitConverter.GetBytes(c.coeff2).CopyTo(buf, 0x1034);
        BitConverter.GetBytes(c.crosstalk).CopyTo(buf, 0x103C);
    }
}
