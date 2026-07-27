using System;
using System.Collections.Generic;
using System.IO;

// dng.cs -- read a 4-channel RGBI DNG/TIFF, and write a 3-channel RGB DNG back out.
//
// A DNG is a TIFF with extra tags. Both directions live here because the output is built by COPYING the
// input's tags rather than inventing a DNG from scratch: the source is a file that demonstrably works in the
// user's pipeline, so mirroring its structure is safer than guessing at the spec.
//
// STRUCTURE (VueScan's, and therefore ours):
//     IFD0     a small RGB thumbnail + the DNG camera profile (ColorMatrix1, AsShotWhiteXY, DNGVersion...)
//     SubIFD   the real image: Photometric 34892 (LinearRaw), uncompressed, chunky
// The real image is NOT in IFD0. Trusting position parses the thumbnail -- or worse, pixel data as tags.
//
// GOING 4-CHANNEL -> 3-CHANNEL. Only a handful of tags are channel-dependent, and the colour matrices are
// laid out so the IR part drops off cleanly:
//     ColorMatrix1    SamplesPerPixel rows x 3   (XYZ -> camera, one row per channel) -> drop the IR ROW
//     ForwardMatrix1  3 rows x SamplesPerPixel   (camera -> XYZ)                      -> drop the IR COLUMN
//     BitsPerSample / MinSampleValue / MaxSampleValue   count 4 -> 3
//     SamplesPerPixel 4 -> 3
// AsShotWhiteXY is a chromaticity, so it is channel-count independent and copies unchanged.
//
// In the sample file the IR row of ColorMatrix1 is (0, 1, 0) -- a placeholder -- and what remains after
// dropping it is the standard sRGB D65 XYZ->RGB matrix. ForwardMatrix1's IR column is all zeros.

class TiffTag
{
    public ushort Tag, Type;
    public uint Count;
    public byte[] Data;          // the value's bytes, in the FILE's byte order

    public static int TypeSize(int t)
    {
        switch (t)
        {
            case 1: case 2: case 6: case 7: return 1;
            case 3: case 8: return 2;
            case 4: case 9: case 11: return 4;
            case 5: case 10: case 12: return 8;
            default: return 1;
        }
    }
    public int Size { get { return TypeSize(Type) * (int)Count; } }
}

class Dng
{
    // ---- tag numbers we actually reason about -------------------------------------------------------------
    public const int NEW_SUBFILE = 254, WIDTH = 256, LENGTH = 257, BITS = 258, COMPRESSION = 259,
                     PHOTOMETRIC = 262, STRIP_OFFSETS = 273, SAMPLES = 277, ROWS_PER_STRIP = 278,
                     STRIP_COUNTS = 279, MIN_SAMPLE = 280, MAX_SAMPLE = 281, XRES = 282, YRES = 283,
                     PLANAR = 284, SOFTWARE = 305, SUBIFDS = 330, EXIF_IFD = 34665,
                     COLOR_MATRIX1 = 50721, FORWARD_MATRIX1 = 50725;
    public const int PHOTOMETRIC_LINEARRAW = 34892;

    public byte[] Raw;
    public bool Le;
    public long Ifd0, ImageIfd;
    public List<TiffTag> Ifd0Tags = new List<TiffTag>();
    public List<TiffTag> ImageTags = new List<TiffTag>();

    public int W, H, Samples, Dpi = 4000, RowsPerStrip;
    public bool DpiFromFile;
    public string Info = "";
    long[] stripOff;
    int rowBytes;

    // SilverFast-style layout: RGB in the main image, IR in a SEPARATE same-size single-channel image. These
    // describe that IR plane; irStripOff stays null when the IR is interleaved into the main image (or absent).
    long[] irStripOff;
    int irRowBytes, irRowsPerStrip;
    public bool HasSeparateIr;

    // GUI "clip IR" levels stretch: when IrClipHi > IrClipLo, LoadRow linearly remaps the IR channel (channel 3)
    // from [IrClipLo, IrClipHi] onto [0, 65535], clamping outside. 0/0 = disabled (identity, the normal case).
    public int IrClipLo = 0, IrClipHi = 0;

    // ---- primitive reads ----------------------------------------------------------------------------------
    ushort U16(long o) { return Le ? (ushort)(Raw[o] | (Raw[o + 1] << 8)) : (ushort)((Raw[o] << 8) | Raw[o + 1]); }
    uint U32(long o)
    {
        return Le ? (uint)(Raw[o] | (Raw[o + 1] << 8) | (Raw[o + 2] << 16) | (Raw[o + 3] << 24))
                  : (uint)((Raw[o] << 24) | (Raw[o + 1] << 16) | (Raw[o + 2] << 8) | Raw[o + 3]);
    }

