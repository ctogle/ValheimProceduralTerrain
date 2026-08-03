using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using UnityEngine;

namespace VoxCaves
{
    [BepInPlugin("com.ctogle.voxcaves", "VoxCaves", "0.2.0")]
    [BepInDependency("com.bogey.voxheim", BepInDependency.DependencyFlags.SoftDependency)]
    public class Plugin : BaseUnityPlugin
    {
        internal static ManualLogSource Log = null!;

        // Zero-alloc voxel access via compiled IL — see BuildDelegates().
        internal static Func<object, int, int, int, sbyte>?   _get;
        internal static Action<object, int, int, int, sbyte>? _set;

        // World units per voxel. Voxheim default 0.5 (2 voxels per metre).
        // Read lazily inside Carve() — s_scale isn't initialized when our plugin loads.
        static FieldInfo? _scaleField;
        static float? _scale;

        // ── Density mode gates ────────────────────────────────────────────────────
        static bool _useNoiseCaves    = false; // domain-warped tunnel network
        static bool _useHeightmapWarp = true;  // warped-heightmap carve (solid → air)
        static bool _useHeightmapAdd  = false; // warped-heightmap add  (air → solid)

        // Noise instances — created lazily on first Carve() so the world seed is ready.
        internal static FastNoiseLite? _caveNoise;
        internal static FastNoiseLite? _warpX;
        internal static FastNoiseLite? _warpZ;
        // Low-freq warp for heightmap carve mode.
        internal static FastNoiseLite? _hmWarpX;
        internal static FastNoiseLite? _hmWarpZ;
        // Separate warp for heightmap add mode — independent seed so pattern differs.
        internal static FastNoiseLite? _hmAddWarpX;
        internal static FastNoiseLite? _hmAddWarpZ;

        // Seam fix: before VoxelZone.Complete() fires, we inject our carved brick
        // coordinates into _editedBricks so Voxheim's own PokeTransitionNeighbors
        // picks them up and tells already-loaded neighbours to re-mesh their boundary.
        internal static ConstructorInfo? _int3Ctor;
        internal static FieldInfo?       _zoneOriginX;
        internal static FieldInfo?       _zoneOriginZ;
        internal static FieldInfo?       _editedBricksField;
        internal static MethodInfo?      _editedBricksAdd;   // cached on first use
        // Brick coords (voxel >> 4) waiting to be injected, keyed by "ox,oz".
        internal static readonly Dictionary<string, HashSet<(int, int, int)>> _pendingBricks = new();

        void Awake()
        {
            Log = Logger;
            if (!BepInEx.Bootstrap.Chainloader.PluginInfos.ContainsKey("com.bogey.voxheim"))
            {
                Log.LogInfo("[VoxCaves] Voxheim absent — disabled.");
                return;
            }
            if (!BuildDelegates())
            {
                Log.LogError("[VoxCaves] Reflection failed — disabled.");
                return;
            }
            new Harmony("com.ctogle.voxcaves").PatchAll();
            Log.LogInfo("[VoxCaves] Ready.");
        }

        // ── Type lookup ───────────────────────────────────────────────────────────
        // Type.GetType("Name, Assembly") fails in BepInEx's isolation context,
        // so we scan loaded assemblies by name instead.
        internal static Type? FindType(string typeName, string assemblyName)
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                if (asm.GetName().Name == assemblyName)
                {
                    var t = asm.GetType(typeName);
                    if (t != null) return t;
                }
            return null;
        }

