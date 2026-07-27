/* ice.h -- shared definitions for the openICE C port.
 *
 * Ported 1:1 from openICE/src (C#). Naming and structure mirror the C# so the two can be diffed. The one
 * deliberate departure: the reconstruction core (ice_core.c) accumulates in `real_t`, which is 80-bit
 * `long double` on 32-bit x87 -- the faithful model of the original's registers -- unless ICE_DOUBLE_CORE is
 * defined, in which case it is `double` and the whole tool reproduces the C# build byte-for-byte.
 *
 * The scalar config region is a flat byte block addressed by the named Cfg_* offsets, exactly as in the C#
 * IceState.  x86 is little-endian and tolerant of unaligned access, matching C#'s BitConverter.
 */
#ifndef ICE_H
#define ICE_H

#include <stddef.h>
#include <string.h>

/* ---- core accumulator precision -------------------------------------------------------------------------- */
#ifdef ICE_DOUBLE_CORE
typedef double real_t;          /* reproduces the C# `double` build exactly */
#else
typedef long double real_t;     /* 80-bit x87: the model faithful to the original */
#endif

/* ---- named config offsets (== C# Cfg) -------------------------------------------------------------------- */
#define Cfg_Dpi               0x00C
#define Cfg_ChannelCount      0x3AC
#define Cfg_BandCount         0x3B0
#define Cfg_TargetId          0x3B4
#define Cfg_RowCounter        0xE10
#define Cfg_WarmupRows        0xE14
#define Cfg_ImageWidth        0xED4
#define Cfg_MaxDensityIdx     0xD70

#define Cfg_ReconIrRefAdjust  0x39C
#define Cfg_ReconBand0        0x3A0
#define Cfg_ReconBand1        0x3A4
#define Cfg_ReconBand2        0x3A8
#define Cfg_BandLookaheadRows 0x450
#define Cfg_WeightMinRows     0x454

#define Cfg_IRref             0xF2C
#define Cfg_CrosstalkScale    0x3C0
#define Cfg_GateBias          0xF78

#define Cfg_WeightSlope       0xF38
#define Cfg_WeightRamp        0xF3C
#define Cfg_WeightBias        0xF40
#define Cfg_MaxDensitySum     0xF44
#define Cfg_DustFloor         0xF4C
#define Cfg_WeightFloor       0x3C8

#define Cfg_ClampToL3Enable   0xF54
#define Cfg_ClampToL3Flag     0xF90
#define Cfg_DrainFlag         0xF8C

#define Cfg_DitherAmtR        0xF58
#define Cfg_DitherAmtG        0xF5C
#define Cfg_DitherAmtB        0xF60

#define Cfg_MaskOverrideR     0xF64
#define Cfg_MaskOverrideG     0xF68
#define Cfg_MaskOverrideB     0xF6C

#define Cfg_TriggerOffsetL    0xF70
#define Cfg_TriggerOffsetR    0xF72
#define Cfg_TriggerEdgeMode   0x398

#define Cfg_StageGain0        0xF88
#define Cfg_StageGain1        0xF84
#define Cfg_StageGain2        0xF80

#define Cfg_ReconCoefBase     0xED8

#define CFG_BYTES 0x1200

/* ---- the core working state (== C# IceState) ------------------------------------------------------------- */
typedef struct IceState {
    unsigned char config[CFG_BYTES]; /* scalar config region, addressed by Cfg_* */
    float *densityLut;               /* [65536], LUT[i] = 65535*log2(i+1)/16 */
    float *gateCenter, *gateUp, *gateDown;  /* gate-pyramid rows, 4/px */
    float *mask;                     /* confidence mask row, 4/px */
    float *L0, *L1, *L2, *L3;        /* RGB L-pyramid planes, 3/px */
    float *outR, *outG, *outB;       /* reconstructed output rows, 1/px */
    float *inR, *inG, *inB;          /* input density rows, 1/px */
    unsigned int ditherLcg;          /* dither LCG state */

    float **productStage;            /* 12 ring slots x ecc*3 */
    float *vLadder0, *vLadder1, *vLadder2, *vLadder3;  /* VBuild cumulative ladders, 3/col */
    int buildCursor;                 /* horizontal build-ahead cursor */
    float *confWindow[9];            /* Trigger 9-row confidence window */
    float *gateHistA, *gateHistB;    /* Trigger two gate-history rows */
    unsigned char *giveUpRecord;     /* optional per-column give-up record (NULL normally) */

    /* fixed constants read by the core */
    float Zero, RConfGain, MaskClampHi;
    float DitherFloor, DitherEnvScale, DitherBandLoAnchor, DitherBandHiAnchor;
    float LcgScale, LcgBias, LcgNegFixup;
    float IrGiveUpThresh;
    float LBuildL0Scale, LBuildL1Scale, LBuildL2Scale, LBuildNormNum, LBuildGateThresh;
    float TriggerEdgeThresh;
} IceState;

