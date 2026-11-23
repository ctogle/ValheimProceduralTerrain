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
        // True for modes that read the running field as a true DISTANCE field (SDF→SDF ops), so the per-mode
        // eikonal redistance only runs when a zone actually has such a mode active. Only surface_blobs needs it.
        public virtual bool NeedsEikonal => false;
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

    // (Lua/MoonSharp mode support removed in 0.0.3 — all shipped modes are native C#.)

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

    // ── SurfaceBlobs: the first SDF→SDF operator (Path B proof-of-concept) ─────
    // Unlike bake-vs-params modes, this reads the running distance field D and unions noise-modulated
    // balls onto EVERY surface (floors and cave ceilings alike) via |D| − rEff. Applied directly to
    // the running field in the sequential compose (see the SurfaceBlobs branch there); needs
    // eikonal=true so |D| is metric (uniform ball size). BakeColumn is a no-op — composed specially.
    internal sealed class SurfaceBlobs : NativeModeDef
    {
        public override bool NeedsEikonal => true;   // SDF→SDF operator: reads the running field as a distance metric
        static readonly (string, float)[] _spec = {
            ("amplitude", 3f),    // MAX ball radius (world units); also the on/off gate
            ("frequency", 0.05f), // lattice density (lump scale)
            ("minradius", 0f),    // if >0, each ball's radius is rolled uniformly in [minradius, amplitude]
            ("stretchn",  1f),    // scale along the surface normal: >1 spikes/towers, <1 flat scales/shields
            ("taper",     0f),    // 0 = sphere, 1 = cone to a point toward the normal tip (stalagmites/crystals)
            ("pnorm",     2f),    // shape exponent: 2 round, →∞ boxy boulders, <2 octahedral/crystalline
            // ── Distribution (Tier 2): bias WHICH lattice points become stamps ──
            ("coverage",    1f),  // probabilistic keep per stamp (1 = all, 0.3 = ~30% → sparse scatter)
            ("bias",        0f),  // 0 = uniform, 1 = contour (periodic Y bands → rings)
            ("biasspacing", 20f), // contour: ring spacing in world Y
            ("biaswidth",   0.2f),// contour: band half-width as a fraction of spacing (0..0.5)
            ("biasphase",   0f),  // contour: ring offset
            // ── Brush anisotropy (Tier 1): independent scale on the surface frame {N, G, T} ──
            ("stretchg",    1f),  // scale along G = downslope (gradient): >1 drips/blades running downhill
            ("stretcht",    1f),  // scale along T = cross-slope (N×G): >1 ridges/scales along the contour
            ("taperpow",    1f),  // taper curve along +N: 1 linear cone, >1 ogive/bullet, <1 trumpet
            // ── Distribution gates (Tier 2): keep a stamp only if it passes all (defaults pass everything) ──
            ("slopelo",     0f),  // keep where steepness (deg) >= slopelo. 0=floor/ground, 90=wall, 180=ceiling
            ("slopehi",   180f),  // ... and <= slopehi. 30..90=cliffs, 0..15=flats, 100..180=overhangs/ceilings
            ("altlo",  -10000f),  // keep only where stamp world-Y >= altlo (snowline/shore bands)
            ("althi",   10000f),  // ... and <= althi
            ("clusterfreq", 0f),  // >0 enables a global 2D noise mask (patchy fields); 0 = uniform
            ("clusterthresh",0f), // keep where cluster noise >= clusterthresh (−1..1); higher = sparser patches
            ("aspectlo",    0f),  // keep where slope faces azimuth in [aspectlo, aspecthi] deg (0=+X/E, 90=+Z/N)
            ("aspecthi",  360f),  // ... compass-facing gate (south-facing only, lee side, etc.)
            ("curvlo", -9999f),   // keep where surface curvature >= curvlo (>0 convex ridge, <0 concave hollow)
            ("curvhi",  9999f),   // ... convex-only (spurs) vs concave-only (hollows)
            // bias: 0=none, 1=contour (periodic elevation → rings), 2=ridge (periodic aspect → radial ribs)
            // ── Operator (Tier 3): how stamps combine into the field ──
            ("carve",       0f),  // 0 = add (union, bumps), 1 = carve (subtract, pits/caves)
            ("fuse",        0f),  // smooth-blend radius: 0 = hard crease, >0 = metaball melt between stamps
            // ── Propagation (Tier 4): each seed walks a chain of child stamps (carve+chain = lumpy tunnels) ──
            ("chain",       0f),  // # child stamps walked from each seed (0 = off). Tiles across zones.
            ("chainstep",   4f),  // world distance between chain links
            ("chainwander", 0.3f),// 0 = straight chain, higher = winding
            ("chaininward", 0f),  // 0 = random walk; 1 = walk straight into the surface (−N) → caves growing inward
            ("ridgeratio",  0f),  // >0: place only where fast principal curvature >= ratio*|slow| (ridge, any orientation),
                                  //     and ALIGN the stamp so stretchg runs along the slow axis (the ridgeline)
            // ── Chain branching (Tier 4+): forks the walk into a network/dendrite ──
            ("branchprob",  0f),  // per-step probability a chain spawns a branch (0 = linear worm)
            ("branchangle",40f),  // branch turn angle (deg) off the parent direction
            ("branchdepth", 2f),  // max branch recursion depth
            // ── Field steering: walk follows a coherent flow field instead of pure random wander ──
            ("flowsteer",   0f),  // 0 = off; how strongly the walk aligns to the flow field
            ("flowfreq",  0.02f), // flow-field noise frequency (lower = broader, smoother flow)
            ("hubseek",     0f),  // 0 = off; steer toward the nearest hub point → tunnels converge on a central chamber
            ("hubspacing",300f),  // spacing of the deterministic hub lattice (≈ one hub per mountain)
            ("keepdepth",   0f),  // >0: keep the walk this far below the surface (bend back into rock if it surfaces) → tunnels stay buried & carve their full length
            ("chainlevel",  0f),  // 0 = free 3D walk; 1 = fully horizontal (damp dir.y each step) → level tunnels that bore into the mountainside, run long, and meet
            ("chaintile",   0f),  // >0: generate the whole chain from the GLOBAL ground surface (not this zone's field) so tunnels TILE across zones = run their full length; Y follows ground − keepdepth
            ("chainslope",  0f),  // target downward drift the walk levels toward (0 = flatten to horizontal; 0.1 = gentle descent into the mountain)
            ("biomemajority",0f), // >0: only seed where THIS mode's biome is the dominant biome at the point (global GetBiome gate) AND use a CONSTANT radius (no biome-blend fade) → features stay full-size inside their biome and cut off at the edge
            ("skyopen",     1f),  // 1 = sky-exposed OUTER surfaces only (default); 0 = either; -1 = strictly INTERNAL (reject anything open to sky) → cave ledges without hillside teeth
            ("concaveratio",0f),  // >0: place only on anisotropic CONCAVE surfaces (|min curvature| >= ratio*|max|, concave) — the inverse of ridgeratio; for stalactites on tube/cave undersides
            ("chainhorizontal",0f), // >0: launch the chain HORIZONTALLY (flatten dir.y) regardless of surface normal — for flat biomes (Plains) where −N points straight down and would dive. Mountains leave 0.
            ("forestlo", -10000f),  // keep a stamp only where the vanilla WorldGenerator.GetForestFactor >= forestlo ...
            ("foresthi",  10000f),  // ... and <= foresthi. Lower factor = DENSER trees (vanilla in-forest = <1.15). Latch features onto the native forest field.
            ("liftfrac",      0f),  // offset the stamp centre UP the surface normal by liftfrac·radius (0 = centred on surface; 0.25 = sits proud by r/4; negative = buries deeper)
            ("seaatten",      1f),  // sea-level amplitude attenuation (same as the isotropic modes): stamp RADIUS → 0 as the surface approaches sealevel. 1 = no atten
            ("seafade",       0f),  // metres above sealevel over which the atten ramps in (0 = OFF)
            ("sealevel",     30f),  // sea level reference
            ("taperflip",     0f),  // >0: taper toward the −N BASE instead of the +N tip → narrow bottom, wide top (mushroom/trumpet bell opening UP). pairs with liftfrac to raise the cap off the ground
            ("flatcap",       0f),  // >0: CYLINDER form — round cross-section (G,T) with FLAT caps along N (comb = max(radial, |uN|)) instead of the rounded ellipsoid. tapered+flatcap = truncated cone / sharp-rimmed bell
            ("coastseed",     0f),  // >0: only seed within this many world-units of the OCEAN biome, AND aim the chain INLAND (up the ocean-distance gradient). For canals that start at the shoreline and run in. 0 = off
            ("chainroute",    0f),  // >0: route the canal through the Voronoi location-corridor graph (CanalRouter) instead of the hashed random walk — snakes AROUND protected locations. needs coastseed + generated locations. 0 = hashed walk (default)
            ("atriumradius",  0f),  // >0: carve a DOME of this radius at each merge-junction atrium site on the shared LocationVoronoi graph (needs chainroute + generated locations). 0 = off
            ("atriumlift",    0.3f),// dome centre raised above the water floor (RiverWaterLevel) by this fraction of the radius → floor clamp leaves a flat-ish water floor, dome ceiling above
            ("atriummerge", 300f),  // cluster merge-junctions within this radius into a single atrium site
            ("atriumnoise",   0.25f),// per-site radius jitter (fraction) so domes aren't uniform spheres
            ("breachradius",  0f),  // >0: carve a vertical SHAFT of this radius from each atrium up through the surface → a visible entrance. 0 = off
            ("breachstep",    4f),  // vertical spacing of shaft stamps
            ("atriumfill",    0.85f),// dome HEIGHT as a fraction of the floor→surface depth (1 = ceiling right at the surface). radius derives from this
            ("atriummindepth",18f), // skip atriums where surface−waterfloor < this → never place a dome with no overburden (no half-exposed rooms)
            ("atriumlumps",   8f),  // satellite stamps per atrium → lumpy, noisy dome instead of one smooth sphere. 0 = single sphere
            ("atriumangularfrac", 0.4f),// fraction of satellites that are ANGULAR (hard-carved superellipsoid) vs round. 0 = all round
            ("atriumangularp",    4f),  // Lp exponent for angular satellites (>2 = boxy; higher = sharper). 4–8 reads clearly
            // ── Open-top floor carve (Tier 3 variant): gorges ──────────────────────────────
            // Upward-OPEN, functionally-infinite parabola carved from a SEA-LEVEL datum (+noise), NOT the stamp centre.
            // For each voxel near the centreline: floorWorldY = base + noise + rise·(d_h/k)^p ; carve EVERYTHING above it
            // (no upper cap) → open to the sky, width grows with height. Bucketed in XZ only (vertical column).
            ("opentop",       0f),  // >0: enable the open parabolic gorge carve
            ("opentopk",      9f),  // half-width (world-units) at height = rise above the datum → gorge WIDTH
            ("opentopp",      2f),  // floor-profile exponent: 2 = parabola; higher (4–8) = FAT flat floor + steep walls (bell/canyon)
            ("opentopbase",  30f),  // floor datum world-Y (30 = sea level). The gorge floor sits here, independent of surface height
            ("opentoprise",  40f),  // height above the datum at which the half-width reaches k → wall steepness (bigger = taller/steeper walls)
            ("opentopnoise",  6f),  // amplitude (m) of floor undulation added to the datum
            ("opentopnoisefreq",0.01f), // frequency of the floor-noise
            ("chainoutward",  0f),  // >0: launch the chain along +N (OUT of the surface into the cavity) — mirror of chaininward. With chainlevel+chainslope the arm then droops DOWNWARD → a buttress/spout that reaches off a wall and drips with gravity
            ("chaingravity",  0f),  // >0: PROJECTILE mode — constant downward accel per step accumulates vertical speed → a true parabola that reaches out then steepens to near-vertical on impact (replaces the chainlevel/chainslope relax). Bigger = drops sooner/steeper
            ("chainlaunchup", 0f),  // projectile INITIAL upward velocity (units of horizontal speed): >0 lobs the arc UP then it apexes and descends STEEPLY (a thrown-rope/flying-buttress arch that hits the ground at a sharp angle), instead of a flat launch that only meets the ground shallowly
            ("chaintaper",    1f),  // radius multiplier at the END of the chain (1 = no taper, uniform tube; 0.2 = thins to 20% at the tip). Keyed on progress-from-ROOT so BRANCHES inherit the thinning (a fork continues getting thinner from where it peeled off, not reset to full)
            ("canalmeander",  0f),  // routed-canal (chainroute) tube: lateral snake amplitude as a fraction of radius (0 = straight centreline, dead-smooth bore; 0.7 = lazy horizontal wander). Per-mode so plains/swamp sewers stay straight unless opted in
        };
        protected override (string name, float def)[] ParamSpec => _spec;
        public override void BakeColumn(ref CarveBuffers cb, in BakeCellArgs args)
        {
            int pi = PipelineIndex, baseIdx = args.cellIdx * args.bh;
            var outCol = cb.bkModeAux![pi][0];
            for (int cy = 0; cy < args.bh; cy++) outCol[baseIdx + cy] = 0f;   // no-op; unioned specially
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
            // basebuffer (m): weld the formation SOLID for this depth just BELOW its anchor so it locks into the
            // vanilla surface instead of floating a hair above it (the warp shifts addh above local ground → a
            // "pinch gap" seam at the base). 0 = off. Appended last → existing param indices unchanged.
            ("basebuffer", 6f),   // TEST: global default 6m to verify the pinch-gap weld works everywhere (was 0)
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
            float baseBuffer = P[19][idx];

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
                float v    = rely - (n - efft) * lamp;
                // Pinch-gap weld: for a band just below the anchor, force the field at least as solid as a plain
                // downward ramp (min with rely, which is <0 there) so noise can't punch a void between the
                // formation base and the vanilla surface it should fuse into. Below the band the SDF is already solid.
                if (baseBuffer > 0f && rely < 0f && rely > -baseBuffer)
                    v = Mathf.Min(v, rely);
                outCol[baseIdx + cy] = v;
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
            ("altlo", -10000f), ("althi", 10000f),   // absolute world-Y band: carve ONLY within [altlo,althi] (hard floor above the sewer). default = unbounded
            ("depthlo", 0f), ("depthhi", 0f),        // DEPTH-RELATIVE band below the composed surface: carve only in [surf−depthhi, surf−depthlo]. tracks terrain (no world-segmentation). 0 = off
            ("bandfade", 0f),                        // soft ramp (m) at the depth-band edges: sheet width fades 0→full over this → no hard terraced ceiling/floor. 0 = hard edges
            // PER-MODE floor: re-solidify THIS mode's carve below (surface − floordepth), independent of (and in addition
            // to) the blended PROFILE floorDepth. Mode params aren't diluted across biome seams, so the tunnel keeps its
            // own floor even blending into a deeper-floored neighbour → no deep drops dragged in by a mountain's floorDepth.
            ("floordepth", 0f),                      // 0 = off; >0 = re-solidify below surface−floordepth
            ("floorfade", 0f),                       // soft taper (m) above the mode floor (0 = hard)
            // (strata floor-galleries extracted to the dedicated isotropic_strata mode)
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
            float altLo = P[10][idx], altHi = P[11][idx];
            float depthLo = P[12][idx], depthHi = P[13][idx], bandFade = P[14][idx];
            float floorDepthMode = P[15][idx];   // P[16] floorfade is unused now the floor is applied per-column (plane intersect), kept in the spec for cfg compat

            var pool = Plugin._noisePool!;
            var wg   = args.wg ?? WorldGenerator.instance;
            float wx = args.worldX, wz = args.worldZ, s = args.s;

            // depth-relative band: carve sheets only between (surface−depthhi) and (surface−depthlo). Tracks the
            // composed surface per column → the band follows the terrain instead of a flat absolute slab.
            float surfH = 0f; bool haveSurf = false;
            float bandHi = float.MaxValue, bandLo = float.MinValue;
            if (depthHi > 0f && wg != null)
            {
                surfH = Plugin.ComposedSurfaceSample(wx, wz, wg); haveSurf = true;
                bandHi = surfH - depthLo;   // top of band (closer to surface)
                bandLo = surfH - depthHi;   // bottom of band (deeper)
            }
            // Per-mode floor anchors to the BASE ground height (what the profile floorDepth uses: hCell − floorDepth),
            // NOT ComposedSurfaceSample — that sample uses the DOMINANT biome's formation and STEPS up to the mountain
            // at seams, floating the floor above the real BF surface. GroundHeight is the vanilla 4-corner-blended base:
            // smooth across seams, so surf−floordepth stays a true 24m below the terrain and never touches the surface.
            bool haveFloor = floorDepthMode > 0f && wg != null;
            float modeFloorY = haveFloor ? Plugin.GroundHeight(wx, wz, wg) - floorDepthMode : float.MinValue;
            // Record the per-mode floor for this cell; the re-solidify is applied PER COLUMN in the compose (bilinear
            // on the recorded per-cell floor), not here — applying it per-cell made the relief floor coarse-cell-stepped (terraces).
            if (haveFloor) Plugin.RecordModeFloor(args.cellIdx, modeFloorY);

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

                // --- tunnel path (3D noise) with its hard band gate ---
                if (wy < altLo || wy > altHi || wy < bandLo || wy > bandHi) { outCol[baseIdx + cy] = -1e4f; continue; }
                float bandFac = 1f;
                if (bandFade > 0f && depthHi > 0f) bandFac = Mathf.Clamp01(Mathf.Min(bandHi - wy, wy - bandLo) / bandFade);
                if (bandFac <= 0f) { outCol[baseIdx + cy] = -1e4f; continue; }

                float bandAmp = amp * bandFac;
                float sy = wy * vsc;

                float awx1 = pool[11].GetNoise(wx * warpF, sy * warpF, wz * warpF) * warpA;
                float awz1 = pool[12].GetNoise(wx * warpF, sy * warpF, wz * warpF) * warpA;
                float awx2 = pool[13].GetNoise(wx * warpF, sy * warpF, wz * warpF) * warpA;
                float awz2 = pool[14].GetNoise(wx * warpF, sy * warpF, wz * warpF) * warpA;

                float na = pool[8].GetNoise((wx + awx1) * freq, sy * freq, (wz + awz1) * freq);
                float nb = pool[9].GetNoise((wx + awx2) * freq, sy * freq, (wz + awz2) * freq);
                float wn = pool[10].GetNoise(wx * wvf, sy * wvf, wz * wvf);

                float localWidth = Mathf.Max(bandAmp * (1f + wv * wn), 0.001f);
                float dist       = Mathf.Sqrt(na * na + nb * nb) * 0.7071f;
                float o = (localWidth - dist) / localWidth * 4f * s;
                // Per-mode floor (surf−floordepth, keeps BF's own un-blended floor at mtn seams) is recorded per cell
                // above and re-solidified PER COLUMN in the compose — not applied here, so the relief floor is smooth
                // instead of coarse-cell-stepped (terraces). See RecordModeFloor / the compose per-column clamp.
                outCol[baseIdx + cy] = o;
            }
        }
    }

    // ── IsotropicStrata: stacked horizontal floor-galleries ───────────────────
    // Extracted from IsotropicTunnel (where strataperiod>0 silently reskinned the tunnel carve — a smell).
    // Carves periodic-Y floor/gap slabs confined to a depth-relative band, with skylight shafts (hole noise),
    // regional slab tilt, drifting/tapering shafts, and internal support pillars. Behaviour is byte-identical
    // to the old isotropic_tunnel strata branch — same noise-pool slots (8..14), same formulas, same *4*s out.
    internal sealed class IsotropicStrata : NativeModeDef
    {
        static readonly (string, float)[] _spec = {
            ("amplitude",    1f),                    // index 0 = fade-to-off amplitude (blend contract): scales gallery OPENNESS; →0 = solid/off.
                                                     //           Diluted by absent biomes at seams so strata fades out cleanly instead of blending its period.
            ("strataperiod", 0f),                    // vertical floor-to-floor spacing (m). frequency-class (index>0 → present-averaged, NOT diluted at seams). <=0 = off (must be set explicitly)
            ("stratagap",    0.5f),                  // fraction of each period that's open walkable air
            ("stratawobble", 0f),                    // XZ height wobble of the floors (m) so they aren't glass-flat
            ("strataholefreq", 0.02f),               // XZ frequency of the vertical connecting holes (stairwells)
            ("strataholethresh", 0.6f),              // noise threshold above which a hole opens (higher = fewer)
            ("stratataper", 0f),                     // skylight MOUTH taper over the whole shaft (0 = constant width)
            ("stratataperfreq", 0.03f),              // XZ scale of the flare-in/flare-out regions
            ("stratatilt", 0f),                      // slab TILT: slow XZ height-warp so slabs cant per region (m)
            ("stratatiltfreq", 0.01f),               // XZ scale of the tilt regions (low = broad regional cant)
            ("stratadrift", 0f),                     // skylight DRIFT: m of XZ wander the shaft centre picks up climbing
            ("stratadriftfreq", 0.02f),              // scale of the drift noise
            ("stratapillar", 0f),                    // internal SUPPORT COLUMNS: m of solid forced through the gaps
            ("stratapillarfreq", 0.04f),             // XZ frequency of the support columns
            ("stratapillarthresh", 0.5f),            // noise threshold a column must exceed
            ("altlo", -10000f),                      // solid below this world-Y (hard floor above the sewer)
            ("althi", 10000f),                       // optional hard top cap
            ("depthlo", 0f),                         // band top = surface−depthlo
            ("depthhi", 0f),                         // band bottom = surface−depthhi (0 = no band → unconfined floors)
        };
        protected override (string name, float def)[] ParamSpec => _spec;

        public override void BakeColumn(ref CarveBuffers cb, in BakeCellArgs args)
        {
            int pi = PipelineIndex;
            var P = cb.bkModeParams![pi];
            int idx = args.cellIdx, baseIdx = idx * args.bh;
            float[] outCol = cb.bkModeAux![pi][0];

            float amp = P[0][idx];
            // amp is the fade-to-off knob (index-0 blend contract). At a biome seam the blend dilutes it toward 0;
            // amp<=0 means the mode is absent/faded-out here → emit solid, don't carve (prevents partial fragments).
            if (amp <= 0f) { for (int cy = 0; cy < args.bh; cy++) outCol[baseIdx + cy] = -1e4f; return; }
            if (amp > 1f) amp = 1f;
            float strataPeriod = P[1][idx];
            if (strataPeriod <= 0f) { for (int cy = 0; cy < args.bh; cy++) outCol[baseIdx + cy] = -1e4f; return; }
            float strataGap = P[2][idx] * amp;   // amp scales OPENNESS → amp→0 closes the floors (solid), period UNCHANGED
            float strataWobble = P[3][idx];
            float strataHoleFreq = P[4][idx], strataHoleThresh = P[5][idx];
            float strataTaper = P[6][idx], strataTaperFreq = P[7][idx];
            float strataTilt = P[8][idx], strataTiltFreq = P[9][idx];
            float strataDrift = P[10][idx], strataDriftFreq = P[11][idx];
            float strataPillar = P[12][idx], strataPillarFreq = P[13][idx], strataPillarThresh = P[14][idx];
            float altLo = P[15][idx], altHi = P[16][idx];
            float depthLo = P[17][idx], depthHi = P[18][idx];

            var pool = Plugin._noisePool!;
            var wg   = args.wg ?? WorldGenerator.instance;
            float wx = args.worldX, wz = args.worldZ, s = args.s;

            float surfH = 0f; bool haveSurf = false;
            float bandHi = float.MaxValue, bandLo = float.MinValue;
            if (depthHi > 0f && wg != null)
            {
                surfH = Plugin.ComposedSurfaceSample(wx, wz, wg); haveSurf = true;
                bandHi = surfH - depthLo;
                bandLo = surfH - depthHi;
            }

            float holeN      = pool[9].GetNoise(wx * strataHoleFreq, 0f, wz * strataHoleFreq);
            float taperN     = strataTaper > 0f ? pool[8].GetNoise(wx * strataTaperFreq, 0f, wz * strataTaperFreq) : 0f;
            float tiltField  = strataTilt  > 0f ? strataTilt * pool[11].GetNoise(wx * strataTiltFreq, 0f, wz * strataTiltFreq) : 0f;
            float pillarSolid = strataPillar > 0f
                ? (pool[14].GetNoise(wx * strataPillarFreq, 0f, wz * strataPillarFreq) - strataPillarThresh) * strataPillar
                : -1f;

            for (int cy = 0; cy < args.bh; cy++)
            {
                float wy = args.extLoS + cy * s;
                float wob = strataWobble != 0f ? strataWobble * pool[10].GetNoise(wx * strataHoleFreq, 0f, wz * strataHoleFreq) : 0f;
                float ph  = (wy + wob - tiltField) / strataPeriod; ph -= Mathf.Floor(ph);
                float gc  = Mathf.Clamp01(strataGap) * 0.5f;
                float dGap = (gc - Mathf.Abs(ph - gc)) * strataPeriod;
                float sdf  = haveSurf ? Mathf.Min(dGap, Mathf.Min(wy - bandLo, bandHi - wy)) : dGap;
                if (pillarSolid > 0f) sdf = Mathf.Min(sdf, -pillarSolid);
                if (haveSurf && wy > bandHi - strataPeriod)
                {
                    float hN = holeN;
                    if (strataDrift > 0f)
                    {
                        float dx = strataDrift * pool[12].GetNoise(wx * strataDriftFreq, wy * strataDriftFreq, wz * strataDriftFreq);
                        float dz = strataDrift * pool[13].GetNoise(wx * strataDriftFreq, wy * strataDriftFreq, wz * strataDriftFreq);
                        hN = pool[9].GetNoise((wx + dx) * strataHoleFreq, 0f, (wz + dz) * strataHoleFreq);
                    }
                    float eth = strataHoleThresh;
                    if (strataTaper > 0f)
                    {
                        float rampLo = bandHi - strataPeriod;
                        eth -= taperN * strataTaper * Mathf.Clamp01((wy - rampLo) / Mathf.Max(surfH + 2f - rampLo, 0.001f));
                    }
                    eth += (1f - amp) * 2f;   // amp-fade the stairwell holes too: at amp→0 the threshold exceeds noise max → no holes (no stray shafts through the closed floors)
                    if (hN > eth) sdf = Mathf.Max(sdf, Mathf.Min(wy - bandLo, (surfH + 2f) - wy));
                }
                sdf = Mathf.Min(sdf, wy - altLo);
                if (altHi < 9000f) sdf = Mathf.Min(sdf, altHi - wy);
                outCol[baseIdx + cy] = sdf * 4f * s;
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
        public float        FloorLevel   = -10000f;   // ABSOLUTE world-Y below which carving re-solidifies (floorFade softens it). -10000 = off
        public float        BlurPasses   = 0f;
        public float        PeronaK      = 0f;
        public float        PeronaLambda = 0.1f;
        public float        ExtendUp     = 0f;
        public float        ComposeSmoothY = 0f;  // per-profile compose-Y anti-alias window (voxels). 0 = point-sample (original). plains sets >0 for the strata terracing fix without touching other biomes.
        public float        VegDensity   = 1f;    // per-biome multiplier on vanilla tree count (m_min/m_max) at placement — non-destructive (restored after the zone). 1 = vanilla
        public float        VegForestWiden = 0f;  // per-biome widen of the vanilla forest band (m_forestTresholdMax += this) → trees spill into more-open ground. 0 = vanilla
        public bool         VegInForest  = false; // force tree items to be forest-gated (m_inForest=true) so they CLUSTER by GetForestFactor (vanilla swamp trees are uniform)
        public float        VegForestMax = -1f;   // if >=0, SET m_forestTresholdMax → trees only where factor <= this (low = dense cores). -1 = leave
        public int          VegGroupSize = 0;     // if >0, SET m_groupSizeMin=m_groupSizeMax → N trees per clump (fat clusters). 0 = leave
        public float        VegGroupRadius = -1f; // if >=0, SET m_groupRadius → clump spread. -1 = leave
        public float        VegTiltInherit = -1f; // if >=0, SET m_chanceToUseGroundTilt → trees LEAN to the ground normal (our conform normal) this fraction of the time. 1 = always lean. -1 = leave vanilla (≈upright)
        public float        VegRandTilt    = -1f; // if >=0, SET m_randTilt → random extra lean (deg) applied regardless of ground. -1 = leave vanilla
        public float        VegMaxTilt     = -1f; // if >=0, SET m_maxTilt → cap on ground-tilt (deg); raise so leaning trees aren't clamped upright. -1 = leave vanilla
        public float        VegConformMaxTilt = -1f; // if >=0, per-biome override of the CONFORM slope-reject threshold (global [Vegetation] maxTilt). -1 = use global
        public float        VegSupportRadius  = -1f; // if >=0, per-biome override of the conform footprint support radius (global [Vegetation] supportRadius). -1 = use global
        public bool         VegVanillaHeight  = false; // if true, place vegetation on the VANILLA heightmap in this biome (skip the voxel-surface conform AND the mesh snap) → base veg ignores PT-added geometry like arches. For light-touch biomes whose surface ≈ vanilla (Mistlands).

        // Build the internal CaveProfile from profile-level params only.
        public CaveProfile ToCaveProfile(Dictionary<string, ModeDefinition> modes)
        {
            return new CaveProfile {
                floorDepth   = FloorDepth,
                floorFade    = FloorFade,
                floorLevel   = FloorLevel,
                blur_passes  = BlurPasses,
                perona_k     = PeronaK,
                perona_lambda = PeronaLambda,
                extendUp     = ExtendUp,
                composeSmoothY = ComposeSmoothY
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
        public float floorLevel;       // ABSOLUTE world-Y floor: carving re-solidifies below this (floorFade softens). -10000 = off
        public float extendUp;         // metres above terrain the profile may raise features (bake vertical window)
        public float composeSmoothY;   // compose-Y anti-alias window (voxels) for THIS profile's modes. 0 = pure point-sample (original). >0 low-passes near-horizontal relief (plains strata). Per-profile so it can't bleed into other biomes.

        public static readonly CaveProfile Default = new CaveProfile
        {
            blur_passes   = 0f,
            perona_k      = 0f,
            perona_lambda = 0.1f,
            floorDepth    = 8f,
            floorFade     = 0f,
            floorLevel    = -10000f,
            extendUp      = 0f,
            composeSmoothY = 0f,
        };
    }

    [BepInPlugin("com.ctogle.procedural_terrain", "ProceduralTerrain", Plugin.Version)]
    [BepInDependency("com.bogey.voxheim", BepInDependency.DependencyFlags.SoftDependency)]
    public partial class Plugin : BaseUnityPlugin
    {
        internal const string Version = "0.0.2";   // single source of truth — also the world version tag (see WorldVersionGuard)
        internal static ManualLogSource Log = null!;

        // HeightGrid scale delegate. Built in BuildDelegates.

        // World units per voxel — read lazily, Voxheim initialises it after plugin load.
        static FieldInfo? _scaleField;
        internal static float? _scale;
        internal static float ActiveScale()
        {
            if (_scale is { } v && v > 0f) return v;
            if (_scaleField != null && _scaleField.GetValue(null) is float sf && sf > 0f) return sf;
            return 1f;
        }

        // ── Debug flags ───────────────────────────────────────────────────────────
        internal static volatile bool _disableVegetation;
        internal static bool  _vegetationConform = true;   // snap vegetation to the voxel surface + reject steep/overhung spots (vanilla placement rides the 2.5D heightmap, blind to our 3D terrain)
        internal static float _vegMaxTilt        = 30f;    // reject vegetation where the voxel surface is steeper than this (degrees); covers "flat enough to hide the trunk base"
        internal static float _vegSupportRadius   = 2.5f;   // footprint radius (m) for the 8-direction support ring; ground must not fall away past maxTilt within this of the trunk base
        // Per-zone EFFECTIVE conform params: default to the globals above, overridden per-biome in the veg Prefix
        // (vegConformMaxTilt / vegSupportRadius profile keys). TrySurfaceConform + its callers read these.
        internal static float _vegMaxTiltEff      = 30f;
        internal static float _vegSupportRadiusEff = 2.5f;
        [System.ThreadStatic] internal static bool _inVegPlacement;   // set only while ZoneSystem.PlaceVegetation runs, so the GetGroundData postfix scopes to vegetation
        internal static string? _forcedSeed;   // if set (cfg [Debug] seed=), every world is forced to this seed string (testing aid); null = normal
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
        static DateTime _clientConfigLastWrite = DateTime.MinValue;

        // ── Pipeline ──────────────────────────────────────────────────────────────
        static volatile ModeDefinition[] _pipeline = Array.Empty<ModeDefinition>();
        // _biomeParams[biome][pipelineIdx][paramIdx] — blendable params per biome per mode
        static volatile Dictionary<Heightmap.Biome, float[][]> _biomeParams = new Dictionary<Heightmap.Biome, float[][]>();
        // Biome mask (OR of Heightmap.Biome flags) of biomes whose profile runs a mode that carves atriums
        // (surface_blobs atriumradius, param idx 56, > 0). Mirrors the carve's biomeMask so the nav/viz commands
        // surface only atriums the carve ACTUALLY emits — not Voronoi junctions in biomes with no sewer (e.g. BF).
        internal static int AtriumBiomeMask()
        {
            int mask = 0;
            foreach (var kv in _biomeParams)
            {
                var bp = kv.Value; if (bp == null) continue;
                for (int mi = 0; mi < bp.Length; mi++)
                    if (bp[mi] != null && bp[mi].Length > 56 && bp[mi][56] > 0f) { mask |= (int)kv.Key; break; }
            }
            return mask;
        }
        // Per-biome vegetation interpretation overrides (applied live at PlaceVegetation, restored after — never baked).
        internal struct VegOverride { public float density, widen, forestMax, groupRadius, tiltInherit, randTilt, maxTilt, conformMaxTilt, supportRadius; public bool inForest; public int groupSize; public bool vanillaHeight; }
        internal static volatile Dictionary<Heightmap.Biome, VegOverride> _vegByBiome = new Dictionary<Heightmap.Biome, VegOverride>();

        // ── Location protection ───────────────────────────────────────────────────
        internal static readonly List<(Vector2 pos, float extR)> _locationPositions = new();
        internal static bool _locationsCached;
        internal static bool  _protectLocations    = true;
        internal static float _locDomeHeight       = 0f;     // metres the location protection DOME rises over the pad centre (hemisphere bubble, terrain free above). 0 = full-column protect (guaranteed open to sky — spawn/altars)
        internal static float _locBandFade         = 8f;     // metres the location protection tapers out above the dome top
        internal static bool  _applyGroundwork     = true;   // bake location terrain modifiers into the baseline
        // Shared global corridor graph (LocationVoronoi). When on, canal/sewer routing pulls paths from ONE global
        // annotated Voronoi graph instead of rebuilding a windowed Delaunay per seed. EXPERIMENTAL, default off —
        // when off, routing is byte-for-byte the legacy windowed CanalRouter.Route path.
        internal static bool  _corridorGraph       = false;
        internal static float _corridorSteiner     = 0f;     // Steiner lattice spacing (0 = pure location Voronoi)
        internal static float _corridorClearance   = 0f;     // prune Steiner within (loc.extR + this) of any location → ≥this/2 corridor clearance
        internal static bool  _flatPlaneLocations  = true;
        internal static bool  _islandRemoval       = true;
        internal static bool  _pocketRemoval       = true;
        internal static bool  _hideLodMeshes       = false;  // DEBUG: hide Valheim's distant TerrainLod meshes (clear view of voxel features)
        internal static bool  _hideMist            = false;  // DEBUG: globally suppress Mistlands ground mist (MistEmitter) so you can see the terrain/sewers
        internal static bool  _lodUnderground      = true;   // auto-hide the distant LOD terrain while the player is underground (in a cave/tunnel)
        internal static volatile bool _playerUnderground;    // updated each Update from player Y vs the carved surface
        const float LOD_UndergroundMargin = 4f;              // metres below surface before we consider the player "underground"
        internal static bool  _debugViz            = false;  // DEBUG: draw see-through overlay lines for brush seeds/paths (tunnels now, spines later)
        internal static bool  _vegViz              = false;  // DEBUG: per-tree conform line — green=kept, red=rejected, grey=vanilla(surfH miss)
        internal static string _vizMode            = "";     // if set (cfg [Debug] vizMode=<modename>), ONLY that mode emits carve debug-viz lines; empty = all modes
        internal static int   _overlap             = 4;      // running-field margin (voxels) shared with neighbour zones; caps seam-free feature reach
        internal static float _fieldDepthLo        = 0f;     // min world-metres the carvable field extends BELOW terrain min (0=default). Raise so deep tunnels stay in-field
        internal static bool  _bakeTrilinear       = false;  // step>1 rehydration interp: false=Catmull-Rom (C1, smooth), true=trilinear (C0, cheap). A/B on the same bake file
        internal static float _biomeBlendRadius    = 192f;
        internal static bool  _biomeBlendSmooth    = true;   // false=linear, true=smoothstep
        internal static int   _transitionBlurPasses = 3;     // min blur passes for any active zone
        internal static int   _fixpointMaxZones     = 8;     // EnsureFixpoint hop cap
        internal static float _coastFade           = 0f;     // world units from the OCEAN BIOME (coastline distance, NOT height) over which Ocean inflates into the blend → suppress carving near the real coast, leave low inland biomes alone (0=off)
        internal static float _coastStrength       = 1f;     // gain on the coastFade Ocean weight — >1 lets Ocean dominate the blend (harder suppression) past the 50/50 a weight of 1 gives
        internal static float _riverProtect        = 0f;     // gain on river-weight Ocean inflation: suppress carving inside river corridors + banks (0=off; >1 widens/strengthens)
        internal static float _riverCore           = 1f;     // river-weight fraction above which protection is FULL (plateau); lower = fatter fully-protected core + harsher shoulder, same outer radius. 1=linear
        internal static float _riverEdge           = 0f;     // river-weight fraction below which NO protection — pulls the OUTER radius inward (raise to tighten the protected band). 0=protect out to the river's full weight edge
        internal static float _riverDomeHeight     = 0f;     // metres the protection DOME rises above water at the channel centre (tapering to the banks → arch cross-section). 0 = flat at water; large (~500) ≈ full-column protect
        internal static float _riverBandFade       = 4f;     // metres over which the river protection tapers out above the dome top
        internal static int   _blendMode           = 1;      // default sdf: bake each present biome, crossfade output columns (correct at dissimilar-profile boundaries). 0=param (one blended bake; chirps at differing-frequency borders — ablation/compat only)
        internal static int   _composeMode         = 0;      // 0=legacy (per-voxel blend in FillPhase1); 1=sequential (Path B: compose modes into a running coarse SDF in pipeline order, then interpolate)
        internal static bool  _eikonal             = false;   // Path B: redistance the running field to true distance after each mode. OFF by default — current modes read zero-crossing elevation/normal (don't need |grad|=1). Enable only for distance-metric modes; it's a per-mode cost that explodes at fine coarseXZ.
        internal static int   _coarseXZ            = 4;      // coarse bake cell size in voxels, XZ (4=default; higher=cheaper/blockier). The XZ resolution of the whole bake grid.
        internal static int   _coarseY             = 1;      // Path B running-field cell size in voxels, Y (1=full-res Y; higher=cheaper/less vertical detail).
        internal static int   _bakeCoarseXZ        = 1;      // carve res used DURING a pt_bake, XZ (decoupled from live coarseXZ). 1 = full-res bake quality.
        internal static int   _bakeCoarseY         = 1;      // carve res used DURING a pt_bake, Y. Live play uses coarseXZ/coarseY; the bake carves at these.
        // Per-thread carve-resolution override (0 = none). The rolling bake sets this to _bakeCoarseXZ/_bakeCoarseY
        // on ITS thread so it carves at full BAKE resolution — without flipping the global (which would corrupt the
        // main thread's concurrent live carves at coarse res). All carve-path resolution reads go through these two.
        [ThreadStatic] internal static int _coarseOverrideXZ;
        [ThreadStatic] internal static int _coarseOverrideY;
        internal static int   CellVox  { get { int c = _coarseOverrideXZ > 0 ? _coarseOverrideXZ : _coarseXZ; return c < 1 ? 1 : c; } }  // XZ coarse cell size (voxels)
        internal static int   CellVoxY { get { int c = _coarseOverrideY  > 0 ? _coarseOverrideY  : _coarseY;  return c < 1 ? 1 : c; } }  // Path B field Y coarse cell size (voxels)
        internal static bool  _surfaceFullRes       = true;   // surface-anchored modes: replace coarse-interp baseline with full-res addh (kills coarse faceting cliffs)
        internal static bool  _vanillaHeight        = true;   // sample ground via vanilla HeightmapBuilder (biome-blended, matches rendered terrain) instead of single-biome GetHeight

        // ── Bake download (client receiving from server) ──────────────────────────
        internal static bool   _bakeDownloadActive;
        internal static bool   _bakeAutoDownload;   // [Debug] bakeautodownload: auto-pull the server bake on connect. Default OFF — rolling bake fills locally; download is opt-in via pt_download (the RPC transfer is slow/throttled for big bakes).
        internal static readonly UnityEngine.KeyCode RollingHudKeyCode = UnityEngine.KeyCode.F8;   // toggle the rolling-bake HUD
        internal static string RollingHudKey => "F8";
        internal static int    _bakeChunksExpected;
        internal static int    _bakeChunksReceived;
        internal static System.IO.MemoryStream? _bakeDownloadBuffer;
        internal static System.Diagnostics.Stopwatch? _bakeDownloadTimer;
        const int BakeChunkSize = 256 * 1024; // 256 KB per RPC chunk

        // ── Bake progress (client watching server bake) ───────────────────────────
        internal static bool _bakeInProgress;
        internal static System.Threading.CancellationTokenSource _bakeCts;   // pt_cancel cancels the running bake; the ForEach throws OCE → partial bake is finalized (valid, loadable)
        internal static int  _bakeProgressDone;
        internal static int  _bakeProgressTotal;
        internal static System.Diagnostics.Stopwatch? _bakeProgressTimer;

        // ── SDF bake ─────────────────────────────────────────────────────────────
        static float _bakeRadius = 0f;
        static int   _bakeStep   = 8;
        internal static bool _bakeVanilla = false;  // false = skip unmodified (no active mode) zones from the bake file; they carve live via VanillaFill (~11ms) and don't inflate the bake. true = bake every zone.
        internal static bool _bakeCrossings = false; // true = store columns as sign-crossings (v6, ~14x smaller) instead of dense distance bands (v5). Surface is exact; off-surface distances are a hydrated ramp.
        internal static System.Collections.Generic.HashSet<Heightmap.Biome> _modifiedBiomes = new System.Collections.Generic.HashSet<Heightmap.Biome>();  // biomes with an active-mode profile; a zone touching none is pure vanilla (skip the blend)
        // Sparse per-zone bake (file v3). Per coarse column (col = xs*nsz + zs) we store only the
        // non-saturated band [colYLo .. colYLo+colYLen) plus the saturated fill below/above it. `step` is
        // PER ZONE: step==1 ⇒ band samples are already full-res (load skips reconstruct); step>1 ⇒ load
        // rehydrates the dense coarse grid from the band and tricubic-upsamples as before.
        internal sealed class BakeZone
        {
            public int extLo, bh, bw, step, nsx, nsy, nsz;
            public int cOrigin;   // step>1: global coarse-cell index of stored column 0 in X/Z (owned+halo grid; skirt not stored). 0 for step==1.
            public int[]   colYLo   = System.Array.Empty<int>();
            public int[]   colYLen  = System.Array.Empty<int>();
            public int[]   colOff   = System.Array.Empty<int>();   // offset into samples[]
            public sbyte[] colBelow = System.Array.Empty<sbyte>(); // fill for ys < colYLo
            public sbyte[] colAbove = System.Array.Empty<sbyte>(); // fill for ys >= colYLo+colYLen
            public sbyte[] samples  = System.Array.Empty<sbyte>(); // packed band samples
            // v6 "crossings" format: a column is stored as its sign-crossing y-positions (sub-voxel) instead of
            // a dense distance band. colCross is flattened per column in colOff order; colNCross packs the count
            // (low 7 bits) + sign-of-below (high bit: 1 = solid/-). crossSlope is the bytes-per-voxel hydrate ramp.
            // When colCross != null the zone is v6 and the band fields above are unused.
            public float[] colCross  = null;
            public byte[]  colNCross = null;
            public float   crossSlope;
            public int[]   colXOff   = null;   // lazy prefix-sum of (colNCross&0x7F) into colCross; built on first surface query
        }
        // Staged: sparse SDF written by Carve() when within bakeRadius.
        static readonly System.Collections.Concurrent.ConcurrentDictionary<(int, int), BakeZone> _bakePending = new();
        // Loaded from disk: populated at world load; null if no valid file exists.
        // v3 = whole file loaded into _bakeData (RAM-resident). v4 = STREAMED: only the offset index is held
        // (_bakeIndex), the file stays open (_bakeStream), and each zone is read+gunzipped on demand (cached).
        internal static Dictionary<(int, int), BakeZone>? _bakeData;
        internal static Dictionary<(int, int), (long off, int len)>? _bakeIndex;  // v4 stream: zone → file location
        internal static System.IO.FileStream? _bakeStream;                        // v4 stream: kept open for seeks
        static readonly Dictionary<(int, int), BakeZone> _bakeZoneCache = new();  // v4: small decoded-zone LRU
        static readonly Queue<(int, int)> _bakeCacheOrder = new();
        const int _bakeCacheCap = 256;
        static int _bakeFileStep = 8;
        internal static bool _bakeFileV6 = false;   // loaded bake file uses the v6 crossings layout
        static bool _bakeLoadAttempted = false;

        internal const float LocProtectR = 10f; // minimum protection radius when exteriorRadius is unavailable

        // ── Zone tracking for hot-reload ──────────────────────────────────────────
        internal static MethodInfo? _skinMethod;          // 0.1.7: static Skin(Heightmap) — re-adds VoxelZone + queues worker
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

        // Deterministic world seed (from the seed STRING), identical on every machine for a given seed — unlike
        // GetWorldUID() which is a random per-world id. Keying noise + bakes on this makes terrain a pure function
        // of (seed, cfg), so a bake is portable: same-seed world + same cfg → the file is found and matches.
        internal static int WorldSeed()
        {
            var w = WorldGenerator.instance?.m_world;
            return w != null ? w.m_seed : 12345;
        }

        internal static void RebuildNoisePool(int count)
        {
            _noiseCount = count;
            int  seed = WorldSeed();
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
        [ThreadStatic] static float[]?       _oceanDistCells;   // world-unit distance to the Ocean biome per coarse cell (coastFade keys on this, not height)
        [ThreadStatic] static int[]?         _biomeBfsQ;
        [ThreadStatic] static float[]?       _biomeTotW;
        [ThreadStatic] static CaveProfile[]? _blendedProfs;
        [ThreadStatic] static float[]?       _runSDF;   // Path B: running coarse SDF (cellCount*bh, world units) modes compose into sequentially
        [ThreadStatic] static float[]?       _eikScratch;   // Path B: unsigned-distance scratch for the eikonal redistance pass
        // Path B increment 3: composed-surface inputs a surface-anchored mode reads (per coarse cell)
        // while it bakes during sequential compose — derived from the running field of PRIOR modes.
        [ThreadStatic] internal static float[]? _composeInH;    // composed-surface elevation (zero-crossing of runSDF)
        [ThreadStatic] internal static float[]? _composeInGX;   // downslope dir x (from the composed surface)
        [ThreadStatic] internal static float[]? _composeInGZ;   // downslope dir z
        [ThreadStatic] internal static float[]? _composeInGMag; // slope magnitude
        [ThreadStatic] internal static bool     _composeIncoming; // true while a surface-anchored mode bakes in sequential compose
        [ThreadStatic] static float[]?          _composeHCell;    // per-cell groundworked base height (cached across the per-mode passes)
        // Per-cell governing PER-MODE floor Y (max across modes that set a per-mode floordepth), recorded during the
        // mode aux passes. Applied PER COLUMN (bilinear) in the compose so the floor is smooth, not coarse-cell-stepped.
        [ThreadStatic] internal static float[]? _composeModeFloorY;
        internal const float NoModeFloor = -1e9f;
        internal static void RecordModeFloor(int cell, float floorY)
        {
            var a = _composeModeFloorY;
            if (a != null && (uint)cell < (uint)a.Length && floorY > a[cell]) a[cell] = floorY;
        }

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

        // Per-zone SDF cache (SPARSE): stores only the deviating band per column. Voxheim's field is
        // sparse over a SolidFloorRule (solid below floor.Y, air above); voxels matching that default are
        // omitted and served by the rule, so we never materialize/hand over the constant fill. Queried by
        // PTGenerator.Generate. Per column (col = ci*width + ri): a single contiguous run of `colLen`
        // densities starting at world-Y (extLo + colStart), packed into `band` at `colOff`.
        internal sealed class ZoneField
        {
            public int extLo, bh, width;
            public int[]   colStart = System.Array.Empty<int>();   // first cy of the run (-1 if empty)
            public int[]   colLen   = System.Array.Empty<int>();   // run length (voxels)
            public int[]   colOff   = System.Array.Empty<int>();   // offset into band[]
            public sbyte[] band     = System.Array.Empty<sbyte>(); // packed run densities
        }
        internal static readonly System.Collections.Concurrent.ConcurrentDictionary<string, ZoneField> _sdfCache = new();

        // DEBUG: decode the real stored column at a world point → all zero-crossing world-Y values (top surface, relief
        // floor, cave floors/ceilings), from the sbyte band exactly as meshed. Returns empty if the zone isn't cached.
        internal static System.Collections.Generic.List<float> DecodeColumnCrossings(float worldX, float worldZ)
        {
            var outList = new System.Collections.Generic.List<float>();
            float s = ActiveScale(); int overlap = _overlap;
            var (ox, oz) = ZoneOriginFromWorld(worldX, worldZ, s);
            if (!_sdfCache.TryGetValue($"{ox},{oz}", out var zf)) return outList;
            int ci = Mathf.RoundToInt(worldX / s) - ox, ri = Mathf.RoundToInt(worldZ / s) - oz;
            if (ci < 0 || ci >= zf.width || ri < 0 || ri >= zf.width) return outList;
            int col = ci * zf.width + ri;
            int start = zf.colStart[col]; if (start < 0) return outList;      // no deviation run → all-solid/all-air column
            int len = zf.colLen[col], off = zf.colOff[col];
            sbyte prev = zf.band[off];
            for (int k = 1; k < len; k++)
            {
                sbyte cur = zf.band[off + k];
                if ((prev < 0) != (cur < 0) && prev != cur)                  // sign change = a surface crossing
                {
                    float t = (float)(-prev) / (cur - prev);                 // sub-voxel fraction (same interp Voxheim MC uses)
                    outList.Add((zf.extLo + (start + k - 1) + t) * s);
                }
                prev = cur;
            }
            return outList;
        }
        // FIFO-bounded like _zoneFaceCache: dedicated servers skip prefetch, so every served zone's SDF accumulates
        // here forever (no eviction = multi-GB heap over a big world). Cap it; an evicted zone just refills on demand
        // (cheap gunzip from the bake, or a live carve where unbaked — already the miss path). Access is spatially
        // local (player neighbour ring) so the working set stays far under the cap. See [[pt-investigation-findings-0-0-2]].
        static readonly System.Collections.Concurrent.ConcurrentQueue<string> _sdfOrder = new();
        const int SdfCacheCap = 1024;
        internal static void PutSdf(string key, ZoneField f)
        {
            _sdfCache[key] = f;
            _sdfOrder.Enqueue(key);
            while (_sdfCache.Count > SdfCacheCap && _sdfOrder.TryDequeue(out var old))
                if (!old.Equals(key)) _sdfCache.TryRemove(old, out _);
        }

        // Reused (thread-local) scratch for sparse packing — avoids per-zone dense allocations (GC).
        [ThreadStatic] static sbyte[]? _sparseScratch;
        [ThreadStatic] static sbyte[]? _colVScratch;
        [ThreadStatic] static float[]? _colFloatScratch;   // Piece 2: per-column reconstruct (no dense buf)
        [ThreadStatic] static sbyte[]? _rehydrateScratch;  // v3: sparse bake → dense coarse grid for reconstruction

        // Zones whose SDF has already been carved with location groundwork baked in. Used to drop a
        // stale (mod-less/early) cache entry once, so the zone re-carves with the edits applied.
        internal static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _groundworkedZones = new();
        // Groundworked heights cached from the first successful gather (modifiers loaded), reused on revisit
        // when the transient TerrainModifier objects aren't loaded — keeps the baseline deterministic so
        // Voxheim's authority doesn't rebase to vanilla. Keyed by Valheim zone index "x,z"; survives unload.
        internal static readonly Dictionary<string, (Voxheim.Core.HeightGrid grid, Voxheim.Core.HeightGrid vanilla, int nmods)> _groundworkCache = new();
        internal static int _gwTransientFallbacks;   // count of zones that fell back to the spawn-dependent TerrainModifier gather (deterministic extractor unavailable/failed)
        internal static int _gwExtractorOk;          // deterministic extractor succeeded with modifiers (changed>0)
        internal static int _gwExtractorEmpty;       // deterministic extractor succeeded but found NO modifiers (changed==0)
        internal static int _gwCalls;                // times BuildGroundworkedHeights was entered at all

        // Per-zone surface height cache: filled after Phase 2, queried by GetWorldHeight patch.
        // Layout: surfH[cx * bw + cz] = world-Y of topmost solid voxel (NaN = no solid in column).
        // Includes overlap region so border columns near zone edges are correct.
        internal static readonly Dictionary<(int ox, int oz), (int bw, int extLo, float s, float[] surfH)> _surfHCache = new();
        // FIFO-bounded, same rationale as _sdfCache. Not concurrent — callers already hold lock(_surfHCache);
        // PutSurfH takes it internally, so call it WITHOUT an outer lock.
        static readonly Queue<(int ox, int oz)> _surfHOrder = new();
        const int SurfHCacheCap = 1024;
        internal static void PutSurfH((int ox, int oz) key, (int bw, int extLo, float s, float[] surfH) v)
        {
            lock (_surfHCache)
            {
                _surfHCache[key] = v;
                _surfHOrder.Enqueue(key);
                while (_surfHCache.Count > SurfHCacheCap && _surfHOrder.Count > 0)
                {
                    var old = _surfHOrder.Dequeue();
                    if (!old.Equals(key)) _surfHCache.Remove(old);
                }
            }
        }

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
        // FIFO-bounded: island-removal writes a face cache PER ZONE; during a big bake that's tens of thousands of
        // multi-KB entries → multi-GB heap → GC overhead climbs and the bake ETA creeps up. Cap it — an evicted
        // neighbour just gets recomputed on demand by EnsureFixpoint (already the miss path), so bounding is safe.
        static readonly System.Collections.Concurrent.ConcurrentQueue<(int, int)> _zoneFaceOrder = new();
        const int ZoneFaceCap = 1024;
        internal static void PutZoneFace((int, int) key, ZoneFaceCache c)
        {
            _zoneFaceCache[key] = c;
            _zoneFaceOrder.Enqueue(key);
            while (_zoneFaceCache.Count > ZoneFaceCap && _zoneFaceOrder.TryDequeue(out var old))
                if (!old.Equals(key)) _zoneFaceCache.TryRemove(old, out _);
        }

        // Set true during parallel bake to suppress main-thread-only side effects.
        static volatile bool _parallelBakeActive;

        // CR reconstruction temp buffers — main thread only, not [ThreadStatic].
        static float[] _crTemp1 = new float[72 * 150 * 36]; // [bw * nsy * nsz]
        static float[] _crTemp2 = new float[72 * 300 * 36]; // [bw * bh  * nsz]

        static FieldInfo? _locationInstancesField;

        // ─────────────────────────────────────────────────────────────────────────

        internal static int _mainThreadId = -1;

        // Ring-scan for the nearest Ocean biome within maxR; returns the INLAND direction (unit, away from ocean).
        // Deterministic pure function of (wx,wz). Mirrors Carve.NearestOcean; used by the pt_canalmap debug command.
        static bool CanalNearestOcean(WorldGenerator wg, float wx, float wz, float maxR, out Vector2 inland)
        {
            inland = Vector2.zero;
            for (float r = 8f; r <= maxR; r += 8f)
            {
                int n = Mathf.Max(8, Mathf.RoundToInt(r / 4f));
                float bestD2 = float.MaxValue; Vector2 toOcean = Vector2.zero; bool hit = false;
                for (int a = 0; a < n; a++)
                {
                    float ang = a * 6.2831853f / n, sx = wx + r * Mathf.Cos(ang), sz = wz + r * Mathf.Sin(ang);
                    if (wg.GetBiome(sx, sz) == Heightmap.Biome.Ocean)
                    { float d2 = (sx - wx) * (sx - wx) + (sz - wz) * (sz - wz); if (d2 < bestD2) { bestD2 = d2; toOcean = new Vector2(sx - wx, sz - wz); hit = true; } }
                }
                if (hit) { inland = (-toOcean).normalized; return true; }
            }
            return false;
        }

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
            LoadPlacementConfig();
            var harmony = new Harmony("com.ctogle.procedural_terrain");
            harmony.PatchAll();

            RegisterVoxheimGenerator();
            var vizGo = new GameObject("PTDebugVizRenderer");
            DontDestroyOnLoad(vizGo);
            vizGo.AddComponent<DebugVizRenderer>();
            var guardGo = new GameObject("PTWorldGuardDriver");
            DontDestroyOnLoad(guardGo);
            guardGo.AddComponent<WorldGuardDriver>();
            Log.LogInfo("[ProceduralTerrain] Ready.");
        }

        // PT commands (re)register from Update, NOT Awake: the game rebuilds the Terminal command table AFTER plugin
        // Awake (observed Terminal.commands 57→416), dropping anything registered in Awake — every other mod registers
        // in an InitTerminal hook. The Update self-heal re-adds ours whenever they're missing from Terminal.commands,
        // so they survive the rebuild (and any later clear by another mod), whenever it happens. Idempotent: the
        // ConsoleCommand ctor overwrites by name, so re-running is harmless.
        internal static void RegisterConsoleCommands()
        {
            new Terminal.ConsoleCommand("pt_viz_clear", "pt_viz_clear — clear the debug brush overlay (seed/path lines)", _ =>
            {
                DebugViz.ClearAll();
                Log.LogInfo("[ProceduralTerrain] debug viz cleared");
            });
            new Terminal.ConsoleCommand("pt_guardtest", "pt_guardtest — stamp this world's PT version tag to a dummy old version (0.0.0) so the NEXT load trips the world-version guard. Log out and back in to verify it holds you on the loading screen with a mismatch prompt (Yes re-stamps to current).", _ =>
            {
                var zs = ZoneSystem.instance;
                if (zs == null) { Console.instance?.Print("[PT] guardtest: no ZoneSystem (load a world first)."); return; }
                zs.SetGlobalKey("pt_ver 0.0.0");
                Console.instance?.Print("[PT] guardtest: world tag set to v0.0.0. Log out and back in — the guard should hold you on the loading screen and prompt.");
            });
            new Terminal.ConsoleCommand("pt_interp", "pt_interp [tricubic|trilinear] — A/B the step>1 bake rehydration interp on the SAME bake (re-serves loaded zones)", args =>
            {
                if (args.Length >= 2) _bakeTrilinear = args[1].Trim().Equals("trilinear", StringComparison.OrdinalIgnoreCase);
                else _bakeTrilinear = !_bakeTrilinear;   // no arg = toggle
                _hotReloadPending = true;                // RecarveAll next frame → re-serve all loaded zones with the new interp
                string msg = $"[ProceduralTerrain] bake interp = {(_bakeTrilinear ? "trilinear (C0, cheap)" : "tricubic (Catmull-Rom, smooth)")} — re-serving loaded zones.";
                Log.LogInfo(msg); Console.instance?.Print(msg);
            });
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
            new Terminal.ConsoleCommand("pt_forestmap", "pt_forestmap [radius] [gate] — write the REAL GetForestFactor field + pedestal-gate mask PNGs (+ value range) to BepInEx/config folder", args =>
            {
                try
                {
                    var player = Player.m_localPlayer;
                    if (player == null) { Log.LogWarning("[PT] forestmap: no player."); return; }
                    var pos = player.transform.position;
                    float cx = pos.x, cz = pos.z, range = 1000f, gate = 0.85f;
                    if (args.Length >= 2 && float.TryParse(args[1], out float r)) range = r;
                    if (args.Length >= 3 && float.TryParse(args[2], out float g)) gate = g;
                    int res = 512; float step = range * 2f / res;
                    var vals = new float[res * res];
                    float mn = 9999f, mx = -9999f, sum = 0f; int inGate = 0, inVanilla = 0;
                    for (int i = 0; i < res * res; i++)
                    {
                        int ix = i % res, iz = i / res;
                        float wx = cx + (ix - res / 2) * step, wz = cz + (iz - res / 2) * step;
                        float ff = WorldGenerator.GetForestFactor(new Vector3(wx, 0f, wz));
                        vals[i] = ff; sum += ff; if (ff < mn) mn = ff; if (ff > mx) mx = ff;
                        if (ff <= gate) inGate++; if (ff < 1.15f) inVanilla++;
                    }
                    float mean = sum / (res * res);
                    var texRaw = new Texture2D(res, res, TextureFormat.RGBA32, false);
                    var texMask = new Texture2D(res, res, TextureFormat.RGBA32, false);
                    for (int i = 0; i < res * res; i++)
                    {
                        int ix = i % res, iz = i / res; float ff = vals[i];
                        float v = Mathf.Clamp01((mx - ff) / Mathf.Max(mx - mn, 1e-4f));   // invert: DENSE (low factor) = BRIGHT
                        texRaw.SetPixel(ix, iz, new Color(v, v, v));
                        // mask: green = inside the pedestal gate (ff<=gate); grey = vanilla in-forest (<1.15); black = open
                        texMask.SetPixel(ix, iz, ff <= gate ? new Color(0.1f, 1f, 0.1f)
                                               : (ff < 1.15f ? new Color(0.25f, 0.25f, 0.25f) : Color.black));
                    }
                    string dir = System.IO.Path.GetDirectoryName(ConfigPath)!;
                    System.IO.File.WriteAllBytes(System.IO.Path.Combine(dir, "pt_forest_raw.png"),  texRaw.EncodeToPNG());
                    System.IO.File.WriteAllBytes(System.IO.Path.Combine(dir, "pt_forest_mask.png"), texMask.EncodeToPNG());
                    UnityEngine.Object.Destroy(texRaw); UnityEngine.Object.Destroy(texMask);
                    var msg = $"[PT] forestmap: range [{mn:F2}..{mx:F2}] mean {mean:F2} | gate(≤{gate:F2}) covers {100f*inGate/(res*res):F0}% | vanilla-forest(<1.15) {100f*inVanilla/(res*res):F0}% | written to {dir}";
                    Log.LogInfo(msg); Console.instance?.Print(msg);
                }
                catch (Exception e) { Log.LogError($"[PT] forestmap exception: {e}"); }
            });
            new Terminal.ConsoleCommand("pt_locmap", "pt_locmap [radius] — write the location-protection field PNG (black=protected/no-carve, white=free) to BepInEx/config/ProceduralTerrain/pt_locmap.png", args =>
            {
                try
                {
                    var player = Player.m_localPlayer;
                    if (player == null) { Log.LogWarning("[PT] locmap: no player."); return; }
                    var pos = player.transform.position;
                    float cx = pos.x, cz = pos.z, range = 1000f;
                    if (args.Length >= 2 && float.TryParse(args[1], out float r)) range = r;
                    // Gather every location whose dome could reach the imaged area (mirror of Carve's nearbyLocs build).
                    // NOTE: LocProtectRadius calls TerrainModifier.GetAllInstances() (transient) — fine for a visual
                    // snapshot, but routing logic must use the deterministic gather, not this.
                    var nearbyLocs = new List<(Vector2 pos, float extR)>();
                    float imgR = range * 1.5f + 500f;
                    foreach (var loc in _locationPositions)
                    {
                        float dx = loc.pos.x - cx, dz = loc.pos.y - cz;
                        if (dx * dx + dz * dz < imgR * imgR)
                            nearbyLocs.Add((loc.pos, LocProtectRadius(loc.pos, loc.extR)));
                    }
                    int res = 512; float step = range * 2f / res;
                    var tex = new Texture2D(res, res, TextureFormat.RGBA32, false);
                    int protPix = 0;
                    for (int i = 0; i < res * res; i++)
                    {
                        int ix = i % res, iz = i / res;
                        float wx = cx + (ix - res / 2) * step, wz = cz + (iz - res / 2) * step;
                        float f = LocationFactor(wx, wz, nearbyLocs);   // 0 = fully protected, 1 = free
                        if (f < 0.999f) protPix++;
                        // grayscale free→protected; tint the hard core (fully damped) red so domes pop
                        Color c = f <= 0.001f ? new Color(0.8f, 0.1f, 0.1f) : new Color(f, f, f);
                        tex.SetPixel(ix, iz, c);
                    }
                    string dir = System.IO.Path.GetDirectoryName(ConfigPath)!;
                    System.IO.File.WriteAllBytes(System.IO.Path.Combine(dir, "pt_locmap.png"), tex.EncodeToPNG());
                    UnityEngine.Object.Destroy(tex);
                    var msg = $"[PT] locmap: {nearbyLocs.Count} location(s) in range, protectLocations={_protectLocations}, {100f * protPix / (res * res):F0}% of a {range * 2f:F0}m window protected → {dir}\\pt_locmap.png";
                    Log.LogInfo(msg); Console.instance?.Print(msg);
                }
                catch (Exception e) { Log.LogError($"[PT] locmap exception: {e}"); }
            });
            new Terminal.ConsoleCommand("pt_canalmap", "pt_canalmap [radius] [coastDist] — seed from the real ocean coastline, route canals through the location-corridor graph, and render a PNG (no carving). -> BepInEx/config/ProceduralTerrain/pt_canalmap.png", args =>
            {
                try
                {
                    var player = Player.m_localPlayer; var wg = WorldGenerator.instance;
                    if (player == null || wg == null) { Log.LogWarning("[PT] canalmap: no player/worldgen."); return; }
                    var pp = player.transform.position;
                    float cx = pp.x, cz = pp.z, range = 1000f, coastDist = 150f;
                    if (args.Length >= 2 && float.TryParse(args[1], out float r)) range = r;
                    if (args.Length >= 3 && float.TryParse(args[2], out float cd)) coastDist = cd;

                    // sites = nearby locations (transient cache — fine for a debug snapshot, NOT for carve routing)
                    var sites = new List<CanalRouter.Site>();
                    float gatherR = range * 1.5f + 500f;
                    foreach (var loc in _locationPositions)
                    {
                        float dx = loc.pos.x - cx, dz = loc.pos.y - cz;
                        if (dx * dx + dz * dz >= gatherR * gatherR) continue;
                        sites.Add(new CanalRouter.Site(loc.pos.x, loc.pos.y, LocProtectRadius(loc.pos, loc.extR)));
                    }
                    // coastline seeds: lattice over the window; keep swamp/land cells within coastDist of Ocean, aim inland
                    var seeds = new List<(Vector2 start, Vector2 dir)>();
                    float spacing = 120f;
                    for (float x = cx - range; x <= cx + range; x += spacing)
                    for (float z = cz - range; z <= cz + range; z += spacing)
                    {
                        if (wg.GetBiome(x, z) == Heightmap.Biome.Ocean) continue;
                        if (!CanalNearestOcean(wg, x, z, coastDist, out Vector2 inland)) continue;
                        seeds.Add((new Vector2(x, z), inland));
                    }

                    var pr = new CanalRouter.Params { InlandGoal = range * 0.9f };
                    var polys = CanalRouter.Route(sites, seeds, pr, out int reached, out int terminated);

                    // render
                    int res = 640; float step = range * 2f / res;
                    var tex = new Texture2D(res, res, TextureFormat.RGBA32, false);
                    var px = new Color[res * res]; for (int i = 0; i < px.Length; i++) px[i] = Color.white;
                    int PX(float wx) => Mathf.RoundToInt((wx - (cx - range)) / step);
                    int PZ(float wz) => Mathf.RoundToInt(((cz + range) - wz) / step);
                    void Dot(int x, int y, float rad, Color c) { int ri = Mathf.CeilToInt(rad); for (int b = -ri; b <= ri; b++) for (int a = -ri; a <= ri; a++) if (a * a + b * b <= rad * rad) { int xx = x + a, yy = y + b; if (xx >= 0 && yy >= 0 && xx < res && yy < res) px[yy * res + xx] = c; } }
                    void Line(float x0, float y0, float x1, float y1, float th, Color c) { float len = Mathf.Sqrt((x1 - x0) * (x1 - x0) + (y1 - y0) * (y1 - y0)); int n = (int)len + 1; for (int i = 0; i <= n; i++) { float t = (float)i / n; Dot((int)(x0 + (x1 - x0) * t), (int)(y0 + (y1 - y0) * t), th, c); } }
                    foreach (var s in sites) { Dot(PX((float)s.x), PZ((float)s.z), (float)(s.r + 10) / step, new Color(0.93f, 0.93f, 0.93f)); Dot(PX((float)s.x), PZ((float)s.z), (float)s.r / step, new Color(0.82f, 0.2f, 0.2f)); }
                    foreach (var poly in polys) if (poly != null) for (int i = 0; i < poly.Count - 1; i++) Line(PX(poly[i].x), PZ(poly[i].y), PX(poly[i + 1].x), PZ(poly[i + 1].y), 2.2f, new Color(0f, 0.35f, 0.8f));
                    for (int i = 0; i < seeds.Count; i++) Dot(PX(seeds[i].start.x), PZ(seeds[i].start.y), 3.5f, polys[i] != null ? new Color(0f, 0.6f, 0f) : new Color(0.6f, 0.6f, 0.6f));
                    tex.SetPixels(px); tex.Apply();
                    string dir = System.IO.Path.GetDirectoryName(ConfigPath)!;
                    System.IO.File.WriteAllBytes(System.IO.Path.Combine(dir, "pt_canalmap.png"), tex.EncodeToPNG());
                    UnityEngine.Object.Destroy(tex);
                    var msg = $"[PT] canalmap: {sites.Count} sites, {seeds.Count} coast seeds, routed {reached} / terminated {terminated} → {dir}\\pt_canalmap.png";
                    Log.LogInfo(msg); Console.instance?.Print(msg);
                }
                catch (Exception e) { Log.LogError($"[PT] canalmap exception: {e}"); }
            });
            new Terminal.ConsoleCommand("pt_voronoi", "pt_voronoi [radius] [steinerSpacing] [clearance] — build the location Voronoi corridor graph for a window, render it with the COASTLINE overlaid. With steinerSpacing>0, overlay a jittered background lattice at that spacing and triangulate the union; edges that touch a Steiner point (the densified fill) draw in a DISTINCT color. clearance>0 prunes Steiner points within (loc.extR + clearance) of any location, guaranteeing ≥clearance/2 margin on orange edges. select=1 overlays the selected corridor network (length-weighted MST; only fully-at-sea edges excluded, so coastal exits are kept) in magenta. atriumMerge (arg6) clusters merge-junctions within that radius into cyan atrium sites (dome candidates); defaults to 0.6×steiner. -> BepInEx/config/ProceduralTerrain/pt_voronoi.png", args =>
            {
                try
                {
                    var player = Player.m_localPlayer; var wg = WorldGenerator.instance;
                    if (player == null || wg == null) { Log.LogWarning("[PT] voronoi: no player/worldgen."); return; }
                    var pp = player.transform.position;
                    float cx = pp.x, cz = pp.z, range = 2000f;
                    if (args.Length >= 2 && float.TryParse(args[1], out float r)) range = r;
                    // default to the LIVE cfg values so the viz reflects the graph generation actually uses; args override
                    float steiner = _corridorSteiner;
                    if (args.Length >= 3 && float.TryParse(args[2], out float st)) steiner = st;
                    float clearance = _corridorClearance;   // prune Steiner within (loc.r + clearance) of any location → ≥ clearance/2 margin
                    if (args.Length >= 4 && float.TryParse(args[3], out float cl)) clearance = cl;
                    bool select = args.Length >= 5 && (args[4] == "1" || args[4].ToLowerInvariant() == "true");   // overlay the selected corridor network (MST)
                    float atriumMerge = steiner > 0f ? steiner * 0.6f : 300f;   // merge junctions within this radius into one atrium site
                    if (args.Length >= 6 && float.TryParse(args[5], out float am)) atriumMerge = am;

                    // Build (or reuse cached) the GLOBAL annotated corridor graph via the shared service — the same
                    // asset live generation consumes. The window `range` only crops the render; the graph is global.
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    var g = LocationVoronoi.Get(steiner, clearance);
                    sw.Stop();
                    if (g == null) { Log.LogWarning("[PT] voronoi: locations not ready (need ≥3). Explore/generate first."); return; }
                    var verts = g.Verts; var edges = g.Edges;
                    int nLoc = _locationPositions.Count;

                    int res = 900; float step = range * 2f / res;
                    var px = new Color[res * res]; for (int i = 0; i < px.Length; i++) px[i] = Color.white;
                    int PX(float wx) => Mathf.RoundToInt((wx - (cx - range)) / step);
                    int PZ(float wz) => Mathf.RoundToInt(((cz + range) - wz) / step);
                    // COASTLINE: ocean mask per pixel, then draw where ocean meets land (4-neighbour transition).
                    var ocean = new bool[res * res];
                    for (int yy = 0; yy < res; yy++)
                    for (int xx = 0; xx < res; xx++)
                    {
                        float wx = cx - range + xx * step, wz = (cz + range) - yy * step;
                        ocean[yy * res + xx] = wg.GetBiome(wx, wz) == Heightmap.Biome.Ocean;
                    }
                    var coastCol = new Color(0.1f, 0.5f, 0.55f);
                    for (int yy = 0; yy < res; yy++)
                    for (int xx = 0; xx < res; xx++)
                    {
                        bool o = ocean[yy * res + xx];
                        bool edge = (xx + 1 < res && ocean[yy * res + xx + 1] != o) || (yy + 1 < res && ocean[(yy + 1) * res + xx] != o);
                        if (edge) px[yy * res + xx] = coastCol;
                        else if (o) px[yy * res + xx] = new Color(0.88f, 0.94f, 0.97f);   // faint ocean tint
                    }
                    void Dot(int x, int y, float rad, Color c) { int ri = Mathf.CeilToInt(rad); for (int b = -ri; b <= ri; b++) for (int a = -ri; a <= ri; a++) if (a * a + b * b <= rad * rad) { int xx = x + a, yy = y + b; if (xx >= 0 && yy >= 0 && xx < res && yy < res) px[yy * res + xx] = c; } }
                    void Line(float x0, float y0, float x1, float y1, Color c) { float len = Mathf.Sqrt((x1 - x0) * (x1 - x0) + (y1 - y0) * (y1 - y0)); int n = (int)len + 1; for (int i = 0; i <= n; i++) { float t = (float)i / n; int xx = (int)(x0 + (x1 - x0) * t), yy = (int)(y0 + (y1 - y0) * t); if (xx >= 0 && yy >= 0 && xx < res && yy < res) px[yy * res + xx] = c; } }
                    var edgeCol = new Color(0.0f, 0.45f, 0.85f);        // location↔location (original)
                    var steinerCol = new Color(0.95f, 0.55f, 0.0f);     // touches a Steiner point (added by densification)
                    int added = 0;
                    for (int i = 0; i < edges.Length; i++)
                    {
                        var e = edges[i];
                        if (e.Steiner) added++;
                        Line(PX(verts[e.A].x), PZ(verts[e.A].y), PX(verts[e.B].x), PZ(verts[e.B].y), e.Steiner ? steinerCol : edgeCol);
                    }
                    // real locations in red (drawn from the location set directly; graph verts are Voronoi vertices)
                    foreach (var loc in _locationPositions)
                    {
                        if ((loc.pos.x - cx) * (loc.pos.x - cx) + (loc.pos.y - cz) * (loc.pos.y - cz) >= range * range) continue;
                        Dot(PX(loc.pos.x), PZ(loc.pos.y), Math.Max(1.5f, Math.Max(loc.extR, LocProtectR) / step), new Color(0.82f, 0.2f, 0.2f));
                    }

                    // MERGE JUNCTIONS: Voronoi vertices where ≥3 corridor edges meet — the atrium-dome candidate sites.
                    var deg = new int[verts.Length];
                    foreach (var e in edges) { deg[e.A]++; deg[e.B]++; }
                    int junctions = 0;
                    var juncCol = new Color(0.15f, 0.7f, 0.2f);
                    for (int i = 0; i < verts.Length; i++)
                    {
                        if (deg[i] < 3) continue;
                        float vx = verts[i].x, vz = verts[i].y;
                        if ((vx - cx) * (vx - cx) + (vz - cz) * (vz - cz) >= range * range) continue;   // only count junctions inside the viewport
                        junctions++;
                        Dot(PX(vx), PZ(vz), 2.6f, juncCol);
                    }

                    // CORRIDOR SELECTION: minimum spanning tree over the graph (via the shared service). Fully-at-sea
                    // edges are dropped so the network stays inland but keeps land↔ocean coastal exits; forest across
                    // disjoint land masses.
                    int selEdges = 0; float selLen = 0f;
                    if (select)
                    {
                        var mst = LocationVoronoi.BuildMST(g, excludeOcean: true);
                        var selCol = new Color(0.65f, 0.0f, 0.55f);
                        foreach (int i in mst)
                        {
                            var e = edges[i]; selEdges++; selLen += e.Length;
                            float x0 = PX(verts[e.A].x), y0 = PZ(verts[e.A].y), x1 = PX(verts[e.B].x), y1 = PZ(verts[e.B].y);
                            Line(x0, y0, x1, y1, selCol); Line(x0 + 1, y0, x1 + 1, y1, selCol); Line(x0, y0 + 1, x1, y1 + 1, selCol);
                        }
                    }

                    // CORRIDOR NETWORK: the actual canals (coast mouths routed inland + unioned) and their junction
                    // atriums — the same object the carve consumes. Canals drawn BLACK, atriums as cyan rings.
                    var net = LocationVoronoi.GetNetwork(steiner, clearance, 0.6f, atriumMerge, 28f + Math.Max(clearance, 20f), 18f, 30f, 600f, AtriumBiomeMask());   // only atrium-carving biomes
                    int atriumsShown = 0, canalsShown = 0;
                    var atriumCol = new Color(0.0f, 0.75f, 0.85f);
                    void Ring(int x, int y, float rad, Color c) { int ri = Mathf.CeilToInt(rad); float r0 = (rad - 1.3f) * (rad - 1.3f), r1 = rad * rad; for (int b = -ri; b <= ri; b++) for (int a = -ri; a <= ri; a++) { int d = a * a + b * b; if (d < r0 || d > r1) continue; int xx = x + a, yy = y + b; if (xx >= 0 && yy >= 0 && xx < res && yy < res) px[yy * res + xx] = c; } }
                    if (net != null)
                    {
                        var canalCol = new Color(0f, 0f, 0f);
                        foreach (var poly in net.Canals)
                        {
                            bool any = false;
                            for (int i = 0; i < poly.Length - 1; i++)
                            {
                                float ax = poly[i].x, az = poly[i].z, bx = poly[i + 1].x, bz = poly[i + 1].z;
                                if ((ax - cx) * (ax - cx) + (az - cz) * (az - cz) >= range * range && (bx - cx) * (bx - cx) + (bz - cz) * (bz - cz) >= range * range) continue;
                                Line(PX(ax), PZ(az), PX(bx), PZ(bz), canalCol); any = true;
                            }
                            if (any) canalsShown++;
                        }
                        foreach (var atr in net.Atriums)
                        {
                            var apos = atr.Pos;
                            if ((apos.x - cx) * (apos.x - cx) + (apos.y - cz) * (apos.y - cz) >= range * range) continue;
                            atriumsShown++;
                            Ring(PX(apos.x), PZ(apos.y), Mathf.Clamp(3f + atr.Conv, 4f, 10f), atriumCol);
                        }
                    }

                    var tex = new Texture2D(res, res, TextureFormat.RGBA32, false); tex.SetPixels(px); tex.Apply();
                    string dir = System.IO.Path.GetDirectoryName(ConfigPath)!;
                    System.IO.File.WriteAllBytes(System.IO.Path.Combine(dir, "pt_voronoi.png"), tex.EncodeToPNG());
                    UnityEngine.Object.Destroy(tex);
                    var clrNote = clearance > 0f ? $", ≥{clearance * 0.5f:F0}m clearance" : "";
                    var selNote = select ? $", corridor MST {selEdges} edges / {selLen:F0}m (magenta)" : "";
                    var msg = $"[PT] voronoi (global graph): {nLoc} locs (spacing {steiner:F0}{clrNote}) → {verts.Length} verts / {edges.Length} edges ({added} steiner-touching), {junctions} junctions (green), {canalsShown} network canals (black), {atriumsShown} atriums (cyan, merge {atriumMerge:F0}m){selNote}, built/cached in {sw.Elapsed.TotalMilliseconds:F0}ms (render window {range * 2f:F0}m) → {dir}\\pt_voronoi.png";
                    Log.LogInfo(msg); Console.instance?.Print(msg);
                }
                catch (Exception e) { Log.LogError($"[PT] voronoi: {e}"); }
            });
            new Terminal.ConsoleCommand("pt_atriums", "pt_atriums [radius] [mergeRadius] — list the nearest atrium (merge-junction) world coords + surface height using the live corridorGraph cfg, so you can navigate to one. Prints a map ping for the nearest.", args =>
            {
                try
                {
                    var player = Player.m_localPlayer; var wg = WorldGenerator.instance;
                    if (player == null || wg == null) { Log.LogWarning("[PT] atriums: no player/worldgen."); return; }
                    float range = 4000f; if (args.Length >= 2 && float.TryParse(args[1], out float r)) range = r;
                    float merge = 300f;  if (args.Length >= 3 && float.TryParse(args[2], out float mg)) merge = mg;
                    float minClr = 28f + Math.Max(_corridorClearance, 20f);   // matches the sewer carve's dome clearance
                    var net = LocationVoronoi.GetNetwork(_corridorSteiner, _corridorClearance, 0.6f, merge, minClr, 18f, 30f, 600f, AtriumBiomeMask());   // only atrium-carving biomes
                    if (net == null) { Log.LogWarning("[PT] atriums: locations not ready (explore/generate first)."); return; }
                    var sites = new List<LocationVoronoi.Atrium>(net.Atriums);
                    var pp2 = new Vector2(player.transform.position.x, player.transform.position.z);
                    sites.Sort((a, b) => (a.Pos - pp2).sqrMagnitude.CompareTo((b.Pos - pp2).sqrMagnitude));
                    var sb = new System.Text.StringBuilder(); int shown = 0;
                    foreach (var atr in sites)
                    {
                        var apos = atr.Pos;
                        float d = Vector2.Distance(apos, pp2); if (d > range) break;
                        float sh = ComposedSurfaceSample(apos.x, apos.y, wg);
                        sb.AppendLine($"  ({apos.x:F0}, {apos.y:F0})  surfH={sh:F0}  conv={atr.Conv}  links={atr.Links?.Length ?? 0}  dist={d:F0}m");
                        if (++shown >= 15) break;
                    }
                    var msg = $"[PT] {shown} nearest atriums (merge {merge:F0}, within {range:F0}m):\n{sb}  NETWORK FUNNEL: {LocationVoronoi._netBuildStats}";
                    Log.LogInfo(msg); Console.instance?.Print(msg);
                }
                catch (Exception e) { Log.LogError($"[PT] atriums: {e}"); }
            });
            new Terminal.ConsoleCommand("pt_gotoatrium", "pt_gotoatrium [n] — teleport to the Nth-nearest atrium the carve ACTUALLY emitted (default 1). These are recorded as zones generate, so explore some plains first if none are found.", args =>
            {
                try
                {
                    var player = Player.m_localPlayer; var wg = WorldGenerator.instance;
                    if (player == null || wg == null) { Log.LogWarning("[PT] gotoatrium: no player/worldgen."); return; }
                    int n = 1; if (args.Length >= 2 && int.TryParse(args[1], out int ni)) n = Math.Max(1, ni);
                    // Use the SERVICE network atriums (global, all of them) — the carve's own source, so teleporting
                    // generates the dome on arrival. (The carved-so-far list only held zones you'd already visited.)
                    // biomeMask = atrium-carving biomes only (plains, mistlands, …), so we never land on a BF Voronoi
                    // junction where no sewer mode actually carved a dome. (Was 0 = all biomes; before that, hardcoded Plains.)
                    var net = LocationVoronoi.GetNetwork(_corridorSteiner, _corridorClearance, 0.6f, 300f, 28f + Math.Max(_corridorClearance, 20f), 18f, 30f, 600f, AtriumBiomeMask());
                    if (net == null || net.Atriums.Count == 0) { Log.LogWarning("[PT] gotoatrium: no atriums (locations not ready, or network empty)."); return; }
                    var pp = player.transform.position; var pp2 = new Vector2(pp.x, pp.z);
                    var sorted = new List<LocationVoronoi.Atrium>(net.Atriums);
                    sorted.Sort((a, b) => (a.Pos - pp2).sqrMagnitude.CompareTo((b.Pos - pp2).sqrMagnitude));
                    var pick = sorted[Math.Min(n - 1, sorted.Count - 1)].Pos;
                    float sh = ComposedSurfaceSample(pick.x, pick.y, wg);   // composed surface so we land ON the raised plains, not ~26m under it
                    var dest = new Vector3(pick.x, sh + 3f, pick.y);
                    player.TeleportTo(dest, player.transform.rotation, true);
                    var msg = $"[PT] teleporting to atrium #{n}/{net.Atriums.Count} at ({pick.x:F0}, {pick.y:F0}) surfH={sh:F0}, dist={Vector2.Distance(pick, pp2):F0}m. Look for the breach pit / dig down.";
                    Log.LogInfo(msg); Console.instance?.Print(msg);
                }
                catch (Exception e) { Log.LogError($"[PT] gotoatrium: {e}"); }
            });
            new Terminal.ConsoleCommand("pt_dumplocs", "pt_dumplocs [radius] — write location sites (worldX,worldZ,protectRadius CSV) around the player to BepInEx/config/ProceduralTerrain/pt_locs.csv (for offline canal-path prototyping)", args =>
            {
                try
                {
                    var player = Player.m_localPlayer;
                    if (player == null) { Log.LogWarning("[PT] dumplocs: no player."); return; }
                    var pos = player.transform.position;
                    float cx = pos.x, cz = pos.z, range = 1000f;
                    if (args.Length >= 2 && float.TryParse(args[1], out float r)) range = r;
                    var sb = new System.Text.StringBuilder();
                    sb.AppendLine("# worldX,worldZ,protectRadius ; center=" + $"{cx:F1},{cz:F1}" + " range=" + range.ToString("F0") + " fade=" + "10");
                    int n = 0; float imgR = range * 1.5f + 500f;
                    foreach (var loc in _locationPositions)
                    {
                        float dx = loc.pos.x - cx, dz = loc.pos.y - cz;
                        if (dx * dx + dz * dz >= imgR * imgR) continue;
                        float pr = LocProtectRadius(loc.pos, loc.extR);
                        sb.AppendLine($"{loc.pos.x:F2},{loc.pos.y:F2},{pr:F2}"); n++;
                    }
                    string dir = System.IO.Path.GetDirectoryName(ConfigPath)!;
                    System.IO.File.WriteAllText(System.IO.Path.Combine(dir, "pt_locs.csv"), sb.ToString());
                    var msg = $"[PT] dumplocs: wrote {n} site(s) to {dir}\\pt_locs.csv (center {cx:F0},{cz:F0})";
                    Log.LogInfo(msg); Console.instance?.Print(msg);
                }
                catch (Exception e) { Log.LogError($"[PT] dumplocs exception: {e}"); }
            });
            new Terminal.ConsoleCommand("pt_dumpprefabs", "pt_dumpprefabs — write every ZNetScene prefab name (sorted) to BepInEx/config/ProceduralTerrain/prefab_names.txt", _ =>
            {
                try
                {
                    var zs = ZNetScene.instance;
                    if (zs == null) { Console.instance?.Print("[PT] ZNetScene not ready (load a world first)."); return; }
                    var f = typeof(ZNetScene).GetField("m_namedPrefabs", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                    var names = new System.Collections.Generic.List<string>();
                    if (f?.GetValue(zs) is System.Collections.IDictionary dict)
                        foreach (var v in dict.Values) if (v is GameObject go) names.Add(go.name);
                    names.Sort(StringComparer.OrdinalIgnoreCase);
                    System.IO.Directory.CreateDirectory(PTConfigDir);
                    string path = System.IO.Path.Combine(PTConfigDir, "prefab_names.txt");
                    System.IO.File.WriteAllLines(path, names);
                    var msg = $"[PT] wrote {names.Count} prefab names → {path}";
                    Log.LogInfo(msg); Console.instance?.Print(msg);
                }
                catch (Exception e) { Log.LogError($"[PT] dumpprefabs: {e}"); }
            });
            new Terminal.ConsoleCommand("pt_lightfield", "pt_lightfield [radius] — draw a cyan marker at every ivy LIGHT SEED (sky-opening: shaft/breach, a local depression) around you. These are the only spots light enters; ivy is allowed within lightrange of them. If markers appear in dark roofed spots, the seed detection is wrong. pt_viz_clear wipes.", args =>
            {
                try
                {
                    var player = Player.m_localPlayer; if (player == null) { Console.instance?.Print("[PT] lightfield: no player."); return; }
                    float radius = 64f; if (args.Length >= 2 && float.TryParse(args[1], out float r)) radius = r;
                    DebugViz.Enabled = true;                           // force the overlay on — else the renderer early-returns and the markers never draw (debugViz=false in cfg)
                    if (DebugViz.Radius > 0f && DebugViz.Radius < radius) DebugViz.Radius = radius;   // don't let the camera-radius cull hide markers inside the requested range
                    DrawLightSeeds(player.transform.position, radius);
                }
                catch (Exception e) { Log.LogError($"[PT] lightfield: {e}"); }
            });
            new Terminal.ConsoleCommand("pt_spawncount", "pt_spawncount [filter] — tally all LOADED networked objects (ZNetViews in the active area) by prefab name, top 30 (or only names containing [filter]). Diagnose spawn-count/draw-call lag — run it standing still.", args =>
            {
                try
                {
                    string? filt = args.Length >= 2 ? args[1] : null;
                    var counts = new Dictionary<string, int>();
                    int total = 0;
                    foreach (var nv in UnityEngine.Object.FindObjectsOfType<ZNetView>())
                    {
                        if (nv == null) continue;
                        string n = nv.gameObject.name.Replace("(Clone)", "");
                        if (filt != null && n.IndexOf(filt, StringComparison.OrdinalIgnoreCase) < 0) continue;
                        counts.TryGetValue(n, out int c); counts[n] = c + 1; total++;
                    }
                    var list = new List<KeyValuePair<string, int>>(counts);
                    list.Sort((a, b) => b.Value.CompareTo(a.Value));
                    var sb = new System.Text.StringBuilder();
                    sb.AppendLine($"[PT] loaded networked objects: {total}" + (filt != null ? $" matching '{filt}'" : "") + " (top 30):");
                    for (int i = 0; i < list.Count && i < 30; i++) sb.AppendLine($"  {list[i].Value,6}  {list[i].Key}");
                    Log.LogInfo(sb.ToString()); Console.instance?.Print(sb.ToString());
                }
                catch (Exception e) { Log.LogError($"[PT] spawncount: {e}"); }
            });
            new Terminal.ConsoleCommand("pt_vegdump", "pt_vegdump [biome] — write the FULL ZoneSystem vegetation table (every spawn incl. pickables/berries, with biomes + rates) to BepInEx/config/ProceduralTerrain/pt_vegdump.txt. Optional biome filter (e.g. Swamp).", args =>
            {
                try
                {
                    var zs = ZoneSystem.instance;
                    if (zs == null || zs.m_vegetation == null) { Console.instance?.Print("[PT] ZoneSystem/vegetation not ready (load a world)."); return; }
                    string filter = args.Length >= 2 ? args[1].Trim() : "";
                    var lines = new System.Collections.Generic.List<string>
                    { "name\tprefab\tkind\tbiome\tenable\tmin\tmax\tgrpMin\tgrpMax\tgrpRadius\tinForest\tforestThr\taltMin\taltMax\ttiltMin\ttiltMax\tscaleMin\tscaleMax\tsnapWater" };
                    int n = 0;
                    foreach (var v in zs.m_vegetation)
                    {
                        if (v == null) continue;
                        string biome = v.m_biome.ToString();
                        if (filter.Length > 0 && biome.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                        string pf = v.m_prefab != null ? v.m_prefab.name : "(null)";
                        string kind = "Other";
                        if (v.m_prefab != null)
                        {
                            if (v.m_prefab.GetComponentInChildren<TreeBase>() != null) kind = "Tree";
                            else if (v.m_prefab.GetComponentInChildren<Pickable>() != null) kind = "Pickable";
                            else if (v.m_prefab.GetComponentInChildren<Destructible>() != null) kind = "Destructible";
                        }
                        lines.Add($"{v.m_name}\t{pf}\t{kind}\t{biome}\t{v.m_enable}\t{v.m_min:F2}\t{v.m_max:F2}\t{v.m_groupSizeMin}\t{v.m_groupSizeMax}\t{v.m_groupRadius:F0}\t{v.m_inForest}\t[{v.m_forestTresholdMin:F2},{v.m_forestTresholdMax:F2}]\t{v.m_minAltitude:F0}\t{v.m_maxAltitude:F0}\t{v.m_minTilt:F0}\t{v.m_maxTilt:F0}\t{v.m_scaleMin:F2}\t{v.m_scaleMax:F2}\t{v.m_snapToWater}");
                        n++;
                    }
                    System.IO.Directory.CreateDirectory(PTConfigDir);
                    string path = System.IO.Path.Combine(PTConfigDir, "pt_vegdump.txt");
                    System.IO.File.WriteAllLines(path, lines);
                    var msg = $"[PT] vegdump: {n} vegetation entr{(n == 1 ? "y" : "ies")}{(filter.Length > 0 ? $" matching '{filter}'" : "")} → {path}";
                    Log.LogInfo(msg); Console.instance?.Print(msg);
                }
                catch (Exception e) { Log.LogError($"[PT] vegdump: {e}"); }
            });
            new Terminal.ConsoleCommand("pt_bakereload", "pt_bakereload — re-scan and load the bake file without restarting (use after uploading a .bin to a running server), then recarve loaded zones", _ =>
            {
                try
                {
                    _bakeLoadAttempted = false;   // LoadBakeFile self-closes the old stream + re-checks the file
                    LoadBakeFile();
                    _bakeLoadAttempted = true;
                    _hotReloadPending  = true;    // re-serve loaded zones from the (now present) bake
                    var msg = $"[PT] bakereload: {(_bakeIndex != null ? _bakeIndex.Count + " zones indexed" : "no bake found — still live-carving")}; scheduling recarve.";
                    Log.LogInfo(msg); Console.instance?.Print(msg);
                }
                catch (Exception e) { Log.LogError($"[PT] bakereload: {e}"); }
            });
            new Terminal.ConsoleCommand("pt_bake", "pt_bake [radius] [player] — bake terrain SDF for a radius in meters; runs on server (admin only). add 'player' to centre the disc on your position instead of world origin.", args =>
            {
                float radius = _bakeRadius;
                if (args.Length >= 2 && float.TryParse(args[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float r))
                    radius = r;
                // Optional centre arg: "player"/"here" → bake around the local player; otherwise (default/"origin") world origin.
                float centerX = 0f, centerZ = 0f;
                bool centerOnPlayer = args.Length >= 3 && (args[2].Equals("player", StringComparison.OrdinalIgnoreCase) || args[2].Equals("here", StringComparison.OrdinalIgnoreCase));
                if (centerOnPlayer)
                {
                    var p = Player.m_localPlayer;
                    if (p != null) { var pos = p.transform.position; centerX = pos.x; centerZ = pos.z; }
                    else { Log.LogWarning("[ProceduralTerrain] pt_bake player: no local player — falling back to world origin."); centerOnPlayer = false; }
                }
                if (ZNet.instance != null && !ZNet.instance.IsServer())
                {
                    // Client: request server to run the bake (send centre too — resolved on the client since the
                    // server has no player position for us).
                    var pkg = new ZPackage();
                    pkg.Write(radius);
                    pkg.Write(centerX);
                    pkg.Write(centerZ);
                    ZRoutedRpc.instance.InvokeRoutedRPC(ZRoutedRpc.Everybody, "PTBakeExecute", pkg);
                    Log.LogInfo($"[ProceduralTerrain] Bake request sent to server (radius={radius}, centre={(centerOnPlayer ? $"{centerX:F0},{centerZ:F0}" : "origin")}).");
                }
                else
                {
                    _bakeInProgress  = true;
                    _bakeProgressDone  = 0;
                    _bakeProgressTotal = 0;
                    _bakeProgressTimer = System.Diagnostics.Stopwatch.StartNew();
                    _bakeCts?.Dispose();
                    _bakeCts = new System.Threading.CancellationTokenSource();
                    BakeProgressUI.Show();
                    System.Threading.Tasks.Task.Run(() => WriteBakeFile(radius, centerX, centerZ))
                        .ContinueWith(_ => { _bakeInProgress = false; });
                }
            });
            new Terminal.ConsoleCommand("pt_cancel", "pt_cancel — stop a running pt_bake (runs on the server if you're a client, admin only); the zones carved so far are finalized into a valid, loadable partial bake", args =>
            {
                if (ZNet.instance != null && !ZNet.instance.IsServer())
                {
                    // Client: the bake runs on the server, so route the cancel there (mirror pt_bake).
                    ZRoutedRpc.instance.InvokeRoutedRPC(ZRoutedRpc.Everybody, "PTBakeCancel", new ZPackage());
                    Log.LogInfo("[ProceduralTerrain] pt_cancel: cancel request sent to server.");
                    args.Context?.AddString("[ProceduralTerrain] bake cancel request sent to server.");
                    return;
                }
                if (!_bakeInProgress || _bakeCts == null)
                {
                    args.Context?.AddString("[ProceduralTerrain] no bake in progress.");
                    return;
                }
                _bakeCts.Cancel();
                Log.LogInfo("[ProceduralTerrain] pt_cancel: cancelling bake — partial bake will be finalized.");
                args.Context?.AddString("[ProceduralTerrain] bake cancelling; partial bake will be saved.");
            });
            new Terminal.ConsoleCommand("pt_bakeinfo", "pt_bakeinfo — current bake status: file, whether a bake is loaded/serving, rolling-bake state, download mode", args =>
            {
                int seed = WorldSeed();
                uint cfgHash = ComputeCfgHash();
                string seedName = WorldGenerator.instance?.m_world?.m_seedName;   // the human-typed seed string; m_seed = its GetStableHashCode
                string path = BakeFilePath(seed, cfgHash);
                string name = System.IO.Path.GetFileName(path);
                bool exists = System.IO.File.Exists(path);
                string size = exists ? $"{new System.IO.FileInfo(path).Length / (1024f * 1024f):F1} MB" : "—";
                string seedLine = string.IsNullOrEmpty(seedName) ? $"seed={seed:X8} ({seed})" : $"seed=\"{seedName}\" → {seed:X8} ({seed})";

                // Loaded/serving state: which store is live and how many zones it holds.
                int loadedZones = (_bakeData?.Count ?? 0) + (_bakeIndex?.Count ?? 0);
                string store = _bakeData != null ? "in-RAM" : (_bakeIndex != null ? $"streamed v{(_bakeFileV6 ? 6 : 5)}" : "none");
                string serving = loadedZones > 0 ? $"YES ({loadedZones} zones, {store})" : "no — live-carving";

                // Rolling bake state.
                string rolling = !RollingBake.Enabled ? "disabled (cfg rollingbake=0)"
                    : RollingBake.IsRunning ? $"ON, running (radius {RollingBake.RadiusMeters:F0}m, +{RollingBake.SessionPersisted} zones this session)"
                    : (ZNet.instance != null && ZNet.instance.IsDedicated()) ? "enabled but off (dedicated server)"
                    : _bakeDownloadActive ? "enabled, paused (download in progress)"
                    : "enabled, arming…";

                // Download / multiplayer context.
                string net = ZNet.instance == null ? "not connected"
                    : ZNet.instance.IsServer() ? "host/server (bake is local)"
                    : $"client (serverCfgHash={(_serverCfgHash.HasValue ? _serverCfgHash.Value.ToString("X8") : "none")}, autoDownload={(_bakeAutoDownload ? "on" : "off")}{(_bakeDownloadActive ? ", DOWNLOADING" : "")})";

                string msg = $"[ProceduralTerrain] {seedLine}  cfgHash={cfgHash:X8}\n"
                           + $"  file: {name}  exists={exists} ({size})\n"
                           + $"  serving from bake: {serving}\n"
                           + $"  rolling: {rolling}\n"
                           + $"  network: {net}";
                Log.LogInfo(msg);
                args.Context?.AddString(msg);
            });
            new Terminal.ConsoleCommand("pt_bakestats", "pt_bakestats — print the bake stats sidecar (timings, distribution, GC/CPU/RAM) for the current world/config; the raw per-zone array is in the .json file on disk", args =>
            {
                string jsonPath = System.IO.Path.ChangeExtension(BakeFilePath(WorldSeed(), ComputeCfgHash()), ".json");
                if (!System.IO.File.Exists(jsonPath))
                {
                    args.Context?.AddString($"[ProceduralTerrain] no stats sidecar at {System.IO.Path.GetFileName(jsonPath)} (bake first).");
                    return;
                }
                string json = System.IO.File.ReadAllText(jsonPath);
                int zi = json.IndexOf("\"zones\"");   // trim the big raw array for the console; it stays in the file
                string summary = zi > 0 ? json.Substring(0, zi).TrimEnd('\n', ' ', ',') + "\n}" : json;
                Log.LogInfo($"[ProceduralTerrain] bake stats ({jsonPath}):\n{summary}");
                args.Context?.AddString(summary);
                args.Context?.AddString($"[ProceduralTerrain] full per-zone data: {jsonPath}");
            });
            new Terminal.ConsoleCommand("pt_carvezone", "pt_carvezone <ox> <oz> — carve one zone at full BAKE resolution on a background thread and time it (repro for a rolling-bake hang). Logs begin + elapsed; no completion line = it hung.", args =>
            {
                if (args.Length < 3 || !int.TryParse(args[1], out int ox) || !int.TryParse(args[2], out int oz))
                { args.Context?.AddString("usage: pt_carvezone <ox> <oz>"); return; }
                args.Context?.AddString($"[ProceduralTerrain] carving zone ({ox},{oz}) at full res on a bg thread — watch the log.");
                Log.LogWarning($"[PT-carvezone] BEGIN zone ({ox},{oz}) full-res carve…");
                System.Threading.Tasks.Task.Run(() =>
                {
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    try { bool mod = RollingCarveBlock(ox, oz, out _); sw.Stop(); Log.LogWarning($"[PT-carvezone] DONE zone ({ox},{oz}) in {sw.ElapsedMilliseconds}ms (modified={mod})"); }
                    catch (Exception e) { sw.Stop(); Log.LogError($"[PT-carvezone] zone ({ox},{oz}) threw after {sw.ElapsedMilliseconds}ms: {e.GetType().Name}: {e.Message}\n{e.StackTrace}"); }
                });
            });
            new Terminal.ConsoleCommand("pt_download", "pt_download — pull the server's bake for this world and merge it into your local bake (opt-in; the transfer is slow for big bakes). No-op on the host/server. Rolling bake fills the rest as you roam.", args =>
            {
                if (ZNet.instance == null) { args.Context?.AddString("[ProceduralTerrain] not connected."); return; }
                if (ZNet.instance.IsServer()) { args.Context?.AddString("[ProceduralTerrain] you're the host — the bake is already local."); return; }
                if (_bakeDownloadActive) { args.Context?.AddString("[ProceduralTerrain] a bake download is already in progress."); return; }
                Plugin.SendBakeQuery("pt_download");
                args.Context?.AddString("[ProceduralTerrain] asked the server for its bake; downloading if available (watch the progress bar).");
            });
            new Terminal.ConsoleCommand("pt_bakemap", "pt_bakemap [radius] — render a PNG of which zones ARE/AREN'T baked for the current world, over a coastline map. auto-fits to the baked extent; pass a radius (m, centred on you) to override. -> BepInEx/config/ProceduralTerrain/pt_bakemap.png", args =>
            {
                try
                {
                    var wg = WorldGenerator.instance;
                    if (wg == null) { Log.LogWarning("[PT] bakemap: no worldgen."); return; }
                    // Gather baked zone origins (voxel coords) from whichever store is loaded.
                    var keys = new List<(int ox, int oz)>();
                    if (_bakeData  != null) keys.AddRange(_bakeData.Keys);
                    if (_bakeIndex != null) foreach (var k in _bakeIndex.Keys) keys.Add(k);
                    if (keys.Count == 0) { args.Context?.AddString("[ProceduralTerrain] no bake loaded — nothing to map (run pt_bakeinfo)."); return; }

                    float s = (_scale.HasValue && _scale.Value > 0f) ? _scale.Value : 1f;
                    float zoneWorld = 64f * s;   // one bake zone's world footprint

                    // Window: default auto-fit to baked extent (+ player) with a margin; a radius arg centres on the player instead.
                    var pp = Player.m_localPlayer != null ? Player.m_localPlayer.transform.position : Vector3.zero;
                    float cx, cz, range;
                    if (args.Length >= 2 && float.TryParse(args[1], out float rad))
                    { cx = pp.x; cz = pp.z; range = rad; }
                    else
                    {
                        float minX = float.MaxValue, maxX = float.MinValue, minZ = float.MaxValue, maxZ = float.MinValue;
                        foreach (var (ox, oz) in keys)
                        { float wx = ox * s, wz = oz * s; if (wx < minX) minX = wx; if (wx > maxX) maxX = wx; if (wz < minZ) minZ = wz; if (wz > maxZ) maxZ = wz; }
                        cx = (minX + maxX) * 0.5f; cz = (minZ + maxZ) * 0.5f;
                        range = Mathf.Max(maxX - minX, maxZ - minZ) * 0.5f + zoneWorld * 4f;   // margin
                        range = Mathf.Max(range, 200f);
                    }

                    int res = 900; float step = range * 2f / res;
                    int PX(float wx) => Mathf.RoundToInt((wx - (cx - range)) / step);
                    int PZ(float wz) => Mathf.RoundToInt(((cz + range) - wz) / step);
                    var px = new Color[res * res];

                    // Background: ocean tint + coastline (same scheme as pt_voronoi).
                    var ocean = new bool[res * res];
                    for (int yy = 0; yy < res; yy++)
                    for (int xx = 0; xx < res; xx++)
                    {
                        float wx = cx - range + xx * step, wz = (cz + range) - yy * step;
                        bool o = wg.GetBiome(wx, wz) == Heightmap.Biome.Ocean;
                        ocean[yy * res + xx] = o;
                        px[yy * res + xx] = o ? new Color(0.82f, 0.90f, 0.95f) : new Color(0.20f, 0.19f, 0.16f);   // land = dark so baked green pops
                    }
                    var coastCol = new Color(0.1f, 0.5f, 0.55f);
                    for (int yy = 0; yy < res; yy++)
                    for (int xx = 0; xx < res; xx++)
                    {
                        bool o = ocean[yy * res + xx];
                        bool edge = (xx + 1 < res && ocean[yy * res + xx + 1] != o) || (yy + 1 < res && ocean[(yy + 1) * res + xx] != o);
                        if (edge) px[yy * res + xx] = coastCol;
                    }

                    // Baked zones: fill each as a green square (origin corner → +zoneWorld). Gaps show through as dark land.
                    var bakedCol = new Color(0.25f, 0.85f, 0.30f);
                    int shown = 0;
                    foreach (var (ox, oz) in keys)
                    {
                        float wx0 = ox * s, wz0 = oz * s;
                        int x0 = PX(wx0), x1 = PX(wx0 + zoneWorld);
                        int y0 = PZ(wz0 + zoneWorld), y1 = PZ(wz0);   // PZ flips, so +Z maps to smaller y
                        if (x1 < 0 || y1 < 0 || x0 >= res || y0 >= res) continue;
                        shown++;
                        for (int yy = Mathf.Max(0, y0); yy <= Mathf.Min(res - 1, y1); yy++)
                        for (int xx = Mathf.Max(0, x0); xx <= Mathf.Min(res - 1, x1); xx++)
                            px[yy * res + xx] = bakedCol;
                    }

                    // Markers: world origin (blue) and player (red).
                    void Dot(float wx, float wz, Color c) { int x = PX(wx), y = PZ(wz), r = 4; for (int b = -r; b <= r; b++) for (int a = -r; a <= r; a++) if (a*a+b*b <= r*r) { int xx = x+a, yy = y+b; if (xx>=0&&yy>=0&&xx<res&&yy<res) px[yy*res+xx] = c; } }
                    Dot(0f, 0f, new Color(0.1f, 0.3f, 0.95f));
                    if (Player.m_localPlayer != null) Dot(pp.x, pp.z, new Color(0.95f, 0.15f, 0.1f));

                    var tex = new Texture2D(res, res, TextureFormat.RGBA32, false); tex.SetPixels(px); tex.Apply();
                    string dir = System.IO.Path.GetDirectoryName(ConfigPath)!;
                    string outPath = System.IO.Path.Combine(dir, "pt_bakemap.png");
                    System.IO.File.WriteAllBytes(outPath, tex.EncodeToPNG());
                    var msg = $"[PT] bakemap: {shown}/{keys.Count} baked zones in window (centre {cx:F0},{cz:F0}, range {range:F0}m, zone {zoneWorld:F0}m) → {outPath}";
                    Log.LogInfo(msg); args.Context?.AddString(msg);
                }
                catch (Exception e) { Log.LogWarning($"[PT] bakemap failed: {e.Message}"); }
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
                            : System.IO.Path.Combine(PTConfigDir, arg);
                    }
                    else
                    {
                        int  seed    = WorldSeed();
                        uint cfgHash = ComputeCfgHash();
                        path = BakeFilePath(seed, cfgHash);
                    }
                    if (File.Exists(path))
                    {
                        CloseBakeStream();   // release any open read handle so delete can't be blocked
                        File.Delete(path);
                        // Reset in-memory cache if we just deleted the active file.
                        if (System.IO.Path.GetFullPath(path) == System.IO.Path.GetFullPath(BakeFilePath(WorldSeed(), ComputeCfgHash())))
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
                    opts.AddRange(System.IO.Directory.GetFiles(PTConfigDir, "procedural_terrain_bake_*.bin")
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
                string dir = PTConfigDir;
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
            new Terminal.ConsoleCommand("pt_linedump", "pt_linedump [len] [step] — walk the player's facing line and dump the REAL carved surface + all field crossings (relief floor, caves) per column to pt_linedump.csv, for terrace diagnosis", args =>
            {
                try
                {
                    var player = Player.m_localPlayer; if (player == null) { Log.LogWarning("[PT] linedump: no player."); return; }
                    float len = 40f; if (args.Length >= 2 && float.TryParse(args[1], out float l)) len = l;
                    float step = 0.25f; if (args.Length >= 3 && float.TryParse(args[2], out float st)) step = Mathf.Max(0.05f, st);
                    var p = player.transform.position;
                    var fwd = player.transform.forward; fwd.y = 0f;
                    if (fwd.sqrMagnitude < 1e-4f) fwd = Vector3.forward;
                    fwd = fwd.normalized;
                    var wg = WorldGenerator.instance;
                    var sb = new System.Text.StringBuilder("dist,wx,wz,vanillaH,surfH,crossings(top..bottom)\n");
                    int n = Mathf.Max(1, Mathf.RoundToInt(len / step));
                    for (int i = 0; i <= n; i++)
                    {
                        float d = i * step;
                        float wx = p.x + fwd.x * d, wz = p.z + fwd.z * d;
                        float vh = wg != null ? GroundHeight(wx, wz, wg) : 0f;
                        float sh = TrySampleSurfaceY(wx, wz, out float s0) ? s0 : float.NaN;
                        var cr = DecodeColumnCrossings(wx, wz);
                        cr.Sort((a, b) => b.CompareTo(a));   // top first
                        sb.Append($"{d:F2},{wx:F2},{wz:F2},{vh:F3},{sh:F3}");
                        foreach (var c in cr) sb.Append($",{c:F3}");
                        sb.Append('\n');
                    }
                    string dir = System.IO.Path.GetDirectoryName(ConfigPath)!;
                    string path = System.IO.Path.Combine(dir, "pt_linedump.csv");
                    System.IO.File.WriteAllText(path, sb.ToString());
                    var msg = $"[PT] linedump: {n + 1} samples over {len:F0}m from ({p.x:F0},{p.z:F0}) dir ({fwd.x:F2},{fwd.z:F2}) → {path}";
                    Log.LogInfo(msg); Console.instance?.Print(msg);
                }
                catch (Exception e) { Log.LogError($"[PT] linedump: {e}"); }
            });
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
        }

        // Is our command still in Valheim's live command table? (cached FieldInfo; reflects the protected static dict)
        static System.Reflection.FieldInfo? _termCmdField;
        static bool PtCommandsPresent()
        {
            try
            {
                _termCmdField ??= typeof(Terminal).GetField("commands", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
                return (_termCmdField?.GetValue(null) as System.Collections.IDictionary)?.Contains("pt_gotoatrium") ?? true;
            }
            catch { return true; }   // on error, assume present so we don't spin re-registering
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        static void RegisterVoxheimGenerator()
        {
            Voxheim.Core.VoxelBaselinePipeline.RegisterGenerator(new PTGenerator());
            Log.LogInfo("[ProceduralTerrain] PTGenerator registered with Voxheim.");
        }

        internal static int _dumpZonesRemaining = 0;

        static int _pollFrame;
        static long _perfLastTs;
        static float _mistSweepT;
        static bool _mistWasHidden;
        static int _perfGc0, _perfGc2;
        const double PerfHitchMs = 33.0;   // flag frames slower than ~30fps

        void Update()
        {
            // ── FarLands: register our far-terrain height delta once the bake is loaded (self-guards; cheap no-op after) ──
            if (FarLandsBridge.IsPresent && (_bakeData != null || _bakeIndex != null))
                FarLandsBridge.TryRegister();


            // ── Frame hitch watchdog (EVERY frame; dominator of a main-thread stall) ──────────────
            {
                long nowTs = System.Diagnostics.Stopwatch.GetTimestamp();
                if (_perfLastTs != 0)
                {
                    double frameMs = (nowTs - _perfLastTs) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                    if (frameMs > PerfHitchMs)
                    {
                        int g0 = GC.CollectionCount(0) - _perfGc0, g2 = GC.CollectionCount(2) - _perfGc2;
                        double ptMain = PtPerf.CarveMs + PtPerf.GwMs + PtPerf.SetMs + PtPerf.VegMs + PtPerf.CaveMs;
                        Log.LogInfo($"[PT-hitch] frame={frameMs:F0}ms  PTmain={ptMain:F1}ms (carve={PtPerf.CarveMs:F1} set={PtPerf.SetMs:F1} gw={PtPerf.GwMs:F1} veg={PtPerf.VegMs:F1} cave={PtPerf.CaveMs:F1})  prefetch={PtPerf.PrefetchCarves}  gc0+{g0} gc2+{g2}");
                    }
                }
                _perfLastTs = nowTs;
                _perfGc0 = GC.CollectionCount(0); _perfGc2 = GC.CollectionCount(2);
                PtPerf.ResetFrame();
            }

            // Self-heal our console commands: the Terminal rebuilds its command table after Awake (and mods may clear
            // it later), dropping ours — re-add whenever they're missing. Cheap (one dict ContainsKey most frames).
            if (Console.instance != null && !PtCommandsPresent())
            {
                RegisterConsoleCommands();
                Log.LogInfo("[ProceduralTerrain] (re)registered console commands (self-heal).");
            }

            if (_hotReloadPending)
            {
                _hotReloadPending = false;
                RecarveAll();
            }

            // Demand-driven carve: the SpawnZone gate (SpawnZonePrefetchGate) requests carves as ZoneSystem
            // asks for zones and makes it retry until ready — no racing ring. Just keep workers alive.
            if (ZonePrefetcher.Active) ZonePrefetcher.Start();   // idempotent; skipped on dedicated servers

            // Rolling bake: arm once a world/bake context is ready, then follow the player. Idempotent arm.
            if (RollingBake.Active) { RollingBake.Arm(); RollingBake.Tick(); RollingBake.Watchdog(); }
            else if (RollingBake.IsRunning) RollingBake.Stop();   // rollingbake toggled off (or download/dedicated) → stop the live session
            if (UnityEngine.Input.GetKeyDown(RollingHudKeyCode)) RollingBake.ShowHud = !RollingBake.ShowHud;   // hotkey toggle
            if (RollingBake.ShowHud && RollingBake.IsRunning) BakeProgressUI.Show();   // ensure the HUD overlay exists (idempotent; covers hot-reload toggling it on)

            // Track underground state for the LOD-hide toggle: player Y well below the carved surface = in a cave.
            if (_lodUnderground)
            {
                var pl = Player.m_localPlayer;
                if (pl != null)
                {
                    var p = pl.transform.position;
                    _playerUnderground = FindCarvedSurfaceY(p.x, p.y, p.z) - p.y > LOD_UndergroundMargin;
                }
                else _playerUnderground = false;
            }
            else _playerUnderground = false;

            // DEBUG: globally suppress the Mistlands ground mist (ParticleMist singleton). The ParticleMist.Update
            // prefix skips emission + clears the system every frame; this 1 Hz sweep clears any particles already in
            // flight (e.g. right after the flag flips) and logs once so the log confirms the feature is live. Flip the
            // cfg back to re-enable (the prefix stops skipping; mist regrows).
            if (_hideMist)
            {
                _mistSweepT += Time.deltaTime;
                if (_mistSweepT >= 1f)
                {
                    _mistSweepT = 0f;
                    var pm = ParticleMist.instance;
                    if (pm != null)
                    {
                        var ps = pm.GetComponent<ParticleSystem>();
                        if (ps != null) ps.Clear(true);
                    }
                    if (!_mistWasHidden) { Log.LogInfo($"[ProceduralTerrain] hideMist ON — suppressing ParticleMist (instance={(pm != null)})."); _mistWasHidden = true; }
                }
            }
            else if (_mistWasHidden) { _mistWasHidden = false; Log.LogInfo("[ProceduralTerrain] hideMist OFF — mist re-enabled."); }

            // Fast-poll while waiting for locations to finish generating (the time-sliced coroutine
            // fills them over several frames); BuildLocationCache no-ops until ZoneSystem reports
            // LocationsGenerated, then caches once. Settle to a slow poll for config changes after.
            int pollEvery = _locationsCached ? 300 : 10;
            if (++_pollFrame % pollEvery != 0) return;

            if (!_locationsCached)
                BuildLocationCache(); // caches once LocationsGenerated; sets _hotReloadPending → RecarveAll

            WatchPlacementConfigReload();   // independent file; no recarve needed

            var t  = File.GetLastWriteTimeUtc(ConfigPath);
            var ct = File.Exists(ClientConfigPath) ? File.GetLastWriteTimeUtc(ClientConfigPath) : DateTime.MinValue;
            if (t == _configLastWrite && ct == _clientConfigLastWrite) return;   // watch client.cfg too
            _configLastWrite = t; _clientConfigLastWrite = ct;
            LoadBiomeConfig();
            RecarveAll();
        }

        // When connected to a server, holds the server's cfg lines (applied in-memory; local file untouched).
        internal static string[]? _serverCfgLines;
        // When connected to a server, holds the server's cfg hash (used for bake file lookup).
        internal static uint? _serverCfgHash;
        // When connected to a server, holds the server's placement cfg lines (applied in-memory; local file untouched).
        // Placements are server-authoritative but NOT hashed (they don't change the baked .bin — veg/content placed live).
        internal static string[]? _serverPlacementLines;

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