        // ── Delegate compilation ──────────────────────────────────────────────────
        // VoxelField and Int3 are in a separate assembly we can't reference at
        // compile time. MethodInfo.Invoke works but boxes every argument into
        // object[], allocating ~300K arrays per zone. DynamicMethod emits IL that
        // calls Get/Set directly with Int3 constructed on the stack — zero alloc.
        static bool BuildDelegates()
        {
            var fieldT = FindType("Voxheim.Core.VoxelField", "Voxheim.Core");
            var int3T  = FindType("Voxheim.Core.Int3",       "Voxheim.Core");
            var zoneT  = FindType("Voxheim.Unity.VoxelZone", "Voxheim");

            if (fieldT == null || int3T == null)
            {
                Log.LogError("[VoxCaves] VoxelField or Int3 type not found.");
                return false;
            }

            _int3Ctor = int3T.GetConstructor(new[] { typeof(int), typeof(int), typeof(int) });
            var get   = fieldT.GetMethod("Get", BindingFlags.Public | BindingFlags.Instance,
                            null, new[] { int3T }, null);
            var set   = fieldT.GetMethod("Set", BindingFlags.Public | BindingFlags.Instance,
                            null, new[] { int3T, typeof(sbyte) }, null);

            if (_int3Ctor == null || get == null || set == null)
            {
                Log.LogError("[VoxCaves] Int3 ctor / VoxelField.Get / VoxelField.Set not found.");
                return false;
            }

            // Cache the FieldInfo for s_scale; we'll read the value lazily in Carve()
            // because Voxheim hasn't initialized the field yet when our plugin loads.
            _scaleField = zoneT?.GetField("s_scale", BindingFlags.NonPublic | BindingFlags.Static);
            if (_scaleField == null) Log.LogWarning("[VoxCaves] s_scale not found, will assume 1.");

            // Fields needed for CompletePatch to inject bricks into Voxheim's edit tracker.
            _zoneOriginX      = zoneT?.GetField("_originX",      BindingFlags.NonPublic | BindingFlags.Instance);
            _zoneOriginZ      = zoneT?.GetField("_originZ",      BindingFlags.NonPublic | BindingFlags.Instance);
            _editedBricksField = zoneT?.GetField("_editedBricks", BindingFlags.NonPublic | BindingFlags.Instance);
            if (_zoneOriginX == null || _zoneOriginZ == null || _editedBricksField == null)
                Log.LogWarning("[VoxCaves] Seam-fix fields missing — zone boundary seams may appear.");

            // Get: (object field, int x, int y, int z) -> sbyte
            var gDm = new DynamicMethod("VC_Get", typeof(sbyte),
                new[] { typeof(object), typeof(int), typeof(int), typeof(int) },
                typeof(Plugin).Module, skipVisibility: true);
            var gi = gDm.GetILGenerator();
            gi.Emit(OpCodes.Ldarg_0); gi.Emit(OpCodes.Castclass, fieldT);
            gi.Emit(OpCodes.Ldarg_1); gi.Emit(OpCodes.Ldarg_2); gi.Emit(OpCodes.Ldarg_3);
            gi.Emit(OpCodes.Newobj, _int3Ctor);
            gi.Emit(OpCodes.Callvirt, get);
            gi.Emit(OpCodes.Ret);
            _get = (Func<object,int,int,int,sbyte>)gDm.CreateDelegate(
                typeof(Func<object,int,int,int,sbyte>));

            // Set: (object field, int x, int y, int z, sbyte value) -> void
            var sDm = new DynamicMethod("VC_Set", typeof(void),
                new[] { typeof(object), typeof(int), typeof(int), typeof(int), typeof(sbyte) },
                typeof(Plugin).Module, skipVisibility: true);
            var si = sDm.GetILGenerator();
            si.Emit(OpCodes.Ldarg_0); si.Emit(OpCodes.Castclass, fieldT);
            si.Emit(OpCodes.Ldarg_1); si.Emit(OpCodes.Ldarg_2); si.Emit(OpCodes.Ldarg_3);
            si.Emit(OpCodes.Newobj, _int3Ctor);
            si.Emit(OpCodes.Ldarg_S, (byte)4);
            si.Emit(OpCodes.Callvirt, set);
            si.Emit(OpCodes.Ret);
            _set = (Action<object,int,int,int,sbyte>)sDm.CreateDelegate(
                typeof(Action<object,int,int,int,sbyte>));

            return true;
        }