    List<TiffTag> ReadIfd(long ifd)
    {
        var list = new List<TiffTag>();
        int n = U16(ifd);
        for (int i = 0; i < n; i++)
        {
            long e = ifd + 2 + i * 12;
            var t = new TiffTag();
            t.Tag = U16(e); t.Type = U16(e + 2); t.Count = U32(e + 4);
            int sz = t.Size;
            // a value is inline when it fits in the entry's 4-byte field, out-of-line otherwise
            long off = (sz <= 4) ? e + 8 : U32(e + 8);
            t.Data = new byte[sz];
            if (off + sz <= Raw.LongLength) Array.Copy(Raw, off, t.Data, 0, sz);
            list.Add(t);
        }
        return list;
    }

    public TiffTag Get(List<TiffTag> tags, int tag)
    {
        foreach (var t in tags) if (t.Tag == tag) return t;
        return null;
    }

    public long Scalar(List<TiffTag> tags, int tag, long dflt)
    {
        var t = Get(tags, tag);
        if (t == null || t.Count == 0) return dflt;
        if (t.Type == 3) return Le ? (ushort)(t.Data[0] | (t.Data[1] << 8)) : (ushort)((t.Data[0] << 8) | t.Data[1]);
        if (t.Type == 4) return Le ? (uint)(t.Data[0] | (t.Data[1] << 8) | (t.Data[2] << 16) | (t.Data[3] << 24))
                                   : (uint)((t.Data[0] << 24) | (t.Data[1] << 16) | (t.Data[2] << 8) | t.Data[3]);
        if (t.Type == 1) return t.Data[0];
        return dflt;
    }

    long[] Longs(TiffTag t)
    {
        var v = new long[t.Count];
        for (int i = 0; i < t.Count; i++)
        {
            int o = i * TiffTag.TypeSize(t.Type);
            if (t.Type == 3) v[i] = Le ? (ushort)(t.Data[o] | (t.Data[o + 1] << 8)) : (ushort)((t.Data[o] << 8) | t.Data[o + 1]);
            else v[i] = Le ? (uint)(t.Data[o] | (t.Data[o + 1] << 8) | (t.Data[o + 2] << 16) | (t.Data[o + 3] << 24))
                           : (uint)((t.Data[o] << 24) | (t.Data[o + 1] << 16) | (t.Data[o + 2] << 8) | t.Data[o + 3]);
        }
        return v;
    }

    // true iff BitsPerSample is present and every sample is 16-bit (an 8-bit thumbnail returns false)
    bool All16(TiffTag bt)
    {
        if (bt == null) return false;
        var bps = Longs(bt);
        if (bps.Length == 0) return false;
        foreach (long x in bps) if (x != 16) return false;
        return true;
    }

    // ---- open ---------------------------------------------------------------------------------------------
    public static Dng Open(string path) { return new Dng(path); }

