using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

// openice_gui.cs -- a small WinForms front-end for openICE. Self-contained.
//
// Drag RGBI DNGs into the list on the left, pick Normal/Fine and the scanner (kind) on the right, press ICE. The
// whole pipeline is compiled INTO this exe -- it calls OpenIce.Process(...) directly (no external openice.exe),
// streaming its per-row progress into the bar. Output is written next to each input as <name>.openice.dng.
//
// Build (Windows, .NET Framework 4.x) -- compile the GUI together with the pipeline, and pick the GUI's Main:
//   csc /platform:x64 /target:winexe /main:OpenIceGui /out:openice_gui.exe ^
//       /r:System.Windows.Forms.dll /r:System.Drawing.dll ^
//       src\openice_gui.cs src\openice.cs src\IcePump.cs src\IceSetup.cs src\IceRow.cs src\IceFront.cs ^
//       src\IceBack.cs src\IceCore.cs src\IceBuffers.cs src\IceAnalyze.cs src\dng.cs

public class OpenIceGui : Form
{
    ListView list;
    RadioButton rNormal, rFine, k8, k7, k9;
    RadioButton rSubfolder, rSameFolder;
    TextBox tbSubfolder, tbPrefix, tbSuffix;
    ComboBox cbThreads;
    CheckBox cbClip;                 // batch "Clip IR": remap IR [min,max]->[0,65535] on every file before ICE
    NumericUpDown numClipLo, numClipHi;
    int batchClipLo, batchClipHi;    // batch-constant clip window, set in RunBatch, read by the workers
    Button addBtn, removeBtn, clearBtn, iceBtn;
    ProgressBar progress;
    Label status, outExample;

    volatile bool running, cancelReq;

    // batch timing -- drives the "s/image  |  elapsed / est. total" readout below the bar
    System.Windows.Forms.Timer etaTimer;
    DateTime startTime;
    int totalImages, lastOk, lastFailed;
    volatile int overallPermille;   // 0..1000 overall progress, set from the worker, read by the UI timer

    // --- Single tab (visual before/after) ---
    Button sOpenBtn, sRunBtn, sIrBtn, sSaveBtn;
    CheckBox sFine;
    ComboBox sKind;
    ProgressBar sProgress;
    Label sStatus;
    CompareCanvas canvas;
    string sImagePath;
    string sResultPath;          // the last successful ICE output (kept temp DNG), copied out by "Save…"; null = none
    volatile bool sRunning;
    IrLevels sLevels;            // IR histogram with a draggable min/max window
    Panel sLevPanel;             // the IR-levels area (histogram + Auto/Full); shown only while "Clip IR" is on
    CheckBox sStretch;           // "Clip IR": apply the IR levels remap when running ICE
    Button sAutoBtn, sFullBtn;   // set the window to the image's data range / the full range
    int[] sIrHist;               // current image's IR histogram (256 buckets), null if no IR
    int sIrDataMin, sIrDataMax;  // the image's actual IR min/max (the "Auto" default window)
    bool sShowingIr, sIrReady;   // IR overlay state for the Visualize tab

