using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

// openice.cs -- the openICE command-line tool: an RGBI DNG in, a clean RGB DNG out.
//
// It wires the pipeline stages into one streaming loop:
//   IceSetup      build the IceState config + density LUT + fixed constants
//   IceBuffers    own every ring / staging buffer as managed float[][]
//   per row:      IceRow.SlotAdvance advances the ring-index fields; then, indexed by those fields,
//                 the front-half builders (Ingest, Weight, GateHist, Products, MaskPyr, Transform) build the
//                 driver's inputs, IceBack.RunRow reconstructs the row, and the output ring is emitted.
//
// Each builder's per-row window is a gather from the rings by the SlotAdvance field values, so there is no
// separate window-setup step.
//
// CALIBRATION. Two ingest scalars steer detection: irCrosstalk (dye->IR crosstalk) and IRref (clear-film IR
// reference). ICE derives them in a separate low-res analysis pass (IceAnalyze). openICE gets them one of three
// ways, in priority order: from -lowres_fields (the three analysis fields supplied directly), from a -lowres scan
// (analyzed here), or -- with neither -- by DOWN-SAMPLING the main scan and analyzing that (a single-scan
// approximation; see docs/VALIDATION.md).
//
// Build (x64 -- managed, fully self-contained in openICE/):
//   csc /platform:x64 /optimize+ /out:openice.exe src\openice.cs src\IcePump.cs src\IceSetup.cs ^
//       src\IceRow.cs src\IceFront.cs src\IceBack.cs src\IceCore.cs src\IceBuffers.cs src\IceAnalyze.cs src\dng.cs

class OpenIce
{
    // the raw-IR "clear film" gate used by the analysis regressions (8847.226), and the density LUT they use.
    static readonly float RAW_IR_GATE = BitConverter.ToSingle(BitConverter.GetBytes(0x460A3CE7u), 0);

    public const string Version = "1.0";

    // Terse synopsis, for no-args and argument errors (exit 2). Full man-style text is in Help() (-h / --help).
    static void Usage()
    {
        Console.WriteLine("openice -- Digital ICE, reimplemented.  RGBI DNG in, clean RGB DNG out.\n");
        Console.WriteLine("  openice <in.dng> [out.dng] [options]");
        Console.WriteLine("  -o <out.dng>        output path (default <in>.openice.dng; a bare 2nd arg works too)");
        Console.WriteLine("  -order <RGBI>       channel order of the source samples (default RGBI; 4th letter = IR)");
        Console.WriteLine("  -dpi <N>            override the source resolution");
        Console.WriteLine("  -lowres <file>      low-res scan for the calibration; if omitted, the main scan is down-sampled");
        Console.WriteLine("  -lowres_fields <V>  the three analysis fields directly: irCrosstalk,Rref,irRefRaw");
        Console.WriteLine("                      (comma-separated); when given, used instead of a low-res scan");
        Console.WriteLine("  -giveup <file.pgm>  also write the give-up map: which pixels the engine reconstructed vs");
        Console.WriteLine("                      copied through. PGM, white = reconstructed (edited), black = gave up");
        Console.WriteLine("  -fine               ICE Fine (default is ICE Normal): stage gains 1.0 not 1.25, L3 clamp off");
        Console.WriteLine("  -kind <7|8|9>       scanner reconstruction target (default 8 = LS-5000, byte-exact; 7,9 selectable but APPROXIMATE)");
        Console.WriteLine("  -h, --help          show full help;  --version  show version");
    }

