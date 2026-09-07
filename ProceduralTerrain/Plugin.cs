using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using UnityEngine;

namespace ProceduralTerrain
{
    // ── Context structs passed into ModeDefinition methods ───────────────────

    struct ColumnCtx {
        public float worldX, worldZ, effectiveH, locFac;
        public float blurH, gradMag, gradDirX, gradDirZ;  // gradient field
        public float bfx, bfz;          // BL weights
        public int b200, b210, b201, b211;   // 2D coarse cell corner indices (into cw4×cw4 arrays)
        public int b3_00, b3_10, b3_01, b3_11; // 3D base indices = b2xx * bh (for bh-deep arrays)
        public int md200, md210, md201, md211; // gradient grid corners (cw4+1 wide)
        public int cw4, bh;
        public int cx, cz, bw;  // full-res column indices and width (for Mode B warpedH lookup)
    }

    struct VoxelCtx {
        public float worldX, worldY, worldZ, effectiveH;
        public int cy;
        public float locFac, floorY, floorFade;
    }

    struct BakeCellArgs {
        public float worldX, worldZ;
        public int cx4, cz4, cellIdx, cw4, bh;
        public float s, extLoS;   // extLoS = extLo * s
        public WorldGenerator wg;
    }

    // Per-column state for each pipeline mode — [ThreadStatic] field lives in Plugin class below.
    internal struct ModeColCtx {
        public float colAmp;              // used by all modes (guard check)
        public float bfx, bfz;           // BL fractions (A, C, Lua)
        public int b00, b10, b01, b11, bh; // BL3Y corner indices (A, C, Lua)
        public float[]? arr0, arr1, arr2; // aux array refs (A: tun/tun2/wBuf, C: noiseBuf, Lua: auxBuf)
        public float f0, f1, f2, f3, f4, f5; // mode-specific floats
    }

    // ── Abstract ModeDefinition ───────────────────────────────────────────────

    internal abstract class ModeDefinition {
        public string Name = "";
        public float Amplitude;
        public int PipelineIndex = -1;

        public virtual bool NeedsGradField => false;
        public virtual float GradBlurRadius => 128f;

        // Per-mode blended param metadata. Param index 0 is always amplitude.
        public abstract int ParamCount { get; }
        public abstract string[] ParamNames { get; }
        public abstract float GetDefaultParam(int i);

        // Normalize accumulated param sums into final blended values.
        // Amplitude (pi=0) is normalized by totalW (includes absent biomes so it fades out).
        // All other params are normalized by presentW (only biomes that have the mode),
        // so absent biomes don't pull frequency/vscale/etc toward zero.
        public static void BlendParams(Span<float> result, Span<float> acc, float totalW, float presentW, int pc)
        {
            float rcpTotal   = totalW   > 0f ? 1f / totalW   : 0f;
            float rcpPresent = presentW > 0f ? 1f / presentW : 0f;
            for (int pi = 0; pi < pc; pi++)
                result[pi] = acc[pi] * (pi == 0 ? rcpTotal : rcpPresent);
        }

        // Allocate this mode's aux arrays in CarveBuffers before the bake cell loop.
        public virtual void BakeBegin(ref CarveBuffers cb, int cellCount, int bh, int cw4) {}

        // Fill this mode's baked data for one coarse cell.
        public abstract void BakeCell(ref CarveBuffers cb, in BakeCellArgs args);

        // Compute column-level intermediates; store them as instance fields.
        public virtual void PrepareColumn(ref CarveBuffers cb, in ColumnCtx col) {}

        // Apply this mode's SDF contribution to `combined`.
        public abstract void ComputeVoxel(ref float combined, in VoxelCtx ctx);
    }

    // ── Mode A: noise tunnel network ──────────────────────────────────────────

    internal sealed class ModeADef : ModeDefinition
    {
        public float Frequency = 0.015f, WarpAmp = 10f, WarpAmpY = 0f, WarpFrequency = 0.04f;
        public float VScale = 0.35f, WidthVary = 0f, WidthFrequency = 0.003f, YOffset = 0f;

        // Param indices: 0=amplitude 1=frequency 2=warpAmp 3=warpAmpY 4=warpFrequency 5=vScale 6=widthVary 7=widthFrequency 8=yOffset
        static readonly string[] _paramNames = { "amplitude","frequency","warpamp","warpampy","warpfrequency","vscale","widthvary","widthfrequency","yoffset" };
        public override int ParamCount => 9;
        public override string[] ParamNames => _paramNames;
        public override float GetDefaultParam(int i) => i switch {
            0 => Amplitude, 1 => Frequency, 2 => WarpAmp, 3 => WarpAmpY, 4 => WarpFrequency,
            5 => VScale, 6 => WidthVary, 7 => WidthFrequency, 8 => YOffset, _ => 0f };


        public override void BakeBegin(ref CarveBuffers cb, int cellCount, int bh, int cw4)
        {
            int pi = PipelineIndex;
            Plugin.EnsureAuxSlots(ref cb, pi, 3);
            int n4 = cellCount * bh;
            Plugin.EnsureArr(ref cb.bkModeAux![pi][0], n4);
            Plugin.EnsureArr(ref cb.bkModeAux![pi][1], n4);
            Plugin.EnsureArr(ref cb.bkModeAux![pi][2], n4);
        }

        public override void BakeCell(ref CarveBuffers cb, in BakeCellArgs args)
        {
            int pi = PipelineIndex;
            float[]? tun  = cb.bkModeAux![pi][0];
            float[]? tun2 = cb.bkModeAux![pi][1];
            float[]? wBuf = cb.bkModeAux![pi][2];
            float amp = cb.bkModeParams![pi][0][args.cellIdx];
            int baseIdx = args.cellIdx * args.bh;
            if (amp <= 0f)
            {
                for (int cy = 0; cy < args.bh; cy++) { tun![baseIdx+cy]=0f; tun2![baseIdx+cy]=0f; wBuf![baseIdx+cy]=0f; }
                return;
            }
            float mawf = cb.bkModeParams![pi][4][args.cellIdx]; // warpFrequency
            float mawa = cb.bkModeParams![pi][2][args.cellIdx]; // warpAmp
            float maf  = cb.bkModeParams![pi][1][args.cellIdx]; // frequency
            float mavs = cb.bkModeParams![pi][5][args.cellIdx]; // vScale
            float mawvf= cb.bkModeParams![pi][7][args.cellIdx]; // widthFrequency
            float mayo = cb.bkModeParams![pi][8][args.cellIdx]; // yOffset
            float addHA = mayo > 0f ? args.wg.GetHeight(args.worldX, args.worldZ) : 0f;
            float s = args.s;
            for (int cy = 0; cy < args.bh; cy++)
            {
                float sy4 = (args.extLoS + cy * s - addHA + mayo) * mavs;
                float awx4  = Plugin._warpX! .GetNoise(args.worldX * mawf, sy4 * mawf, args.worldZ * mawf) * mawa;
                float awz4  = Plugin._warpZ! .GetNoise(args.worldX * mawf, sy4 * mawf, args.worldZ * mawf) * mawa;
                float awx4b = Plugin._warpX2!.GetNoise(args.worldX * mawf, sy4 * mawf, args.worldZ * mawf) * mawa;
                float awz4b = Plugin._warpZ2!.GetNoise(args.worldX * mawf, sy4 * mawf, args.worldZ * mawf) * mawa;
                tun! [baseIdx + cy] = Plugin._noisePool![0].GetNoise((args.worldX + awx4)  * maf, sy4 * maf, (args.worldZ + awz4)  * maf);
                tun2![baseIdx + cy] = Plugin._noisePool![1].GetNoise((args.worldX + awx4b) * maf, sy4 * maf, (args.worldZ + awz4b) * maf);
                wBuf![baseIdx + cy] = Plugin._noisePool![2].GetNoise(args.worldX * mawvf, sy4 * mawvf, args.worldZ * mawvf);
            }
        }