    [STAThread]
    static void Main()
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new OpenIceGui());
    }

    public OpenIceGui()
    {
        SuspendLayout();   // batch the whole build; the autoscale baseline is set at the very end (see below)
        Text = "openICE";
        Font = new Font("Segoe UI", 9f);
        ClientSize = new Size(880, 600);
        MinimumSize = new Size(720, 480);
        StartPosition = FormStartPosition.CenterScreen;

        // --- left: the image list (drag-and-drop target) ---
        list = new ListView
        {
            Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true,
            AllowDrop = true, HideSelection = false, GridLines = false
        };
        list.Columns.Add("Image", 300);
        list.Columns.Add("Path", 220);
        list.Columns.Add("Status", 120);
        list.DragEnter += (s, e) =>
            e.Effect = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        list.DragDrop += (s, e) => AddFiles((string[])e.Data.GetData(DataFormats.FileDrop));
        list.KeyDown += (s, e) => { if (e.KeyCode == Keys.Delete) RemoveSelected(); };

        // --- right: settings (scrollable) with the ICE button pinned at the bottom ---
        addBtn = MakeButton("Add Images…", (s, e) => AddViaDialog());
        removeBtn = MakeButton("Remove Selected", (s, e) => RemoveSelected());
        clearBtn = MakeButton("Clear List", (s, e) => { if (!running) list.Items.Clear(); });

        var qual = new GroupBox { Text = "Quality", Width = 206, Height = 70, Margin = new Padding(0, 0, 0, 6) };
        rFine = new RadioButton { Text = "Fine", Location = new Point(12, 36), AutoSize = true };
        rNormal = new RadioButton { Text = "Normal", Location = new Point(12, 12), AutoSize = true, Checked = true };
        qual.Controls.Add(rFine); qual.Controls.Add(rNormal); 

        var scan = new GroupBox { Text = "ICE type", Width = 206, Height = 96, Margin = new Padding(0, 0, 0, 6) };
        k8 = new RadioButton { Text = "LS-5000  (kind 8)", Location = new Point(12, 12), AutoSize = true, Checked = true };
        k7 = new RadioButton { Text = "LS-9000  (kind 7)", Location = new Point(12, 36), AutoSize = true };
        k9 = new RadioButton { Text = "LS-50  (kind 9)", Location = new Point(12, 60), AutoSize = true };
        scan.Controls.Add(k9); scan.Controls.Add(k7); scan.Controls.Add(k8); 

        // Parallel: how many images to reconstruct at once. Each image runs on its own thread with its own buffers,
        // so N-at-a-time roughly scales throughput with cores; more also means more RAM (~0.3 GB per in-flight 24 MP
        // frame). 4 is a good default; drop to 1 on a low-memory machine, raise to 8/16 on a many-core one.
        var par = new GroupBox { Text = "Parallel", Width = 206, Height = 60, Margin = new Padding(0, 0, 0, 6) };
        cbThreads = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Location = new Point(12, 24), Width = 56 };
        cbThreads.Items.AddRange(new object[] { "1", "4", "8", "16" });
        cbThreads.SelectedIndex = 1;   // "4" -- the default
        var lPar = new Label { Text = "images at a time", Location = new Point(76, 27), AutoSize = true, ForeColor = SystemColors.GrayText };
        par.Controls.Add(cbThreads); par.Controls.Add(lPar);

        // IR clip: optionally remap the IR channel's [min,max] window onto the full 0..65535 range before ICE, on
        // every file in the batch. Handy for narrow, low-contrast IR (e.g. Epson V600 flatbed). Off by default =
        // no-op. The window is entered as raw 16-bit IR levels; use the Visualize tab's histogram to pick them.
        var irGroup = new GroupBox { Text = "IR", Width = 206, Height = 110, Margin = new Padding(0, 0, 0, 6) };
        cbClip = new CheckBox { Text = "Clip IR", Location = new Point(12, 24), AutoSize = true };
        var lClipLo = new Label { Text = "min", Location = new Point(12, 55), AutoSize = true, ForeColor = SystemColors.GrayText };
        numClipLo = new NumericUpDown { Location = new Point(44, 52), Width = 58, Minimum = 0, Maximum = 65535, Increment = 256, Value = 0, Enabled = false };
        var lClipHi = new Label { Text = "max", Location = new Point(110, 55), AutoSize = true, ForeColor = SystemColors.GrayText };
        numClipHi = new NumericUpDown { Location = new Point(142, 52), Width = 58, Minimum = 0, Maximum = 65535, Increment = 256, Value = 65535, Enabled = false };
        var irHint = new Label { Text = "Clip IR for non-Coolscan scanners", Location = new Point(12, 80), Size = new Size(188, 22), ForeColor = SystemColors.GrayText, Font = new Font("Segoe UI", 9f, FontStyle.Italic) };
        cbClip.CheckedChanged += (s, e) => { numClipLo.Enabled = numClipHi.Enabled = cbClip.Checked; };
        irGroup.Controls.Add(cbClip); irGroup.Controls.Add(lClipLo); irGroup.Controls.Add(numClipLo);
        irGroup.Controls.Add(lClipHi); irGroup.Controls.Add(numClipHi); irGroup.Controls.Add(irHint);

        // Output naming: either drop results in a subfolder, or keep them in the same folder with a distinct name
        // (a prefix and/or suffix, so the .dng never overwrites the source).
        // Laid out with a top-down FlowLayoutPanel rather than absolute positions: the flow stacks each control
        // below the previous with margins, so nothing can overlap and the caption inset is honored at any DPI.
        // (Absolute positioning kept letting the AutoSize "Subfolder:" radio cover the textbox at 200% scaling.)
        var outGroup = new GroupBox { Text = "Output", Width = 206, Height = 188, Margin = new Padding(0, 0, 0, 6) };
        var outFlow = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown,
                                            WrapContents = false, Padding = new Padding(8, 2, 8, 2) };
        rSubfolder = new RadioButton { Text = "Subfolder:", AutoSize = true, Checked = true, Margin = new Padding(2, 2, 0, 0) };
        tbSubfolder = new TextBox { Text = "iced", Width = 168, Margin = new Padding(18, 2, 0, 10) };
        rSameFolder = new RadioButton { Text = "Same folder", AutoSize = true, Margin = new Padding(2, 0, 0, 2) };
        var lPre = new Label { Text = "prefix", AutoSize = true, ForeColor = SystemColors.GrayText, Margin = new Padding(18, 6, 0, 0) };
        tbPrefix = new TextBox { Text = "", Width = 168, Enabled = false, Margin = new Padding(18, 0, 0, 6) };
        var lSuf = new Label { Text = "suffix", AutoSize = true, ForeColor = SystemColors.GrayText, Margin = new Padding(18, 0, 0, 0) };
        tbSuffix = new TextBox { Text = "_ice", Width = 168, Enabled = false, Margin = new Padding(18, 0, 0, 0) };
        outFlow.Controls.Add(rSubfolder); outFlow.Controls.Add(tbSubfolder); outFlow.Controls.Add(rSameFolder);
        outFlow.Controls.Add(lPre); outFlow.Controls.Add(tbPrefix); outFlow.Controls.Add(lSuf); outFlow.Controls.Add(tbSuffix);
        outGroup.Controls.Add(outFlow);
        EventHandler outMode = (s, e) =>
        {
            tbSubfolder.Enabled = rSubfolder.Checked;
            tbPrefix.Enabled = tbSuffix.Enabled = rSameFolder.Checked;
            UpdateOutputExample();
        };
        rSubfolder.CheckedChanged += outMode;
        rSameFolder.CheckedChanged += outMode;
        EventHandler outText = (s, e) => UpdateOutputExample();
        tbSubfolder.TextChanged += outText;
        tbPrefix.TextChanged += outText;
        tbSuffix.TextChanged += outText;

        outExample = new Label
        {
            Width = 206, AutoSize = true, MaximumSize = new Size(206, 0), ForeColor = SystemColors.GrayText,
            Margin = new Padding(0, 4, 0, 0)
        };

        var settings = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false,
            AutoScroll = true, Padding = new Padding(12, 12, 12, 12)
        };
        // trailing spacer: FlowLayoutPanel's AutoScroll can stop one item short, so a small spacer after the last
        // real control (outExample) guarantees the whole list -- including that caption -- scrolls into view.
        var scrollPad = new Panel { Width = 1, Height = 1, Margin = new Padding(0, 0, 0, 12) };
        foreach (var c in new Control[] { addBtn, removeBtn, clearBtn, qual, scan, par, irGroup, outGroup, outExample, scrollPad }) settings.Controls.Add(c);
        UpdateOutputExample();

        iceBtn = new Button
        {
            Text = "RUN ICE", Dock = DockStyle.Fill, Margin = new Padding(6, 6, 12, 12),
            Font = new Font("Segoe UI", 14f, FontStyle.Bold), BackColor = Color.FromArgb(220, 236, 255)
        };
        iceBtn.Click += OnIce;

        // --- bottom-left (under the list): progress bar over a status row (status left, version/About at right) ---
        progress = new ProgressBar { Dock = DockStyle.Top, Height = 22, Maximum = 100 };
        status = new Label
        {
            Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft,
            Text = "Ready. Drag RGBI DNG images into the list."
        };
        var aboutLink = new LinkLabel
        {
            Text = "openICE " + OpenIce.Version, Dock = DockStyle.Right, AutoSize = true,
            TextAlign = ContentAlignment.MiddleRight, LinkBehavior = LinkBehavior.HoverUnderline,
            LinkColor = SystemColors.GrayText, ActiveLinkColor = SystemColors.HotTrack,
            Padding = new Padding(10, 0, 0, 0)
        };
        aboutLink.LinkClicked += (s, e) => ShowAbout();
        var statusRow = new Panel { Dock = DockStyle.Bottom, Height = 20 };
        statusRow.Controls.Add(status);      // Fill -- added first so the version link (Right) takes the right edge
        statusRow.Controls.Add(aboutLink);
        var bottomLeft = new Panel { Dock = DockStyle.Fill, Padding = new Padding(12, 8, 6, 8) };
        bottomLeft.Controls.Add(progress);
        bottomLeft.Controls.Add(statusRow);

        // --- assemble: list | settings on top; progress (under the list) | RUN ICE (bottom-right corner) below ---
        var grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2 };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 250f));
        grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
        grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 64f));
        grid.Controls.Add(list, 0, 0);
        grid.Controls.Add(settings, 1, 0);
        grid.Controls.Add(bottomLeft, 0, 1);
        grid.Controls.Add(iceBtn, 1, 1);

        // --- tabs: Batch (the grid above) + Single (visual before/after) ---
        var tabs = new TabControl { Dock = DockStyle.Fill };
        var tabBatch = new TabPage("Batch") { UseVisualStyleBackColor = true };
        tabBatch.Controls.Add(grid);
        var tabSingle = new TabPage("Visualize") { UseVisualStyleBackColor = true };
        tabSingle.Controls.Add(BuildSingleTab());
        tabs.TabPages.Add(tabBatch);
        tabs.TabPages.Add(tabSingle);
        Controls.Add(tabs);

        etaTimer = new System.Windows.Forms.Timer { Interval = 500 };
        etaTimer.Tick += (s, e) => UpdateEta();

        FormClosing += (s, e) => { if (running) cancelReq = true; ClearResult(); };

        // DPI: now that every control is added, set the Font-mode autoscale baseline (Segoe UI 9pt @ 96dpi = 7x15)
        // and resume layout, so the whole tree scales up to the display DPI in one pass. This MUST come after all
        // the controls exist -- set earlier, only the controls present at that moment scale and the rest stay at
        // 96-dpi sizes (the "squeezed" look). The app.manifest is what makes the process DPI-aware / crisp.
        AutoScaleDimensions = new SizeF(7f, 15f);
        AutoScaleMode = AutoScaleMode.Font;
        ResumeLayout(false);
        PerformLayout();
    }

    Button MakeButton(string text, EventHandler onClick)
    {
        var b = new Button { Text = text, Width = 206, Height = 30, Margin = new Padding(0, 0, 0, 6) };
        b.Click += onClick;
        return b;
    }

    // The "About openICE" dialog (Help menu). Mirrors the README's one-liner + the GPL/copyright notice; the
    // version string is the CLI's OpenIce.Version, so the two stay in step.
    void ShowAbout()
    {
        using (var dlg = new Form
        {
            Text = "About openICE", Font = new Font("Segoe UI", 9f),
            FormBorderStyle = FormBorderStyle.FixedDialog, StartPosition = FormStartPosition.CenterParent,
            MaximizeBox = false, MinimizeBox = false, ShowInTaskbar = false, ClientSize = new Size(468, 320)
        })
        {
            dlg.SuspendLayout();
            var title = new Label { Text = "openICE", AutoSize = true, Location = new Point(22, 18),
                                    Font = new Font("Segoe UI", 20f, FontStyle.Bold) };
            var ver = new Label { Text = "Version " + OpenIce.Version, AutoSize = true, Location = new Point(26, 62),
                                  ForeColor = SystemColors.GrayText };
            var body = new Label
            {
                Location = new Point(26, 96), Size = new Size(416, 140), AutoSize = false,
                Text =
                    "An open reimplementation of Digital ICE " +
                    "infrared dust-and-scratch removal for film scans. It reads an RGBI DNG and writes " +
                    "a clean RGB DNG.\r\n\r\n" +
                    "Based on the Nikon Coolscan LS-5000 / 9000 profile in Nikon Scan 4.\r\n\r\n" +
                    "Free software under the GNU General Public License v3.0.\r\n" +
                    "Copyright © 2026 <a6o>."
            };
            var repo = new LinkLabel
            {
                Text = "https://github.com/a6o/openICE", AutoSize = true, Location = new Point(26, 244)
            };
            repo.LinkClicked += (s, e) =>
            {
                try { System.Diagnostics.Process.Start("https://github.com/a6o/openICE"); }
                catch (Exception ex) { MessageBox.Show(dlg, ex.Message, "openICE"); }
            };
            var ok = new Button
            {
                Text = "OK", DialogResult = DialogResult.OK, Size = new Size(88, 28),
                Location = new Point(dlg.ClientSize.Width - 110, dlg.ClientSize.Height - 40),
                Anchor = AnchorStyles.Bottom | AnchorStyles.Right
            };
            dlg.Controls.Add(title); dlg.Controls.Add(ver); dlg.Controls.Add(body); dlg.Controls.Add(repo); dlg.Controls.Add(ok);
            dlg.AutoScaleDimensions = new SizeF(7f, 15f);   // baseline set AFTER the controls are added, then scale
            dlg.AutoScaleMode = AutoScaleMode.Font;
            dlg.ResumeLayout(false);
            dlg.AcceptButton = ok; dlg.CancelButton = ok;
            dlg.ShowDialog(this);
        }
    }

    // ---- list management ----
    void AddViaDialog()
    {
        if (running) return;
        using (var ofd = new OpenFileDialog { Multiselect = true, Title = "Add RGBI DNG images",
                                              Filter = "DNG images|*.dng|All files|*.*" })
            if (ofd.ShowDialog(this) == DialogResult.OK) AddFiles(ofd.FileNames);
    }

    void AddFiles(IEnumerable<string> paths)
    {
        if (running) return;
        foreach (var p in paths)
        {
            if (Directory.Exists(p)) { foreach (var f in Directory.GetFiles(p, "*.dng")) AddOne(f); }
            else if (File.Exists(p)) AddOne(p);
        }
        UpdateReadyStatus();
    }

    void AddOne(string path)
    {
        foreach (ListViewItem it in list.Items)
            if (string.Equals((string)it.Tag, path, StringComparison.OrdinalIgnoreCase)) return;   // no dupes
        var item = new ListViewItem(Path.GetFileName(path)) { Tag = path, ToolTipText = path };
        item.SubItems.Add(Path.GetDirectoryName(path));
        item.SubItems.Add("Queued");
        list.Items.Add(item);
    }

    void RemoveSelected()
    {
        if (running) return;
        var sel = new List<ListViewItem>();
        foreach (ListViewItem it in list.SelectedItems) sel.Add(it);
        foreach (var it in sel) list.Items.Remove(it);
        UpdateReadyStatus();
    }

    // Show what a sample input's output would be named under the current settings (updates live).
    void UpdateOutputExample()
    {
        const string sample = "{filename}";
        string ex;
        if (rSubfolder.Checked)
        {
            string sf = tbSubfolder.Text.Trim();
            ex = (sf.Length == 0 ? "<subfolder>" : sf) + "\\" + sample + ".dng";
        }
        else
        {
            string p = tbPrefix.Text, s = tbSuffix.Text;
            ex = (p.Length == 0 && s.Length == 0) ? "(set a prefix and/or suffix)" : p + sample + s + ".dng";
        }
        outExample.Text = "Output: " + ex;
    }

    void UpdateReadyStatus()
    {
        if (!running) status.Text = list.Items.Count == 0
            ? "Ready. Drag RGBI DNG images into the list."
            : list.Items.Count + " image(s) queued. Pick settings and press ICE.";
    }

    // ---- run ----
    void OnIce(object sender, EventArgs e)
    {
        if (running) { cancelReq = true; status.Text = "Cancelling…"; return; }
        if (list.Items.Count == 0) { MessageBox.Show(this, "Add some RGBI DNG images first.", "openICE"); return; }

        // --- output naming, captured + validated on the UI thread ---
        bool sub = rSubfolder.Checked;
        string subName = tbSubfolder.Text.Trim(), prefix = tbPrefix.Text, suffix = tbSuffix.Text;
        char[] bad = Path.GetInvalidFileNameChars();
        if (sub)
        {
            if (subName.Length == 0 || subName.IndexOfAny(bad) >= 0)
            { MessageBox.Show(this, "Enter a valid subfolder name.", "openICE"); return; }
        }
        else
        {
            if (prefix.Length == 0 && suffix.Length == 0)
            { MessageBox.Show(this, "Output filename cannot be same as the input filename!", "openICE"); return; }
            if ((prefix + suffix).IndexOfAny(bad) >= 0)
            { MessageBox.Show(this, "Prefix/suffix contain invalid filename characters.", "openICE"); return; }
        }

        var files = new List<string>();
        foreach (ListViewItem it in list.Items) { files.Add((string)it.Tag); it.SubItems[2].Text = "Queued"; }
        bool fine = rFine.Checked;
        int kind = k7.Checked ? 7 : (k9.Checked ? 9 : 8);
        int threads = 4; int.TryParse((string)cbThreads.SelectedItem, out threads); if (threads < 1) threads = 1;
        // IR clip window (raw 16-bit levels), captured on the UI thread; an empty/inverted window means "off"
        int clipLo = 0, clipHi = 0;
        if (cbClip.Checked && (int)numClipHi.Value > (int)numClipLo.Value)
        { clipLo = (int)numClipLo.Value; clipHi = (int)numClipHi.Value; }

        totalImages = files.Count; overallPermille = 0; lastOk = 0; lastFailed = 0;
        startTime = DateTime.Now;
        running = true; cancelReq = false;
        SetUiRunning(true);
        etaTimer.Start();
        UpdateEta();
        var th = new Thread(() =>
        {
            try { RunBatch(files.ToArray(), fine, kind, sub, subName, prefix, suffix, threads, clipLo, clipHi); }
            finally { Ui(() => { running = false; etaTimer.Stop(); SetUiRunning(false); ShowFinalStatus(); }); }
        }) { IsBackground = true };
        th.Start();
    }

    // Per-batch shared progress/counters, updated by the parallel workers (Interlocked). imgPct[i] is image i's
    // progress in permille (0..1000) and is written only by the one worker that owns image i; sumPermille is their
    // running total, so overall = sumPermille / total. ok/failed are Interlocked counters; next hands out work.
    class BatchProg { public int[] imgPct; public long sumPermille; public int ok, failed, next, total; }

    void RunBatch(string[] files, bool fine, int kind, bool sub, string subName, string prefix, string suffix, int threads, int clipLo, int clipHi)
    {
        int total = files.Length;
        batchClipLo = clipLo; batchClipHi = clipHi;   // batch-constant IR clip window, read by every worker
        // batch-constant: set once here, before any worker starts, so the pipeline's statics are never mutated
        // while the parallel reconstructions are reading them (all other per-image state lives in IceState).
        IceSetup.Fine = fine;
        IceSetup.Kind = kind;

        var bp = new BatchProg { imgPct = new int[total], total = total };

        int nThreads = Math.Max(1, Math.Min(threads, total));   // no point spawning more workers than images
        var workers = new Thread[nThreads];
        for (int w = 0; w < nThreads; w++)
        {
            workers[w] = new Thread(() =>
            {
                while (!cancelReq)
                {
                    int fi = Interlocked.Increment(ref bp.next) - 1;   // pull the next image (0,1,2,...)
                    if (fi >= total) break;
                    ProcessOne(bp, files[fi], fi, sub, subName, prefix, suffix);
                }
            }) { IsBackground = true };
        }
        foreach (var t in workers) t.Start();
        foreach (var t in workers) t.Join();

        lastOk = bp.ok; lastFailed = bp.failed;
        if (!cancelReq) { overallPermille = 1000; SetOverall(100); }
        // the final status line is set by ShowFinalStatus() from the OnIce finally (UI thread)
    }

    // Reconstruct one image (runs on a worker thread). Resolves the output path, guards against overwriting the
    // source, then streams the row progress into this image's slot. On cancel the partial output is deleted.
    void ProcessOne(BatchProg bp, string inPath, int fi, bool sub, string subName, string prefix, string suffix)
    {
        string dir = Path.GetDirectoryName(inPath), baseName = Path.GetFileNameWithoutExtension(inPath);
        string outPath;
        if (sub)
        {
            string od = Path.Combine(dir, subName);
            try { Directory.CreateDirectory(od); }   // idempotent + safe if several workers share the subfolder
            catch (Exception ex) { FailOne(bp, fi, "Error: " + ex.Message); return; }
            outPath = Path.Combine(od, baseName + ".dng");
        }
        else outPath = Path.Combine(dir, prefix + baseName + suffix + ".dng");

        // never write over the source
        if (string.Equals(Path.GetFullPath(outPath), Path.GetFullPath(inPath), StringComparison.OrdinalIgnoreCase))
        { FailOne(bp, fi, "Skipped (would overwrite input)"); return; }

        SetItemStatus(fi, "Processing…");
        try
        {
            // openICE is compiled in -- call the shared core directly, streaming its per-row progress into this
            // image's slot. Throwing from onProgress aborts the pump; Process still closes the output stream
            // (finally), so the partial file can be deleted below.
            OpenIce.Process(inPath, outPath, null, 0, null, null, null, null,
                delegate (int r, int t)
                {
                    if (cancelReq) throw new OperationCanceledException();
                    if (t <= 0) return;
                    int pct = (int)(100L * r / t);
                    SetItemStatus(fi, "Processing… " + pct + "%");
                    SetImgProgress(bp, fi, pct * 10);   // permille
                },
                batchClipLo, batchClipHi);
            Interlocked.Increment(ref bp.ok);
            SetItemStatus(fi, "Done");
            SetImgProgress(bp, fi, 1000);
        }
        catch (OperationCanceledException)
        {
            SetItemStatus(fi, "Cancelled");
            try { if (File.Exists(outPath)) File.Delete(outPath); } catch { }   // drop the partial output
        }
        catch (Exception ex) { FailOne(bp, fi, "Error: " + ex.Message); }
    }

    void FailOne(BatchProg bp, int fi, string msg)
    {
        Interlocked.Increment(ref bp.failed);
        SetItemStatus(fi, msg);
        SetImgProgress(bp, fi, 1000);   // count this image as "resolved" so the overall bar still advances
    }

    // Advance image fi's progress and fold the delta into the batch total. Only fi's own worker calls this for fi,
    // so imgPct[fi] needs no lock; the shared sum is Interlocked. The UI timer reads overallPermille to move the bar.
    void SetImgProgress(BatchProg bp, int fi, int permille)
    {
        int old = bp.imgPct[fi];
        if (permille <= old) return;
        bp.imgPct[fi] = permille;
        long sum = Interlocked.Add(ref bp.sumPermille, permille - old);
        overallPermille = (int)(sum / bp.total);
    }

    // Live readout under the bar: seconds-per-image and elapsed / estimated-total as HH:MM:SS / HH:MM:SS. Runs on
    // the UI thread (Windows.Forms.Timer) so it can touch the label directly. Estimates from the overall progress.
    void UpdateEta()
    {
        if (!running) return;
        TimeSpan el = DateTime.Now - startTime;
        int perm = overallPermille;
        progress.Value = Math.Max(0, Math.Min(100, perm / 10));   // workers only update the field; the bar moves here
        string spi = "—", tot = "--:--:--";
        if (perm >= 5)   // >= 0.5% done: enough signal to project
        {
            double totSec = el.TotalSeconds * 1000.0 / perm;
            tot = Fmt(TimeSpan.FromSeconds(totSec));
            spi = (totSec / Math.Max(1, totalImages)).ToString("0.0");
        }
        status.Text = spi + " s/image     " + Fmt(el) + " / " + tot;
    }

    void ShowFinalStatus()
    {
        TimeSpan el = DateTime.Now - startTime;
        string spi = lastOk > 0 ? (el.TotalSeconds / lastOk).ToString("0.0") : "—";
        status.Text = (cancelReq ? "Cancelled" : "Finished") + " in " + Fmt(el) + "  (" + spi + " s/image).  "
                    + lastOk + " done" + (lastFailed > 0 ? ", " + lastFailed + " failed." : ".");
    }

    static string Fmt(TimeSpan t) { return string.Format("{0:00}:{1:00}:{2:00}", (int)t.TotalHours, t.Minutes, t.Seconds); }

    void SetUiRunning(bool run)
    {
        Ui(() =>
        {
            addBtn.Enabled = removeBtn.Enabled = clearBtn.Enabled = !run;
            rNormal.Enabled = rFine.Enabled = k8.Enabled = k7.Enabled = k9.Enabled = !run;
            rSubfolder.Enabled = rSameFolder.Enabled = !run;
            cbThreads.Enabled = !run;
            cbClip.Enabled = !run;
            numClipLo.Enabled = numClipHi.Enabled = !run && cbClip.Checked;
            tbSubfolder.Enabled = !run && rSubfolder.Checked;
            tbPrefix.Enabled = tbSuffix.Enabled = !run && rSameFolder.Checked;
            list.AllowDrop = !run;
            iceBtn.Text = run ? "Cancel" : "RUN ICE";
            if (!run) progress.Value = 0;
        });
    }

    // ---- Single tab: run one image and show an original/ICE wipe comparison ----
    Control BuildSingleTab()
    {
        int btnH = 26;   // toolbar button height -- one knob for all four buttons (scales with DPI)
        sOpenBtn = new Button { Text = "Open Image…", AutoSize = false, Size = new Size(112, btnH), Margin = new Padding(0, 0, 6, 0) };
        sOpenBtn.Click += (s, e) => OpenSingle();
        sFine = new CheckBox { Text = "Fine", AutoSize = true, Margin = new Padding(6, 4, 6, 0) };
        var kindLbl = new Label { Text = "ICE type:", AutoSize = true, Margin = new Padding(6, 4, 2, 0) };
        sKind = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 150, Margin = new Padding(0, 4, 6, 0) };
        sKind.Items.AddRange(new object[] { "LS-5000 (kind 8)", "LS-9000 (kind 7)", "LS-50 (kind 9)" });
        sKind.SelectedIndex = 0;
        sRunBtn = new Button { Text = "Run ICE", AutoSize = false, Size = new Size(84, btnH), Margin = new Padding(6, 0, 6, 0) };
        sRunBtn.Click += (s, e) => RunSingle();
        sIrBtn = new Button { Text = "IR", AutoSize = false, Size = new Size(44, btnH), Margin = new Padding(6, 0, 6, 0) };
        sIrBtn.Click += (s, e) => ToggleIr();
        sSaveBtn = new Button { Text = "Save…", AutoSize = false, Size = new Size(72, btnH), Margin = new Padding(6, 0, 6, 0), Enabled = false };
        sSaveBtn.Click += (s, e) => SaveSingle();
        sProgress = new ProgressBar { Dock = DockStyle.Right, Width = 180, Maximum = 100 };
        sStatus = new Label { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, ForeColor = SystemColors.GrayText, Padding = new Padding(8, 0, 6, 0), Text = "Open an RGBI DNG to preview." };

        // toolbar (buttons only) -- AutoSize keeps it exactly one row tall at any DPI; WrapContents off keeps it on
        // one line. Progress + status live on their own strip at the bottom (added to the host below), so the wide
        // status text can't push the toolbar to a second line. "Clip IR" lives here.
        var bar = new FlowLayoutPanel { Dock = DockStyle.Top, WrapContents = false, AutoSize = true,
                                        AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(8, 5, 8, 5) };
        sStretch = new CheckBox { Text = "Clip IR", AutoSize = true, Margin = new Padding(12, 4, 6, 0) };
        sStretch.CheckedChanged += (s, e) => ApplyClipState();
        foreach (var c in new Control[] { sOpenBtn, sFine, kindLbl, sKind, sRunBtn, sIrBtn, sSaveBtn, sStretch })
            bar.Controls.Add(c);

        canvas = new CompareCanvas { Dock = DockStyle.Fill, AllowDrop = true };
        canvas.DragEnter += (s, e) => e.Effect = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        canvas.DragDrop += (s, e) => { var f = (string[])e.Data.GetData(DataFormats.FileDrop); if (f.Length > 0) OpenSingle(f[0]); };

        // IR levels area (shown only while "Clip IR" is on): the histogram with a draggable min/max window, plus
        // Auto/Full. Clip IR linearly remaps [min,max] -> the full 0..65535 range before ICE, spreading out a
        // narrow, low-contrast IR (e.g. the Epson V600 flatbed) so dust separates more.
        sAutoBtn = new Button { Text = "Auto", AutoSize = true, Margin = new Padding(0, 0, 6, 0) };
        sAutoBtn.Click += (s, e) => { if (sIrHist != null) sLevels.SetWindow(sIrDataMin, sIrDataMax); };
        sFullBtn = new Button { Text = "Full", AutoSize = true, Margin = new Padding(0, 0, 6, 0) };
        sFullBtn.Click += (s, e) => { if (sIrHist != null) sLevels.SetWindow(0, 65535); };
        var levLbl = new Label { Text = "IR histogram — drag the handles to set the clip window", AutoSize = true, ForeColor = SystemColors.GrayText, Margin = new Padding(10, 7, 0, 0) };
        var levBar = new FlowLayoutPanel { Dock = DockStyle.Top, WrapContents = false, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
        foreach (var c in new Control[] { sAutoBtn, sFullBtn, levLbl }) levBar.Controls.Add(c);
        sLevels = new IrLevels { Dock = DockStyle.Fill };
        sLevPanel = new Panel { Dock = DockStyle.Top, Height = 118, Padding = new Padding(8, 2, 8, 6), Visible = false };
        sLevPanel.Controls.Add(sLevels);   // Fill -- added first
        sLevPanel.Controls.Add(levBar);    // Top -- the Auto/Full row sits above the histogram

        // Dock assembly (top -> bottom): toolbar, IR levels (collapsible), comparison canvas (fills), status strip.
        // Add the Fill control first, then the edge-docked ones (last added docks outermost).
        var sBottom = new Panel { Dock = DockStyle.Bottom, Height = 24 };
        sBottom.Controls.Add(sStatus);     // Fill -- added first
        sBottom.Controls.Add(sProgress);   // Right
        var host = new Panel { Dock = DockStyle.Fill };
        host.Controls.Add(canvas);
        host.Controls.Add(sBottom);
        host.Controls.Add(sLevPanel);
        host.Controls.Add(bar);
        ApplyClipState();
        return host;
    }

    // The IR window is editable (handles shown) only while "Clip IR" is on. Off = full range, no clip,
    // handles hidden. On = default to the image's actual IR extent (the useful "stretch to full" start point).
    void ApplyClipState()
    {
        bool on = sStretch.Checked;
        sLevPanel.Visible = on;   // the histogram only shows while Clip IR is on (canvas reclaims the space otherwise)
        sLevels.Interactive = on;
        sAutoBtn.Enabled = sFullBtn.Enabled = on && sIrHist != null;
        if (on && sIrHist != null) sLevels.SetWindow(sIrDataMin, sIrDataMax);
        else sLevels.SetWindow(0, 65535);
    }

    void OpenSingle()
    {
        if (sRunning) return;
        using (var ofd = new OpenFileDialog { Title = "Open RGBI DNG", Filter = "DNG images|*.dng|All files|*.*" })
            if (ofd.ShowDialog(this) == DialogResult.OK) OpenSingle(ofd.FileName);
    }

    void OpenSingle(string path)
    {
        if (sRunning) return;
        sImagePath = path;
        ClearResult();   // a new image invalidates the previous ICE result (disables Save until the next run)
        sRunning = true; SetSingleRunning(true);
        sStatus.Text = "Loading " + Path.GetFileName(path) + " …";
        var th = new Thread(() =>
        {
            Bitmap bmp = null; string err = null; int[] irh = null; int irMin = 0, irMax = 65535;
            try { bmp = RenderDng(path, 4000); irh = ComputeIrHist(path, out irMin, out irMax); } catch (Exception ex) { err = ex.Message; }
            Ui(() =>
            {
                sRunning = false; SetSingleRunning(false);
                sShowingIr = false; sIrReady = false; sIrBtn.Text = "IR";   // SetImages disposes any old IR overlay
                if (err != null) { sStatus.Text = "Error: " + err; canvas.SetImages(null, null); sIrHist = null; sLevels.SetHist(null, 0, 65535); ApplyClipState(); return; }
                canvas.SetImages(bmp, null);
                sIrHist = irh; sIrDataMin = irMin; sIrDataMax = irMax;   // default the clip window to the image's actual IR range
                sLevels.SetHist(irh, irMin, irMax);
                ApplyClipState();
                sStatus.Text = Path.GetFileName(path) + "  —  press Run ICE";
            });
        }) { IsBackground = true };
        th.Start();
    }

    void RunSingle()
    {
        if (sRunning) return;
        if (sImagePath == null) { MessageBox.Show(this, "Open an image first.", "openICE"); return; }
        if (running) { MessageBox.Show(this, "A batch is running -- wait for it to finish.", "openICE"); return; }
        bool fine = sFine.Checked;
        int kind = sKind.SelectedIndex == 1 ? 7 : (sKind.SelectedIndex == 2 ? 9 : 8);
        int clipLo = 0, clipHi = 0;   // IR levels stretch (read on the UI thread before the worker starts)
        if (sStretch.Checked && sIrHist != null) { clipLo = sLevels.Lo; clipHi = sLevels.Hi; }
        string inPath = sImagePath;
        if (sShowingIr) { sShowingIr = false; canvas.ShowIr(false); sIrBtn.Text = "IR"; }   // show the RGB result, not IR
        sRunning = true; SetSingleRunning(true);
        Ui(() => { sProgress.Value = 0; sStatus.Text = "Processing…"; });
        var th = new Thread(() =>
        {
            string temp = Path.Combine(Path.GetTempPath(), "openice_preview_" + Guid.NewGuid().ToString("N") + ".dng");
            Bitmap after = null; string err = null; bool ok = false;
            try
            {
                IceSetup.Fine = fine; IceSetup.Kind = kind;
                OpenIce.Process(inPath, temp, null, 0, null, null, null, null,
                    delegate (int r, int t) { if (t > 0) Ui(() => sProgress.Value = Math.Max(0, Math.Min(100, (int)(100L * r / t)))); },
                    clipLo, clipHi);
                after = RenderDng(temp, 4000);
                ok = true;
            }
            catch (Exception ex) { err = ex.Message; }
            if (!ok) { try { if (File.Exists(temp)) File.Delete(temp); } catch { } }   // keep it on success (for Save)
            Ui(() =>
            {
                if (ok) { ClearResult(); sResultPath = temp; }   // this result replaces any previous one; enables Save
                sRunning = false; SetSingleRunning(false);
                if (err != null) { sStatus.Text = "Error: " + err; MessageBox.Show(this, err, "openICE"); return; }
                canvas.SetAfter(after);
                sStatus.Text = "Done. Drag the divider to compare, or Save the result.";
            });
        }) { IsBackground = true };
        th.Start();
    }

    void SetSingleRunning(bool run) { Ui(() => { sOpenBtn.Enabled = sRunBtn.Enabled = sIrBtn.Enabled = sFine.Enabled = sKind.Enabled = sStretch.Enabled = !run; sSaveBtn.Enabled = !run && sResultPath != null; }); }

    // Drop the kept ICE result (a temp DNG). Called on the UI thread when a new image is opened or a new run replaces it.
    void ClearResult() { if (sResultPath != null) { try { if (File.Exists(sResultPath)) File.Delete(sResultPath); } catch { } sResultPath = null; } }

    // Save the previewed ICE result to a user-chosen .dng (a copy of the kept temp output, so it matches what's shown).
    void SaveSingle()
    {
        if (sResultPath == null || !File.Exists(sResultPath)) { MessageBox.Show(this, "Run ICE first, then Save.", "openICE"); return; }
        string baseName = sImagePath != null ? Path.GetFileNameWithoutExtension(sImagePath) : "openice";
        using (var sfd = new SaveFileDialog { Title = "Save ICE result", Filter = "DNG image|*.dng", FileName = baseName + "_ice.dng",
                                              InitialDirectory = sImagePath != null ? Path.GetDirectoryName(sImagePath) : null })
        {
            if (sfd.ShowDialog(this) != DialogResult.OK) return;
            if (sImagePath != null && string.Equals(Path.GetFullPath(sfd.FileName), Path.GetFullPath(sImagePath), StringComparison.OrdinalIgnoreCase))
            { MessageBox.Show(this, "Choose a different name -- that would overwrite the input.", "openICE"); return; }
            try { File.Copy(sResultPath, sfd.FileName, true); sStatus.Text = "Saved " + Path.GetFileName(sfd.FileName); }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "openICE"); }
        }
    }

    // Toggle the IR overlay: show the infrared channel (what ICE uses to detect dust) as a stretched grayscale, or
    // switch back to the RGB before/after view. The IR bitmap is rendered once on first view, then cached.
    void ToggleIr()
    {
        if (sRunning) return;
        if (sImagePath == null) { MessageBox.Show(this, "Open an image first.", "openICE"); return; }
        if (sShowingIr) { sShowingIr = false; canvas.ShowIr(false); sIrBtn.Text = "IR"; sStatus.Text = "RGB view."; return; }
        if (sIrReady) { sShowingIr = true; canvas.ShowIr(true); sIrBtn.Text = "RGB"; sStatus.Text = "Infrared channel — dark spots are the dust/scratches ICE removes."; return; }

        string inPath = sImagePath;
        sRunning = true; SetSingleRunning(true);
        sStatus.Text = "Rendering infrared…";
        var th = new Thread(() =>
        {
            Bitmap ir = null; string err = null;
            try { ir = RenderIr(inPath, 4000); } catch (Exception ex) { err = ex.Message; }
            Ui(() =>
            {
                sRunning = false; SetSingleRunning(false);
                if (err != null) { sStatus.Text = "Error: " + err; return; }
                if (ir == null) { MessageBox.Show(this, "This image has no infrared channel.", "openICE"); return; }
                canvas.SetIr(ir); sIrReady = true; sShowingIr = true; canvas.ShowIr(true); sIrBtn.Text = "RGB";
                sStatus.Text = "Infrared channel — dark spots are the dust/scratches ICE removes.";
            });
        }) { IsBackground = true };
        th.Start();
    }

    // Compute a 256-bucket histogram of the IR channel (bucket = IR >> 8), plus the actual min/max, for the levels
    // control. Subsampled for speed. Returns null if the DNG has no IR channel.
    static int[] ComputeIrHist(string path, out int dataMin, out int dataMax)
    {
        dataMin = 0; dataMax = 65535;
        Dng d = Dng.Open(path);
        if (!d.HasIr) return null;
        int W = d.W, H = d.H;
        int[] map = d.MakeMap(null);
        int step = 1; while (Math.Max(W, H) / step > 2000) step++;
        int[] hist = new int[256];
        short[] srow = new short[W * 4];
        int mn = 65535, mx = 0;
        for (int iy = 0; iy < H; iy += step)
        {
            d.LoadRow(iy, map, srow);
            for (int ix = 0; ix < W; ix += step)
            {
                int v = (ushort)srow[ix * 4 + 3];
                hist[v >> 8]++;
                if (v < mn) mn = v; if (v > mx) mx = v;
            }
        }
        if (mx <= mn) { mn = 0; mx = 65535; }
        dataMin = mn; dataMax = mx;
        return hist;
    }

    // Render the IR channel to a viewable grayscale bitmap, auto-stretched (min..max -> full range, gamma 2.2) so the
    // dust and scratches stand out against the near-uniform film base. Returns null if the DNG has no IR channel.
    static Bitmap RenderIr(string path, int maxDim)
    {
        Dng d = Dng.Open(path);
        if (!d.HasIr) return null;
        int W = d.W, H = d.H;
        int[] map = d.MakeMap(null);
        int step = 1; while (Math.Max(W, H) / step > maxDim) step++;
        int ow = (W + step - 1) / step, oh = (H + step - 1) / step;

        ushort[] ir = new ushort[ow * oh];
        short[] srow = new short[W * 4];
        int mn = 65535, mx = 0;
        for (int oy = 0; oy < oh; oy++)
        {
            int iy = oy * step; if (iy >= H) iy = H - 1;
            d.LoadRow(iy, map, srow);
            for (int ox = 0; ox < ow; ox++)
            {
                int ix = ox * step; if (ix >= W) ix = W - 1;
                int v = (ushort)srow[ix * 4 + 3];
                ir[oy * ow + ox] = (ushort)v;
                if (v < mn) mn = v; if (v > mx) mx = v;
            }
        }
        if (mx <= mn) mx = mn + 1;

        var bmp = new Bitmap(ow, oh, PixelFormat.Format24bppRgb);
        var bd = bmp.LockBits(new Rectangle(0, 0, ow, oh), ImageLockMode.WriteOnly, PixelFormat.Format24bppRgb);
        try
        {
            byte[] orow = new byte[bd.Stride];
            double inv = 1.0 / (mx - mn);
            for (int oy = 0; oy < oh; oy++)
            {
                int p = 0;
                for (int ox = 0; ox < ow; ox++)
                {
                    double t = (ir[oy * ow + ox] - mn) * inv;             // stretch min..max -> 0..1
                    byte g = (byte)(Math.Pow(t, 1.0 / 2.2) * 255.0 + 0.5);
                    orow[p++] = g; orow[p++] = g; orow[p++] = g;          // gray: B=G=R
                }
                Marshal.Copy(orow, 0, (IntPtr)(bd.Scan0.ToInt64() + (long)oy * bd.Stride), bd.Stride);
            }
        }
        finally { bmp.UnlockBits(bd); }
        return bmp;
    }

    // Render a 16-bit linear RGBI/RGB DNG to a viewable 8-bit bitmap (gamma 2.2), down-sampled to fit maxDim. This
    // is a rough preview (no colour management) -- the point is to see the dust and its removal, and before/after
    // use the same transform so the comparison is fair.
    static byte[] gammaLut;
    static Bitmap RenderDng(string path, int maxDim)
    {
        if (gammaLut == null)
        {
            var g = new byte[65536];
            for (int i = 0; i < 65536; i++) g[i] = (byte)(Math.Pow(i / 65535.0, 1.0 / 2.2) * 255.0 + 0.5);
            gammaLut = g;
        }
        Dng d = Dng.Open(path);
        int W = d.W, H = d.H;
        int[] map = d.MakeMap(null);
        int step = 1; while (Math.Max(W, H) / step > maxDim) step++;
        int ow = (W + step - 1) / step, oh = (H + step - 1) / step;
        var bmp = new Bitmap(ow, oh, PixelFormat.Format24bppRgb);
        var bd = bmp.LockBits(new Rectangle(0, 0, ow, oh), ImageLockMode.WriteOnly, PixelFormat.Format24bppRgb);
        try
        {
            short[] srow = new short[W * 4];
            byte[] orow = new byte[bd.Stride];
            for (int oy = 0; oy < oh; oy++)
            {
                int iy = oy * step; if (iy >= H) iy = H - 1;
                d.LoadRow(iy, map, srow);
                int p = 0;
                for (int ox = 0; ox < ow; ox++)
                {
                    int ix = ox * step; if (ix >= W) ix = W - 1;
                    int r = (ushort)srow[ix * 4 + 0], gg = (ushort)srow[ix * 4 + 1], b = (ushort)srow[ix * 4 + 2];
                    orow[p++] = gammaLut[b]; orow[p++] = gammaLut[gg]; orow[p++] = gammaLut[r];   // 24bpp is BGR
                }
                Marshal.Copy(orow, 0, (IntPtr)(bd.Scan0.ToInt64() + (long)oy * bd.Stride), bd.Stride);
            }
        }
        finally { bmp.UnlockBits(bd); }
        return bmp;
    }

    // marshal onto the UI thread
    void Ui(Action a) { if (IsHandleCreated && InvokeRequired) BeginInvoke(a); else if (IsHandleCreated) a(); }
    void SetStatus(string t) { Ui(() => status.Text = t); }
    void SetOverall(int pct) { Ui(() => progress.Value = Math.Max(0, Math.Min(100, pct))); }
    void SetItemStatus(int i, string st)
    {
        Ui(() => { if (i >= 0 && i < list.Items.Count) list.Items[i].SubItems[2].Text = st; });
    }
}