        // ── Noise initialisation ──────────────────────────────────────────────────
        static void InitNoise()
        {
            if (_caveNoise != null) return;
            long uid = ZNet.instance?.GetWorldUID() ?? 0L;
            int seed = uid != 0L ? (int)(uid * 2654435761L) : 12345;
            Log.LogInfo($"[VoxCaves] noise seed={seed} (uid={uid})");

            _caveNoise = new FastNoiseLite(seed);
            _caveNoise.SetNoiseType(FastNoiseLite.NoiseType.OpenSimplex2);
            _caveNoise.SetFrequency(0.015f);

            _warpX = new FastNoiseLite(seed + 1);
            _warpX.SetNoiseType(FastNoiseLite.NoiseType.OpenSimplex2);
            _warpX.SetFrequency(0.04f);

            _warpZ = new FastNoiseLite(seed + 2);
            _warpZ.SetNoiseType(FastNoiseLite.NoiseType.OpenSimplex2);
            _warpZ.SetFrequency(0.04f);

            // Heightmap-warp mode: very low frequency so warp patterns are hundreds of
            // metres across, displacing the sampled heightmap surface by large amounts.
            _hmWarpX = new FastNoiseLite(seed + 3);
            _hmWarpX.SetNoiseType(FastNoiseLite.NoiseType.OpenSimplex2);
            _hmWarpX.SetFrequency(0.005f);

            _hmWarpZ = new FastNoiseLite(seed + 4);
            _hmWarpZ.SetNoiseType(FastNoiseLite.NoiseType.OpenSimplex2);
            _hmWarpZ.SetFrequency(0.005f);

            _hmAddWarpX = new FastNoiseLite(seed + 5);
            _hmAddWarpX.SetNoiseType(FastNoiseLite.NoiseType.OpenSimplex2);
            _hmAddWarpX.SetFrequency(0.005f);

            _hmAddWarpZ = new FastNoiseLite(seed + 6);
            _hmAddWarpZ.SetNoiseType(FastNoiseLite.NoiseType.OpenSimplex2);
            _hmAddWarpZ.SetFrequency(0.005f);
        }

        // ── Per-column analytic floor ─────────────────────────────────────────────
        // Single-sample: GetHeight at the column's own XZ. This is globally continuous
        // (terrain height is smooth noise), so adjacent columns have nearly identical
        // floors. The 5-point-minimum approach caused discrete jumps when the minimum
        // sample changed, producing terracing.
        internal static float ComputeColumnFloor(float worldX, float worldZ, WorldGenerator? wg)
        {
            return wg != null ? wg.GetHeight(worldX, worldZ) - 8f : 20f;
        }

        // ── Density function ──────────────────────────────────────────────────────
        // Returns [0, 1] for Mode A (noise tunnel network) only. Mode B is handled
        // directly in Carve() using a proper signed-distance field on both sides.
        static float Density(float worldX, float worldY, float worldZ, float floorY,
                             WorldGenerator? wg)
        {
            if (!_useNoiseCaves) return 0f;
            if (worldY <= floorY) return 0f;

            InitNoise();

            const float vscale      = 0.35f;
            const float warpAmp     = 10f;
            const float tunnelWidth = 0.18f;

            float sy = worldY * vscale;
            float wx = _warpX!.GetNoise(worldX, sy, worldZ) * warpAmp;
            float wz = _warpZ!.GetNoise(worldX, sy, worldZ) * warpAmp;

            float n    = _caveNoise!.GetNoise(worldX + wx, sy, worldZ + wz);
            float absN = Mathf.Abs(n);
            if (absN >= tunnelWidth) return 0f;
            return (tunnelWidth - absN) / tunnelWidth;
        }