        public override void PrepareColumn(ref CarveBuffers cb, in ColumnCtx col)
        {
            int pi = PipelineIndex;
            ref var c = ref Plugin._modeColCtx![pi];
            c.colAmp = Plugin.BL(cb.bkModeParams![pi][0], col.b200, col.b210, col.b201, col.b211, col.bfx, col.bfz);
            if (c.colAmp <= 0f) return;
            c.f0 = Plugin.BL(cb.bkModeParams![pi][6], col.b200, col.b210, col.b201, col.b211, col.bfx, col.bfz); // widthVary
            c.bfx = col.bfx; c.bfz = col.bfz;
            c.b00 = col.b3_00; c.b10 = col.b3_10; c.b01 = col.b3_01; c.b11 = col.b3_11; c.bh = col.bh;
            c.arr0 = cb.bkModeAux![pi][0];
            c.arr1 = cb.bkModeAux![pi][1];
            c.arr2 = cb.bkModeAux![pi][2];
        }

        public override void ComputeVoxel(ref float combined, in VoxelCtx ctx)
        {
            ref var c = ref Plugin._modeColCtx![PipelineIndex];
            if (c.colAmp <= 0f) return;
            int cy = ctx.cy;
            float na = Plugin.BL3Y(c.arr0!, c.b00, c.b10, c.b01, c.b11, cy, c.bfx, c.bfz);
            float nb = Plugin.BL3Y(c.arr1!, c.b00, c.b10, c.b01, c.b11, cy, c.bfx, c.bfz);
            float wn = Plugin.BL3Y(c.arr2!, c.b00, c.b10, c.b01, c.b11, cy, c.bfx, c.bfz);
            float localWidth = Mathf.Max(c.colAmp * (1f + c.f0 * wn), 0.001f);
            float dist       = Mathf.Sqrt(na * na + nb * nb) * 0.7071f;
            float s          = Plugin._scale ?? 1f;
            float tunnelSDF  = (localWidth - dist) / localWidth * 4f * s;
            float sdf        = Mathf.Min(tunnelSDF, ctx.worldY - ctx.floorY);
            combined = Plugin.SmoothMax(combined, sdf, 2f * s);
        }
    }

    // ── Mode B: warped-heightmap carved band ──────────────────────────────────

    internal sealed class ModeBDef : ModeDefinition
    {
        public float WarpAmp = 80f, WarpFrequency = 0.005f, VScale = 0.5f;
        public float NoiseAmp = 0f, YOffset = 0f, Twist = 0f;

        // Param indices: 0=amplitude 1=warpAmp 2=warpFrequency 3=vScale 4=noiseAmp 5=yOffset 6=twist
        static readonly string[] _paramNames = { "amplitude","warpamp","warpfrequency","vscale","noiseamp","yoffset","twist" };
        public override int ParamCount => 7;
        public override string[] ParamNames => _paramNames;
        public override float GetDefaultParam(int i) => i switch {
            0 => Amplitude, 1 => WarpAmp, 2 => WarpFrequency, 3 => VScale,
            4 => NoiseAmp, 5 => YOffset, 6 => Twist, _ => 0f };


        public override void BakeCell(ref CarveBuffers cb, in BakeCellArgs args)
        {
            // All params already set by BakeBlendedProfiles into bkModeParams
        }

        public override void PrepareColumn(ref CarveBuffers cb, in ColumnCtx col)
        {
            int pi = PipelineIndex;
            ref var c = ref Plugin._modeColCtx![pi];
            c.colAmp = Plugin.BL(cb.bkModeParams![pi][0], col.b200, col.b210, col.b201, col.b211, col.bfx, col.bfz);
            if (c.colAmp <= 0f) return;
            c.f0 = cb.bkWarpedH![col.cx * col.bw + col.cz];                                                                    // warpedH
            c.f1 = Plugin.BL(cb.bkModeParams![pi][4], col.b200, col.b210, col.b201, col.b211, col.bfx, col.bfz);              // noiseAmp
            c.f2 = Plugin.BL(cb.bkModeParams![pi][3], col.b200, col.b210, col.b201, col.b211, col.bfx, col.bfz);              // vScale
            c.f3 = Plugin.BL(cb.bkModeParams![pi][2], col.b200, col.b210, col.b201, col.b211, col.bfx, col.bfz);              // warpFreq
            c.f4 = Plugin.BL(cb.bkModeParams![pi][6], col.b200, col.b210, col.b201, col.b211, col.bfx, col.bfz);              // twist
        }

        public override void ComputeVoxel(ref float combined, in VoxelCtx ctx)
        {
            ref var c = ref Plugin._modeColCtx![PipelineIndex];
            if (c.colAmp <= 0f) return;
            float worldY = ctx.worldY;
            float bandSDF = c.colAmp - Mathf.Abs(worldY - c.f0);
            if (c.f1 > 0f)
            {
                float sy = worldY * c.f2;
                float nx = ctx.worldX, nz = ctx.worldZ;
                if (c.f4 != 0f)
                {
                    float ang = worldY * c.f4;
                    float cs = Mathf.Cos(ang), ss = Mathf.Sin(ang);
                    nx = ctx.worldX * cs - ctx.worldZ * ss;
                    nz = ctx.worldX * ss + ctx.worldZ * cs;
                }
                bandSDF += Plugin._noisePool![8].GetNoise(nx * c.f3, sy * c.f3, nz * c.f3) * c.f1;
            }
            float fd  = worldY - ctx.floorY;
            float sdf = bandSDF < fd ? bandSDF : fd;
            if (sdf > combined) combined = sdf;
        }
    }

    // ── Mode C: 3D density field above heightmap ──────────────────────────────

    internal sealed class ModeCDef : ModeDefinition
    {
        public float Threshold = 0f, Frequency = 0.015f, WarpAmp = 8f, WarpFrequency = 0.005f;
        public float VScale = 0.4f, Twist = 0f, TwistVary = 0f, TwistFrequency = 0.003f;
        public float AmpVary = 0f, AmpFrequency = 0.003f, Flare = 0f, YOffset = 0f;

        // Param indices: 0=amplitude 1=threshold 2=frequency 3=warpAmp 4=warpFrequency 5=vScale
        //                6=twist 7=twistVary 8=twistFrequency 9=ampVary 10=ampFrequency 11=flare 12=yOffset
        static readonly string[] _paramNames = { "amplitude","threshold","frequency","warpamp","warpfrequency","vscale",
            "twist","twistvary","twistfrequency","ampvary","ampfrequency","flare","yoffset" };
        public override int ParamCount => 13;
        public override string[] ParamNames => _paramNames;
        public override float GetDefaultParam(int i) => i switch {
            0 => Amplitude, 1 => Threshold, 2 => Frequency, 3 => WarpAmp, 4 => WarpFrequency,
            5 => VScale, 6 => Twist, 7 => TwistVary, 8 => TwistFrequency,
            9 => AmpVary, 10 => AmpFrequency, 11 => Flare, 12 => YOffset, _ => 0f };


        public override void BakeBegin(ref CarveBuffers cb, int cellCount, int bh, int cw4)
        {
            int pi = PipelineIndex;
            Plugin.EnsureAuxSlots(ref cb, pi, 3);
            Plugin.EnsureArr(ref cb.bkModeAux![pi][0], cellCount);      // addH
            Plugin.EnsureArr(ref cb.bkModeAux![pi][1], cellCount);      // lAmp
            Plugin.EnsureArr(ref cb.bkModeAux![pi][2], cellCount * bh); // noiseBuf
        }