// A before/after "wipe" viewer with zoom + pan. Shows the ICE (After) image; the left part, up to a draggable
// divider, reveals the Original (Before). Interactions:
//   left-drag         move the divider
//   Ctrl + wheel      zoom, centred on the cursor (touchpad pinch maps to this in Windows)
//   wheel / Shift+wheel / two-finger scroll   pan
//   right/middle-drag pan
//   double-click      reset to fit
class CompareCanvas : Panel
{
    Bitmap before, after, ir;
    bool showIr;            // when true (and ir != null), show the IR channel full-frame instead of the wipe
    double split = 0.5;
    float scale = 0;        // display px per image px; 0 = "needs fit"
    PointF offset;          // canvas px of image pixel (0,0)
    bool dragDivider, panning;
    Point lastMouse;

    public CompareCanvas()
    {
        DoubleBuffered = true;
        BackColor = Color.FromArgb(28, 28, 30);
        SetStyle(ControlStyles.Selectable, true); TabStop = false;
        MouseEnter += (s, e) => { if (CanFocus) Focus(); };   // so the wheel reaches us without needing a click
        MouseDown += OnDown;
        MouseMove += OnMove;
        MouseUp += (s, e) => { bool was = dragDivider || panning; dragDivider = panning = false; Cursor = Cursors.Default; if (was) Invalidate(); };
        DoubleClick += (s, e) => FitView();
    }

