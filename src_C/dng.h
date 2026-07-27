/* dng.h -- read a 4-channel RGBI DNG/TIFF, write a 3-channel RGB DNG. Ported from dng.cs.
 * The output is built by copying the source's tags, so mirroring its structure is safer than inventing one. */
#ifndef DNG_H
#define DNG_H

#include <stdio.h>

/* tag numbers we reason about */
#define DNG_NEW_SUBFILE 254
#define DNG_WIDTH 256
#define DNG_LENGTH 257
#define DNG_BITS 258
#define DNG_COMPRESSION 259
#define DNG_PHOTOMETRIC 262
#define DNG_STRIP_OFFSETS 273
#define DNG_SAMPLES 277
#define DNG_ROWS_PER_STRIP 278
#define DNG_STRIP_COUNTS 279
#define DNG_MIN_SAMPLE 280
#define DNG_MAX_SAMPLE 281
#define DNG_XRES 282
#define DNG_YRES 283
#define DNG_PLANAR 284
#define DNG_SOFTWARE 305
#define DNG_SUBIFDS 330
#define DNG_EXIF_IFD 34665
#define DNG_COLOR_MATRIX1 50721
#define DNG_FORWARD_MATRIX1 50725
#define DNG_PHOTOMETRIC_LINEARRAW 34892

typedef struct {
    unsigned short Tag, Type;
    unsigned int Count;
    unsigned char *Data;
    int DataLen;
} TiffTag;

typedef struct {
    TiffTag *items;
    int count, cap;
} TagList;

typedef struct {
    unsigned char *Raw;
    long RawLen;
    int Le;
    long Ifd0, ImageIfd;
    TagList Ifd0Tags, ImageTags;
    int W, H, Samples, Dpi, RowsPerStrip;
    int DpiFromFile;
    char Info[320];
    long *stripOff;
    int stripCount;
    int rowBytes;
} Dng;

typedef struct {
    FILE *fs;
    int le;
    long ifd0PtrPos;
    long DataPos, DataLen;
} DngWriter;

extern char dng_error[512];   /* last error message */

int  tiff_type_size(int t);

/* reader */
Dng *dng_open(const char *path);                 /* NULL on error (see dng_error) */
void dng_free(Dng *d);
int  dng_has_ir(const Dng *d);
int  dng_make_map(const Dng *d, const char *order, int map[4]);   /* 0 ok, -1 error */
void dng_load_row(const Dng *d, int row, const int map[4], short *dst);  /* W*4 shorts, RGBI */
unsigned char *dng_thumbnail(const Dng *d, int *outLen);          /* malloc'd or NULL */

/* tag lists */
void     tl_init(TagList *t);
void     tl_free(TagList *t);
void     tl_copy(TagList *dst, const TagList *src);
TiffTag *tl_get(TagList *t, int tag);
long     tl_scalar(TagList *t, int tag, long dflt);
void     tl_put(TagList *t, TiffTag tag);        /* replace-or-add, keeps ascending */
void     tl_drop(TagList *t, int tag);
TiffTag  tag_make(int tag, int type, const unsigned char *data, int len);  /* copies data */

/* writer */
DngWriter *dngw_create(const char *path, int le);
void  dngw_write_data(DngWriter *w, const unsigned char *b, int len);
long  dngw_position(DngWriter *w);
void  dngw_align(DngWriter *w);
long  dngw_write_blob(DngWriter *w, const unsigned char *b, int len);
long  dngw_write_ifd(DngWriter *w, TagList *tags, long nextIfd);
void  dngw_set_ifd0(DngWriter *w, long pos);
void  dngw_dispose(DngWriter *w);

#endif /* DNG_H */