    Dng(string path)
    {
        Raw = File.ReadAllBytes(path);
        if (Raw.Length < 8) throw new Exception(path + ": not a TIFF/DNG (too short)");
        if (Raw[0] == 0x49 && Raw[1] == 0x49) Le = true;
        else if (Raw[0] == 0x4D && Raw[1] == 0x4D) Le = false;
        else throw new Exception(path + ": not a TIFF/DNG (bad byte-order mark)");
        if (U16(2) != 42) throw new Exception(path + ": not a TIFF/DNG (bad magic)");

        Ifd0 = U32(4);
        Ifd0Tags = ReadIfd(Ifd0);

        // every IFD: the header chain, plus SubIFDs -- where a DNG hides the real image
        var ifds = new List<long>();
        long p = Ifd0;
        while (p != 0 && p + 2 < Raw.LongLength) { ifds.Add(p); int n = U16(p); p = U32(p + 2 + n * 12); }
        for (int i = 0; i < ifds.Count; i++)
        {
            var t = Get(ReadIfd(ifds[i]), SUBIFDS);
            if (t != null) foreach (long s in Longs(t)) if (s > 0 && s + 2 < Raw.LongLength && !ifds.Contains(s)) ifds.Add(s);
        }

        // Pick the IFD that is genuinely 16-bit uncompressed chunky -- NOT the thumbnail, which is 8-bit.
        // 3 samples is accepted as well as 4 so that ice's own OUTPUT can be read back (for rendering and for
        // round-trip checks); whether an IR channel is actually required is the caller's business, since only
        // ICE needs one.
        // Choose the real raw, not a preview/thumbnail. A DNG can hold several 16-bit RGB images -- SilverFast ships
        // a 16-bit thumbnail + a preview + the full-res raw -- so first-match is wrong. Prefer the one tagged
        // LinearRaw, breaking ties by pixel area (the raw is the largest). VueScan's thumbnail is 8-bit (excluded by
        // all16) and its raw is both LinearRaw and largest, so this still selects exactly what it did before.
        long img = -1; bool bestLin = false; long bestArea = -1;
        var seen = new List<string>();
        foreach (long ifd in ifds)
        {
            var tags = ReadIfd(ifd);
            long spp = Scalar(tags, SAMPLES, 1), comp = Scalar(tags, COMPRESSION, 1), plan = Scalar(tags, PLANAR, 1);
            long w = Scalar(tags, WIDTH, 0), h = Scalar(tags, LENGTH, 0);
            bool all16 = All16(Get(tags, BITS));
            seen.Add(string.Format("IFD@0x{0:X}: {1}x{2} samples={3} all16={4} compression={5} planar={6}",
                                   ifd, w, h, spp, all16, comp, plan));
            if (spp >= 3 && all16 && comp == 1 && plan == 1)
            {
                bool lin = Scalar(tags, PHOTOMETRIC, 0) == PHOTOMETRIC_LINEARRAW; long area = w * h;
                if (img < 0 || (lin && !bestLin) || (lin == bestLin && area > bestArea))
                { img = ifd; ImageTags = tags; bestLin = lin; bestArea = area; }
            }
        }
        if (img < 0)
            throw new Exception(path + ": no uncompressed 16-bit chunky image found.\n"
                + "  Expected a linear raw (VueScan: Output > Raw file type = \"48 bit RGBI\").\n"
                + "  IFDs found:\n    " + string.Join("\n    ", seen.ToArray()));

        ImageIfd = img;
        W = (int)Scalar(ImageTags, WIDTH, 0);
        H = (int)Scalar(ImageTags, LENGTH, 0);
        Samples = (int)Scalar(ImageTags, SAMPLES, 4);
        RowsPerStrip = (int)Scalar(ImageTags, ROWS_PER_STRIP, H);
        if (RowsPerStrip <= 0) RowsPerStrip = H;
        rowBytes = W * Samples * 2;
        stripOff = Longs(Get(ImageTags, STRIP_OFFSETS));

        // If the main image isn't already RGBI, look for a SEPARATE same-size single-channel 16-bit image and treat
        // it as the IR channel (SilverFast writes RGB and IR as two sibling images rather than one 4-sample image).
        if (Samples < 4)
        {
            foreach (long ifd in ifds)
            {
                if (ifd == ImageIfd) continue;
                var tags = ReadIfd(ifd);
                var so = Get(tags, STRIP_OFFSETS);
                if (so == null || Scalar(tags, SAMPLES, 1) != 1 || !All16(Get(tags, BITS))) continue;
                if (Scalar(tags, COMPRESSION, 1) != 1 || Scalar(tags, PLANAR, 1) != 1) continue;
                if (Scalar(tags, WIDTH, 0) != W || Scalar(tags, LENGTH, 0) != H) continue;
                irStripOff = Longs(so);
                irRowsPerStrip = (int)Scalar(tags, ROWS_PER_STRIP, H); if (irRowsPerStrip <= 0) irRowsPerStrip = H;
                irRowBytes = W * 2;
                HasSeparateIr = true;
                break;
            }
        }

        // XResolution is a RATIONAL. ICE needs the TRUE OPTICAL SCALE -- a defect's size in pixels depends on
        // it -- so this is read, never assumed. If the tag is missing, say so rather than quietly using 4000.
        var xr = Get(ImageTags, XRES);
        if (xr == null) xr = Get(Ifd0Tags, XRES);
        if (xr != null && xr.Count >= 1)
        {
            uint num = Le ? (uint)(xr.Data[0] | (xr.Data[1] << 8) | (xr.Data[2] << 16) | (xr.Data[3] << 24))
                          : (uint)((xr.Data[0] << 24) | (xr.Data[1] << 16) | (xr.Data[2] << 8) | xr.Data[3]);
            uint den = Le ? (uint)(xr.Data[4] | (xr.Data[5] << 8) | (xr.Data[6] << 16) | (xr.Data[7] << 24))
                          : (uint)((xr.Data[4] << 24) | (xr.Data[5] << 16) | (xr.Data[6] << 8) | xr.Data[7]);
            if (den != 0) { Dpi = (int)Math.Round(num / (double)den); DpiFromFile = true; }
        }

        Info = string.Format("{0}x{1}, {2} samples x 16-bit, chunky, {3}-endian, {4} strip(s) of {5} row(s), {6} dpi{7}",
                             W, H, Samples, Le ? "little" : "big", stripOff.Length, RowsPerStrip, Dpi,
                             DpiFromFile ? "" : " (ASSUMED -- no XResolution; use -dpi)");
        if (HasSeparateIr) Info += "  + separate 1-ch IR plane";
    }