    public void SetImages(Bitmap b, Bitmap a)
    {
        if (before != null && before != b) before.Dispose();
        if (after != null && after != a) after.Dispose();
        if (ir != null) { ir.Dispose(); ir = null; }   // a new image invalidates the cached IR overlay
        showIr = false;
        before = b; after = a; split = 0.5; scale = 0;   // new image -> refit
        Invalidate();
    }
    public void SetAfter(Bitmap a)
    {
        if (after != null && after != a) after.Dispose();
        after = a; split = 0.5;   // keep the current zoom/pan when the result arrives
        Invalidate();
    }
    public void SetIr(Bitmap b) { if (ir != null && ir != b) ir.Dispose(); ir = b; Invalidate(); }
    public void ShowIr(bool on) { showIr = on; Invalidate(); }

    float FitScale()
    {
        if (before == null) return 1;
        float iw = before.Width, ih = before.Height, cw = ClientSize.Width, ch = ClientSize.Height;
        if (iw <= 0 || ih <= 0 || cw <= 0 || ch <= 0) return 1;
        return Math.Min(cw / iw, ch / ih);
    }
    void DoFit()
    {
        scale = FitScale();
        offset = new PointF((ClientSize.Width - before.Width * scale) / 2f, (ClientSize.Height - before.Height * scale) / 2f);
    }
    void FitView() { if (before != null) { DoFit(); Invalidate(); } }
    void EnsureView() { if (before != null && scale <= 0) DoFit(); }

