/* ice_buffers.c -- the streaming engine's ring / plane / staging buffers. Ported from IceBuffers.cs. */
#include "ice.h"
#include <stdlib.h>

static float **ring(int count, int width, int elemFloats) {
    float **a = (float **)malloc(count * sizeof(float *));
    int i; for (i = 0; i < count; i++) a[i] = (float *)calloc((size_t)width * elemFloats, sizeof(float));
    return a;
}
static void ring_free(float **a, int count) { int i; if (!a) return; for (i = 0; i < count; i++) free(a[i]); free(a); }

IceBuffers *ice_buffers_create(int W) {
    IceBuffers *b = (IceBuffers *)calloc(1, sizeof(IceBuffers));
    int ecc = W + 8;
    b->W = W; b->ecc = ecc; b->ec8 = 4;
    b->inR = ring(12, ecc, 1); b->inG = ring(12, ecc, 1); b->inB = ring(12, ecc, 1);
    b->gateHist = ring(11, ecc, 1);
    b->weight   = ring(11, ecc, 1);
    b->gateCur  = ring(3, W, 1);
    b->mask     = ring(2, W, 4);
    b->pyr      = ring(3, W + 2, 4);
    b->outR = ring(3, ecc, 1); b->outG = ring(3, ecc, 1); b->outB = ring(3, ecc, 1);
    b->L0 = ring(1, W, 3); b->L1 = ring(1, W, 3); b->L2 = ring(1, W, 3); b->L3 = ring(1, W, 3);
    b->pyrStage = ring(12, 36000, 1);
    b->prodStage = ring(11, 12000, 1);
    b->weightLadder = ring(5, ecc, 1); b->productLadder = ring(5, ecc, 1);
    return b;
}

void ice_buffers_free(IceBuffers *b) {
    if (!b) return;
    ring_free(b->inR, 12); ring_free(b->inG, 12); ring_free(b->inB, 12);
    ring_free(b->gateHist, 11); ring_free(b->weight, 11);
    ring_free(b->gateCur, 3); ring_free(b->mask, 2); ring_free(b->pyr, 3);
    ring_free(b->outR, 3); ring_free(b->outG, 3); ring_free(b->outB, 3);
    ring_free(b->L0, 1); ring_free(b->L1, 1); ring_free(b->L2, 1); ring_free(b->L3, 1);
    ring_free(b->pyrStage, 12); ring_free(b->prodStage, 11);
    ring_free(b->weightLadder, 5); ring_free(b->productLadder, 5);
    free(b);
}
