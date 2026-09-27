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
        public float colAmp;                 // interpolated amplitude gate (0 => mode inactive this column)
        public float bfx, bfz;               // BL fractions
        public int   b00, b10, b01, b11, bh; // BL3Y corner indices
        public float[]? arr0;                // finished SDF column aux buffer
        public float baselineCorr;           // per-column surface-baseline correction (full-res addh vs coarse-interp), 0 if not surface-anchored
    }

    // ── Abstract ModeDefinition ───────────────────────────────────────────────

    // ── Single-method mode contract ───────────────────────────────────────────
    // A mode (Lua or native C#) implements exactly ONE thing: BakeColumn — given a coarse
    // cell (worldX,worldZ), its blended params, and the vertical window (extLoS,s,bh), write bh
    // finished SDF values into its aux buffer [pi][0]. The framework owns everything else
    // identically for every mode: coarse-grid evaluation, param blending, trilinear interpolation
    // to full resolution, the floor clamp, and the blend op. This is what makes Lua↔C# a 1:1 port
    // and makes duplication/drift structurally impossible — the shared machinery lives here once.
    internal abstract class ModeDefinition {
        public string Name = "";
        public float  Amplitude;
        public int    PipelineIndex = -1;
        public string Blend  = "min";   // min | max | smoothmax
        public float  BlendK = 2f;      // smoothmax join width

        public virtual bool NeedsGradField => false;
        public virtual float GradBlurRadius => 128f;

        // Surface-anchored modes reference the vanilla surface (addh) as their baseline. When true,
        // BakeColumn writes that coarse addh into cb.bkModeBaseline[pi][cellIdx], and the framework
        // replaces the coarse-interpolated baseline with a full-res one (kills coarse faceting/cliffs)
        // by adding BL(addh_coarse) - SampleBaselineFull() per column. Cost: +1 GetHeight per column.
        public virtual bool SurfaceAnchored => false;
        // Recompute this mode's surface baseline (addh, world units) at the full-res column position
        // col.worldX/worldZ, using BL-interpolated cell params. Only called when SurfaceAnchored && toggle on.
        public virtual float SampleBaselineFull(ref CarveBuffers cb, in ColumnCtx col, WorldGenerator? wg) => 0f;

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

        // Allocate this mode's aux buffer: one column-array of cellCount*bh finished SDF values.
        // Modes that need scratch may override to allocate more slots, but slot 0 is the output.
        public virtual void BakeBegin(ref CarveBuffers cb, int cellCount, int bh, int cw4)
        {
            Plugin.EnsureAuxSlots(ref cb, PipelineIndex, 1);
            Plugin.EnsureArr(ref cb.bkModeAux![PipelineIndex][0], cellCount * bh);
        }

        // THE per-mode method: fill this cell's finished SDF column into aux[pi][0] at
        // [cellIdx*bh .. +bh). worldX/worldZ = coarse cell centre; blended params in cb.bkModeParams.
        public abstract void BakeColumn(ref CarveBuffers cb, in BakeCellArgs args);

        // ── Generic framework interpolation + floor + blend (shared by every mode) ────────────
        // Set up the column: interpolated amplitude gate + BL3Y corner indices + the aux ref.
        public void PrepareColumn(ref CarveBuffers cb, in ColumnCtx col)
        {
            ref var c = ref Plugin._modeColCtx![PipelineIndex];
            c.colAmp = Plugin.BL(cb.bkModeParams![PipelineIndex][0], col.b200, col.b210, col.b201, col.b211, col.bfx, col.bfz);
            if (c.colAmp <= 0f) return;
            c.bfx = col.bfx; c.bfz = col.bfz;
            c.b00 = col.b3_00; c.b10 = col.b3_10; c.b01 = col.b3_01; c.b11 = col.b3_11; c.bh = col.bh;
            c.arr0 = cb.bkModeAux![PipelineIndex][0];
            // Full-res surface baseline: replace the coarse-interpolated addh with an exact per-column one.
            // correction = BL(addh_coarse) - addh_fullres; a constant vertical shift down the column.
            c.baselineCorr = 0f;
            if (SurfaceAnchored && Plugin._surfaceFullRes && cb.bkModeBaseline != null
                && PipelineIndex < cb.bkModeBaseline.Length && cb.bkModeBaseline[PipelineIndex] != null)
            {
                float baseInterp = Plugin.BL(cb.bkModeBaseline[PipelineIndex]!, col.b200, col.b210, col.b201, col.b211, col.bfx, col.bfz);
                float addhFull   = SampleBaselineFull(ref cb, in col, WorldGenerator.instance);
                c.baselineCorr   = baseInterp - addhFull;
            }
        }

        // Interpolate the finished SDF column to this voxel, clamp to the floor, and blend in.
        public void ComputeVoxel(ref float combined, in VoxelCtx ctx)
        {
            ref var c = ref Plugin._modeColCtx![PipelineIndex];
            if (c.colAmp <= 0f || c.arr0 == null) return;
            float scale = Plugin._scale ?? 1f;
            float raw   = (Plugin.BL3Y(c.arr0, c.b00, c.b10, c.b01, c.b11, ctx.cy, c.bfx, c.bfz) + c.baselineCorr) * scale;
            float sdf   = Mathf.Min(raw, ctx.worldY - ctx.floorY);
            switch (Blend)
            {
                case "smoothmax": combined = Plugin.SmoothMax(combined, sdf, BlendK * scale); break;
                case "max":       if (sdf > combined) combined = sdf; break;
                // "add": base = the INCOMING sdf (compose on the running result) instead of vanilla.
                // Stack this mode's displacement-above-base onto whatever's already there, so additive
                // modes sum rather than the taller one winning. Order-dependent (unlike min/max/smoothmax).
                case "add":       combined += sdf - (ctx.worldY - ctx.effectiveH); break;
                // "subtract": add's inverse — apply this mode's displacement-above-base as a carve
                // (raise-pattern -> valley). Distinct from negative amplitude (modes clamp amp >= 0).
                case "subtract":  combined -= sdf - (ctx.worldY - ctx.effectiveH); break;
                default:          if (sdf < combined) combined = sdf; break; // "min"
            }
        }
    }

    // ── Mode Lua: user-defined Lua bake function ──────────────────────────────

    internal sealed class LuaModeDef : ModeDefinition
    {
        public string FilePath = "";
        // Blend/BlendK inherited from ModeDefinition.

        // Pool entry: a Script plus its reusable params table and output buffer. Reused per BakeColumn so
        // the hot path allocates nothing — the output column is written into a CLR float[] (wrapped as
        // MoonSharp userdata) instead of a fresh per-call table, which was the bake's dominant GC source.
        internal sealed class LuaEntry
        {
            public MoonSharp.Interpreter.Script s = null!;
            public MoonSharp.Interpreter.Table  p = null!;
            public float[]  outBuf = System.Array.Empty<float>();
            public MoonSharp.Interpreter.DynValue outUD = MoonSharp.Interpreter.DynValue.Nil;
        }

        static LuaModeDef()
        {
            // float[] element get/set from Lua (0-based) — lets bake() write its column into a shared buffer.
            if (!MoonSharp.Interpreter.UserData.IsTypeRegistered(typeof(float[])))
                MoonSharp.Interpreter.UserData.RegisterType<float[]>();
        }

        System.Collections.Concurrent.ConcurrentBag<LuaEntry>? _scriptPool;
        string? _luaSrc;
        string[] _dynParamNames  = Array.Empty<string>();
        float[]  _dynDefaults    = Array.Empty<float>();

        public override int    ParamCount => _dynParamNames.Length;
        public override string[] ParamNames => _dynParamNames;
        public override float GetDefaultParam(int i) => i < _dynDefaults.Length ? _dynDefaults[i] : 0f;

        // Surface-anchored lua modes (radial ridges/rings) ride on the injected bed height (terrain_h0).
        // Setting surfaceAnchored=true in [Mode] gives them the same full-res baseline correction as
        // native formation — replacing the coarse-baked bed with a per-column one, killing bed faceting.
        // Baseline = GroundHeight (exactly what th0 is), so the correction cancels cleanly.
        public bool SurfaceAnchoredFlag;
        public override bool SurfaceAnchored => SurfaceAnchoredFlag;
        public override float SampleBaselineFull(ref CarveBuffers cb, in ColumnCtx col, WorldGenerator? wg)
            => Plugin.GroundHeight(col.worldX, col.worldZ, wg ?? WorldGenerator.instance);

        public override bool NeedsGradField => false;
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

        LuaEntry CreateEntry()
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
                { var w = WorldGenerator.instance; return w != null ? Plugin.GroundHeight(x, z, w) : 0f; });
            s.DoString(_luaSrc!);
            // Pre-allocate params table — reused each BakeColumn, values updated in place.
            return new LuaEntry { s = s, p = new MoonSharp.Interpreter.Table(s) };
        }

        LuaEntry RentEntry()
        {
            if (_scriptPool != null && _scriptPool.TryTake(out var e)) return e;
            return CreateEntry();
        }

        void ReturnEntry(LuaEntry e) => _scriptPool?.Add(e);

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
                _scriptPool = new System.Collections.Concurrent.ConcurrentBag<LuaEntry>();
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


        // BakeBegin (aux slot alloc) inherited from ModeDefinition.

        public override void BakeColumn(ref CarveBuffers cb, in BakeCellArgs args)
        {
            if (_scriptPool == null) return;
            int pi = PipelineIndex;
            int idx = args.cellIdx;
            float amp = cb.bkModeParams![pi][0][idx];
            int baseIdx = idx * args.bh;
            float[]? auxBuf = cb.bkModeAux![pi][0];
            var wg2 = args.wg ?? WorldGenerator.instance;

            // Bed height (terrain_h0), vanilla-blended. For surface-anchored modes, store it as the
            // baseline UNCONDITIONALLY — before the amp<=0 gate — so the full-res correction is defined
            // at every cell (amp gates application, not existence; else stale baselines → boundary spikes).
            float th0 = wg2 != null ? Plugin.GroundHeight(args.worldX, args.worldZ, wg2) : 0f;
            if (SurfaceAnchoredFlag && cb.bkModeBaseline != null && pi < cb.bkModeBaseline.Length && cb.bkModeBaseline[pi] != null)
                cb.bkModeBaseline[pi]![idx] = th0;

            if (amp <= 0f)
            {
                for (int cy = 0; cy < args.bh; cy++) auxBuf![baseIdx + cy] = 0f;
                return;
            }

            (float dirx, float dirz, float mag) = wg2 != null
                ? Plugin.ZoneGradCache.Sample(wg2, args.worldX, args.worldZ, GradBlurRadius)
                : (0f, 0f, 0f);

            var e = RentEntry();
            var s = e.s; var p = e.p;
            try
            {
                // Mutate pre-allocated params table in place — avoids per-cell Table allocation.
                for (int i = 0; i < _dynParamNames.Length; i++)
                    p[_dynParamNames[i]] = (double)cb.bkModeParams![pi][i][idx];
                p["grad_dirx"]   = (double)dirx;
                p["grad_dirz"]   = (double)dirz;
                p["grad_mag"]    = (double)mag;
                p["terrain_h0"]  = (double)th0;

                // Reusable CLR output buffer (wrapped as userdata) — bake() writes its column into this
                // (0-based) instead of returning a fresh table, so the hot path allocates nothing.
                if (e.outBuf.Length < args.bh)
                {
                    e.outBuf = new float[args.bh];
                    e.outUD  = MoonSharp.Interpreter.UserData.Create(e.outBuf);
                }

                var res = s.Call(s.Globals["bake"], args.worldX, args.worldZ, p, args.extLoS, (double)args.s, args.bh, e.outUD);

                // Buffer-write modes return nil; legacy modes still return a table (read that instead).
                var tbl = res.Table;
                if (tbl != null)
                    for (int cy = 0; cy < args.bh; cy++)
                        auxBuf![baseIdx + cy] = (float)(tbl.Get(cy + 1).CastToNumber() ?? 0.0);
                else
                    for (int cy = 0; cy < args.bh; cy++) auxBuf![baseIdx + cy] = e.outBuf[cy];
            }
            catch { for (int cy = 0; cy < args.bh; cy++) auxBuf![baseIdx + cy] = 0f; }
            finally { ReturnEntry(e); }
        }
        // PrepareColumn / ComputeVoxel (interpolate + floor + blend) inherited from ModeDefinition.
    }

    // ── Native (C#) mode base ─────────────────────────────────────────────────
    // Same contract as a Lua mode — implement BakeColumn — but the column formula is native C#, so
    // it allocates nothing and bakes at full speed. Param names/defaults are declared once via
    // ParamSpec (index 0 is amplitude); the config parser sets defaults generically by name, and the
    // framework's blending/interpolation/floor/blend apply identically to Lua and native modes.
    internal abstract class NativeModeDef : ModeDefinition
    {
        protected abstract (string name, float def)[] ParamSpec { get; }
        string[]? _names;
        float[]?  _defs;
        void Ensure()
        {
            if (_names != null) return;
            var spec = ParamSpec;
            _names = new string[spec.Length];
            _defs  = new float[spec.Length];
            for (int i = 0; i < spec.Length; i++) { _names[i] = spec[i].name; _defs[i] = spec[i].def; }
            Amplitude = _defs.Length > 0 ? _defs[0] : 0f;
        }
        public override int      ParamCount        { get { Ensure(); return _names!.Length; } }
        public override string[] ParamNames        { get { Ensure(); return _names!; } }
        public override float    GetDefaultParam(int i) { Ensure(); return i < _defs!.Length ? _defs[i] : 0f; }

        // Set a default param by config key (index 0 = amplitude mirrors the Amplitude field).
        public void SetParam(string key, float v)
        {
            Ensure();
            int idx = Array.IndexOf(_names!, key);
            if (idx < 0) return;
            _defs![idx] = v;
            if (idx == 0) Amplitude = v;
        }
    }

    // ── IsotropicFormation: native C# port of formation_dev.lua ───────────────
    // Isotropic (direction-independent) domain-warped 3D-noise surface formation. Line-for-line
    // equivalent to formation_dev.lua, reading the same shared noise pool so output is identical.
    internal sealed class IsotropicFormation : NativeModeDef
    {
        // Index order IS the param contract — must match names used in the [Mode:*] cfg / profile overrides.
        static readonly (string, float)[] _spec = {
            ("amplitude", 14f), ("frequency", 0.035f), ("warpamp", 26f), ("warpfrequency", 0.01f),
            ("vscale", 0.4f), ("threshold", 0f), ("twist", 0.001f), ("twistvary", 0.3f),
            ("twistfrequency", 0.008f), ("ampvary", 0.2f), ("ampfrequency", 0.005f), ("flare", 0.6f),
            ("yoffset", 0f), ("smoothradius", 0f), ("gradfade", 0f), ("yanchor", 0f),
            ("seaatten", 1f), ("seafade", 0f), ("sealevel", 30f),
        };
        protected override (string name, float def)[] ParamSpec => _spec;

        public override bool SurfaceAnchored => true;

        // Recompute addh (the warped vanilla surface this formation anchors to) at the full-res
        // column position, using BL-interpolated cell params. Mirrors the addh block in BakeColumn.
        public override float SampleBaselineFull(ref CarveBuffers cb, in ColumnCtx col, WorldGenerator? wg)
        {
            var P = cb.bkModeParams![PipelineIndex];
            int c200 = col.b200, c210 = col.b210, c201 = col.b201, c211 = col.b211;
            float fx = col.bfx, fz = col.bfz;
            float BLp(int p) => Plugin.BL(P[p], c200, c210, c201, c211, fx, fz);
            float warpAmp = BLp(2), warpFreq = BLp(3), yoffset = BLp(12), smoothR = BLp(13), yanchor = BLp(15);
            wg ??= WorldGenerator.instance;
            var pool = Plugin._noisePool!;
            float wx = col.worldX, wz = col.worldZ;
            float warpx = pool[3].GetNoise(wx * warpFreq, wz * warpFreq, 0f) * warpAmp;
            float warpz = pool[4].GetNoise(wx * warpFreq, wz * warpFreq, 0f) * warpAmp;
            float addh;
            if (yanchor != 0f) addh = yanchor;
            else if (smoothR > 0f)
            {
                float sum = 0f;
                for (int dx = -1; dx <= 1; dx++)
                for (int dz = -1; dz <= 1; dz++)
                    sum += Plugin.GroundHeight(wx + dx * smoothR + warpx, wz + dz * smoothR + warpz, wg);
                addh = sum / 9f;
            }
            else addh = Plugin.GroundHeight(wx + warpx, wz + warpz, wg);
            return addh - yoffset;
        }

        public override void BakeColumn(ref CarveBuffers cb, in BakeCellArgs args)
        {
            int pi = PipelineIndex;
            var P = cb.bkModeParams![pi];
            int idx = args.cellIdx, baseIdx = idx * args.bh;
            float[] outCol = cb.bkModeAux![pi][0];

            float amp = P[0][idx];

            float freq      = P[1][idx],  warpAmp  = P[2][idx],  warpFreq = P[3][idx], vscale  = P[4][idx];
            float threshold = P[5][idx],  twist    = P[6][idx],  twistVary= P[7][idx], twistFq = P[8][idx];
            float ampVary   = P[9][idx],  ampFreq  = P[10][idx], flare    = P[11][idx], yoffset = P[12][idx];
            float smoothR   = P[13][idx], gradFade = P[14][idx], yanchor  = P[15][idx];
            float seaAtten  = P[16][idx], seaFade  = P[17][idx], seaLevel = P[18][idx];

            var pool = Plugin._noisePool!;
            var wg   = args.wg ?? WorldGenerator.instance;

            float wx = args.worldX, wz = args.worldZ;
            float warpx = pool[3].GetNoise(wx * warpFreq, wz * warpFreq, 0f) * warpAmp;
            float warpz = pool[4].GetNoise(wx * warpFreq, wz * warpFreq, 0f) * warpAmp;

            float addh;
            if (yanchor != 0f) addh = yanchor;
            else if (smoothR > 0f)
            {
                float sum = 0f;
                for (int dx = -1; dx <= 1; dx++)
                for (int dz = -1; dz <= 1; dz++)
                    sum += Plugin.GroundHeight(wx + dx * smoothR + warpx, wz + dz * smoothR + warpz, wg);
                addh = sum / 9f;
            }
            else addh = Plugin.GroundHeight(wx + warpx, wz + warpz, wg);
            addh -= yoffset;
            // Store coarse addh UNCONDITIONALLY — the baseline field must be defined at every cell for
            // PrepareColumn's bilinear correction. Amplitude gates whether the correction is APPLIED
            // (via colAmp), never whether the baseline EXISTS. Storing only at amp>0 left stale
            // [ThreadStatic] baselines at absent/faded cells → garbage correction → boundary spikes.
            if (cb.bkModeBaseline != null && pi < cb.bkModeBaseline.Length && cb.bkModeBaseline[pi] != null)
                cb.bkModeBaseline[pi]![idx] = addh;

            // Amplitude gate: no carving contribution here, but the baseline above is already recorded.
            if (amp <= 0f) { for (int cy = 0; cy < args.bh; cy++) outCol[baseIdx + cy] = 0f; return; }

            float lamp = Mathf.Max(amp * (1f + ampVary * pool[5].GetNoise(wx * ampFreq, wz * ampFreq, 0f)), 0f);
            if (gradFade > 0f && wg != null)
            {
                var (_, _, gmag) = Plugin.ZoneGradCache.Sample(wg, wx, wz, GradBlurRadius);
                lamp *= 1f - Mathf.Min(1f, gmag / gradFade);
            }

            // Per-mode sea-level attenuation: scale amplitude toward 0 as the surface approaches sea level.
            if (seaFade > 0f)
            {
                float baseSurfaceH = addh + yoffset;   // GetHeight-derived surface (before yoffset shift)
                lamp *= seaAtten + (1f - seaAtten) * Mathf.Clamp01((baseSurfaceH - seaLevel) / seaFade);
            }

            float ts = 1f + twistVary * pool[6].GetNoise(wx * twistFq, wz * twistFq, 0f);
            for (int cy = 0; cy < args.bh; cy++)
            {
                float wy   = args.extLoS + cy * args.s;
                float rely = wy - addh;
                float sy   = rely * vscale;
                float ang  = wy * twist * ts;
                float c    = Mathf.Cos(ang), ss = Mathf.Sin(ang);
                float n    = pool[7].GetNoise((wx * c - wz * ss) * freq, sy * freq, (wx * ss + wz * c) * freq);
                float ft   = Mathf.Clamp01(rely / Mathf.Max(lamp, 0.001f));
                float efft = threshold - flare * (4f * ft * (1f - ft));
                outCol[baseIdx + cy] = rely - (n - efft) * lamp;
            }
        }
    }

    // ── IsotropicTunnel: native C# port of tunnel_carve.lua ───────────────────
    // Isotropic 3D-noise tunnel network: two warped noise fields form a distance field carved into
    // a smoothmax blend. Line-for-line equivalent to tunnel_carve.lua, same shared noise pool
    // (slots n9..n15 = pool[8..14]). Writes SDF pre-scaled by s, matching the Lua mode; the shared
    // ComputeVoxel applies the further ×scale identically for every mode, preserving exact parity.
    internal sealed class IsotropicTunnel : NativeModeDef
    {
        static readonly (string, float)[] _spec = {
            ("amplitude", 0.6f), ("frequency", 0.018f), ("warpamp", 6f), ("warpfrequency", 0.012f),
            ("vscale", 1.5f), ("widthvary", 0.5f), ("widthfrequency", 0.008f),
            ("seaatten", 1f), ("seafade", 0f), ("sealevel", 30f),
        };
        protected override (string name, float def)[] ParamSpec => _spec;

        public override void BakeColumn(ref CarveBuffers cb, in BakeCellArgs args)
        {
            int pi = PipelineIndex;
            var P = cb.bkModeParams![pi];
            int idx = args.cellIdx, baseIdx = idx * args.bh;
            float[] outCol = cb.bkModeAux![pi][0];

            float amp = P[0][idx];
            if (amp <= 0f) { for (int cy = 0; cy < args.bh; cy++) outCol[baseIdx + cy] = 0f; return; }

            float freq  = P[1][idx], warpA = P[2][idx], warpF = P[3][idx], vsc = P[4][idx];
            float wv    = P[5][idx], wvf   = P[6][idx];
            float seaAtten = P[7][idx], seaFade = P[8][idx], seaLevel = P[9][idx];

            var pool = Plugin._noisePool!;
            var wg   = args.wg ?? WorldGenerator.instance;
            float wx = args.worldX, wz = args.worldZ, s = args.s;

            // Per-mode sea-level attenuation: shrink the tunnel radius (amplitude) as the surface
            // approaches sea level. Must scale amplitude, NOT the output SDF — a carve mode's "off"
            // state is radius->0 (giving a large-negative, solid SDF), not output->0 (which smoothmax
            // reads as carved air).
            if (seaFade > 0f)
            {
                float surfaceH = wg != null ? Plugin.GroundHeight(wx, wz, wg) : 0f;
                float seaFac = seaAtten + (1f - seaAtten) * Mathf.Clamp01((surfaceH - seaLevel) / seaFade);
                amp *= seaFac;
            }

            for (int cy = 0; cy < args.bh; cy++)
            {
                float wy = args.extLoS + cy * s;
                float sy = wy * vsc;

                float awx1 = pool[11].GetNoise(wx * warpF, sy * warpF, wz * warpF) * warpA;
                float awz1 = pool[12].GetNoise(wx * warpF, sy * warpF, wz * warpF) * warpA;
                float awx2 = pool[13].GetNoise(wx * warpF, sy * warpF, wz * warpF) * warpA;
                float awz2 = pool[14].GetNoise(wx * warpF, sy * warpF, wz * warpF) * warpA;

                float na = pool[8].GetNoise((wx + awx1) * freq, sy * freq, (wz + awz1) * freq);
                float nb = pool[9].GetNoise((wx + awx2) * freq, sy * freq, (wz + awz2) * freq);
                float wn = pool[10].GetNoise(wx * wvf, sy * wvf, wz * wvf);

                float localWidth = Mathf.Max(amp * (1f + wv * wn), 0.001f);
                float dist       = Mathf.Sqrt(na * na + nb * nb) * 0.7071f;
                outCol[baseIdx + cy] = (localWidth - dist) / localWidth * 4f * s;
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
        public float        ExtendUp     = 0f;

        // Build the internal CaveProfile from profile-level params only.
        public CaveProfile ToCaveProfile(Dictionary<string, ModeDefinition> modes)
        {
            return new CaveProfile {
                floorDepth   = FloorDepth,
                floorFade    = FloorFade,
                blur_passes  = BlurPasses,
                perona_k     = PeronaK,
                perona_lambda = PeronaLambda,
                extendUp     = ExtendUp
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
        public float extendUp;         // metres above terrain the profile may raise features (bake vertical window)

        public static readonly CaveProfile Default = new CaveProfile
        {
            blur_passes   = 0f,
            perona_k      = 0f,
            perona_lambda = 0.1f,
            floorDepth    = 8f,
            floorFade     = 0f,
            extendUp      = 0f,
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
        internal static int   _blendMode           = 1;      // default sdf: bake each present biome, crossfade output columns (correct at dissimilar-profile boundaries). 0=param (one blended bake; chirps at differing-frequency borders — ablation/compat only)
        internal static float _coarseMult          = 1f;     // coarse bake cell = 4*mult voxels (0.5=2m, 1=4m, 2=8m, 4=16m; 0.25=1m≈per-voxel). Diagnostic knob for coarse-resolution faceting.
        internal static int   CellVox { get { int v = Mathf.RoundToInt(4f * _coarseMult); return v < 1 ? 1 : v; } }  // voxels per coarse cell in XZ
        internal static bool  _surfaceFullRes       = true;   // surface-anchored modes: replace coarse-interp baseline with full-res addh (kills coarse faceting cliffs)
        internal static bool  _vanillaHeight        = true;   // sample ground via vanilla HeightmapBuilder (biome-blended, matches rendered terrain) instead of single-biome GetHeight

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

        // SDF-blend (_blendMode==1) retained per-cell contributions, populated in BakeBlendedProfiles,
        // consumed by BakeColumnSdfBlend. Only allocated/filled when _blendMode==1.
        //   _sdfBiomeInt/_sdfBiomeW: [cellIdx*MaxBiomeSlots + k] biome enum-int + its geometric blend weight
        //   _sdfBiomeN:  [cellIdx] number of contributing biomes at that cell
        //   _sdfPresentW:[mi*cellCount + cellIdx] per-mode sum of weights over biomes that HAVE the mode
        [ThreadStatic] static int[]?   _sdfBiomeInt;
        [ThreadStatic] static float[]? _sdfBiomeW;
        [ThreadStatic] static int[]?   _sdfBiomeN;
        [ThreadStatic] static float[]? _sdfPresentW;

        // wg.GetHeight is a pure function of (worldX, worldZ) for a given world seed.
        // Cache it keyed by integer coarse voxel coords — cleared on world load only.
        internal static readonly System.Collections.Concurrent.ConcurrentDictionary<long, float> _heightCache = new();

        // Per-zone SDF cache: filled by Phase 3, queried by FallbackDensityPatch.
        // Layout: data[(ci * bh + cy) * width + ri]  (ci=zoneX, ri=zoneZ, cy=Y-extLo)
        internal static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (int extLo, int bh, int width, sbyte[] data)> _sdfCache = new();

        // Zones whose SDF has already been carved with location groundwork baked in. Used to drop a
        // stale (mod-less/early) cache entry once, so the zone re-carves with the edits applied.
        internal static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _groundworkedZones = new();

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
                        { wx = ax; wz = az; wy = GroundHeight(ax, az, wg); }
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
                        float dhdx = (GroundHeight(wx + hStp, wz, wg) - GroundHeight(wx - hStp, wz, wg)) / (2f * hStp);
                        float dhdz = (GroundHeight(wx, wz + hStp, wg) - GroundHeight(wx, wz - hStp, wg)) / (2f * hStp);
                        float gm   = Mathf.Sqrt(dhdx * dhdx + dhdz * dhdz);
                        texGrad.SetPixel(ix, iz, new Color(Mathf.Clamp01(gm), dhdx * 0.5f + 0.5f, dhdz * 0.5f + 0.5f));

                        // 4. Formation SDF at rely=7 (mid-feature depth)
                        float addh = GroundHeight(wx + wx4, wz + wz4, wg);
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
                    float h = GroundHeight(originX + cx * hStep, originZ + cz2 * hStep, wg);
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

            // Fast-poll while waiting for locations to finish generating (the time-sliced coroutine
            // fills them over several frames); BuildLocationCache no-ops until ZoneSystem reports
            // LocationsGenerated, then caches once. Settle to a slow poll for config changes after.
            int pollEvery = _locationsCached ? 300 : 10;
            if (++_pollFrame % pollEvery != 0) return;

            if (!_locationsCached)
                BuildLocationCache(); // caches once LocationsGenerated; sets _hotReloadPending → RecarveAll

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