        public override void BakeCell(ref CarveBuffers cb, in BakeCellArgs args)
        {
            int pi = PipelineIndex;
            float[]? addHArr   = cb.bkModeAux![pi][0];
            float[]? lAmpArr   = cb.bkModeAux![pi][1];
            float[]? noiseBuf  = cb.bkModeAux![pi][2];
            float amp = cb.bkModeParams![pi][0][args.cellIdx];
            int baseIdx = args.cellIdx * args.bh;
            if (amp <= 0f)
            {
                addHArr! [args.cellIdx] = args.wg.GetHeight(args.worldX, args.worldZ);
                lAmpArr! [args.cellIdx] = 0f;
                for (int cy = 0; cy < args.bh; cy++) noiseBuf![baseIdx + cy] = 0f;
                return;
            }
            float awf4  = cb.bkModeParams![pi][4][args.cellIdx]; // warpFrequency
            float bwarpA= cb.bkModeParams![pi][3][args.cellIdx]; // warpAmp
            float awx4 = Plugin._noisePool![3].GetNoise(args.worldX * awf4, args.worldZ * awf4) * bwarpA;
            float awz4 = Plugin._noisePool![4].GetNoise(args.worldX * awf4, args.worldZ * awf4) * bwarpA;
            float byo4  = cb.bkModeParams![pi][12][args.cellIdx]; // yOffset
            float addH4 = args.wg.GetHeight(args.worldX + awx4, args.worldZ + awz4) - byo4;
            float avf4  = cb.bkModeParams![pi][10][args.cellIdx]; // ampFrequency
            float bav4  = cb.bkModeParams![pi][9][args.cellIdx];  // ampVary
            addHArr![args.cellIdx] = addH4;
            lAmpArr![args.cellIdx] = Mathf.Max(amp * (1f + bav4
                * Plugin._noisePool![5].GetNoise(args.worldX * avf4, args.worldZ * avf4)), 0f);
            float btv4  = cb.bkModeParams![pi][7][args.cellIdx];  // twistVary
            float btf4  = cb.bkModeParams![pi][8][args.cellIdx];  // twistFrequency
            float ts4 = 1f + btv4 * Plugin._noisePool![6].GetNoise(args.worldX * btf4, args.worldZ * btf4);
            float af4 = cb.bkModeParams![pi][2][args.cellIdx];  // frequency
            float vs4 = cb.bkModeParams![pi][5][args.cellIdx];  // vScale
            float tw4 = cb.bkModeParams![pi][6][args.cellIdx];  // twist
            float s = args.s;
            for (int cy = 0; cy < args.bh; cy++)
            {
                float wy4  = args.extLoS + cy * s;
                float sy4  = (wy4 - addH4) * vs4;
                float ang4 = wy4 * tw4 * ts4;
                float cos4 = Mathf.Cos(ang4), sin4 = Mathf.Sin(ang4);
                float tx4  = args.worldX * cos4 - args.worldZ * sin4;
                float tz4  = args.worldX * sin4 + args.worldZ * cos4;
                noiseBuf![baseIdx + cy] = Plugin._noisePool![7].GetNoise(tx4 * af4, sy4 * af4, tz4 * af4);
            }
        }

        public override void PrepareColumn(ref CarveBuffers cb, in ColumnCtx col)
        {
            int pi = PipelineIndex;
            ref var c = ref Plugin._modeColCtx![pi];
            c.colAmp = Plugin.BL(cb.bkModeParams![pi][0], col.b200, col.b210, col.b201, col.b211, col.bfx, col.bfz);
            if (c.colAmp <= 0f) return;
            c.f0 = Plugin.BL(cb.bkModeParams![pi][1],  col.b200, col.b210, col.b201, col.b211, col.bfx, col.bfz); // threshold
            c.f1 = Plugin.BL(cb.bkModeParams![pi][11], col.b200, col.b210, col.b201, col.b211, col.bfx, col.bfz); // flare
            c.f2 = Plugin.BL(cb.bkModeAux![pi][0], col.b200, col.b210, col.b201, col.b211, col.bfx, col.bfz);     // colAddH
            c.f3 = Plugin.BL(cb.bkModeAux![pi][1], col.b200, col.b210, col.b201, col.b211, col.bfx, col.bfz);     // colLocalAmp
            c.bfx = col.bfx; c.bfz = col.bfz;
            c.b00 = col.b3_00; c.b10 = col.b3_10; c.b01 = col.b3_01; c.b11 = col.b3_11; c.bh = col.bh;
            c.arr0 = cb.bkModeAux![pi][2];
        }

        public override void ComputeVoxel(ref float combined, in VoxelCtx ctx)
        {
            ref var c = ref Plugin._modeColCtx![PipelineIndex];
            if (c.colAmp <= 0f || c.f3 <= 0f) return;
            float n    = Plugin.BL3Y(c.arr0!, c.b00, c.b10, c.b01, c.b11, ctx.cy, c.bfx, c.bfz);
            float relY = ctx.worldY - c.f2;
            float ft   = Mathf.Clamp01(relY / c.f3);
            float effT = c.f0 - c.f1 * (4f * ft * (1f - ft));
            float sdf  = relY - (n - effT) * c.f3;
            if (sdf < combined) combined = sdf;
        }
    }

    // ── Mode D: coast-aligned anisotropic noise ridges ────────────────────────

    internal sealed class ModeDDef : ModeDefinition
    {
        public float FreqT = 0.02f, FreqN = 0.02f, BlurRadius = 128f;
        public float Alignment = 0f, VScale = 0.1f, GradFade = 0f, SeaFade = 0f;

        public override bool NeedsGradField => true;
        public override float GradBlurRadius => BlurRadius;

        // Param indices: 0=amplitude 1=freqT 2=freqN 3=blurRadius 4=alignment 5=vScale 6=gradFade 7=seaFade
        static readonly string[] _paramNames = { "amplitude","freqt","freqn","blurradius","alignment","vscale","gradfade","seafade" };
        public override int ParamCount => 8;
        public override string[] ParamNames => _paramNames;
        public override float GetDefaultParam(int i) => i switch {
            0 => Amplitude, 1 => FreqT, 2 => FreqN, 3 => BlurRadius,
            4 => Alignment, 5 => VScale, 6 => GradFade, 7 => SeaFade, _ => 0f };


        public override void BakeCell(ref CarveBuffers cb, in BakeCellArgs args)
        {
            // All params already set by BakeBlendedProfiles into bkModeParams
        }

        public override void PrepareColumn(ref CarveBuffers cb, in ColumnCtx col)
        {
            int pi = PipelineIndex;
            ref var c = ref Plugin._modeColCtx![pi];
            float mdAmp = Plugin.BL(cb.bkModeParams![pi][0], col.b200, col.b210, col.b201, col.b211, col.bfx, col.bfz);
            if (mdAmp <= 0f) { c.colAmp = 0f; return; }
            float freqT      = Plugin.BL(cb.bkModeParams![pi][1], col.b200, col.b210, col.b201, col.b211, col.bfx, col.bfz);
            float freqN      = Plugin.BL(cb.bkModeParams![pi][2], col.b200, col.b210, col.b201, col.b211, col.bfx, col.bfz);
            float blurRadius = Plugin.BL(cb.bkModeParams![pi][3], col.b200, col.b210, col.b201, col.b211, col.bfx, col.bfz);
            float alignment  = Plugin.BL(cb.bkModeParams![pi][4], col.b200, col.b210, col.b201, col.b211, col.bfx, col.bfz);
            c.f2             = Plugin.BL(cb.bkModeParams![pi][5], col.b200, col.b210, col.b201, col.b211, col.bfx, col.bfz); // vScale
            float gradFade   = Plugin.BL(cb.bkModeParams![pi][6], col.b200, col.b210, col.b201, col.b211, col.bfx, col.bfz);
            float seaFade    = Plugin.BL(cb.bkModeParams![pi][7], col.b200, col.b210, col.b201, col.b211, col.bfx, col.bfz);
            float nmX = col.gradDirX, nmZ = col.gradDirZ;
            float gradFactor = gradFade > 0f ? Mathf.SmoothStep(0f, gradFade, col.gradMag) : 1f;
            float mdRadial   = col.blurH / Mathf.Max(blurRadius, 1f);
            float fT = Mathf.Lerp(freqT, freqN, alignment);
            float fN = Mathf.Lerp(freqN, freqT, alignment);
            c.f0 = (col.worldX * (-nmZ) + col.worldZ * nmX) * fT * gradFactor;  // mdTCoord
            c.f1 = mdRadial * fN * gradFactor;                                    // mdNCoord
            float seaFactor = seaFade > 0f ? Mathf.SmoothStep(0f, seaFade, col.effectiveH - 30f) : 1f;
            c.colAmp = mdAmp * seaFactor;
        }

        public override void ComputeVoxel(ref float combined, in VoxelCtx ctx)
        {
            ref var c = ref Plugin._modeColCtx![PipelineIndex];
            if (c.colAmp <= 0f) return;
            float mdn   = Plugin._noisePool![9].GetNoise(c.f0, ctx.worldY * c.f2, c.f1) * 0.5f + 0.5f;
            float mdSdf = (ctx.worldY - ctx.effectiveH) - mdn * c.colAmp;
            if (mdSdf < combined) combined = mdSdf;
        }
    }

    // ── Mode Lua: user-defined Lua bake function ──────────────────────────────

    internal sealed class LuaModeDef : ModeDefinition
    {
        public string FilePath = "";
        public string Blend    = "min";
        public float  BlendK   = 2f;