    void ClampOffset()
    {
        if (before == null) return;
        float iw = before.Width * scale, ih = before.Height * scale, cw = ClientSize.Width, ch = ClientSize.Height, m = 40;
        offset.X = Math.Min(cw - m, Math.Max(m - iw, offset.X));
        offset.Y = Math.Min(ch - m, Math.Max(m - ih, offset.Y));
    }

    void ZoomAt(Point c, float factor)
    {
        EnsureView();
        float fit = FitScale(), ns = Math.Max(fit, Math.Min(fit * 40f, scale * factor));
        if (ns == scale) return;
        offset.X = c.X - (c.X - offset.X) * (ns / scale);   // keep the pixel under the cursor fixed
        offset.Y = c.Y - (c.Y - offset.Y) * (ns / scale);
        scale = ns; ClampOffset(); Invalidate();
    }

    void OnDown(object s, MouseEventArgs e)
    {
        if (CanFocus) Focus();
        if (e.Button == MouseButtons.Left) { dragDivider = true; SetSplitFromMouse(e.X); }
        else if (e.Button == MouseButtons.Right || e.Button == MouseButtons.Middle) { panning = true; lastMouse = e.Location; Cursor = Cursors.SizeAll; }
    }
    void OnMove(object s, MouseEventArgs e)
    {
        if (dragDivider) SetSplitFromMouse(e.X);
        else if (panning) { offset.X += e.X - lastMouse.X; offset.Y += e.Y - lastMouse.Y; lastMouse = e.Location; ClampOffset(); Invalidate(); }
    }
    void SetSplitFromMouse(int mx)
    {
        EnsureView();
        if (before == null) return;
        float iw = before.Width * scale; if (iw <= 0) return;
        split = Math.Max(0, Math.Min(1, (mx - offset.X) / iw));
        Invalidate();
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        if ((ModifierKeys & Keys.Control) != 0) ZoomAt(e.Location, e.Delta > 0 ? 1.15f : 1f / 1.15f);   // Ctrl+wheel / pinch
        else if ((ModifierKeys & Keys.Shift) != 0) { EnsureView(); offset.X += e.Delta * 0.5f; ClampOffset(); Invalidate(); }
        else { EnsureView(); offset.Y += e.Delta * 0.5f; ClampOffset(); Invalidate(); }   // two-finger scroll = pan
    }