/* ---- config accessors (little-endian, unaligned-safe via memcpy) ----------------------------------------- */
static inline int    cfg_i  (const IceState *s, int off) { int v;            memcpy(&v, s->config + off, 4); return v; }
static inline float  cfg_f  (const IceState *s, int off) { float v;          memcpy(&v, s->config + off, 4); return v; }
static inline unsigned short cfg_u16(const IceState *s, int off) { unsigned short v; memcpy(&v, s->config + off, 2); return v; }
static inline void   cfg_wi (IceState *s, int off, int v)            { memcpy(s->config + off, &v, 4); }
static inline void   cfg_wf (IceState *s, int off, float v)          { memcpy(s->config + off, &v, 4); }
static inline void   cfg_wu16(IceState *s, int off, unsigned short v){ memcpy(s->config + off, &v, 2); }
static inline void   cfg_wbits(IceState *s, int off, unsigned int b) { memcpy(s->config + off, &b, 4); }
static inline float  recon_coef(const IceState *s, int ch, int k)    { return cfg_f(s, Cfg_ReconCoefBase + ch * 0x1C + k * 4); }

static inline float  float_from_bits(unsigned int b) { float f; memcpy(&f, &b, 4); return f; }
static inline unsigned int bits_of_float(float f)    { unsigned int b; memcpy(&b, &f, 4); return b; }
static inline int    bits_of_float_i(float f)        { int b; memcpy(&b, &f, 4); return b; }

/* ---- ring / staging buffers (== C# IceBuffers) ----------------------------------------------------------- */
typedef struct IceBuffers {
    int W, ecc, ec8;
    float **inR, **inG, **inB;   /* (12, ecc) */
    float **gateHist;            /* (11, ecc) */
    float **weight;              /* (11, ecc) */
    float **gateCur;             /* (3,  W)   */
    float **mask;                /* (2,  W*4) */
    float **pyr;                 /* (3,  (W+2)*4) */
    float **outR, **outG, **outB;/* (3,  ecc) */
    float **L0, **L1, **L2, **L3;/* (1,  W*3) */
    float **pyrStage;            /* (12, 36000) */
    float **prodStage;           /* (11, 12000) */
    float **weightLadder, **productLadder; /* (5, ecc) */
} IceBuffers;

IceBuffers *ice_buffers_create(int W);
void        ice_buffers_free(IceBuffers *b);

/* ---- calibration ----------------------------------------------------------------------------------------- */
typedef struct { float crosstalk, coeff2, rref, irRefRaw; } IceCalib;
IceCalib ice_analyze_run(const unsigned short *img, int W, int H, const float *lut, int maxIdx, float irGate);
void     ice_analyze_install(IceCalib c, float *irCrosstalk, float *irref);

/* ---- streaming per-row calibration estimator -------------------------------------------------------------
 * Reproduces the per-row {ct, rref, irRefRaw} bit-exactly (verified 5959/5959 vs the original).
 * Call once per INPUT row before ingest; the returned (ct, IRref) replace the fixed low-res calibration. */
typedef struct IceStreamCalib IceStreamCalib;
IceStreamCalib *ice_streamcalib_create(int W, const float *lut);
void            ice_streamcalib_free(IceStreamCalib *sc);
/* processes input row y's RGBI samples; outputs the updated grid[1] fields (raw, pre-install). */
void ice_streamcalib_row(IceStreamCalib *sc, int y, const short *rgbi, float *ctOut, float *rrefOut, float *irRefRawOut);

/* ---- setup / row ----------------------------------------------------------------------------------------- */
IceState *ice_setup_create(int W, int H, int dpi);
void      ice_state_free(IceState *s);
void      ice_row_slot_advance(IceState *s);
void      ice_row_frame_reset(IceState *s);

/* ---- front half ------------------------------------------------------------------------------------------ */
void ice_front_weight(float *weightOut, const float *gate, int pad, int width, int use3TapMin,
                      float irref, float bias, float slope, float ramp, float floorv, float cap);
void ice_front_ingest(float *planeR, float *planeG, float *planeB, int planeOff, float *gate,
                      const float *convR, const float *convG, const float *convB, const float *convIR,
                      int width, float irCrosstalk, float crosstalkScale, float one, float gateBias, float irref);
void ice_front_gatehist(float *hist, const float *gate, int pad, int width);
void ice_front_transform(float *stg, const float *iR, const float *iG, const float *iB, const float *w, int ecc);
void ice_front_products(float *gateWeight, float *const *w9, const float *gateCur, float *const *prevProds,
                        float **weightLadder, float **productLadder, int ecc, float centerScale);
void ice_front_maskpyr(float *mask, float *pyr, float *const *weightLadder, float *const *productLadder,
                       const float *weightCenter, const float *irRatio, int width,
                       float scale5, float confFloor, float scale9, float irref);

/* ---- back half + core ------------------------------------------------------------------------------------ */
void ice_back_vbuild(IceState *s, int col);
void ice_back_lbuild(IceState *s, int col);
int  ice_back_trigger(IceState *s, int col);
void ice_back_runrow(IceState *s);
void ice_back_output_convert(IceState *s, float *dstR, float *dstG, float *dstB, int count);
void ice_core_giveup(IceState *s, int outCol);
void ice_core_reconstruct(IceState *s, int outCol, int gateCol, int col);

/* ---- pump ------------------------------------------------------------------------------------------------ */
typedef void (*load_row_fn)(void *ctx, int row, short *rgbi);
typedef void (*emit_row_fn)(void *ctx, int row, unsigned char *rgb, int n);
typedef void (*giveup_sink_fn)(void *ctx, int row, const unsigned char *gu);

int ice_pump_run(load_row_fn load, void *load_ctx, emit_row_fn emit, void *emit_ctx,
                 int W, int H, int dpi, float irref, float irCrosstalk, int emitRows,
                 giveup_sink_fn giveup, void *giveup_ctx);

#endif /* ICE_H */