        // ── Additive density ──────────────────────────────────────────────────────
        // Companion to Density: returns [0,1] for voxels that should GAIN solid.
        // Applied only to currently-air voxels — the warp-carve analogue in reverse.
        // Uses independent warp seeds so the add surface has a different pattern.
        static float AddDensity(float worldX, float worldY, float worldZ, WorldGenerator? wg)
        {
            if (!_useHeightmapAdd || wg == null) return 0f;
            InitNoise();
            const float hmAddWarpAmp = 80f;
            const float bandRadius   = 6f;
            float wx = _hmAddWarpX!.GetNoise(worldX, worldZ) * hmAddWarpAmp;
            float wz = _hmAddWarpZ!.GetNoise(worldX, worldZ) * hmAddWarpAmp;
            float addH = wg.GetHeight(worldX + wx, worldZ + wz);
            float dist = Mathf.Abs(worldY - addH);
            if (dist >= bandRadius) return 0f;
            return (bandRadius - dist) / bandRadius;
        }

        // ── Carve pass ────────────────────────────────────────────────────────────
        // Iterates every voxel in the zone. Skips voxels that are already air
        // (positive SDF). Calls Density() and writes air where it returns > 0.
        internal static void Carve(object field, int ox, int oz, int width, int yLo, int yHi)
        {
            if (_scale == null)
            {
                _scale = _scaleField != null ? (float)_scaleField.GetValue(null)! : 1f;
                Log.LogInfo($"[VoxCaves] s_scale resolved to {_scale}");
            }
            float s = _scale.Value;

            // Resolve WorldGenerator once per zone — avoids per-voxel singleton lookup
            // and lets us detect zones that fire before WG is ready (carved=0 diagnostic).
            var wg = WorldGenerator.instance;

            int carved = 0;
            var brickSet = new HashSet<(int, int, int)>();

            for (int ci = 0; ci < width; ci++)
            for (int ri = 0; ri < width; ri++)
            {
                int gx = ox + ci, gz = oz + ri;
                float worldX = gx * s, worldZ = gz * s;
                float floorY = ComputeColumnFloor(worldX, worldZ, wg);

                // ── Mode B: warped-heightmap carve ────────────────────────────────
                // We write a proper signed-distance field to BOTH sides of each carved
                // surface. Voxheim's mesher expects SDF values that encode the true
                // distance to the iso-surface — small positive inside, small negative
                // outside — so it can interpolate the surface to sub-voxel precision.
                // Writing only to the air side (the old approach) left adjacent solid
                // voxels at –127 regardless of their actual distance to our surface,
                // producing a step function across columns → terracing.
                //
                // bandSDF  = min(ceiling – worldY, worldY – floor): positive inside
                //            the band, negative outside, zero on the surface.
                // floorSDF = worldY – floorY: positive above terrain floor, negative
                //            below — limits carving depth without a hard gate.
                // Combined = min(bandSDF, floorSDF): intersection of both regions.
                // Normalised by 1/s * 127: 1 world-unit of distance → ±127.
                if (_useHeightmapWarp && wg != null)
                {
                    InitNoise();
                    const float hmWarpAmp = 80f, bandRadius = 6f;
                    float wx2    = _hmWarpX!.GetNoise(worldX, worldZ) * hmWarpAmp;
                    float wz2    = _hmWarpZ!.GetNoise(worldX, worldZ) * hmWarpAmp;
                    float warpedH = wg.GetHeight(worldX + wx2, worldZ + wz2)
                                  + _caveNoise!.GetNoise(worldX * 1.3f, worldZ * 1.3f) * 0.45f;
                    float hmC = warpedH + bandRadius;
                    float hmF = warpedH - bandRadius;

                    for (int gy = yLo; gy <= yHi; gy++)
                    {
                        float worldY  = gy * s;
                        float bandSDF = Mathf.Min(hmC - worldY, worldY - hmF);
                        float sdf     = Mathf.Min(bandSDF, worldY - floorY);
                        sbyte sdfByte = (sbyte)Mathf.Clamp(sdf / s * 127f, -127f, 127f);

                        sbyte cur = _get!(field, gx, gy, gz);
                        if (sdfByte > 0 && cur < 0)
                        {
                            _set!(field, gx, gy, gz, sdfByte);
                            brickSet.Add((gx >> 4, gy >> 4, gz >> 4));
                            carved++;
                        }
                        else if (sdfByte < 0 && sdfByte > cur)
                        {
                            // Raise the solid's SDF toward 0 — gives the mesher an
                            // accurate gradient on the outside of the carved surface.
                            _set!(field, gx, gy, gz, sdfByte);
                            brickSet.Add((gx >> 4, gy >> 4, gz >> 4));
                        }
                    }
                }

                // ── Mode A: noise-tunnel carve ────────────────────────────────────
                if (_useNoiseCaves)
                {
                    for (int gy = yLo; gy <= yHi; gy++)
                    {
                        float worldY = gy * s;
                        sbyte cur = _get!(field, gx, gy, gz);
                        if (cur >= 0) continue;
                        float d = Density(worldX, worldY, worldZ, floorY, wg);
                        if (d <= 0f) continue;
                        _set!(field, gx, gy, gz, (sbyte)Mathf.Clamp(d * 127f, 1f, 127f));
                        brickSet.Add((gx >> 4, gy >> 4, gz >> 4));
                        carved++;
                    }
                }

                // ── Mode C: warp-add (write solid into air) ───────────────────────
                if (_useHeightmapAdd && wg != null)
                {
                    for (int gy = yLo; gy <= yHi; gy++)
                    {
                        float worldY = gy * s;
                        sbyte cur = _get!(field, gx, gy, gz);
                        if (cur < 0) continue;
                        float d = AddDensity(worldX, worldY, worldZ, wg);
                        if (d <= 0f) continue;
                        _set!(field, gx, gy, gz, (sbyte)Mathf.Clamp(-d * 127f, -127f, -1f));
                        brickSet.Add((gx >> 4, gy >> 4, gz >> 4));
                        carved++;
                    }
                }
            }

            if (carved > 0)
                _pendingBricks[$"{ox},{oz}"] = brickSet;

            Log.LogInfo($"[VoxCaves] zone ({ox},{oz}) wg={wg != null} yLo={yLo} yHi={yHi} s={s:F3} carved={carved}");
        }

