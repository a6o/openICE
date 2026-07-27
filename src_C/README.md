# openICE — C port (`src_C/`)

The **byte-exact** C reimplementation of Digital ICE. It is a 1:1 port of the C# code in [`../src/`](../src):
same stages, same file names, same logic. The difference is precision — this build reproduces Nikon Scan's
ICE **bit-for-bit**, where the C# twin only comes visually close.

Use `src_C/` when byte-exactness matters; use `../src/` (C#) when you want the readable reference.

## Byte-exact — measured

Each result below is openICE running the **whole pipeline itself** — its own low-res analysis pass, the
reconstruction, and the dither — from the captured input, compared pixel-for-pixel against the scanner's own
`_iced.dng` output. Not fed any intermediate values from the original.

| `-kind` | scanner | test frame | result |
|:--:|---|---|---|
| **8** (default) | Coolscan **LS-5000** | `frame3_f01`, `cfgnormal_f01` | **70,542,642 / 70,542,642 — 0 diff** |
| **7** | Coolscan **LS-9000** | `nk9000_f01` (real capture) | **180,068,832 / 180,068,832 — 0 diff** |
| 9 | LS-50 *(assumed)* | — | modelled, **unverified** (no hardware) |

Both verified end-to-end with the current build. `-kind 7` reaches 0 diff via `-lowres` (an ordinary low-res
scan) — the LS-9000 analysis calibration is reproduced exactly.

## Why a separate C build — the x87 requirement

The original was built with MSVC and runs the FPU in **x87** mode: 80-bit registers, rounded to 32-bit only
when a value is *stored*. To match that bit-for-bit this port **must** be compiled with:

```
-m32 -mfpmath=387
```

- `-mfpmath=387` (true x87, 80-bit intermediates) is **mandatory**. With `-mfpmath=sse` every operation rounds
  to 32 bits and the reconstruction drifts ~33 pixels/frame — close, but not exact.
- `-m32` (32-bit) matches the original's word size.

This is also why the C# build (`../src/`) can never be byte-exact: .NET evaluates in SSE `double`, so it carries
a last-bit gap the original doesn't. Same algorithm, different arithmetic model.

## Build

Needs a **32-bit MinGW gcc**. `build.ps1` puts `C:\msys64\mingw32\bin` on `PATH` for the current run.

```powershell
cd openICE\src_C
.\build.ps1            # -> openice.exe  (+ pixdiff.exe)
.\build.ps1 -verify    # also runs the nodither self-test against a reference
```

The exact command it runs:

```
gcc -m32 -O2 -msse2 -mfpmath=387 -o openice.exe \
    openice.c dng.c ice_setup.c ice_row.c ice_buffers.c ice_front.c ice_back.c \
    ice_core.c ice_analyze.c ice_pump.c ice_streamcalib.c -lm
```

## Usage

Identical CLI to the C# tool:

```
openice <in.dng> [out.dng] [options]

  -o <out.dng>        output path (default <in>.openice.dng; a bare 2nd arg also works)
  -order <RGBI>       channel order of the source samples (default RGBI; 4th letter = IR)
  -dpi <N>            override the source resolution
  -lowres <file>      low-res scan for the calibration; if omitted, the main scan is down-sampled
  -lowres_fields <V>  the three analysis fields directly: irCrosstalk,Rref,irRefRaw
  -giveup <file.pgm>  also write the give-up map (white = reconstructed, black = gave up)
  -fine               ICE Fine (default is Normal): stage gains 1.0 not 1.25, L3 clamp off
  -kind <7|8|9>       reconstruction target: 8 = LS-5000 (default), 7 = LS-9000
```

```powershell
# LS-5000, ICE Normal
.\openice.exe scan.dng -o clean.dng -lowres scan_lowres.dng

# LS-9000
.\openice.exe scan.dng -o clean.dng -lowres scan_lowres.dng -kind 7

# ICE Fine
.\openice.exe scan.dng -o clean.dng -lowres scan_lowres.dng -fine
```

`-kind` and `-fine` are independent knobs and compose. See [`../docs/parameters.md`](../docs/parameters.md) for
what each changes.

## Precision knobs (research / A-B testing)

- **FPU control word** is forced to `0x027F` (53-bit mantissa, the MSVCRT default the original runs under) at
  startup. Override for testing with `ICE_FPUCW=<hex>` (e.g. `037F` for 64-bit extended).
- **`real_t`** ([`ice.h`](ice.h)) is `long double` (80-bit x87) by default — the model faithful to the original.
  Compile with `-DICE_DOUBLE_CORE` to make it `double`, which reproduces the C# (SSE) build instead of the
  original. Useful for isolating x87-vs-SSE differences.
- **`ICE_NODITHER` / `ICE_ZERODITHER`** env vars disable the dither (a stabler reference when validating the
  front/back halves without the dither LCG in play).

## Editing warning — the hot paths are codegen-fragile

The 0-diff result is sensitive to gcc's register allocation. Adding code to the hot paths — `ice_core.c`
(`add_band_detail`, `reconstruct`), `ice_front.c` (`ice_front_weight`), the dither helper — can perturb
register spills and silently re-introduce deltas even if the arithmetic looks unchanged.

**After any change, re-verify kind 8 is still 0 diff** before trusting it. Prefer setup-time tables / branching
(as `-kind` and `-fine` do) over new code inside the per-pixel loop.

## Verify it yourself

`pixdiff.exe` (built alongside `openice.exe`) reports the differing-pixel count. Reference frames live in
[`../data/`](../data) — `*_f01_{postlut,lowres,iced}.dng` for the LS-5000 frames and `nk9000_f01_*` for the
LS-9000:

```powershell
# LS-5000 (kind 8)
.\openice.exe ..\data\frame3_f01_postlut.dng out.dng -lowres ..\data\frame3_f01_lowres.dng
.\pixdiff.exe ..\data\frame3_f01_postlut.dng ..\data\frame3_f01_iced.dng out.dng
#   -> total 0 differing pixels

# LS-9000 (kind 7)
.\openice.exe ..\data\nk9000_f01_postlut.dng out.dng -lowres ..\data\nk9000_f01_lowres.dng -kind 7
.\pixdiff.exe ..\data\nk9000_f01_postlut.dng ..\data\nk9000_f01_iced.dng out.dng
#   -> total 0 differing pixels
```

(`pixdiff <postlut> <ref> <openice-output>` — the postlut argument is only for row alignment.)

## Files

| file | stage (mirrors `../src/*.cs`) |
|---|---|
| `openice.c` | CLI: RGBI DNG in → clean RGB DNG out |
| `ice_pump.c` | per-row streaming driver |
| `ice_analyze.c` | low-res calibration pass (crosstalk + IR-ref → irCrosstalk, IRref) |
| `ice_setup.c` | Phase 0: density LUT, geometry, per-scan constants, per-`-kind` coefficient tables |
| `ice_buffers.c` | ring / plane / staging buffers |
| `ice_row.c` | Phase 1: per-row ring slot-advance + frame reset |
| `ice_front.c` | Phase 2: the front-half builders |
| `ice_back.c` | Phase 3: reconstruction driver + v/L build + output convert |
| `ice_core.c` | soft-threshold reconstruction core + dither/LCG |
| `dng.c` / `dng.h` | DNG read/write |
| `ice.h` | shared config offsets, `IceState`, inline helpers, `real_t` (no C# counterpart) |
| `ice_streamcalib.c` | streaming per-row calibration estimator (no C# counterpart) |
| `pixdiff.c` | the byte-diff tool used by `build.ps1 -verify` |
| `build.ps1` | build + optional nodither self-test |

## Relationship to `../src/` (C#)

`../src/` is the readable C# twin — the reference you read to understand the algorithm. It runs on x64/SSE and
is **not** byte-exact (visually indistinguishable, but with last-bit gaps that widen under Fine). `src_C/` is
the one that matches the original exactly. Keep the two in sync when changing logic; keep hot-path *code shape*
stable here to preserve the x87 match.

See the parent [`../README.md`](../README.md) for the project overview and the pipeline diagram.
