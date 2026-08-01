using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEngine;
using YamlDotNet.Serialization;

namespace VoxCaves
{
    [BepInPlugin("com.ctogle.voxcaves", "VoxCaves", "0.1.0")]
    [BepInDependency("com.bogey.voxheim", BepInDependency.DependencyFlags.SoftDependency)]
    [BepInDependency("ValheimModding.YamlDotNet", BepInDependency.DependencyFlags.HardDependency)]
    public class Plugin : BaseUnityPlugin
    {
        internal static ManualLogSource Log = null!;
        internal static VoxCavesConfig Cfg = VoxCavesConfig.Default();

        // Reflected Voxheim internals — resolved once on startup if Voxheim is present
        static bool _voxheimPresent;
        static FieldInfo? _fieldInfo;       // VoxelZone._field  (VoxelField)
        static MethodInfo? _getMethod;      // VoxelField.Get(Int3) : sbyte
        static MethodInfo? _setMethod;      // VoxelField.Set(Int3, sbyte)
        static MethodInfo? _bricksMethod;   // VoxelField.Bricks : IEnumerable<KeyValuePair<Int3,Brick>>
        static Type? _int3Type;

        // Zones we've already processed this session (world-pos key)
        static readonly HashSet<string> _carved = new HashSet<string>();

        // World-seed-derived noise offset, set on world load
        static int _seedOffset;

        void Awake()
        {
            Log = Logger;
            LoadConfig();
            _voxheimPresent = BepInEx.Bootstrap.Chainloader.PluginInfos.ContainsKey("com.bogey.voxheim");
            if (!_voxheimPresent)
            {
                Log.LogInfo("[VoxCaves] Voxheim not present — disabled.");
                return;
            }
            if (!ResolveVoxheimReflection())
            {
                Log.LogError("[VoxCaves] Failed to resolve Voxheim internals — disabled.");
                return;
            }
            new Harmony("com.ctogle.voxcaves").PatchAll();
            Log.LogInfo("[VoxCaves] Loaded.");
        }

        static void LoadConfig()
        {
            string path = Path.Combine(Paths.ConfigPath, "voxcaves.yaml");
            if (!File.Exists(path))
            {
                var serializer = new SerializerBuilder().Build();
                File.WriteAllText(path, serializer.Serialize(VoxCavesConfig.Default()));
                return;
            }
            try
            {
                var des = new DeserializerBuilder().IgnoreUnmatchedProperties().Build();
                Cfg = des.Deserialize<VoxCavesConfig>(File.ReadAllText(path)) ?? VoxCavesConfig.Default();
            }
            catch (Exception e)
            {
                Log.LogWarning($"[VoxCaves] Config parse error: {e.Message} — using defaults.");
            }
        }

        static bool ResolveVoxheimReflection()
        {
            try
            {
                var voxZoneType  = Type.GetType("Voxheim.VoxelZone, Voxheim");
                var voxFieldType = Type.GetType("Voxheim.Core.VoxelField, Voxheim.Core");
                _int3Type        = Type.GetType("Voxheim.Core.Int3, Voxheim.Core");
                if (voxZoneType == null || voxFieldType == null || _int3Type == null) return false;

                _fieldInfo   = voxZoneType.GetField("_field",  BindingFlags.NonPublic | BindingFlags.Instance);
                _getMethod   = voxFieldType.GetMethod("Get",   BindingFlags.Public | BindingFlags.Instance);
                _setMethod   = voxFieldType.GetMethod("Set",   BindingFlags.Public | BindingFlags.Instance);
                var bricksProp = voxFieldType.GetProperty("Bricks", BindingFlags.Public | BindingFlags.Instance);
                _bricksMethod = bricksProp?.GetGetMethod();

                return _fieldInfo != null && _getMethod != null && _setMethod != null && _bricksMethod != null;
            }
            catch (Exception e)
            {
                Log.LogError($"[VoxCaves] Reflection error: {e}");
                return false;
            }
        }