        // ── Placement height ──────────────────────────────────────────────────────
        // PlaceVegetation/PlaceLocations use WorldGenerator.GetHeight() for Y — the
        // raw noise surface, which ignores our voxel carving entirely. This function
        // finds the correct surface Y analytically by scanning downward from the
        // original height until we hit a voxel our density function would leave solid.
        internal static float FindCarvedSurfaceY(float worldX, float originalY, float worldZ)
        {
            // s_scale may not be initialized yet (Regenerate fires after PlaceVegetation);
            // fall back to 1f which is correct for this Voxheim install.
            float s = _scale is { } v && v > 0f ? v
                    : _scaleField != null && _scaleField.GetValue(null) is float sf && sf > 0f ? sf
                    : 1f;
            var wg = WorldGenerator.instance;
            float floorY = ComputeColumnFloor(worldX, worldZ, wg);
            float y = originalY;
            for (int i = 0; i < 256; i++, y -= s)
                if (Density(worldX, y, worldZ, floorY, wg) <= 0f) return y;
            return y;
        }
    }

    // ── Harmony patches ───────────────────────────────────────────────────────────

    // Postfix on HeightFieldVoxelizer.VoxelizeColumns — the point in Voxheim's
    // pipeline where the heightmap is converted to voxel SDF values.
    // We run after it fills the field, before Compact() and mesh build.
    [HarmonyPatch]
    static class VoxelizeColumnsPatch
    {
        static MethodBase? TargetMethod()
        {
            var t = Plugin.FindType("Voxheim.Core.HeightFieldVoxelizer", "Voxheim.Core");
            var m = t?.GetMethod("VoxelizeColumns", BindingFlags.Public | BindingFlags.Static);
            if (m == null) Plugin.Log.LogError("[VoxCaves] VoxelizeColumns not found — carving disabled.");
            return m;
        }

        static void Postfix(object field, int originX, int originZ,
                            int gx0, int gx1, int gz0, int gz1, int yLo, int yHi)
        {
            // Full zone init starts at column (0,0). Single-column terrain edits do not.
            if (gx0 != 0 || gz0 != 0) return;

            // No deduplication — Voxheim resets the SDF field from the heightmap every
            // time a zone reloads, so skipping the re-carve leaves it uncarved.

            try { Plugin.Carve(field, originX, originZ, gx1 + 1, yLo, yHi); }
            catch (Exception e) { Plugin.Log.LogError($"[VoxCaves] Carve error: {e.Message}"); }
        }
    }

    // Before VoxelZone.Complete() runs, inject our carved brick coordinates into
    // _editedBricks. Voxheim's own PokeTransitionNeighbors (called inside Complete())
    // then filters to boundary bricks and tells already-loaded neighbours to re-mesh —
    // exactly the same path vanilla edits take, no parallel seam-fix mechanism needed.
    [HarmonyPatch]
    static class CompletePatch
    {
        static MethodBase? TargetMethod()
        {
            var t = Plugin.FindType("Voxheim.Unity.VoxelZone", "Voxheim");
            return t?.GetMethod("Complete", BindingFlags.NonPublic | BindingFlags.Instance);
        }

        static void Prefix(object __instance)
        {
            if (Plugin._editedBricksField == null || Plugin._int3Ctor == null) return;
            if (Plugin._zoneOriginX == null || Plugin._zoneOriginZ == null) return;

            int ox = (int)Plugin._zoneOriginX.GetValue(__instance)!;
            int oz = (int)Plugin._zoneOriginZ.GetValue(__instance)!;
            if (!Plugin._pendingBricks.Remove($"{ox},{oz}", out var bricks)) return;

            var editedBricks = Plugin._editedBricksField.GetValue(__instance);
            if (editedBricks == null) return;

            // Cache Add method once — type is HashSet<Int3> but we don't have the type at compile time.
            Plugin._editedBricksAdd ??= editedBricks.GetType().GetMethod("Add");
            if (Plugin._editedBricksAdd == null) return;

            foreach (var (bx, by, bz) in bricks)
            {
                var i3 = Plugin._int3Ctor.Invoke(new object[] { bx, by, bz });
                Plugin._editedBricksAdd.Invoke(editedBricks, new[] { i3 });
            }
        }
    }

    // Clear state on world load so a fresh session carves every zone again.
    [HarmonyPatch(typeof(ZNet), nameof(ZNet.SetServer))]
    static class WorldLoadPatch
    {
        static void Postfix()
        {
            Plugin._pendingBricks.Clear();
            Plugin._caveNoise = null;
            Plugin._warpX     = null;
            Plugin._warpZ     = null;
            Plugin._hmWarpX    = null;
            Plugin._hmWarpZ    = null;
            Plugin._hmAddWarpX = null;
            Plugin._hmAddWarpZ = null;
        }
    }

    // Postfix on ZoneSystem.PlaceVegetation — fires after both PlaceLocations and
    // PlaceVegetation have run (both write to the same spawnedObjects list).
    // We correct every spawned object's Y to match our analytically carved surface,
    // then persist the new position into the ZDO so it survives ghost-mode destruction.
    [HarmonyPatch]
    static class PlaceVegetationPatch
    {
        static FieldInfo? _zdoPosField;

        static MethodBase? TargetMethod() =>
            typeof(ZoneSystem).GetMethod("PlaceVegetation", BindingFlags.NonPublic | BindingFlags.Instance);

        static void Postfix(List<GameObject> spawnedObjects)
        {
            foreach (var go in spawnedObjects)
            {
                if (go == null) continue;
                var pos = go.transform.position;
                float newY = Plugin.FindCarvedSurfaceY(pos.x, pos.y, pos.z);
                if (Mathf.Abs(newY - pos.y) < 0.05f) continue;
                pos.y = newY;
                go.transform.position = pos;
                var zdo = go.GetComponent<ZNetView>()?.GetZDO();
                if (zdo == null) continue;
                if (_zdoPosField == null)
                    _zdoPosField = typeof(ZDO).GetField("m_position",
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                _zdoPosField?.SetValue(zdo, pos);
            }
        }
    }
}