    protected override void WndProc(ref Message m)
    {
        const int WM_MOUSEHWHEEL = 0x020E;   // horizontal (two-finger) scroll -- WinForms doesn't surface it
        if (m.Msg == WM_MOUSEHWHEEL)
        {
            int delta = (short)((m.WParam.ToInt64() >> 16) & 0xFFFF);
            EnsureView(); offset.X -= delta * 0.5f; ClampOffset(); Invalidate();
            m.Result = (IntPtr)1; return;
        }
        base.WndProc(ref m);
    }

    protected override void OnResize(EventArgs e) { base.OnResize(e); scale = 0; Invalidate(); }   // refit on resize

    void DrawPart(Graphics g, Bitmap img)   // draw only the visible source region -> fast at any zoom
    {
        int iw = img.Width, ih = img.Height;
        float cw = ClientSize.Width, ch = ClientSize.Height;
        float sx0 = Math.Max(0, (0 - offset.X) / scale), sy0 = Math.Max(0, (0 - offset.Y) / scale);
        float sx1 = Math.Min(iw, (cw - offset.X) / scale), sy1 = Math.Min(ih, (ch - offset.Y) / scale);
        if (sx1 <= sx0 || sy1 <= sy0) return;
        var src = new RectangleF(sx0, sy0, sx1 - sx0, sy1 - sy0);
        var dst = new RectangleF(offset.X + sx0 * scale, offset.Y + sy0 * scale, (sx1 - sx0) * scale, (sy1 - sy0) * scale);
        g.DrawImage(img, dst, src, GraphicsUnit.Pixel);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        if (before == null)
        {
            TextRenderer.DrawText(g, "Open an RGBI DNG (or drag one here)", Font, ClientRectangle,
                Color.Gray, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            return;
        }
        EnsureView();
        // Bilinear at fit is the pricey downscale of a large preview. While actively dragging the divider or panning
        // -- repainting on every mouse-move, and at high DPI that's ~4x the pixels -- drop to NearestNeighbor for
        // speed; the mouse-up Invalidate then repaints once at full quality.
        g.InterpolationMode = (dragDivider || panning || scale > FitScale() * 1.5f)
            ? System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor
            : System.Drawing.Drawing2D.InterpolationMode.Bilinear;
        g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;

        if (showIr && ir != null)   // IR overlay: full-frame grayscale, no wipe (there is no "after" for the IR)
        {
            DrawPart(g, ir);
            using (var br = new SolidBrush(Color.FromArgb(150, 0, 0, 0))) g.FillRectangle(br, 6, 6, 74, 19);
            TextRenderer.DrawText(g, "Infrared", Font, new Point(9, 7), Color.White);
            return;
        }

        DrawPart(g, after ?? before);
        if (after != null)
        {
            float sx = offset.X + (float)(split * before.Width * scale);
            g.SetClip(new RectangleF(0, 0, Math.Max(0, sx), ClientSize.Height));
            DrawPart(g, before);
            g.ResetClip();
            using (var pen = new Pen(Color.White, 2)) g.DrawLine(pen, sx, 0, sx, ClientSize.Height);
            using (var br = new SolidBrush(Color.White)) g.FillEllipse(br, sx - 7, ClientSize.Height / 2f - 7, 14, 14);
            using (var br = new SolidBrush(Color.FromArgb(150, 0, 0, 0)))
            { g.FillRectangle(br, 6, 6, 62, 19); g.FillRectangle(br, ClientSize.Width - 40, 6, 35, 19); }
            TextRenderer.DrawText(g, "Original", Font, new Point(9, 7), Color.White);
            TextRenderer.DrawText(g, "ICE", Font, new Point(ClientSize.Width - 34, 7), Color.White);
        }
    }
}

// An IR-channel histogram with two draggable vertical handles marking a [lo, hi] clip window. When "Clip IR"
// is ticked, that window is linearly remapped onto the full 0..65535 range before ICE (see Dng.IrClipLo/Hi). Drag
// either handle to move it; outside the window is shaded. The bars are log-scaled so the sparse dust tail stays
// visible. While Interactive is false the handles are hidden and the full range is shown (no clip).
class IrLevels : Panel
{
    int[] hist;                 // 256 buckets of IR values (bucket = IR >> 8)
    int lo = 0, hi = 65535;
    int drag;                   // 0 = none, 1 = lo handle, 2 = hi handle
    bool interactive;           // handles editable/visible only while "Clip IR" is on
    const int Gap = 512;        // minimum lo..hi separation