        // Called by ZNet on world load — seed is stable per world
        internal static void OnWorldLoad()
        {
            _carved.Clear();
            int seed = ZNet.instance?.GetWorldUID() ?? 0;
            _seedOffset = seed * 2654435761; // Knuth multiplicative hash
            Log.LogDebug($"[VoxCaves] World seed offset: {_seedOffset}");
        }

        // Entry point called from Harmony patch after Voxheim skins a zone
        internal static void ProcessZone(object voxelZone, Vector3 zoneOrigin)
        {
            if (!Cfg.Enabled) return;

            string key = $"{Mathf.RoundToInt(zoneOrigin.x)},{Mathf.RoundToInt(zoneOrigin.z)}";
            if (_carved.Contains(key)) return;
            _carved.Add(key);

            object? field = _fieldInfo!.GetValue(voxelZone);
            if (field == null) return;

            // Collect all brick coords so we know the voxel domain
            var bricks = (System.Collections.IEnumerable)_bricksMethod!.Invoke(field, null)!;

            // Gather voxel positions that are currently solid
            var solidVoxels = new List<(object int3, int wx, int wy, int wz)>();
            foreach (var kvp in bricks)
            {
                // kvp is KeyValuePair<Int3, Brick> — get key (Int3 brick coord)
                var brickCoord = ((System.Collections.DictionaryEntry)(object)kvp).Key; // fallback; see below
                // Actually iterate via reflection on the KVP type
                var kvpType = kvp.GetType();
                var brickInt3 = kvpType.GetProperty("Key")!.GetValue(kvp)!;
                // Brick coord is in voxel space (each brick is 16 voxels)
                int bx = (int)_int3Type!.GetField("X")!.GetValue(brickInt3)!;
                int by = (int)_int3Type.GetField("Y")!.GetValue(brickInt3)!;
                int bz = (int)_int3Type.GetField("Z")!.GetValue(brickInt3)!;
                for (int lx = 0; lx < 16; lx++)
                for (int ly = 0; ly < 16; ly++)
                for (int lz = 0; lz < 16; lz++)
                {
                    int wx = bx * 16 + lx;
                    int wy = by * 16 + ly;
                    int wz = bz * 16 + lz;
                    var voxelCoord = MakeInt3(wx, wy, wz);
                    sbyte val = (sbyte)_getMethod!.Invoke(field, new[] { voxelCoord })!;
                    if (val > 0) // positive = solid
                        solidVoxels.Add((voxelCoord, wx, wy, wz));
                }
            }

            if (solidVoxels.Count == 0) return;

            // Find height range of solid terrain in this zone
            int minSolidY = int.MaxValue, maxSolidY = int.MinValue;
            foreach (var (_, _, wy, _) in solidVoxels) { minSolidY = Math.Min(minSolidY, wy); maxSolidY = Math.Max(maxSolidY, wy); }

            float scale = 1f; // Voxheim default: 1 voxel = 1 metre
            int carveCount = 0;

            foreach (var (voxelCoord, wx, wy, wz) in solidVoxels)
            {
                // Depth from surface — only carve below MinDepth
                int depthFromTop = maxSolidY - wy;
                if (depthFromTop < Cfg.MinDepth || depthFromTop > Cfg.MaxDepth) continue;

                // World-space position for noise (continuous across zone boundaries)
                float fx = wx * scale;
                float fy = wy * scale;
                float fz = wz * scale;

                if (ShouldCarve(fx, fy, fz))
                {
                    _setMethod!.Invoke(field, new object[] { voxelCoord, (sbyte)(-127) });
                    carveCount++;
                }
            }

            if (carveCount > 0)
                Log.LogDebug($"[VoxCaves] Zone {key}: carved {carveCount} voxels.");
        }

        static bool ShouldCarve(float x, float y, float z)
        {
            // Multi-octave 3D value noise — coherent across zone boundaries
            float n = 0f;
            float amp = 1f;
            float freq = Cfg.Frequency;
            float totalAmp = 0f;
            int seed = _seedOffset;

            for (int o = 0; o < Cfg.Octaves; o++)
            {
                n += ValueNoise3D(x * freq + seed, y * freq * Cfg.VerticalScale, z * freq + seed) * amp;
                totalAmp += amp;
                amp   *= Cfg.Persistence;
                freq  *= 2f;
                seed  += 1234567;
            }
            n /= totalAmp; // normalize to [-1, 1]

            // Carve where noise exceeds threshold
            return n > Cfg.Threshold;
        }