    public bool HasIr { get { return Samples >= 4 || HasSeparateIr; } }

    // order names what each SOURCE channel IS -- "RGBI" for a normal RGBI raw, "IRGB" for an IR-first one,
    // "RGB" for a file with no IR. map[c] = source channel for output channel c (R,G,B,IR);
    // map[3] = -1 when the file has no IR.
    public int[] MakeMap(string order)
    {
        if (string.IsNullOrEmpty(order)) order = HasIr ? "RGBI" : "RGB";
        order = order.ToUpper();
        if (order.Length != 3 && order.Length != 4)
            throw new Exception("order must be a permutation of RGB or RGBI, got: " + order);
        var map = new int[4];
        for (int c = 0; c < 3; c++)
        {
            int i = order.IndexOf("RGB"[c]);
            if (i < 0) throw new Exception("order must be a permutation of RGB or RGBI, got: " + order);
            if (i >= Samples) throw new Exception("order " + order + " needs more channels than the file has (" + Samples + ")");
            map[c] = i;
        }
        int ip = order.IndexOf('I');
        if (ip < 0) map[3] = -1;                      // no IR requested
        else if (HasSeparateIr) map[3] = ip;          // IR lives in its own plane (LoadRow reads it there)
        else if (ip >= Samples)
            throw new Exception("order " + order + " asks for an IR channel, but the file has only " + Samples + " channels");
        else map[3] = ip;                             // IR interleaved in the main image
        return map;
    }

    // one row as chunky RGBI (dst needs W*4 shorts) as native 16-bit values, which is what ICE wants.
    // If the file has no IR, channel 3 is left zero. When the IR is a separate plane, RGB comes from the main
    // image and IR from that plane; otherwise all four channels are interleaved in the main image.
    public void LoadRow(int row, int[] map, short[] dst)
    {
        // --- RGB from the main image (plus interleaved IR when the raw is itself RGBI) ---
        int strip = row / RowsPerStrip;
        long baseOff = (strip < stripOff.Length) ? stripOff[strip] + (long)(row % RowsPerStrip) * rowBytes : -1;
        int lastC = HasSeparateIr ? 2 : 3;   // IR (channel 3) is read from its own plane below when separate
        for (int x = 0; x < W; x++)
            for (int c = 0; c <= lastC; c++)
            {
                if (map[c] < 0 || baseOff < 0) { dst[x * 4 + c] = 0; continue; }
                long o = baseOff + ((long)x * Samples + map[c]) * 2;
                dst[x * 4 + c] = (o + 1 < Raw.LongLength)
                    ? (short)(Le ? (ushort)(Raw[o] | (Raw[o + 1] << 8)) : (ushort)((Raw[o] << 8) | Raw[o + 1]))
                    : (short)0;
            }

        // --- IR from a separate same-size single-channel plane (SilverFast-style) ---
        if (HasSeparateIr)
        {
            int istrip = row / irRowsPerStrip;
            long ibase = (map[3] >= 0 && istrip < irStripOff.Length)
                       ? irStripOff[istrip] + (long)(row % irRowsPerStrip) * irRowBytes : -1;
            for (int x = 0; x < W; x++)
            {
                long o = ibase + (long)x * 2;
                dst[x * 4 + 3] = (ibase >= 0 && o + 1 < Raw.LongLength)
                    ? (short)(Le ? (ushort)(Raw[o] | (Raw[o + 1] << 8)) : (ushort)((Raw[o] << 8) | Raw[o + 1]))
                    : (short)0;
            }
        }

        // --- optional IR levels stretch (GUI "clip IR"): map [IrClipLo, IrClipHi] -> [0, 65535], clamp outside ---
        if (IrClipHi > IrClipLo && map[3] >= 0)
        {
            double s = 65535.0 / (IrClipHi - IrClipLo);
            for (int x = 0; x < W; x++)
            {
                int o = (int)(((ushort)dst[x * 4 + 3] - IrClipLo) * s + 0.5);
                dst[x * 4 + 3] = (short)(ushort)(o < 0 ? 0 : (o > 65535 ? 65535 : o));
            }
        }
    }

