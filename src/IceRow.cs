using System;

// IceRow.cs -- per-row row setup: the ring slot advance and the once-per-frame reset. Pure integer work,
// so unlike the float pipeline there is no fidelity gap here -- it is exact.
//
//   SlotAdvance: reads the row counter (Cfg.RowCounter) + band count (Cfg.BandCount), writes the 32 ring-index
//                fields (config 0xe44..0xec4) the downstream stages index through, and decrements the counter.
//                Four concentric rings: mask mod 2, band mod 3, weight mod 11, gate mod 12.
//   FrameReset:  the fixed config the core reads + the ring write-cursors, reset once per frame.

public static class IceRow
{
    static void WriteI(IceState s, int off, int v) { BitConverter.GetBytes(v).CopyTo(s.config, off); }

    // C# `%` truncates toward zero (may return negative); fix it up with `if (r < 0) r += m;` after every
    // modulo -- the row counter runs 0,-1,-2,... so that branch is really taken.
    static int PosMod(int a, int m) { int r = a % m; if (r < 0) r += m; return r; }

    // per-row slot advance.
    public static void SlotAdvance(IceState s)
    {
        int n = s.ReadI(Cfg.RowCounter);        // row counter
        WriteI(s, Cfg.RowCounter, n - 1);       // counts down one per row
        int bandCount = s.ReadI(Cfg.BandCount); // -> last mod-3 slot

        // mask ring, mod 2
        WriteI(s, 0xe44, PosMod(n + 4, 2));
        WriteI(s, 0xe48, PosMod(n + 5, 2));
        // band ring, mod 3
        WriteI(s, 0xe4c, PosMod(n - 1, 3));
        WriteI(s, 0xe50, PosMod(n,     3));
        WriteI(s, 0xe54, PosMod(n + 1, 3));
        WriteI(s, 0xe58, PosMod(n + 4, 3));
        WriteI(s, 0xe5c, PosMod(n + 5, 3));
        WriteI(s, 0xe60, PosMod(n + 6, 3));
        WriteI(s, 0xe64, PosMod(bandCount - 1 + n, 3));
        // weight ring, mod 11
        WriteI(s, 0xe68, PosMod(n - 1, 11));
        WriteI(s, 0xe6c, PosMod(n,     11));
        WriteI(s, 0xe70, PosMod(n + 1, 11));
        WriteI(s, 0xe74, PosMod(n + 2, 11));
        WriteI(s, 0xe78, PosMod(n + 3, 11));
        WriteI(s, 0xe7c, PosMod(n + 4, 11));
        WriteI(s, 0xe80, PosMod(n + 5, 11));
        WriteI(s, 0xe84, PosMod(n + 6, 11));
        WriteI(s, 0xe88, PosMod(n + 7, 11));
        WriteI(s, 0xe8c, PosMod(n + 8, 11));
        WriteI(s, 0xe90, PosMod(n + 9, 11));
        // gate ring, mod 12
        WriteI(s, 0xe98, PosMod(n - 1,  12));
        WriteI(s, 0xe9c, PosMod(n,      12));
        WriteI(s, 0xea0, PosMod(n + 1,  12));
        WriteI(s, 0xea4, PosMod(n + 2,  12));
        WriteI(s, 0xea8, PosMod(n + 3,  12));
        WriteI(s, 0xeac, PosMod(n + 4,  12));
        WriteI(s, 0xeb0, PosMod(n + 5,  12));
        WriteI(s, 0xeb4, PosMod(n + 6,  12));
        WriteI(s, 0xeb8, PosMod(n + 7,  12));
        WriteI(s, 0xebc, PosMod(n + 8,  12));
        WriteI(s, 0xec0, PosMod(n + 9,  12));
        WriteI(s, 0xec4, PosMod(n + 10, 12));
    }

    // The two static staging rings, as (slots, bytes-per-slot). These pin the buffer allocation the row pump
    // makes (see IceBuffers); FrameReset only records them.
    public const int PyrStageSlots  = 12,    PyrStageBytes  = 0x23280;   // gate-pyramid staging, mod-12 ring
    public const int ProdStageSlots = 11,    ProdStageBytes = 48000;     // product/weight staging, mod-11 ring

    // once-per-frame core reset (the scalar half; the buffer clears are the row pump's arrays).
    public static void FrameReset(IceState s)
    {
        // fixed config the streaming core reads
        WriteI(s, 0xe2c, 0); WriteI(s, 0xe30, 1); WriteI(s, 0xe34, 2);   // input channel routing R/G/B -> 0/1/2
        WriteI(s, 0xe38, 0); WriteI(s, 0xe3c, 3); WriteI(s, 0xe40, 1);
        WriteI(s, Cfg.RowCounter, 0);                                    // row counter starts at 0 (SlotAdvance counts down)
        WriteI(s, Cfg.WarmupRows, 7); WriteI(s, 0xe18, 0); WriteI(s, 0xe1c, 6); WriteI(s, 0xe20, 0);
        WriteI(s, 0xec8, 4);                                             // vertical lookahead margin

        // ring buffer-base/header slots. In the original these hold ring pointers; openICE keeps the rings as
        // managed arrays (see IceBuffers), so nulling them here is retained only for parity and is otherwise
        // superseded by the row pump.
        int[] cursors = { 0xf94, 0xfc8, 0xffc, 0x1030, 0x1060, 0x1090, 0x10bc, 0x10cc,
                          0x10dc, 0x10a0, 0x10ac, 0x10ec, 0x10f4, 0x10fc, 0x1104 };
        foreach (int off in cursors) WriteI(s, off, 0);
    }
}