    // The "man page": full help printed on -h / --help / /?  (exit 0). Windows has no man(1), so a structured
    // --help is the convention. Kept in the same NAME/SYNOPSIS/DESCRIPTION/OPTIONS/EXAMPLES layout a man page uses.
    static void Help()
    {
        string[] L = {
        "NAME",
        "    openice -- infrared dust-and-scratch removal for film scans; an open",
        "    reimplementation of Digital ICE. openICE " + Version + ".",
        "",
        "SYNOPSIS",
        "    openice <in.dng> [out.dng] [options]",
        "    openice -h | --help",
        "    openice --version",
        "",
        "DESCRIPTION",
        "    Reads an RGBI DNG (red, green, blue + infrared) and writes a clean RGB DNG",
        "    with dust and scratches removed. Film dyes are transparent to infrared, so a",
        "    defect that blocks IR maps the physical damage independently of the picture;",
        "    ICE uses that map to detect defects and rebuild the colour beneath them from",
        "    surrounding intact pixels.",
        "",
        "    ICE needs three per-frame calibration values (dye->IR crosstalk and the",
        "    clear-film IR reference). openice obtains them, in priority order, from",
        "    -lowres_fields, from a -lowres scan, or -- with neither -- by down-sampling",
        "    the main scan itself (visually close, but not bit-exact).",
        "",
        "OPTIONS",
        "    -o <out.dng>        Output path. Default <in>.openice.dng. A bare second",
        "                        positional argument is taken as the output too.",
        "    -order <RGBI>       Channel order of the source samples (default RGBI); the",
        "                        4th letter marks the IR channel. Set this if your DNG",
        "                        stores the channels in another order.",
        "    -dpi <N>            Override the source resolution instead of reading it",
        "                        from the DNG.",
        "    -lowres <file>      A low-resolution scan of the same frame, used to",
        "                        calibrate ICE. Reproduces the original engine bit-exact.",
        "    -lowres_fields <V>  The three calibration values directly, comma-separated",
        "                        as irCrosstalk,Rref,irRefRaw. Used instead of -lowres.",
        "    -giveup <file.pgm>  Also write the give-up map (PGM): white = a pixel was",
        "                        reconstructed, black = copied through unchanged.",
        "    -fine               ICE Fine instead of the default Normal (stage gains 1.0",
        "                        not 1.25, L3 output clamp off): softer, edits the whole",
        "                        frame. See docs/parameters.md.",
        "    -kind <7|8|9>       Scanner reconstruction target: 8 = LS-5000 (default,",
        "                        byte-exact), 7 = LS-9000, 9 = LS-50 (approximate).",
        "    -h, --help          Show this help and exit.",
        "    --version           Show version and exit.",
        "",
        "EXAMPLES",
        "    Clean a scan, down-sampling the main scan for calibration:",
        "        openice scan.dng clean.dng",
        "",
        "    Calibrate from a real low-res prescan (bit-exact):",
        "        openice scan.dng clean.dng -lowres scan_lowres.dng",
        "",
        "    LS-9000 target, ICE Fine:",
        "        openice scan.dng clean.dng -lowres scan_lowres.dng -kind 7 -fine",
        "",
        "NOTES",
        "    Output DNGs are stamped  Software = \"openICE (open reimplementation)\".",
        "    This C# build matches the original to within a last-bit dither difference;",
        "    for byte-exact output use the C build (src_C, compiled -mfpmath=387).",
        "    See also: docs/pipeline.md, docs/parameters.md, docs/VALIDATION.md.",
        "",
        "LICENSE",
        "    Free software under the GNU GPL v3.0.  Copyright (C) 2026 <a6o>.",
        };
        foreach (var s in L) Console.WriteLine(s);
    }

    static float[] BuildLut()
    {
        var lut = new float[65536];
        double k = 65535.0 / (16.0 * Math.Log(2.0));
        for (int i = 0; i <= 65535; i++) lut[i] = (float)(k * Math.Log(i + 1));
        return lut;
    }

    // The low-res analysis on an RGBI image: the three image-dependent calibration fields -- irCrosstalk, and the
    // IR-reference stats Rref (xx/xr) and irRefRaw (xy/xr) -- matching the original engine's analysis exactly on a
    // genuine low-res scan.
    static IceCalib AnalyzeImage(ushort[] img, int W, int H)
    {
        return IceAnalyze.Run(img, W, H, BuildLut(), 65535, RAW_IR_GATE);
    }