    // the thumbnail's pixel bytes, concatenated across its strips (IFD0)
    public byte[] ThumbnailBytes()
    {
        var so = Get(Ifd0Tags, STRIP_OFFSETS); var sc = Get(Ifd0Tags, STRIP_COUNTS);
        if (so == null || sc == null) return null;
        long[] offs = Longs(so), cnts = Longs(sc);
        long total = 0; foreach (long c in cnts) total += c;
        var outb = new byte[total];
        long p = 0;
        for (int i = 0; i < offs.Length; i++)
        {
            if (offs[i] + cnts[i] > Raw.LongLength) return null;
            Array.Copy(Raw, offs[i], outb, p, cnts[i]); p += cnts[i];
        }
        return outb;
    }
}

// ---------------------------------------------------------------------------------------------------------
// Writes a 3-channel linear RGB DNG, built from the source's tags.
//
// Two-pass by construction: the bulk data goes down first (so its offsets are known), then the IFDs, then the
// header's IFD0 pointer is patched. Output uses the SOURCE's byte order, so copied tag bytes stay valid
// without reinterpretation.
class DngWriter : IDisposable
{
    FileStream fs;
    bool le;
    long ifd0PtrPos;
    public long DataPos, DataLen;

    public DngWriter(string path, bool littleEndian)
    {
        le = littleEndian;
        fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20);
        fs.WriteByte((byte)(le ? 0x49 : 0x4D)); fs.WriteByte((byte)(le ? 0x49 : 0x4D));
        WU16(42);
        ifd0PtrPos = fs.Position;
        WU32(0);                       // patched at Close
        DataPos = fs.Position;
    }

    void WU16(int v)
    {
        if (le) { fs.WriteByte((byte)(v & 0xFF)); fs.WriteByte((byte)((v >> 8) & 0xFF)); }
        else { fs.WriteByte((byte)((v >> 8) & 0xFF)); fs.WriteByte((byte)(v & 0xFF)); }
    }
    void WU32(long v)
    {
        if (le) { fs.WriteByte((byte)(v & 0xFF)); fs.WriteByte((byte)((v >> 8) & 0xFF)); fs.WriteByte((byte)((v >> 16) & 0xFF)); fs.WriteByte((byte)((v >> 24) & 0xFF)); }
        else { fs.WriteByte((byte)((v >> 24) & 0xFF)); fs.WriteByte((byte)((v >> 16) & 0xFF)); fs.WriteByte((byte)((v >> 8) & 0xFF)); fs.WriteByte((byte)(v & 0xFF)); }
    }

    public void WriteData(byte[] b, int len) { fs.Write(b, 0, len); DataLen += len; }
    public long Position { get { return fs.Position; } }
    public void Align() { if ((fs.Position & 1) != 0) fs.WriteByte(0); }

    public long WriteBlob(byte[] b) { Align(); long p = fs.Position; fs.Write(b, 0, b.Length); return p; }

    // Serialise an IFD at the current position: entries first, then this IFD's out-of-line values.
    public long WriteIfd(List<TiffTag> tags, long nextIfd)
    {
        Align();
        long ifdPos = fs.Position;
        int n = tags.Count;
        long valuePos = ifdPos + 2 + n * 12 + 4;
        if ((valuePos & 1) != 0) valuePos++;

        // assign out-of-line offsets in tag order
        var offs = new long[n];
        long vp = valuePos;
        for (int i = 0; i < n; i++)
        {
            if (tags[i].Size > 4) { offs[i] = vp; vp += tags[i].Size; if ((vp & 1) != 0) vp++; }
            else offs[i] = -1;
        }

        WU16(n);
        for (int i = 0; i < n; i++)
        {
            var t = tags[i];
            WU16(t.Tag); WU16(t.Type); WU32(t.Count);
            if (offs[i] < 0)
            {
                var pad = new byte[4];
                Array.Copy(t.Data, pad, Math.Min(4, t.Data.Length));
                fs.Write(pad, 0, 4);
            }
            else WU32(offs[i]);
        }
        WU32(nextIfd);

        for (int i = 0; i < n; i++)
            if (offs[i] >= 0)
            {
                if (fs.Position < offs[i]) fs.WriteByte(0);        // the alignment pad
                fs.Write(tags[i].Data, 0, tags[i].Data.Length);
            }
        return ifdPos;
    }

    public void SetIfd0(long pos)
    {
        long save = fs.Position;
        fs.Position = ifd0PtrPos;
        WU32(pos);
        fs.Position = save;
    }

    public void Dispose() { if (fs != null) { fs.Close(); fs = null; } }
}