        // Pool stores (Script, pre-allocated params Table) pairs to avoid per-cell allocation.
        System.Collections.Concurrent.ConcurrentBag<(MoonSharp.Interpreter.Script s, MoonSharp.Interpreter.Table p)>? _scriptPool;
        string? _luaSrc;
        string[] _dynParamNames  = Array.Empty<string>();
        float[]  _dynDefaults    = Array.Empty<float>();

        public override int    ParamCount => _dynParamNames.Length;
        public override string[] ParamNames => _dynParamNames;
        public override float GetDefaultParam(int i) => i < _dynDefaults.Length ? _dynDefaults[i] : 0f;

        public override bool NeedsGradField => true;
        public override float GradBlurRadius
        {
            get
            {
                for (int i = 0; i < _dynParamNames.Length; i++)
                    if (_dynParamNames[i] == "blurradius") return _dynDefaults[i];
                return 128f;
            }
        }

        // Called from ParseModeDefinition after all fields are set.
        public void SetDynParams(List<(string name, float def)> plist)
        {
            _dynParamNames = new string[plist.Count];
            _dynDefaults   = new float[plist.Count];
            for (int i = 0; i < plist.Count; i++)
            {
                _dynParamNames[i] = plist[i].name;
                _dynDefaults[i]   = plist[i].def;
            }
        }

        (MoonSharp.Interpreter.Script s, MoonSharp.Interpreter.Table p) CreateEntry()
        {
            var s = new MoonSharp.Interpreter.Script(MoonSharp.Interpreter.CoreModules.Preset_HardSandbox);
            int nc = Plugin._noiseCount;
            for (int k = 0; k < nc; k++)
            {
                int kk = k;
                s.Globals[$"n{kk + 1}"] = (Func<float,float,float,float>)((x, y, z) =>
                    Plugin._noisePool != null ? Plugin._noisePool[kk].GetNoise(x, y, z) : 0f);
            }
            s.Globals["log"]        = (Action<string>)(msg => Plugin.Log.LogInfo($"[lua] {msg}"));
            s.Globals["lerp"]       = (Func<float,float,float,float>)Mathf.Lerp;
            s.Globals["smoothstep"] = (Func<float,float,float,float>)Mathf.SmoothStep;
            s.Globals["terrain_h"]  = (Func<float,float,float>)((x, z) =>
                WorldGenerator.instance?.GetHeight(x, z) ?? 0f);
            s.DoString(_luaSrc!);
            // Pre-allocate params table — reused each BakeCell, values updated in place.
            var p = new MoonSharp.Interpreter.Table(s);
            return (s, p);
        }

        (MoonSharp.Interpreter.Script s, MoonSharp.Interpreter.Table p) RentEntry()
        {
            if (_scriptPool != null && _scriptPool.TryTake(out var e)) return e;
            return CreateEntry();
        }

        void ReturnEntry((MoonSharp.Interpreter.Script s, MoonSharp.Interpreter.Table p) e) => _scriptPool?.Add(e);

        public void WarmPool(int count)
        {
            if (_scriptPool == null || _luaSrc == null) return;
            int have = _scriptPool.Count;
            for (int i = have; i < count; i++)
                try { _scriptPool.Add(CreateEntry()); }
                catch { break; }
        }

        public void LoadScript()
        {
            try
            {
                if (!System.IO.File.Exists(FilePath))
                {
                    Plugin.Log.LogWarning($"[ProceduralTerrain] LuaMode '{Name}': file not found: {FilePath}");
                    return;
                }
                _luaSrc = System.IO.File.ReadAllText(FilePath);
                // Create one entry eagerly to validate the source; pool is pre-seeded with it.
                var first = CreateEntry();
                _scriptPool = new System.Collections.Concurrent.ConcurrentBag<(MoonSharp.Interpreter.Script, MoonSharp.Interpreter.Table)>();
                _scriptPool.Add(first);
                Plugin.Log.LogInfo($"[ProceduralTerrain] LuaMode '{Name}': loaded {FilePath}");
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"[ProceduralTerrain] LuaMode '{Name}': LoadScript failed: {ex.Message}");
                _scriptPool = null;
                _luaSrc = null;
            }
        }


        public override void BakeBegin(ref CarveBuffers cb, int cellCount, int bh, int cw4)
        {
            int pi  = PipelineIndex;
            Plugin.EnsureAuxSlots(ref cb, pi, 1);
            Plugin.EnsureArr(ref cb.bkModeAux![pi][0], cellCount * bh);
        }

        public override void BakeCell(ref CarveBuffers cb, in BakeCellArgs args)
        {
            if (_scriptPool == null) return;
            int pi = PipelineIndex;
            float amp = cb.bkModeParams![pi][0][args.cellIdx];
            int baseIdx = args.cellIdx * args.bh;
            float[]? auxBuf = cb.bkModeAux![pi][0];
            if (amp <= 0f)
            {
                for (int cy = 0; cy < args.bh; cy++) auxBuf![baseIdx + cy] = 0f;
                return;
            }

            int idx = args.cellIdx;
            float dirx  = cb.bk2dTgX    != null && idx < cb.bk2dTgX.Length    ? cb.bk2dTgX[idx]    : 0f;
            float dirz  = cb.bk2dTgZ    != null && idx < cb.bk2dTgZ.Length    ? cb.bk2dTgZ[idx]    : 0f;
            float mag   = cb.bk2dGradMag != null && idx < cb.bk2dGradMag.Length ? cb.bk2dGradMag[idx] : 0f;
            float blurh = cb.bk2dBlurH  != null && idx < cb.bk2dBlurH.Length  ? cb.bk2dBlurH[idx]  : 0f;

            var (s, p) = RentEntry();
            try
            {
                // Mutate pre-allocated params table in place — avoids per-cell Table allocation.
                for (int i = 0; i < _dynParamNames.Length; i++)
                    p[_dynParamNames[i]] = (double)cb.bkModeParams![pi][i][idx];
                p["grad_dirx"]  = (double)dirx;
                p["grad_dirz"]  = (double)dirz;
                p["grad_mag"]   = (double)mag;
                p["grad_blurh"] = (double)blurh;

                var res = s.Call(s.Globals["bake"], args.worldX, args.worldZ, p, args.extLoS, (double)args.s, args.bh);
                var tbl = res.Table;
                if (tbl != null)
                    for (int cy = 0; cy < args.bh; cy++)
                        auxBuf![baseIdx + cy] = (float)(tbl.Get(cy + 1).CastToNumber() ?? 0.0);
                else
                    for (int cy = 0; cy < args.bh; cy++) auxBuf![baseIdx + cy] = 0f;
            }
            catch { for (int cy = 0; cy < args.bh; cy++) auxBuf![baseIdx + cy] = 0f; }
            finally { ReturnEntry((s, p)); }
        }

        public override void PrepareColumn(ref CarveBuffers cb, in ColumnCtx col)
        {
            int pi = PipelineIndex;
            ref var c = ref Plugin._modeColCtx![pi];
            c.colAmp = Plugin.BL(cb.bkModeParams![pi][0], col.b200, col.b210, col.b201, col.b211, col.bfx, col.bfz);
            if (c.colAmp <= 0f) return;
            c.bfx = col.bfx; c.bfz = col.bfz;
            c.b00 = col.b3_00; c.b10 = col.b3_10; c.b01 = col.b3_01; c.b11 = col.b3_11; c.bh = col.bh;
            c.arr0 = cb.bkModeAux![pi][0];
        }