    // Load a low-res RGBI scan into the interleaved 16-bit buffer the analysis expects.
    static ushort[] LoadRgbi(Dng d, int[] map, int W, int H)
    {
        var img = new ushort[(long)W * H * 4];
        var row = new short[W * 4];
        for (int r = 0; r < H; r++) { d.LoadRow(r, map, row); for (int i = 0; i < W * 4; i++) img[(long)r * W * 4 + i] = (ushort)row[i]; }
        return img;
    }

    // Down-sample the main RGBI scan by box-averaging f x f blocks -> a low-res RGBI image. Used when no -lowres
    // scan is given: a single-scan approximation of the separate low-res scan the original engine calibrates on. The
    // factor targets ~281px wide (the LS-5000 low-res scan's size), so a 3946-wide main gives 281x425.
    static ushort[] DownsampleMain(Dng src, int[] map, int W, int H, out int lw, out int lh, out int f)
    {
        f = Math.Max(1, (int)Math.Round(W / 281.0));
        lw = W / f; lh = H / f;
        var img = new ushort[(long)lw * lh * 4];
        var rows = new short[f][]; for (int k = 0; k < f; k++) rows[k] = new short[W * 4];
        for (int ry = 0; ry < lh; ry++)
        {
            for (int k = 0; k < f; k++) src.LoadRow(ry * f + k, map, rows[k]);
            for (int rx = 0; rx < lw; rx++)
                for (int c = 0; c < 4; c++)
                {
                    long s = 0; int bx = rx * f;
                    for (int ky = 0; ky < f; ky++) for (int kx = 0; kx < f; kx++) s += (ushort)rows[ky][(bx + kx) * 4 + c];
                    img[((long)ry * lw + rx) * 4 + c] = (ushort)(s / (f * f));
                }
        }
        return img;
    }

    static void Main(string[] args)
    {
        try { Environment.ExitCode = Run(args); }
        catch (Exception ex) { Console.WriteLine("ERROR: " + ex.Message + "\n" + ex.StackTrace); Environment.ExitCode = 2; }
    }

    static int Run(string[] args)
    {
        if (Environment.GetEnvironmentVariable("ICE_NODITHER") != null) IceCore.NoDither = true;   // TEST hook
        if (args.Length == 0) { Usage(); return 2; }
        switch (args[0])
        {
            case "-h": case "-help": case "--help": case "/?": case "/h": case "help": Help(); return 0;
            case "-version": case "--version": case "/version": Console.WriteLine("openice (openICE) " + Version); return 0;
        }
        string path = args[0], outp = null, order = null, lowresPath = null, giveupPath = null;
        float[] lowresFields = null;
        int argDpi = 0;
        for (int i = 1; i < args.Length; i++)
        {
            string a = args[i];
            if (a == "-o") outp = args[++i];
            else if (a == "-order") order = args[++i];
            else if (a == "-dpi") argDpi = int.Parse(args[++i]);
            else if (a == "-lowres") lowresPath = args[++i];
            else if (a == "-giveup") giveupPath = args[++i];
            else if (a == "-fine") IceSetup.Fine = true;   // ICE Fine
            else if (a == "-kind") { IceSetup.Kind = int.Parse(args[++i]);   // reconstruction target
                if (IceSetup.Kind != 7 && IceSetup.Kind != 8 && IceSetup.Kind != 9) { Console.Error.WriteLine("ERROR: -kind must be 7, 8, or 9 (8 = LS-5000, default)"); return 2; }
                if (IceSetup.Kind == 9) Console.Error.WriteLine("note: -kind 9 is UNVERIFIED -- no LS-50 hardware to validate against.");
                else if (IceSetup.Kind == 7) Console.Error.WriteLine("note: -kind 7 (LS-9000) is fully modelled. This x64 (SSE) C# build matches the original's\n      80-bit (x87) reconstruction to within a few ULPs on a handful of pixels; on the 9000 that residual\n      can desync the frame-global dither. Use the C build (src_C, -mfpmath=387) for bit-exact output."); }
            else if (a == "-lowres_fields")
            {
                var parts = args[++i].Split(',');
                if (parts.Length != 3) throw new Exception("-lowres_fields needs 3 comma-separated values: irCrosstalk,Rref,irRefRaw");
                lowresFields = new float[3];
                for (int k = 0; k < 3; k++) lowresFields[k] = float.Parse(parts[k], CultureInfo.InvariantCulture);
            }
            else if (outp == null) outp = a;
        }

        // Reads IceSetup.Fine / IceSetup.Kind, set above from -fine / -kind.
        return Process(path, outp, order, argDpi, lowresPath, lowresFields, giveupPath,
                       Console.WriteLine,
                       delegate (int r, int t) { Console.Write("\r  rows " + r + "/" + t + "        "); if (r >= t) Console.WriteLine(); Console.Out.Flush(); });
    }