    public int Lo { get { return lo; } }
    public int Hi { get { return hi; } }
    public bool Interactive { set { interactive = value; if (!value) drag = 0; Invalidate(); } }

    public IrLevels()
    {
        DoubleBuffered = true;
        BackColor = Color.FromArgb(250, 250, 252);
        MouseDown += OnDown; MouseMove += OnMove;
        MouseUp += (s, e) => { drag = 0; };
    }

    public void SetHist(int[] h, int lo0, int hi0) { hist = h; SetWindow(lo0, hi0); }
    public void SetWindow(int lo0, int hi0)
    {
        lo = Clamp(lo0); hi = Clamp(hi0);
        if (hi < lo + Gap) hi = Math.Min(65535, lo + Gap);
        if (lo > hi - Gap) lo = Math.Max(0, hi - Gap);
        Invalidate();
    }
    static int Clamp(int v) { return v < 0 ? 0 : (v > 65535 ? 65535 : v); }

    int Xof(int v) { int w = Math.Max(1, ClientSize.Width - 1); return (int)((double)v / 65535 * w); }
    int Vof(int x) { int w = Math.Max(1, ClientSize.Width - 1); return Clamp((int)((double)x / w * 65535 + 0.5)); }

    void OnDown(object s, MouseEventArgs e)
    {
        if (hist == null || !interactive) return;
        drag = Math.Abs(e.X - Xof(lo)) <= Math.Abs(e.X - Xof(hi)) ? 1 : 2;
        OnMove(s, e);
    }
    void OnMove(object s, MouseEventArgs e)
    {
        Cursor = (hist != null && interactive) ? Cursors.VSplit : Cursors.Default;
        if (drag == 0) return;
        int v = Vof(e.X);
        if (drag == 1) lo = Math.Min(v, hi - Gap); else hi = Math.Max(v, lo + Gap);
        lo = Clamp(lo); hi = Clamp(hi);
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics; int W = ClientSize.Width, H = ClientSize.Height;
        int plot = H - (Font.Height + 4);   // reserve room for the value labels below -- scales with the DPI-scaled font
        if (hist == null)
        {
            TextRenderer.DrawText(g, "no IR channel", Font, ClientRectangle, Color.Gray,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            return;
        }
        int max = 1; foreach (int c in hist) if (c > max) max = c;
        double denom = Math.Log(max + 1);
        using (var br = new SolidBrush(Color.FromArgb(160, 90, 120, 190)))
            for (int b = 0; b < 256; b++)
            {
                if (hist[b] == 0) continue;
                int x0 = b * W / 256, x1 = (b + 1) * W / 256;
                int bh = (int)(Math.Log(hist[b] + 1) / denom * (plot - 2));
                g.FillRectangle(br, x0, plot - bh, Math.Max(1, x1 - x0), bh);
            }
        g.DrawLine(Pens.Silver, 0, plot, W, plot);
        if (interactive)
        {
            int xl = Xof(lo), xh = Xof(hi);
            using (var sh = new SolidBrush(Color.FromArgb(110, 210, 210, 216)))
            { g.FillRectangle(sh, 0, 0, xl, plot); g.FillRectangle(sh, xh, 0, W - xh, plot); }
            using (var pen = new Pen(Color.FromArgb(40, 100, 200), 2))
            { g.DrawLine(pen, xl, 0, xl, plot); g.DrawLine(pen, xh, 0, xh, plot); }
            using (var br = new SolidBrush(Color.DimGray))
            {
                g.DrawString(lo.ToString() + " (" + (lo * 100 / 65535) + "%)", Font, br, 2, plot);
                string hs = hi.ToString() + " (" + (hi * 100 / 65535) + "%)"; var sz = g.MeasureString(hs, Font);
                g.DrawString(hs, Font, br, W - sz.Width - 2, plot);
            }
        }
    }
}