        public override void ComputeVoxel(ref float combined, in VoxelCtx ctx)
        {
            ref var c = ref Plugin._modeColCtx![PipelineIndex];
            if (c.colAmp <= 0f || c.arr0 == null) return;
            float scale = Plugin._scale ?? 1f;
            float raw   = Plugin.BL3Y(c.arr0, c.b00, c.b10, c.b01, c.b11, ctx.cy, c.bfx, c.bfz) * scale;
            float sdf   = Mathf.Min(raw, ctx.worldY - ctx.floorY);
            switch (Blend)
            {
                case "smoothmax":
                    combined = Plugin.SmoothMax(combined, sdf, BlendK * scale);
                    break;
                case "max":
                    if (sdf > combined) combined = sdf;
                    break;
                default: // "min"
                    if (sdf < combined) combined = sdf;
                    break;
            }
        }
    }

    // An ordered collection of named mode references, plus profile-level params.
    internal class TerrainProfile
    {
        public string       Name         = "";
        public List<string> ModeNames    = new();
        public float        FloorDepth   = 8f;
        public float        FloorFade    = 0f;
        public float        BlurPasses   = 0f;
        public float        PeronaK      = 0f;
        public float        PeronaLambda = 0.1f;

        // Build the internal CaveProfile from profile-level params only.
        public CaveProfile ToCaveProfile(Dictionary<string, ModeDefinition> modes)
        {
            return new CaveProfile {
                floorDepth   = FloorDepth,
                floorFade    = FloorFade,
                blur_passes  = BlurPasses,
                perona_k     = PeronaK,
                perona_lambda = PeronaLambda
            };
        }

        public static TerrainProfile Default => new TerrainProfile();
    }

    struct CaveProfile
    {
        // ── Shared ────────────────────────────────────────────────────────────────
        public float blur_passes;      // smoothing iterations (0=off)
        public float perona_k;         // Perona-Malik edge threshold (0=Gaussian; >0=PM diffusion)
        public float perona_lambda;    // PM diffusion rate per iteration (must be < 1/6; default 0.1)
        public float floorDepth;       // metres below terrain surface where carving stops
        public float floorFade;        // metres above floorY over which carving tapers to zero (0=hard stop)

        public static readonly CaveProfile Default = new CaveProfile
        {
            blur_passes   = 0f,
            perona_k      = 0f,
            perona_lambda = 0.1f,
            floorDepth    = 8f,
            floorFade     = 0f,
        };
    }

    [BepInPlugin("com.ctogle.procedural_terrain", "ProceduralTerrain", "0.1.0")]
    [BepInDependency("com.bogey.voxheim", BepInDependency.DependencyFlags.SoftDependency)]
    public partial class Plugin : BaseUnityPlugin
    {
        internal static ManualLogSource Log = null!;

        // HeightGrid scale delegate. Built in BuildDelegates.

        // World units per voxel — read lazily, Voxheim initialises it after plugin load.
        static FieldInfo? _scaleField;
        internal static float? _scale;

        // ── Debug flags ───────────────────────────────────────────────────────────
        internal static volatile bool _disableVegetation;
        internal static bool _compressBake = false;

        // ── Biome profiles ────────────────────────────────────────────────────────
        static volatile Dictionary<string, ModeDefinition> _modeDefinitions
            = new Dictionary<string, ModeDefinition>(StringComparer.OrdinalIgnoreCase);
        static volatile Dictionary<string, TerrainProfile> _namedProfiles
            = new Dictionary<string, TerrainProfile>(StringComparer.OrdinalIgnoreCase);
        static volatile Dictionary<Heightmap.Biome, CaveProfile> _biomeProfiles
            = BuildDefaultProfiles();
        static volatile bool _hotReloadPending;
        static DateTime _configLastWrite = DateTime.MinValue;
        static DateTime _luaLastWrite    = DateTime.MinValue;

        // ── Pipeline ──────────────────────────────────────────────────────────────
        static volatile ModeDefinition[] _pipeline = Array.Empty<ModeDefinition>();
        // _biomeParams[biome][pipelineIdx][paramIdx] — blendable params per biome per mode
        static volatile Dictionary<Heightmap.Biome, float[][]> _biomeParams = new Dictionary<Heightmap.Biome, float[][]>();

        // ── Location protection ───────────────────────────────────────────────────
        internal static readonly List<(Vector2 pos, float extR)> _locationPositions = new();
        internal static bool _locationsCached;
        internal static bool  _protectLocations    = true;
        internal static bool  _flatPlaneLocations  = true;
        internal static bool  _islandRemoval       = true;
        internal static bool  _pocketRemoval       = true;
        internal static float _biomeBlendRadius    = 192f;
        internal static bool  _biomeBlendSmooth    = true;   // false=linear, true=smoothstep
        internal static int   _transitionBlurPasses = 3;     // min blur passes for any active zone
        internal static int   _fixpointMaxZones     = 8;     // EnsureFixpoint hop cap
        internal static float _coastFade           = 0f;     // world units above sea level over which Ocean inflates into blend (0=off)
        internal static float _seaLevelFade        = 0f;     // distance above sea level where fade begins (factor=1 here, seaLevelAtten at sea level)
        internal static float _seaLevelAtten       = 0f;     // factor at sea level (0=fully suppressed, 1=no suppression)

        // ── Bake download (client receiving from server) ──────────────────────────
        internal static bool   _bakeDownloadActive;
        internal static int    _bakeChunksExpected;
        internal static int    _bakeChunksReceived;
        internal static System.IO.MemoryStream? _bakeDownloadBuffer;
        internal static System.Diagnostics.Stopwatch? _bakeDownloadTimer;
        const int BakeChunkSize = 256 * 1024; // 256 KB per RPC chunk

        // ── Bake progress (client watching server bake) ───────────────────────────
        internal static bool _bakeInProgress;
        internal static int  _bakeProgressDone;
        internal static int  _bakeProgressTotal;
        internal static System.Diagnostics.Stopwatch? _bakeProgressTimer;

        // ── SDF bake ─────────────────────────────────────────────────────────────
        static float _bakeRadius = 0f;
        static int   _bakeStep   = 8;
        // Staged: subsampled SDF written by Carve() when within bakeRadius.
        static readonly System.Collections.Concurrent.ConcurrentDictionary<(int, int), (int extLo, int bh, int bw, sbyte[] data)> _bakePending = new();
        // Loaded from disk: populated at world load; null if no valid file exists.
        internal static Dictionary<(int, int), (int extLo, int bh, int bw, sbyte[] data)>? _bakeData;
        static int _bakeFileStep = 8;
        static bool _bakeLoadAttempted = false;

        const float LocProtectR = 10f; // minimum protection radius when exteriorRadius is unavailable

        // ── Zone tracking for hot-reload ──────────────────────────────────────────
        internal static MethodInfo? _finishSkinNowMethod;
        internal static MethodInfo? _activateMethod;
        internal static MethodInfo? _unskinMethod;
        internal static FieldInfo?  _hmapField;

        // ── Noise ─────────────────────────────────────────────────────────────────
        // All noise objects use frequency=1.0; call sites scale coordinates by
        // the blended per-biome frequency so blended frequencies interpolate cleanly.
        //
        // Named pool indices (n1..n10 in Lua):
        //   0=_caveNoise  1=_caveNoise2  2=_widthNoise
        //   3=_hmAddWarpX 4=_hmAddWarpZ  5=_addVaryNoise 6=_twistVaryNoise 7=_addNoise
        //   8=_mbNoise    9=_mdNoise
        internal static FastNoiseLite[]? _noisePool;
        internal static int _noiseCount = 16;
        [ThreadStatic] internal static ModeColCtx[]? _modeColCtx;

        // Warp noises remain as named fields (not in pool — used in tight inner loops).
        internal static FastNoiseLite? _warpX;          // Mode A: 3D warp X
        internal static FastNoiseLite? _warpY;          // Mode A: 3D warp Y (sy)
        internal static FastNoiseLite? _warpZ;          // Mode A: 3D warp Z
        internal static FastNoiseLite? _warpX2;         // Mode A: 3D warp X for field 2
        internal static FastNoiseLite? _warpY2;         // Mode A: 3D warp Y (sy) for field 2
        internal static FastNoiseLite? _warpZ2;         // Mode A: 3D warp Z for field 2
        internal static FastNoiseLite? _hmWarpX;       // Mode B: XZ warp X
        internal static FastNoiseLite? _hmWarpZ;       // Mode B: XZ warp Z

        internal static void RebuildNoisePool(int count)
        {
            _noiseCount = count;
            long uid  = ZNet.instance?.GetWorldUID() ?? 0L;
            int  seed = uid != 0L ? (int)(uid * 2654435761L) : 12345;
            var pool  = new FastNoiseLite[count];
            for (int i = 0; i < count; i++)
            {
                var n = new FastNoiseLite(seed + 1337 + i * 7919);
                n.SetNoiseType(FastNoiseLite.NoiseType.OpenSimplex2);
                n.SetFrequency(1f);
                pool[i] = n;
            }
            _noisePool = pool;
        }

        // ── Per-brick SDF cache ───────────────────────────────────────────────────
        internal static FieldInfo?       _zoneOriginX;
        internal static FieldInfo?       _zoneOriginZ;

        // Scratch buffers for BFS biome blend.
        // _biomeFrac: supersampled fractional biome weights per padded coarse cell.
        //   Layout: [px * pcw + pz] * MaxBiomeSlots + slotIdx
        //   Slot mapping built fresh each zone (biomeSlot dict in BakeBlendedProfiles).
        const int SuperN        = 2;   // NxN sub-samples per coarse cell (2→4 samples, 9x smoother boundary)
        const int MaxBiomeSlots = 16;  // Valheim has ~9 biomes; 16 is safe headroom
        // All per-zone scratch buffers are [ThreadStatic] so parallel bake workers
        // each get their own copies without allocation conflicts.
        [ThreadStatic] static float[]?       _biomeFrac;
        [ThreadStatic] static int[]?         _biomeDist;
        [ThreadStatic] static int[]?         _biomeBfsQ;
        [ThreadStatic] static float[]?       _biomeTotW;
        [ThreadStatic] static CaveProfile[]? _blendedProfs;

        // wg.GetHeight is a pure function of (worldX, worldZ) for a given world seed.
        // Cache it keyed by integer coarse voxel coords — cleared on world load only.
        internal static readonly System.Collections.Concurrent.ConcurrentDictionary<long, float> _heightCache = new();

        // Per-zone SDF cache: filled by Phase 3, queried by FallbackDensityPatch.
        // Layout: data[(ci * bh + cy) * width + ri]  (ci=zoneX, ri=zoneZ, cy=Y-extLo)
        internal static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (int extLo, int bh, int width, sbyte[] data)> _sdfCache = new();

        // Per-zone surface height cache: filled after Phase 2, queried by GetWorldHeight patch.
        // Layout: surfH[cx * bw + cz] = world-Y of topmost solid voxel (NaN = no solid in column).
        // Includes overlap region so border columns near zone edges are correct.
        internal static readonly Dictionary<(int ox, int oz), (int bw, int extLo, float s, float[] surfH)> _surfHCache = new();

        // ── Cross-zone grounding face cache ──────────────────────────────────────
        // Indexed by (ox, oz) territory origin.
        // Each face array is [bh * bw] bytes: 0=air, 1=grounded solid.
        // Faces are at the territory boundary: negX=cx=overlap, posX=cx=overlap+w-1, etc.
        internal struct ZoneFaceCache {
            public int extLo, bh, bw;
            public bool[] negX,    posX,    negZ,    posZ;    // [bh * bw]; true = grounded solid
            public bool[] negXAir, posXAir, negZAir, posZAir; // [bh * bw]; true = air connected to boundary
            // percol[from, to]: ungrounded blob at face 'from' percolates through this zone to face 'to'.
            // Face indices: 0=NegX 1=PosX 2=NegZ 3=PosZ. Diagonal (from==to) is always false.
            // percolValid: true only when ComputeZoneFaceCache populated percol; Carve's island-BFS
            // entry fills negX/posX/negZ/posZ correctly but cannot compute percol.
            public bool percolValid;
            public bool[,] percol;
        }
        internal static readonly System.Collections.Concurrent.ConcurrentDictionary<(int, int), ZoneFaceCache> _zoneFaceCache = new();

        // Set true during parallel bake to suppress main-thread-only side effects.
        static volatile bool _parallelBakeActive;

        // CR reconstruction temp buffers — main thread only, not [ThreadStatic].
        static float[] _crTemp1 = new float[72 * 150 * 36]; // [bw * nsy * nsz]
        static float[] _crTemp2 = new float[72 * 300 * 36]; // [bw * bh  * nsz]

        static FieldInfo? _locationInstancesField;

        // ─────────────────────────────────────────────────────────────────────────

        internal static int _mainThreadId = -1;

        void Awake()
        {
            Log = Logger;
            _mainThreadId = System.Threading.Thread.CurrentThread.ManagedThreadId;
            if (!BepInEx.Bootstrap.Chainloader.PluginInfos.ContainsKey("com.bogey.voxheim"))
            {
                Log.LogInfo("[ProceduralTerrain] Voxheim absent — disabled.");
                return;
            }
            if (!BuildDelegates())
            {
                Log.LogError("[ProceduralTerrain] Reflection failed — disabled.");
                return;
            }
            RebuildNoisePool(_noiseCount);
            LoadBiomeConfig();
            WatchBiomeConfig();
            var harmony = new Harmony("com.ctogle.procedural_terrain");
            harmony.PatchAll();

            RegisterVoxheimGenerator();
            new Terminal.ConsoleCommand("pt_dump", "pt_dump — dump SDF + biome blend data for the next 9 zones processed to BepInEx/config/voxcaves_dump_*.json", _ =>
            {
                _dumpZonesRemaining = 9;
                Log.LogInfo("[ProceduralTerrain] Will dump next 9 zones to voxcaves_dump_*.json");
            });
            new Terminal.ConsoleCommand("pt_probe", "pt_probe [x z] — print biome blend breakdown + profile params at world pos (default: player pos)", args =>
            {
                try
                {
                    var wg = WorldGenerator.instance;
                    if (wg == null) { Log.LogWarning("[ProceduralTerrain] probe: WorldGenerator not ready."); return; }
                    float s = _scale is { } sv && sv > 0f ? sv : 1f;
                    float wx, wy, wz;
                    if (args.Length >= 3 && float.TryParse(args[1], out float ax) && float.TryParse(args[2], out float az))
                        { wx = ax; wz = az; wy = wg.GetHeight(ax, az); }
                    else
                    {
                        var player = Player.m_localPlayer;
                        if (player == null) { Log.LogWarning("[ProceduralTerrain] probe: no player; pass x z args."); return; }
                        var pos = player.transform.position;
                        wx = pos.x; wy = pos.y; wz = pos.z;
                    }
                    var hdr = $"[ProceduralTerrain] Probing at world ({wx:F1}, {wy:F1}, {wz:F1}):";
                    Log.LogInfo(hdr);
                    Console.instance?.Print(hdr);
                    ProbeBlend(wx, wz, wy, wg, s);
                }
                catch (Exception e) { Log.LogError($"[ProceduralTerrain] probe exception: {e}"); }
            });
            new Terminal.ConsoleCommand("pt_noisemap", "pt_noisemap [radius] — write diagnostic PNG images to BepInEx/config folder", args =>
            {
                try
                {
                    var wg = WorldGenerator.instance;
                    if (wg == null || _noisePool == null) { Log.LogWarning("[PT] noisemap: not ready."); return; }
                    var player = Player.m_localPlayer;
                    if (player == null) { Log.LogWarning("[PT] noisemap: no player."); return; }
                    var pos = player.transform.position;
                    float cx = pos.x, cz = pos.z;
                    float range = 200f;
                    if (args.Length >= 2 && float.TryParse(args[1], out float r)) range = r;

                    int res = 512;
                    float step = range * 2f / res;
                    // Use formation mode params if available
                    float freq = 0.02f, warpAmp = 26f, warpFreq = 0.01f, vScale = 0.4f, rely = 7f;

                    var texRaw    = new Texture2D(res, res, TextureFormat.RGBA32, false);
                    var texWarped = new Texture2D(res, res, TextureFormat.RGBA32, false);
                    var texGrad   = new Texture2D(res, res, TextureFormat.RGBA32, false);
                    var texSdf    = new Texture2D(res, res, TextureFormat.RGBA32, false);

                    for (int iz = 0; iz < res; iz++)
                    for (int ix = 0; ix < res; ix++)
                    {
                        float wx = cx + (ix - res / 2) * step;
                        float wz = cz + (iz - res / 2) * step;

                        // 1. Raw n8 — no warp
                        float nRaw = _noisePool[7].GetNoise(wx * freq, rely * vScale * freq, wz * freq);
                        float v = nRaw * 0.5f + 0.5f;
                        texRaw.SetPixel(ix, iz, new Color(v, v, v));

                        // 2. Warped n8 — warp applied to XZ sample coords
                        float wx4 = _noisePool[3].GetNoise(wx * warpFreq, wz * warpFreq) * warpAmp;
                        float wz4 = _noisePool[4].GetNoise(wx * warpFreq, wz * warpFreq) * warpAmp;
                        float nWarp = _noisePool[7].GetNoise((wx + wx4) * freq, rely * vScale * freq, (wz + wz4) * freq);
                        float vw = nWarp * 0.5f + 0.5f;
                        texWarped.SetPixel(ix, iz, new Color(vw, vw, vw));

                        // 3. Gradient magnitude + direction
                        float hStp = 2f;
                        float dhdx = (wg.GetHeight(wx + hStp, wz) - wg.GetHeight(wx - hStp, wz)) / (2f * hStp);
                        float dhdz = (wg.GetHeight(wx, wz + hStp) - wg.GetHeight(wx, wz - hStp)) / (2f * hStp);
                        float gm   = Mathf.Sqrt(dhdx * dhdx + dhdz * dhdz);
                        texGrad.SetPixel(ix, iz, new Color(Mathf.Clamp01(gm), dhdx * 0.5f + 0.5f, dhdz * 0.5f + 0.5f));

                        // 4. Formation SDF at rely=7 (mid-feature depth)
                        float addh = wg.GetHeight(wx + wx4, wz + wz4);
                        float sdf  = rely - (nWarp - 0f) * 14f; // threshold=0, amp=14
                        float sv   = Mathf.Clamp01(sdf * 0.5f + 0.5f);
                        texSdf.SetPixel(ix, iz, new Color(sv, sv, sv));
                    }

                    string dir = System.IO.Path.GetDirectoryName(ConfigPath)!;
                    System.IO.File.WriteAllBytes(System.IO.Path.Combine(dir, "pt_noise_raw.png"),    texRaw.EncodeToPNG());
                    System.IO.File.WriteAllBytes(System.IO.Path.Combine(dir, "pt_noise_warped.png"), texWarped.EncodeToPNG());
                    System.IO.File.WriteAllBytes(System.IO.Path.Combine(dir, "pt_grad.png"),         texGrad.EncodeToPNG());
                    System.IO.File.WriteAllBytes(System.IO.Path.Combine(dir, "pt_sdf_slice.png"),    texSdf.EncodeToPNG());
                    var msg = $"[PT] noisemap written to {dir}";
                    Log.LogInfo(msg); Console.instance?.Print(msg);
                    UnityEngine.Object.Destroy(texRaw); UnityEngine.Object.Destroy(texWarped);
                    UnityEngine.Object.Destroy(texGrad); UnityEngine.Object.Destroy(texSdf);
                }
                catch (Exception e) { Log.LogError($"[PT] noisemap exception: {e}"); }
            });
            new Terminal.ConsoleCommand("pt_bake", "pt_bake [radius] — bake terrain SDF for a radius in meters; runs on server (admin only)", args =>
            {
                float radius = _bakeRadius;
                if (args.Length >= 2 && float.TryParse(args[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float r))
                    radius = r;
                if (ZNet.instance != null && !ZNet.instance.IsServer())
                {
                    // Client: request server to run the bake.
                    var pkg = new ZPackage();
                    pkg.Write(radius);
                    ZRoutedRpc.instance.InvokeRoutedRPC(ZRoutedRpc.Everybody, "PTBakeExecute", pkg);
                    Log.LogInfo("[ProceduralTerrain] Bake request sent to server.");
                }
                else
                {
                    _bakeInProgress  = true;
                    _bakeProgressDone  = 0;
                    _bakeProgressTotal = 0;
                    _bakeProgressTimer = System.Diagnostics.Stopwatch.StartNew();
                    BakeProgressUI.Show();
                    System.Threading.Tasks.Task.Run(() => WriteBakeFile(radius))
                        .ContinueWith(_ => { _bakeInProgress = false; });
                }
            });
            new Terminal.ConsoleCommand("pt_clearbake",
                "pt_clearbake [filename|server] — delete a local bake file (default: current world); 'server' deletes from server (admin only)",
                args =>
                {
                    string arg = args.Length >= 2 ? args[1].ToLower() : "";
                    if (arg == "server")
                    {
                        if (ZNet.instance != null && !ZNet.instance.IsServer())
                        {
                            ZRoutedRpc.instance.InvokeRoutedRPC(ZRoutedRpc.Everybody, "PTClearBake", new ZPackage());
                            Log.LogInfo("[ProceduralTerrain] Clear bake request sent to server.");
                        }
                        else
                        {
                            RPC_ClearBake(0L, new ZPackage());
                        }
                        return;
                    }
                    string path;
                    if (arg.Length > 0)
                    {
                        // Specific filename given (with or without directory).
                        path = System.IO.Path.IsPathRooted(arg)
                            ? arg
                            : System.IO.Path.Combine(Paths.ConfigPath, arg);
                    }
                    else
                    {
                        int  seed    = (int)(ZNet.instance?.GetWorldUID() ?? 0L);
                        uint cfgHash = ComputeCfgHash();
                        path = BakeFilePath(seed, cfgHash);
                    }
                    if (File.Exists(path))
                    {
                        File.Delete(path);
                        // Reset in-memory cache if we just deleted the active file.
                        if (System.IO.Path.GetFullPath(path) == System.IO.Path.GetFullPath(BakeFilePath((int)(ZNet.instance?.GetWorldUID() ?? 0L), ComputeCfgHash())))
                            { _bakeData = null; _bakeLoadAttempted = false; }
                        Console.instance?.Print($"[ProceduralTerrain] Deleted: {System.IO.Path.GetFileName(path)}");
                        Log.LogInfo($"[ProceduralTerrain] Deleted local bake file: {path}");
                    }
                    else
                    {
                        Console.instance?.Print($"[ProceduralTerrain] File not found: {path}");
                    }
                },
                optionsFetcher: () =>
                {
                    var opts = new List<string> { "server" };
                    opts.AddRange(System.IO.Directory.GetFiles(Paths.ConfigPath, "procedural_terrain_bake_*.bin")
                        .Select(System.IO.Path.GetFileName));
                    return opts;
                });
            new Terminal.ConsoleCommand("pt_listbakes", "pt_listbakes [server] — list local bake files with sizes; 'server' queries server list (admin only)", args =>
            {
                if (args.Length > 1 && args[1].Equals("server", System.StringComparison.OrdinalIgnoreCase))
                {
                    if (ZNet.instance == null || ZNet.instance.IsServer())
                    {
                        // Running locally — just scan directly
                        RPC_ListBakes(0L, new ZPackage());
                        return;
                    }
                    ZRoutedRpc.instance.InvokeRoutedRPC(ZRoutedRpc.Everybody, "PTListBakes", new ZPackage());
                    Console.instance?.Print("[ProceduralTerrain] Querying server bake files…");
                    return;
                }
                string dir = Paths.ConfigPath;
                var files  = System.IO.Directory.GetFiles(dir, "procedural_terrain_bake_*.bin");
                if (files.Length == 0)
                {
                    Console.instance?.Print("[ProceduralTerrain] No local bake files found.");
                    return;
                }
                Console.instance?.Print($"[ProceduralTerrain] {files.Length} bake file(s) in {dir}:");
                foreach (var f in files)
                {
                    long mb = new System.IO.FileInfo(f).Length / (1024 * 1024);
                    Console.instance?.Print($"  {System.IO.Path.GetFileName(f)}  ({mb} MB)");
                }
            },
            optionsFetcher: () => new List<string> { "server" });
            new Terminal.ConsoleCommand("pt_loc_probe", "pt_loc_probe [x z] — print nearby locations and location protection factor at world pos (default: player pos)", args =>
            {
                float wx, wz;
                if (args.Length >= 3 && float.TryParse(args[1], out float ax) && float.TryParse(args[2], out float az))
                { wx = ax; wz = az; }
                else
                {
                    var p = Player.m_localPlayer;
                    if (p == null) { Console.instance?.Print("[PT] No player."); return; }
                    wx = p.transform.position.x;
                    wz = p.transform.position.z;
                }
                string header = $"[PT] loc_probe at ({wx:F1}, {wz:F1})  protectLocations={_protectLocations}  cached={_locationsCached}  total={_locationPositions.Count}";
                Console.instance?.Print(header); Log.LogInfo(header);
                float worstF = 1f; (Vector2 pos, float extR) worstLoc = default;
                foreach (var loc in _locationPositions)
                {
                    float f = LocFactor(loc, wx, wz);
                    float d = LocDist(loc, wx, wz);
                    float r = Mathf.Max(loc.extR, LocProtectR);
                    if (d < r + LocFadeR + 10f)
                    {
                        string line = $"  loc ({loc.pos.x:F0},{loc.pos.y:F0}) extR={loc.extR:F1} dist={d:F1} factor={f:F3}";
                        Console.instance?.Print(line); Log.LogInfo(line);
                    }
                    if (f < worstF) { worstF = f; worstLoc = loc; }
                }
                if (worstF < 1f)
                {
                    string worst = $"  worst: loc ({worstLoc.pos.x:F0},{worstLoc.pos.y:F0}) extR={worstLoc.extR:F1} dist={LocDist(worstLoc, wx, wz):F1} factor={worstF:F3}";
                    Console.instance?.Print(worst); Log.LogInfo(worst);
                }
                else { Console.instance?.Print("[PT] No locations nearby."); Log.LogInfo("[PT] No locations nearby."); }
                string summary = $"[PT] final LocationFactor = {LocationFactor(wx, wz, _locationPositions):F3}";
                Console.instance?.Print(summary); Log.LogInfo(summary);
            });
            new Terminal.ConsoleCommand("pt_gradmap", "pt_gradmap [zones] [blurRadius] — save height/smooth/gradient PNGs at terrain coarse resolution (4wu/px); zones=half-radius in zones (default 3), blurRadius in wu (default 64)", args =>
            {
                var wg = WorldGenerator.instance;
                if (wg == null) { Console.instance?.Print("[PT] WorldGenerator not ready."); return; }
                var player = Player.m_localPlayer;
                if (player == null) { Console.instance?.Print("[PT] No player."); return; }
                int   N      = (args.Length >= 2 && int.TryParse(args[1], out int nArg))    ? nArg    : 3;
                float blurWu = (args.Length >= 3 && float.TryParse(args[2], out float bArg)) ? bArg   : 64f;
                // cw: coarse grid width — same formula as terrain (zones * 16 coarse cells per zone, + 1)
                // Each coarse cell = 4wu, so cw = (2*N+1)*16+1 covers (2*N+1) zones at 64wu each
                int   cw     = (2 * N + 1) * 16 + 1;
                const float hStep = 4f;
                float cWx    = player.transform.position.x;
                float cWz    = player.transform.position.z;
                float originX = cWx - (cw / 2) * hStep;
                float originZ = cWz - (cw / 2) * hStep;
                string dir   = Paths.BepInExRootPath;

                // Raw height at each coarse point (no blur, no padding needed)
                var heights  = new float[cw * cw];
                float hMin = float.MaxValue, hMax = float.MinValue;
                for (int cx = 0; cx < cw; cx++)
                for (int cz2 = 0; cz2 < cw; cz2++)
                {
                    float h = wg.GetHeight(originX + cx * hStep, originZ + cz2 * hStep);
                    heights[cx * cw + cz2] = h;
                    if (h < hMin) hMin = h; if (h > hMax) hMax = h;
                }

                // Gradient field — identical algorithm to BakeSdfSetup via ComputeGradField
                var tgX     = new float[cw * cw];
                var tgZ     = new float[cw * cw];
                var blurH   = new float[cw * cw];
                var gradMag = new float[cw * cw];
                ComputeGradField(wg, originX, originZ, cw, blurWu, hStep, tgX, tgZ, blurH, gradMag);

                void SaveGray(float[] data, string name)
                {
                    float lo = float.MaxValue, hi2 = float.MinValue;
                    foreach (var v in data) { if (v < lo) lo = v; if (v > hi2) hi2 = v; }
                    float rng = Mathf.Max(hi2 - lo, 0.001f);
                    var t = new Texture2D(cw, cw, TextureFormat.RGB24, false);
                    for (int cx = 0; cx < cw; cx++)
                    for (int cz2 = 0; cz2 < cw; cz2++)
                    { float v = (data[cx * cw + cz2] - lo) / rng; t.SetPixel(cx, cz2, new Color(v, v, v)); }
                    t.Apply();
                    System.IO.File.WriteAllBytes(System.IO.Path.Combine(dir, name), t.EncodeToPNG());
                    UnityEngine.Object.Destroy(t);
                }
                SaveGray(heights, "gradmap_height.png");
                SaveGray(blurH,   "gradmap_smooth.png");

                float magMax = 0.00001f;
                foreach (var m in gradMag) if (m > magMax) magMax = m;
                var tex = new Texture2D(cw, cw, TextureFormat.RGB24, false);
                for (int cx = 0; cx < cw; cx++)
                for (int cz2 = 0; cz2 < cw; cz2++)
                {
                    int idx = cx * cw + cz2;
                    float angle  = (Mathf.Atan2(tgZ[idx], tgX[idx]) / (2f * Mathf.PI) + 1f) % 1f;
                    float bright = gradMag[idx] / magMax;
                    float H = angle * 6f; int hi = (int)H; float f = H - hi;
                    float vv = bright, q = vv*(1f-f), t2 = vv*f;
                    float r2, g2, b2;
                    switch (hi % 6) {
                        case 0: r2=vv; g2=t2; b2=0f; break;
                        case 1: r2=q;  g2=vv; b2=0f; break;
                        case 2: r2=0f; g2=vv; b2=t2; break;
                        case 3: r2=0f; g2=q;  b2=vv; break;
                        case 4: r2=t2; g2=0f; b2=vv; break;
                        default:r2=vv; g2=0f; b2=q;  break;
                    }
                    tex.SetPixel(cx, cz2, new Color(r2, g2, b2));
                }
                tex.Apply();
                System.IO.File.WriteAllBytes(System.IO.Path.Combine(dir, "gradmap_field.png"), tex.EncodeToPNG());
                UnityEngine.Object.Destroy(tex);

                string msg = $"[PT] saved gradmap_height/smooth/field.png  ({cw}x{cw}px @ 4wu/px, blur={blurWu}wu)";
                Console.instance?.Print(msg); Log.LogInfo(msg);
            });

            Log.LogInfo("[ProceduralTerrain] Ready.");
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        static void RegisterVoxheimGenerator()
        {
            Voxheim.Core.VoxelBaselinePipeline.RegisterGenerator(new PTGenerator());
            Log.LogInfo("[ProceduralTerrain] PTGenerator registered with Voxheim.");
        }

        internal static int _dumpZonesRemaining = 0;

        static int _pollFrame;
        void Update()
        {
            if (_hotReloadPending)
            {
                _hotReloadPending = false;
                RecarveAll();
            }

            // Fast-poll while waiting for location data (zone modifications are deferred
            // until locations are known), then settle to a slow poll for config changes.
            int pollEvery = _locationsCached ? 300 : 10;
            if (++_pollFrame % pollEvery != 0) return;

            if (!_locationsCached)
                BuildLocationCache(); // sets _hotReloadPending → RecarveAll once locations arrive

            var t    = File.GetLastWriteTimeUtc(ConfigPath);
            var tLua = LatestLuaWrite();
            if (t == _configLastWrite && tLua == _luaLastWrite) return;
            _configLastWrite = t;
            _luaLastWrite    = tLua;
            LoadBiomeConfig();
            RecarveAll();
        }

        // When connected to a server, holds the server's cfg lines (applied in-memory; local file untouched).
        internal static string[]? _serverCfgLines;
        // When connected to a server, holds the server's cfg hash (used for bake file lookup).
        internal static uint? _serverCfgHash;

        // ── Helpers ───────────────────────────────────────────────────────────────

        internal static float BL3Y(float[] a, int b00, int b10, int b01, int b11, int cy, float fx, float fz)
        {
            float r0 = a[b00+cy] + (a[b10+cy]-a[b00+cy]) * fx;
            float r1 = a[b01+cy] + (a[b11+cy]-a[b01+cy]) * fx;
            return r0 + (r1-r0) * fz;
        }

        internal static void EnsureArr(ref float[]? arr, int minLen)
        {
            if (arr == null || arr.Length < minLen) arr = new float[minLen];
        }

        internal static void EnsureAuxSlots(ref CarveBuffers cb, int pipelineIdx, int numAux)
        {
            if (cb.bkModeAux == null || cb.bkModeAux.Length <= pipelineIdx)
                throw new InvalidOperationException("bkModeAux not sized");
            if (cb.bkModeAux[pipelineIdx] == null || cb.bkModeAux[pipelineIdx].Length < numAux)
                cb.bkModeAux[pipelineIdx] = new float[numAux][];
        }
    }

} // namespace ProceduralTerrain