    // The full run for one file, factored out so both the CLI (above) and the GUI (openice_gui.cs) share
    // it: open the RGBI DNG, derive the calibration (supplied fields > -lowres scan > down-sampled main scan),
    // install it, stream every row through IcePump, and write the clean RGB DNG. Reads IceSetup.Fine /
    // IceSetup.Kind (set by the caller). `log` receives the human-readable status lines -- pass null to stay
    // silent (the GUI does); `onProgress(rowsDone, totalRows)` is called as rows are written. Returns 0 when the
    // whole frame was emitted, 1 if the pump drained short. Throws on bad input; onProgress may throw to abort
    // (the output stream is still closed via the finally, so the partial file is not left locked).
    public static int Process(string path, string outp, string order, int argDpi, string lowresPath,
                              float[] lowresFields, string giveupPath, Action<string> log, Action<int, int> onProgress,
                              int irClipLo = 0, int irClipHi = 0)
    {
        Dng img = Dng.Open(path);
        img.IrClipLo = irClipLo; img.IrClipHi = irClipHi;   // GUI "clip IR": remap IR [lo,hi]->[0,65535] before ICE (0/0 = off)
        int W = img.W, H = img.H;
        int[] map = img.MakeMap(order);
        int dpi = argDpi > 0 ? argDpi : img.Dpi;
        if (map[3] < 0) throw new Exception(path + ": no IR channel (" + img.Samples + "ch). Digital ICE needs one.");

        // --- calibration: the two ingest scalars (irCrosstalk = core 0x34, IRref = core 0xf2c). Priority:
        //     -lowres_fields (supplied) > -lowres <scan> (analyzed) > down-sampled main scan (approximation). ---
        IceCalib cal;
        string calSrc;
        if (lowresFields != null)
        {
            cal = new IceCalib { crosstalk = lowresFields[0], coeff2 = 1.5f * lowresFields[0], rref = lowresFields[1], irRefRaw = lowresFields[2] };
            calSrc = "-lowres_fields";
        }
        else if (lowresPath != null)
        {
            Dng low = Dng.Open(lowresPath); int[] lmap = low.MakeMap(order);
            cal = AnalyzeImage(LoadRgbi(low, lmap, low.W, low.H), low.W, low.H);
            calSrc = "-lowres " + lowresPath + " (" + low.W + "x" + low.H + ", analyzed, bit-exact)";
        }
        else
        {
            int lw, lh, f;
            ushort[] limg = DownsampleMain(img, map, W, H, out lw, out lh, out f);
            cal = AnalyzeImage(limg, lw, lh);
            calSrc = "down-sampled main scan 1/" + f + " -> " + lw + "x" + lh + " (single-scan approximation; see docs/VALIDATION.md)";
        }
        float irCrosstalk, irref;
        IceAnalyze.Install(cal, out irCrosstalk, out irref);

        if (outp == null)
            outp = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path)), Path.GetFileNameWithoutExtension(path) + ".openice.dng");
        int emitRows = H;

        if (log != null)
        {
            log("openICE          : " + path);
            log("source           : " + img.Info);
            log("geometry         : " + W + " x " + H + " @ " + dpi + " dpi");
            log("calibration      : " + calSrc);
            log("  fields         : irCrosstalk=" + cal.crosstalk.ToString("R") + "  Rref=" + cal.rref.ToString("R") + "  irRefRaw=" + cal.irRefRaw.ToString("R"));
            log("  installed      : irCrosstalk=" + irCrosstalk.ToString("R") + "  IRref=" + irref.ToString("R"));
        }

        // optional give-up map: per output row, which pixels the engine reconstructed (white) vs copied (black).
        byte[] guMap = null;
        if (giveupPath != null)
        {
            guMap = new byte[(long)W * H];
            IcePump.GiveUpSink = delegate (int row, byte[] gu)
            { long b = (long)row * W; for (int x = 0; x < W; x++) guMap[b + x] = gu[x] == 0 ? (byte)255 : (byte)0; };
        }

        DngWriter dw = new DngWriter(outp, img.Le);
        int rowsOut;
        try
        {
            long dataPos = dw.DataPos;
            rowsOut = IcePump.Run(
                delegate (int row, short[] rgbi) { img.LoadRow(row, map, rgbi); },
                delegate (int row, byte[] rgb, int n) {
                    // IcePump packs pixels little-endian; the output DNG inherits the source byte order, so for a
                    // big-endian source (e.g. SilverFast) swap each 16-bit sample to match the header. (No-op for
                    // little-endian sources, so byte-exactness on the Nikon/VueScan path is untouched.)
                    if (!img.Le) for (int i = 0; i + 1 < n; i += 2) { byte t = rgb[i]; rgb[i] = rgb[i + 1]; rgb[i + 1] = t; }
                    dw.WriteData(rgb, n);
                    if (onProgress != null && ((row + 1) & 255) == 0) onProgress(row + 1, emitRows);
                },
                W, H, dpi, irref, irCrosstalk, emitRows);
            if (onProgress != null) onProgress(rowsOut, emitRows);
            WriteDng(dw, img, W, rowsOut, W * 6, dataPos);
        }
        finally { IcePump.GiveUpSink = null; dw.Dispose(); }

        if (log != null) log("-> " + outp + "   " + new FileInfo(outp).Length.ToString("N0") + " bytes  (3-ch linear RGB DNG, " + W + " x " + rowsOut + ")");

        if (guMap != null)
        {
            long edited = 0, tot = (long)W * rowsOut;
            for (long i = 0; i < tot; i++) if (guMap[i] != 0) edited++;
            WritePgm(giveupPath, W, rowsOut, guMap);
            if (log != null) { double pct = 100.0 * edited / tot; log("-> " + giveupPath + "   give-up map (" + W + " x " + rowsOut + "), reconstructed " + pct.ToString("F1") + "%  (gave up " + (100.0 - pct).ToString("F1") + "%)"); }
        }
        return rowsOut == emitRows ? 0 : 1;
    }

    // write a binary PGM (P5): one byte/pixel, no external image dependency. Height H may be < the buffer's if the
    // run drained short; only the first W*H bytes are written.
    static void WritePgm(string path, int W, int H, byte[] data)
    {
        using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write))
        {
            byte[] hdr = System.Text.Encoding.ASCII.GetBytes("P5\n" + W + " " + H + "\n255\n");
            fs.Write(hdr, 0, hdr.Length);
            long tot = (long)W * H, off = 0;
            while (off < tot) { int chunk = (int)Math.Min(1 << 20, tot - off); fs.Write(data, (int)off, chunk); off += chunk; }
        }
    }

    // ---- build the output DNG from the source's tags ----
    static TiffTag Mk(int tag, int type, byte[] data) { var t = new TiffTag(); t.Tag = (ushort)tag; t.Type = (ushort)type; t.Count = (uint)(data.Length / TiffTag.TypeSize(type)); t.Data = data; return t; }
    static byte[] U16s(bool le, params int[] v) { var b = new byte[v.Length * 2]; for (int i = 0; i < v.Length; i++) if (le) { b[i * 2] = (byte)(v[i] & 0xFF); b[i * 2 + 1] = (byte)((v[i] >> 8) & 0xFF); } else { b[i * 2] = (byte)((v[i] >> 8) & 0xFF); b[i * 2 + 1] = (byte)(v[i] & 0xFF); } return b; }
    static byte[] U32s(bool le, params long[] v) { var b = new byte[v.Length * 4]; for (int i = 0; i < v.Length; i++) for (int k = 0; k < 4; k++) b[i * 4 + (le ? k : 3 - k)] = (byte)((v[i] >> (k * 8)) & 0xFF); return b; }
    static void Put(List<TiffTag> tags, TiffTag t) { for (int i = 0; i < tags.Count; i++) if (tags[i].Tag == t.Tag) { tags[i] = t; return; } tags.Add(t); tags.Sort(delegate (TiffTag a, TiffTag b) { return a.Tag.CompareTo(b.Tag); }); }
    static void Drop(List<TiffTag> tags, int tag) { for (int i = 0; i < tags.Count; i++) if (tags[i].Tag == tag) { tags.RemoveAt(i); return; } }

    static void WriteDng(DngWriter dw, Dng src, int W, int H, int outRow, long dataPos)
    {
        bool le = src.Le; long dataLen = (long)outRow * H;
        var im = new List<TiffTag>(); foreach (var t in src.ImageTags) im.Add(t);
        Put(im, Mk(Dng.BITS, 3, U16s(le, 16, 16, 16)));
        Put(im, Mk(Dng.SAMPLES, 3, U16s(le, 3)));
        Put(im, Mk(Dng.ROWS_PER_STRIP, 3, U16s(le, H)));
        Put(im, Mk(Dng.STRIP_OFFSETS, 4, U32s(le, dataPos)));
        Put(im, Mk(Dng.STRIP_COUNTS, 4, U32s(le, dataLen)));
        Put(im, Mk(Dng.LENGTH, 3, U16s(le, H)));
        if (src.Get(im, Dng.MIN_SAMPLE) != null) Put(im, Mk(Dng.MIN_SAMPLE, 3, U16s(le, 0, 0, 0)));
        if (src.Get(im, Dng.MAX_SAMPLE) != null) Put(im, Mk(Dng.MAX_SAMPLE, 3, U16s(le, 65535, 65535, 65535)));
        byte[] thumb = src.ThumbnailBytes(); long thumbPos = 0;
        if (thumb != null) thumbPos = dw.WriteBlob(thumb);
        long subPos = dw.WriteIfd(im, 0);

        var i0 = new List<TiffTag>(); foreach (var t in src.Ifd0Tags) i0.Add(t);
        Put(i0, Mk(Dng.SUBIFDS, 4, U32s(le, subPos)));
        if (thumb != null) { Put(i0, Mk(Dng.STRIP_OFFSETS, 4, U32s(le, thumbPos))); Put(i0, Mk(Dng.STRIP_COUNTS, 4, U32s(le, thumb.Length))); Put(i0, Mk(Dng.ROWS_PER_STRIP, 3, U16s(le, (int)src.Scalar(src.Ifd0Tags, Dng.LENGTH, 0)))); }
        var cm = src.Get(i0, Dng.COLOR_MATRIX1);
        if (cm != null && cm.Count == 12) { var d = new byte[9 * 8]; Array.Copy(cm.Data, 0, d, 0, 9 * 8); Put(i0, Mk(Dng.COLOR_MATRIX1, cm.Type, d)); }
        var fm = src.Get(i0, Dng.FORWARD_MATRIX1);
        if (fm != null && fm.Count == 12) { var d = new byte[9 * 8]; for (int r = 0; r < 3; r++) Array.Copy(fm.Data, (r * 4) * 8, d, (r * 3) * 8, 3 * 8); Put(i0, Mk(Dng.FORWARD_MATRIX1, fm.Type, d)); }
        Put(i0, Mk(Dng.SOFTWARE, 2, System.Text.Encoding.ASCII.GetBytes("openICE (open reimplementation)\0")));
        Drop(i0, Dng.EXIF_IFD);
        long ifd0 = dw.WriteIfd(i0, 0); dw.SetIfd0(ifd0);
    }
}
