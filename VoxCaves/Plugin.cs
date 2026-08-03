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

        // Zones we have already carved. Prevents re-carving on column-update
        // calls to VoxelizeColumns (player terrain edits) and zone re-activation.
        internal static readonly HashSet<string> _done = new();

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

        // ── Density function ──────────────────────────────────────────────────────
        // Returns a value in [0, 1].
        //   0   → leave voxel as-is
        //   > 0 → carve to air; the value becomes the SDF magnitude written
        //
        // worldX/Y/Z are Valheim world-space coordinates (metres).
        // This is the only function you need to change to alter cave shape.
        //
        // v1: horizontal sine bands — proves the hook is working and modifications
        // are globally seamless (same value at any world position, no zone artefacts).
        static float Density(float worldX, float worldY, float worldZ)
        {
            const float period = 8f;
            float vy = Mathf.Sin(worldY * (Mathf.PI * 2f / period));
            float vx = Mathf.Sin(worldX * (Mathf.PI * 2f / period));
            float v = vy * vx; // positive where both waves peak together → grid of pockets
            return v > 0.2f ? v : 0f;
        }

        // ── Carve pass ────────────────────────────────────────────────────────────
        // Iterates every voxel in the zone. Skips voxels that are already air
        // (positive SDF). Calls Density() and writes air where it returns > 0.
        internal static void Carve(object field, int ox, int oz, int width, int yLo, int yHi)
        {
            // Read s_scale here, not at startup — Voxheim initializes it after plugin load.
            if (_scale == null)
            {
                _scale = _scaleField != null ? (float)_scaleField.GetValue(null)! : 1f;
                Log.LogInfo($"[VoxCaves] s_scale resolved to {_scale}");
            }
            float s = _scale.Value;
            int carved = 0;
            var brickSet = new HashSet<(int, int, int)>();

            for (int ci = 0; ci < width; ci++)
            for (int ri = 0; ri < width; ri++)
            {
                int gx = ox + ci, gz = oz + ri;
                for (int gy = yLo; gy <= yHi; gy++)
                {
                    if (_get!(field, gx, gy, gz) >= 0) continue; // already air — skip

                    float d = Density(gx * s, gy * s, gz * s);
                    if (d <= 0f) continue;

                    _set!(field, gx, gy, gz, (sbyte)Mathf.Clamp(d * 127f, 1f, 127f));
                    brickSet.Add((gx >> 4, gy >> 4, gz >> 4));
                    carved++;
                }
            }

            if (carved > 0)
                _pendingBricks[$"{ox},{oz}"] = brickSet;

            Log.LogInfo($"[VoxCaves] zone ({ox},{oz}) carved={carved}");
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
            float y = originalY;
            for (int i = 0; i < 256; i++, y -= s)
                if (Density(worldX, y, worldZ) <= 0f) return y;
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

            // Deduplicate: skip if we already carved this zone this session.
            if (!Plugin._done.Add($"{originX},{originZ}")) return;

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
            Plugin._done.Clear();
            Plugin._pendingBricks.Clear();
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
