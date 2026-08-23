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
    public partial class Plugin
    {
        // ── Type lookup ───────────────────────────────────────────────────────────
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
        static bool BuildDelegates()
        {
            var fieldT = FindType("Voxheim.Core.VoxelField", "Voxheim.Core");
            var int3T  = FindType("Voxheim.Core.Int3",       "Voxheim.Core");
            var zoneT  = FindType("Voxheim.Unity.VoxelZone", "Voxheim");

            if (fieldT == null || int3T == null)
            {
                Log.LogError("[ProceduralTerrain] VoxelField or Int3 type not found.");
                return false;
            }

            _int3Ctor = int3T.GetConstructor(new[] { typeof(int), typeof(int), typeof(int) });
            var get = fieldT.GetMethod("Get", BindingFlags.Public | BindingFlags.Instance,
                          null, new[] { int3T }, null);
            var set = fieldT.GetMethod("Set", BindingFlags.Public | BindingFlags.Instance,
                          null, new[] { int3T, typeof(sbyte) }, null);

            if (_int3Ctor == null || get == null || set == null)
            {
                Log.LogError("[ProceduralTerrain] Int3 ctor / Get / Set not found.");
                return false;
            }

            _scaleField = zoneT?.GetField("s_scale",
                BindingFlags.NonPublic | BindingFlags.Static);
            if (_scaleField == null) Log.LogWarning("[ProceduralTerrain] s_scale not found, will assume 1.");

            _enqueueMeshMethod   = zoneT?.GetMethod("EnqueueMesh",
                BindingFlags.NonPublic | BindingFlags.Instance);
            _finishSkinNowMethod = zoneT?.GetMethod("FinishSkinNow",
                BindingFlags.NonPublic | BindingFlags.Instance);
            if (_enqueueMeshMethod   == null) Log.LogWarning("[ProceduralTerrain] EnqueueMesh not found.");
            if (_finishSkinNowMethod == null) Log.LogWarning("[ProceduralTerrain] FinishSkinNow not found.");

            _zoneOriginX         = zoneT?.GetField("_originX",
                BindingFlags.NonPublic | BindingFlags.Instance);
            _zoneOriginZ         = zoneT?.GetField("_originZ",
                BindingFlags.NonPublic | BindingFlags.Instance);
            _yScanHiField        = zoneT?.GetField("_yScanHi",
                BindingFlags.NonPublic | BindingFlags.Instance);
            if (_zoneOriginX == null || _zoneOriginZ == null)
                Log.LogWarning("[ProceduralTerrain] Zone origin fields missing.");
            if (_yScanHiField == null)
                Log.LogWarning("[ProceduralTerrain] _yScanHi not found — Mode C ceiling extension disabled.");

            _locationInstancesField =
                typeof(ZoneSystem).GetField("m_locationInstances",
                    BindingFlags.NonPublic | BindingFlags.Instance) ??
                typeof(ZoneSystem).GetField("m_locationInstances",
                    BindingFlags.Public    | BindingFlags.Instance);
            if (_locationInstancesField == null)
                Log.LogWarning("[ProceduralTerrain] m_locationInstances not found — location protection off.");

            _int3FieldX = int3T.GetField("X", BindingFlags.Public | BindingFlags.Instance)
                          ?? int3T.GetField("x", BindingFlags.Public | BindingFlags.Instance);
            _int3FieldY = int3T.GetField("Y", BindingFlags.Public | BindingFlags.Instance)
                          ?? int3T.GetField("y", BindingFlags.Public | BindingFlags.Instance);
            _int3FieldZ = int3T.GetField("Z", BindingFlags.Public | BindingFlags.Instance)
                          ?? int3T.GetField("z", BindingFlags.Public | BindingFlags.Instance);
            if (_int3FieldX == null || _int3FieldY == null || _int3FieldZ == null)
                Log.LogWarning("[ProceduralTerrain] Int3 X/Y/Z fields not found — FallbackDensity patch will use vanilla.");

            // HeightGrid delegates — Scale property and [x,z] indexer
            var gridT      = FindType("Voxheim.Core.HeightGrid", "Voxheim.Core");
            var gridGet    = gridT?.GetMethod("get_Item",
                BindingFlags.Public | BindingFlags.Instance,
                null, new[] { typeof(int), typeof(int) }, null);
            var gridScaleP = gridT?.GetProperty("Scale", BindingFlags.Public | BindingFlags.Instance);
            if (gridT != null && gridGet != null)
            {
                var gdm = new DynamicMethod("HG_Get", typeof(float),
                    new[] { typeof(object), typeof(int), typeof(int) },
                    typeof(Plugin).Module, skipVisibility: true);
                var ggi = gdm.GetILGenerator();
                ggi.Emit(OpCodes.Ldarg_0); ggi.Emit(OpCodes.Castclass, gridT);
                ggi.Emit(OpCodes.Ldarg_1); ggi.Emit(OpCodes.Ldarg_2);
                ggi.Emit(OpCodes.Callvirt, gridGet);
                ggi.Emit(OpCodes.Ret);
                _gridGet = (Func<object,int,int,float>)gdm.CreateDelegate(typeof(Func<object,int,int,float>));
            }
            else { Log.LogWarning("[ProceduralTerrain] HeightGrid.get_Item not found — pt_probe_grid unavailable."); }
            if (gridT != null && gridScaleP?.GetMethod != null)
            {
                var sdm = new DynamicMethod("HG_Scale", typeof(float),
                    new[] { typeof(object) }, typeof(Plugin).Module, skipVisibility: true);
                var sgi = sdm.GetILGenerator();
                sgi.Emit(OpCodes.Ldarg_0); sgi.Emit(OpCodes.Castclass, gridT);
                sgi.Emit(OpCodes.Callvirt, gridScaleP.GetMethod);
                sgi.Emit(OpCodes.Ret);
                _gridScale = (Func<object,float>)sdm.CreateDelegate(typeof(Func<object,float>));
                Log.LogInfo("[ProceduralTerrain] HeightGrid.Scale delegate ready — scale read from grid, no s_scale dependency.");
            }
            else { Log.LogWarning("[ProceduralTerrain] HeightGrid.Scale not found — falling back to s_scale."); }

            return true;
        }
    }

    // ── Harmony patches ───────────────────────────────────────────────────────────

    [HarmonyPatch]
    // AUTHOR NOTE FOR VOXHEIM — API gaps this patch works around:
    //
    // 1. Y EXTENT DECLARATION: Our generator writes solid voxels ABOVE the vanilla
    //    heightmap surface (Mode C — overhangs, arches). Voxheim finalizes the zone's
    //    render extent (_yScanHi) before the generator runs, so that geometry is clipped.
    //    We patch Complete() to bump _yScanHi to our actual max Y after the fact.
    //    Ideally IVoxelBaselineGenerator would let us declare a Y extent upfront so
    //    Voxheim allocates the correct range before finalizing.
    //
    // 2. LIVE ZONE REGENERATION: When the user changes terrain config at runtime, we need
    //    to re-run the generator on already-active zones so changes are visible immediately
    //    without a full reload. No API for this exists today, so hot-reload only clears
    //    caches — zones pick up the new config on next unload/reload cycle.
    //
    // 3. LOCATION TERRAIN FLATTENING: Voxheim patches TerrainOp.Awake to route all
    //    terrain ops (level/raise/smooth) through the authority system as voxel deltas,
    //    suppressing direct heightmap modification. This means hmap.m_heights — and
    //    therefore context.Heights passed to the generator — contains only raw world
    //    generator heights with no terrain ops applied. Location flattening arrives as
    //    authority deltas on top of the baseline, after the generator has already run.
    //    Our location-protection fader suppresses PT's deviation from vanilla heights,
    //    but the reference surface at a location is unflattened world terrain, not the
    //    flat floor the location expects. The generator has no way to know where terrain
    //    ops will be applied, so it cannot account for location footprints upfront.
    //    A possible solution: expose pending TerrainOperationIntents (or their baked
    //    result) as part of VoxelBaselineContext so generators can treat those areas
    //    as authority-owned and leave them alone.
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
            if (Plugin._zoneOriginX == null || Plugin._zoneOriginZ == null) return;
            int ox = (int)Plugin._zoneOriginX.GetValue(__instance)!;
            int oz = (int)Plugin._zoneOriginZ.GetValue(__instance)!;
            string key = $"{ox},{oz}";

            Plugin._zoneObjects[key] = __instance;

            if (Plugin._yScanHiField != null && Plugin._zoneMaxY.TryGetValue(key, out int maxY))
            {
                int cur = (int)Plugin._yScanHiField.GetValue(__instance)!;
                if (maxY > cur) Plugin._yScanHiField.SetValue(__instance, maxY);
                Plugin._zoneMaxY.Remove(key);
            }
        }
    }

    [HarmonyPatch]
    static class VoxelZoneDestroyPatch
    {
        static MethodBase? TargetMethod()
        {
            var t = Plugin.FindType("Voxheim.Unity.VoxelZone", "Voxheim");
            return t?.GetMethod("OnDestroy", BindingFlags.NonPublic | BindingFlags.Instance);
        }

        static void Postfix(object __instance)
        {
            if (Plugin._zoneOriginX == null || Plugin._zoneOriginZ == null) return;
            int ox = (int)Plugin._zoneOriginX.GetValue(__instance)!;
            int oz = (int)Plugin._zoneOriginZ.GetValue(__instance)!;
            string key = $"{ox},{oz}";
            Plugin._zoneParams.Remove(key);
            Plugin._sdfCache.Remove(key);
            Plugin._zoneObjects.Remove(key);
            Plugin._zoneMaxY.Remove(key);
            Plugin._zoneExtHi.Remove(key);
            lock (Plugin._surfHCache) Plugin._surfHCache.Remove((ox, oz));
        }
    }

    // Patches StitchedSource.FallbackDensity (private nested class inside VoxelZone).
    // FallbackDensity is called when a boundary voxel's zone is absent or inactive in s_zones.
    // We return our own SDF so boundary bricks are built correctly on first mesh — no post-hoc repair needed.
    [HarmonyPatch]
    static class FallbackDensityPatch
    {
        static MethodBase? TargetMethod()
        {
            var zoneT     = Plugin.FindType("Voxheim.Unity.VoxelZone", "Voxheim");
            var stitchedT = zoneT?.GetNestedType("StitchedSource", BindingFlags.NonPublic);
            var m         = stitchedT?.GetMethod("FallbackDensity", BindingFlags.NonPublic | BindingFlags.Instance);
            if (m == null) Plugin.Log.LogWarning("[ProceduralTerrain] FallbackDensity not found — cross-zone boundary density will be vanilla.");
            return m;
        }

        // __0 is the Int3 voxel parameter, boxed because we can't reference the type at compile time.
        static bool Prefix(ref sbyte __result, object __0)
        {
            if (Plugin._int3FieldX == null || Plugin._int3FieldY == null || Plugin._int3FieldZ == null)
                return true;
            int vx = (int)Plugin._int3FieldX.GetValue(__0)!;
            int vy = (int)Plugin._int3FieldY.GetValue(__0)!;
            int vz = (int)Plugin._int3FieldZ.GetValue(__0)!;
            if (Plugin.TryComputePointSDF(vx, vy, vz, out sbyte r))
            {
                __result = r;
                return false;
            }
            return true;
        }
    }

    // ── Voxheim IVoxelBaselineGenerator ──────────────────────────────────────────
    sealed class PTGenerator : Voxheim.Core.IVoxelBaselineGenerator
    {
        public string Id       => "com.ctogle.procedural_terrain";
        public string Revision => "1";

        public void Generate(Voxheim.Core.VoxelBaselineContext context, Voxheim.Core.VoxelBaselineBuilder builder)
        {
            int ox    = context.OriginX;
            int oz    = context.OriginZ;
            int width = context.Width;

            builder.SetFloor(context.Floor);

            string zoneKey = $"{ox},{oz}";
            if (!Plugin._sdfCache.TryGetValue(zoneKey, out var c))
            {
                float gridS = Plugin._gridScale != null ? Plugin._gridScale(context.Heights) : 0f;
                try { Plugin.Carve(context.Heights, ox, oz, width, context.InitialScanLo, context.InitialScanHi, gridS); }
                catch (Exception e) { Plugin.Log.LogError($"[PT] Carve ({ox},{oz}): {e.Message}"); return; }
                if (!Plugin._sdfCache.TryGetValue(zoneKey, out c)) return;
            }

            for (int ci = 0; ci < width; ci++)
            for (int ri = 0; ri < width; ri++)
            {
                int wx = ox + ci, wz = oz + ri;
                for (int cy = 0; cy < c.bh; cy++)
                {
                    sbyte d = c.data[(ci * c.bh + cy) * c.width + ri];
                    int   wy = c.extLo + cy;
                    builder.Set(new Voxheim.Core.Int3(wx, wy, wz), d);
                }
            }
        }
    }

} // namespace ProceduralTerrain