        // Smooth value noise (no external library needed)
        static float ValueNoise3D(float x, float y, float z)
        {
            int ix = Mathf.FloorToInt(x), iy = Mathf.FloorToInt(y), iz = Mathf.FloorToInt(z);
            float fx = x - ix, fy = y - iy, fz = z - iz;
            float ux = Smooth(fx), uy = Smooth(fy), uz = Smooth(fz);

            float v000 = Hash(ix,   iy,   iz);
            float v100 = Hash(ix+1, iy,   iz);
            float v010 = Hash(ix,   iy+1, iz);
            float v110 = Hash(ix+1, iy+1, iz);
            float v001 = Hash(ix,   iy,   iz+1);
            float v101 = Hash(ix+1, iy,   iz+1);
            float v011 = Hash(ix,   iy+1, iz+1);
            float v111 = Hash(ix+1, iy+1, iz+1);

            return Mathf.Lerp(
                Mathf.Lerp(Mathf.Lerp(v000, v100, ux), Mathf.Lerp(v010, v110, ux), uy),
                Mathf.Lerp(Mathf.Lerp(v001, v101, ux), Mathf.Lerp(v011, v111, ux), uy),
                uz) * 2f - 1f; // remap [0,1] → [-1,1]
        }

        static float Smooth(float t) => t * t * t * (t * (t * 6f - 15f) + 10f);

        static float Hash(int x, int y, int z)
        {
            uint h = (uint)(x * 1619 + y * 31337 + z * 6971 + _seedOffset * 1013904223);
            h ^= h >> 16; h *= 0x45d9f3b; h ^= h >> 16;
            return (h & 0xFFFF) / 65535f;
        }

        static object MakeInt3(int x, int y, int z)
        {
            var inst = Activator.CreateInstance(_int3Type!);
            _int3Type.GetField("X")!.SetValue(inst, x);
            _int3Type.GetField("Y")!.SetValue(inst, y);
            _int3Type.GetField("Z")!.SetValue(inst, z);
            return inst!;
        }
    }

    // ── Harmony patches ──────────────────────────────────────────────────────

    [HarmonyPatch]
    static class VoxelZoneActivatePatch
    {
        static MethodBase? TargetMethod()
        {
            var t = Type.GetType("Voxheim.VoxelZone, Voxheim");
            // Patch FinishSkinNow — called after Activate, when the field is fully built
            return t?.GetMethod("FinishSkinNow", BindingFlags.NonPublic | BindingFlags.Instance);
        }

        static void Prefix(object __instance)
        {
            try
            {
                var go = (__instance as Component)?.gameObject;
                if (go == null) return;
                Plugin.ProcessZone(__instance, go.transform.position);
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"[VoxCaves] ProcessZone error: {e}");
            }
        }
    }

    [HarmonyPatch(typeof(ZNet), nameof(ZNet.SetServer))]
    static class WorldLoadPatch
    {
        static void Postfix() => Plugin.OnWorldLoad();
    }

    // ── Config ───────────────────────────────────────────────────────────────

    public class VoxCavesConfig
    {
        public bool  Enabled       { get; set; } = true;
        public float Frequency     { get; set; } = 0.05f;  // noise frequency — lower = larger caves
        public float Threshold     { get; set; } = 0.55f;  // carve where noise > this (0–1 range)
        public int   Octaves       { get; set; } = 3;      // noise octaves for detail
        public float Persistence   { get; set; } = 0.5f;   // amplitude falloff per octave
        public float VerticalScale { get; set; } = 0.5f;   // squash noise vertically for horizontal caves
        public int   MinDepth      { get; set; } = 8;      // min voxels below terrain surface to carve
        public int   MaxDepth      { get; set; } = 60;     // max voxels below surface

        public static VoxCavesConfig Default() => new VoxCavesConfig();
    }
}
