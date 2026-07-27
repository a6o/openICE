/* ice_row.c -- Phase 1: ring slot-advance + once-per-frame reset. Ported from IceRow.cs. Pure integer, exact. */
#include "ice.h"

static int pos_mod(int a, int m) { int r = a % m; if (r < 0) r += m; return r; }

void ice_row_slot_advance(IceState *s) {
    int n = cfg_i(s, Cfg_RowCounter);
    cfg_wi(s, Cfg_RowCounter, n - 1);
    int bandCount = cfg_i(s, Cfg_BandCount);

    cfg_wi(s, 0xe44, pos_mod(n + 4, 2));
    cfg_wi(s, 0xe48, pos_mod(n + 5, 2));
    cfg_wi(s, 0xe4c, pos_mod(n - 1, 3));
    cfg_wi(s, 0xe50, pos_mod(n,     3));
    cfg_wi(s, 0xe54, pos_mod(n + 1, 3));
    cfg_wi(s, 0xe58, pos_mod(n + 4, 3));
    cfg_wi(s, 0xe5c, pos_mod(n + 5, 3));
    cfg_wi(s, 0xe60, pos_mod(n + 6, 3));
    cfg_wi(s, 0xe64, pos_mod(bandCount - 1 + n, 3));
    cfg_wi(s, 0xe68, pos_mod(n - 1, 11));
    cfg_wi(s, 0xe6c, pos_mod(n,     11));
    cfg_wi(s, 0xe70, pos_mod(n + 1, 11));
    cfg_wi(s, 0xe74, pos_mod(n + 2, 11));
    cfg_wi(s, 0xe78, pos_mod(n + 3, 11));
    cfg_wi(s, 0xe7c, pos_mod(n + 4, 11));
    cfg_wi(s, 0xe80, pos_mod(n + 5, 11));
    cfg_wi(s, 0xe84, pos_mod(n + 6, 11));
    cfg_wi(s, 0xe88, pos_mod(n + 7, 11));
    cfg_wi(s, 0xe8c, pos_mod(n + 8, 11));
    cfg_wi(s, 0xe90, pos_mod(n + 9, 11));
    cfg_wi(s, 0xe98, pos_mod(n - 1,  12));
    cfg_wi(s, 0xe9c, pos_mod(n,      12));
    cfg_wi(s, 0xea0, pos_mod(n + 1,  12));
    cfg_wi(s, 0xea4, pos_mod(n + 2,  12));
    cfg_wi(s, 0xea8, pos_mod(n + 3,  12));
    cfg_wi(s, 0xeac, pos_mod(n + 4,  12));
    cfg_wi(s, 0xeb0, pos_mod(n + 5,  12));
    cfg_wi(s, 0xeb4, pos_mod(n + 6,  12));
    cfg_wi(s, 0xeb8, pos_mod(n + 7,  12));
    cfg_wi(s, 0xebc, pos_mod(n + 8,  12));
    cfg_wi(s, 0xec0, pos_mod(n + 9,  12));
    cfg_wi(s, 0xec4, pos_mod(n + 10, 12));
}

void ice_row_frame_reset(IceState *s) {
    int i;
    static const int cursors[] = { 0xf94, 0xfc8, 0xffc, 0x1030, 0x1060, 0x1090, 0x10bc, 0x10cc,
                                   0x10dc, 0x10a0, 0x10ac, 0x10ec, 0x10f4, 0x10fc, 0x1104 };
    cfg_wi(s, 0xe2c, 0); cfg_wi(s, 0xe30, 1); cfg_wi(s, 0xe34, 2);
    cfg_wi(s, 0xe38, 0); cfg_wi(s, 0xe3c, 3); cfg_wi(s, 0xe40, 1);
    cfg_wi(s, Cfg_RowCounter, 0);
    cfg_wi(s, Cfg_WarmupRows, 7); cfg_wi(s, 0xe18, 0); cfg_wi(s, 0xe1c, 6); cfg_wi(s, 0xe20, 0);
    cfg_wi(s, 0xec8, 4);
    for (i = 0; i < (int)(sizeof cursors / sizeof cursors[0]); i++) cfg_wi(s, cursors[i], 0);
}
