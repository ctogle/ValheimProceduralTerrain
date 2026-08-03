using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Reflection.Emit;
using UnityEngine;

namespace VoxCaves
{
    // All Mode-B parameters for one biome. Floats throughout so biome-boundary
    // blending is a plain weighted average of two profile values.
    struct CaveProfile
    {
        public float strength;   // 0 = no carving, 1 = full
        public float bandRadius; // half-thickness of carved band (world units)
        public float warpAmp;    // XZ warp displacement (world units); 0 = actual terrain surface
        public float ditherAmp;  // per-column surface-noise offset (world units)

        public static readonly CaveProfile Default =
            new CaveProfile { strength = 1f, bandRadius = 6f, warpAmp = 80f, ditherAmp = 0.45f };
    }

    [BepInPlugin("com.ctogle.voxcaves", "VoxCaves", "0.2.0")]
    [BepInDependency("com.bogey.voxheim", BepInDependency.DependencyFlags.SoftDependency)]
    public class Plugin : BaseUnityPlugin
    {
        internal static ManualLogSource Log = null!;

        // Zero-alloc voxel access via compiled IL — see BuildDelegates().
        internal static Func<object, int, int, int, sbyte>?   _get;
        internal static Action<object, int, int, int, sbyte>? _set;

        // World units per voxel — read lazily, Voxheim initialises it after plugin load.
        static FieldInfo? _scaleField;
        static float? _scale;

        // ── Mode gates ────────────────────────────────────────────────────────────
        static bool _useNoiseCaves    = false; // domain-warped tunnel network (Mode A)
        static bool _useHeightmapWarp = true;  // warped-heightmap carve (Mode B)
        static bool _useHeightmapAdd  = false; // warped-heightmap add   (Mode C)

        // ── Biome profiles ────────────────────────────────────────────────────────
        // Volatile: the FileSystemWatcher callback writes this from a background
        // thread; the main thread reads it in BlendProfile() during Carve().
        static volatile Dictionary<Heightmap.Biome, CaveProfile> _biomeProfiles
            = BuildDefaultProfiles();
        static volatile bool _hotReloadPending;
        static FileSystemWatcher? _configWatcher;

        // ── Location protection ───────────────────────────────────────────────────
        // Positions of all placed ZoneSystem locations, populated once per world load.
        // Used to fade warpAmp → 0 and SDF → 0 near spawned structures.
        internal static readonly List<Vector2> _locationPositions = new();
        internal static bool _locationsCached;
        const float LocProtectR = 30f; // full-zero radius around location centres
        const float LocFadeR    = 40f; // distance over which protection fades to 1

        // ── Zone tracking for hot-reload ──────────────────────────────────────────
        // On hot-reload: call VoxelizeColumns on the stored field — Voxheim resets
        // the baseline SDF, our postfix re-carves with the new profiles, then Complete()
        // rebuilds the mesh. This uses the legitimate pipeline instead of patching SDF
        // ourselves and trying to force a rebuild from the outside.
        internal static readonly Dictionary<string, (object field, object grid, int ox, int oz, int w, int yLo, int yHi)>
            _zoneParams = new();
        internal static readonly Dictionary<string, object> _zoneObjects = new();
        internal static MethodInfo? _completeMethod;         // VoxelZone.Complete
        internal static MethodInfo? _afterFieldEditMethod;   // VoxelZone.AfterFieldEdit (diagnostic)
        internal static MethodInfo? _enqueueMeshMethod;      // VoxelZone.EnqueueMesh
        internal static MethodInfo? _finishSkinNowMethod;    // VoxelZone.FinishSkinNow
        internal static MethodInfo? _voxelizeColumnsMethod;  // HeightFieldVoxelizer.VoxelizeColumns

        // ── Noise ─────────────────────────────────────────────────────────────────
        internal static FastNoiseLite? _caveNoise;
        internal static FastNoiseLite? _warpX;
        internal static FastNoiseLite? _warpZ;
        internal static FastNoiseLite? _hmWarpX;
        internal static FastNoiseLite? _hmWarpZ;
        internal static FastNoiseLite? _hmAddWarpX;
        internal static FastNoiseLite? _hmAddWarpZ;

        // ── Seam fix ──────────────────────────────────────────────────────────────
        internal static ConstructorInfo? _int3Ctor;
        internal static FieldInfo?       _zoneOriginX;
        internal static FieldInfo?       _zoneOriginZ;
        internal static FieldInfo?       _editedBricksField;
        internal static MethodInfo?      _editedBricksAdd;
        internal static readonly Dictionary<string, HashSet<(int, int, int)>> _pendingBricks = new();

        // ZoneSystem.m_locationInstances — reflected in BuildDelegates().
        static FieldInfo? _locationInstancesField;

        // ─────────────────────────────────────────────────────────────────────────

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
            LoadBiomeConfig();
            WatchBiomeConfig();
            new Harmony("com.ctogle.voxcaves").PatchAll();
            Log.LogInfo("[VoxCaves] Ready.");
        }

        // Called every frame on the main thread — checks for pending hot-reload.
        void Update()
        {
            if (!_hotReloadPending) return;
            _hotReloadPending = false;
            LoadBiomeConfig();
            RecarveAll();
        }

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
                Log.LogError("[VoxCaves] VoxelField or Int3 type not found.");
                return false;
            }

            _int3Ctor = int3T.GetConstructor(new[] { typeof(int), typeof(int), typeof(int) });
            var get = fieldT.GetMethod("Get", BindingFlags.Public | BindingFlags.Instance,
                          null, new[] { int3T }, null);
            var set = fieldT.GetMethod("Set", BindingFlags.Public | BindingFlags.Instance,
                          null, new[] { int3T, typeof(sbyte) }, null);

            if (_int3Ctor == null || get == null || set == null)
            {
                Log.LogError("[VoxCaves] Int3 ctor / Get / Set not found.");
                return false;
            }

            _scaleField = zoneT?.GetField("s_scale",
                BindingFlags.NonPublic | BindingFlags.Static);
            if (_scaleField == null) Log.LogWarning("[VoxCaves] s_scale not found, will assume 1.");

            // AfterFieldEdit is what Voxheim calls after player terrain edits —
            // it enqueues mesh rebuilds for the affected bricks.
            _afterFieldEditMethod = zoneT?.GetMethod("AfterFieldEdit",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (_afterFieldEditMethod != null)
            {
                var parms = _afterFieldEditMethod.GetParameters();
                Log.LogInfo($"[VoxCaves] AfterFieldEdit params: " +
                    string.Join(", ", System.Linq.Enumerable.Select(parms,
                        p => $"{p.ParameterType.Name} {p.Name}")));
            }
            else Log.LogWarning("[VoxCaves] AfterFieldEdit not found.");

            _enqueueMeshMethod   = zoneT?.GetMethod("EnqueueMesh",
                BindingFlags.NonPublic | BindingFlags.Instance);
            _finishSkinNowMethod = zoneT?.GetMethod("FinishSkinNow",
                BindingFlags.NonPublic | BindingFlags.Instance);
            if (_enqueueMeshMethod   == null) Log.LogWarning("[VoxCaves] EnqueueMesh not found.");
            if (_finishSkinNowMethod == null) Log.LogWarning("[VoxCaves] FinishSkinNow not found.");

            _zoneOriginX       = zoneT?.GetField("_originX",
                BindingFlags.NonPublic | BindingFlags.Instance);
            _zoneOriginZ       = zoneT?.GetField("_originZ",
                BindingFlags.NonPublic | BindingFlags.Instance);
            _editedBricksField = zoneT?.GetField("_editedBricks",
                BindingFlags.NonPublic | BindingFlags.Instance);
            _completeMethod    = zoneT?.GetMethod("Complete",
                BindingFlags.NonPublic | BindingFlags.Instance);
            if (_zoneOriginX == null || _zoneOriginZ == null || _editedBricksField == null)
                Log.LogWarning("[VoxCaves] Seam-fix fields missing.");

            _locationInstancesField =
                typeof(ZoneSystem).GetField("m_locationInstances",
                    BindingFlags.NonPublic | BindingFlags.Instance) ??
                typeof(ZoneSystem).GetField("m_locationInstances",
                    BindingFlags.Public    | BindingFlags.Instance);
            if (_locationInstancesField == null)
                Log.LogWarning("[VoxCaves] m_locationInstances not found — location protection off.");

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

            // Set: (object field, int x, int y, int z, sbyte v) -> void
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

        // ── Biome profile defaults ────────────────────────────────────────────────
        static CaveProfile P(float s, float r, float a, float d) =>
            new CaveProfile { strength = s, bandRadius = r, warpAmp = a, ditherAmp = d };

        static Dictionary<Heightmap.Biome, CaveProfile> BuildDefaultProfiles() =>
            new Dictionary<Heightmap.Biome, CaveProfile>
            {
                [Heightmap.Biome.Meadows]     = P(0.8f,  6f,  80f, 0.45f),
                [Heightmap.Biome.BlackForest] = P(1.0f,  6f,  80f, 0.45f),
                [Heightmap.Biome.Swamp]       = P(1.0f,  8f,  90f, 0.50f),
                [Heightmap.Biome.Mountain]    = P(1.0f,  6f,  80f, 0.45f),
                [Heightmap.Biome.Plains]      = P(0.9f,  6f,  80f, 0.45f),
                [Heightmap.Biome.Mistlands]   = P(1.0f,  7f,  80f, 0.60f),
                [Heightmap.Biome.AshLands]    = P(1.0f,  8f, 100f, 0.45f),
                [Heightmap.Biome.DeepNorth]   = P(0.8f,  4f,  50f, 0.20f),
                [Heightmap.Biome.Ocean]       = P(0f,    0f,   0f, 0f),
                [Heightmap.Biome.None]        = P(0f,    0f,   0f, 0f),
            };

        // ── Config file ───────────────────────────────────────────────────────────
        static string ConfigPath =>
            Path.Combine(BepInEx.Paths.ConfigPath, "voxcaves_biomes.cfg");

        internal static void LoadBiomeConfig()
        {
            string path = ConfigPath;
            if (!File.Exists(path))
            {
                WriteDefaultBiomeConfig(path);
                _biomeProfiles = BuildDefaultProfiles();
                return;
            }
            var profiles = BuildDefaultProfiles();
            string? section = null;
            foreach (string raw in File.ReadAllLines(path))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line[0] == '#') continue;
                if (line[0] == '[')
                {
                    section = line.Trim('[', ']').Trim();
                    continue;
                }
                if (section == null) continue;
                if (!Enum.TryParse<Heightmap.Biome>(section, out var biome)) continue;
                int eq = line.IndexOf('=');
                if (eq < 0) continue;
                string key = line.Substring(0, eq).Trim().ToLowerInvariant();
                string val = line.Substring(eq + 1).Trim();
                if (!float.TryParse(val,
                        System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out float v)) continue;
                if (!profiles.TryGetValue(biome, out var p)) p = CaveProfile.Default;
                switch (key)
                {
                    case "strength":   p.strength   = v; break;
                    case "bandradius": p.bandRadius  = v; break;
                    case "warpamp":    p.warpAmp     = v; break;
                    case "ditheramp":  p.ditherAmp   = v; break;
                }
                profiles[biome] = p;
            }
            _biomeProfiles = profiles;
            Log.LogInfo("[VoxCaves] Biome profiles reloaded.");
        }

        static void WriteDefaultBiomeConfig(string path)
        {
            var lines = new System.Text.StringBuilder();
            lines.AppendLine("# VoxCaves biome profiles — edit and save to hot-reload in-game");
            lines.AppendLine("# strength:   0=no carving, 1=full");
            lines.AppendLine("# bandRadius: half-thickness of carved band (world units)");
            lines.AppendLine("# warpAmp:    XZ warp displacement in metres (0 = actual terrain surface)");
            lines.AppendLine("# ditherAmp:  per-column surface noise amplitude (world units)");
            lines.AppendLine();
            var defs = BuildDefaultProfiles();
            foreach (var (biome, p) in defs)
            {
                if (biome == Heightmap.Biome.None) continue;
                lines.AppendLine($"[{biome}]");
                lines.AppendLine($"strength   = {p.strength.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)}");
                lines.AppendLine($"bandRadius = {p.bandRadius.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)}");
                lines.AppendLine($"warpAmp    = {p.warpAmp.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)}");
                lines.AppendLine($"ditherAmp  = {p.ditherAmp.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)}");
                lines.AppendLine();
            }
            File.WriteAllText(path, lines.ToString());
            Log.LogInfo($"[VoxCaves] Wrote default biome config to {path}");
        }

        static void WatchBiomeConfig()
        {
            string path = ConfigPath;
            string dir  = Path.GetDirectoryName(path) ?? ".";
            _configWatcher = new FileSystemWatcher(dir, Path.GetFileName(path))
            {
                NotifyFilter = NotifyFilters.LastWrite,
                EnableRaisingEvents = true,
            };
            // Background thread — only set the flag; all state mutation on main thread.
            _configWatcher.Changed += (_, _) => _hotReloadPending = true;
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
        internal static float ComputeColumnFloor(float worldX, float worldZ, WorldGenerator? wg) =>
            wg != null ? wg.GetHeight(worldX, worldZ) - 8f : 20f;

        // ── Mode A density (noise-tunnel network) ─────────────────────────────────
        static float Density(float worldX, float worldY, float worldZ,
                             float floorY, WorldGenerator? wg)
        {
            if (!_useNoiseCaves) return 0f;
            if (worldY <= floorY) return 0f;
            InitNoise();
            const float vscale = 0.35f, warpAmp = 10f, tunnelWidth = 0.18f;
            float sy   = worldY * vscale;
            float wx   = _warpX!.GetNoise(worldX, sy, worldZ) * warpAmp;
            float wz   = _warpZ!.GetNoise(worldX, sy, worldZ) * warpAmp;
            float n    = _caveNoise!.GetNoise(worldX + wx, sy, worldZ + wz);
            float absN = Mathf.Abs(n);
            if (absN >= tunnelWidth) return 0f;
            return (tunnelWidth - absN) / tunnelWidth;
        }

        // ── Mode C additive density ───────────────────────────────────────────────
        static float AddDensity(float worldX, float worldY, float worldZ, WorldGenerator? wg)
        {
            if (!_useHeightmapAdd || wg == null) return 0f;
            InitNoise();
            const float hmAddWarpAmp = 80f, bandRadius = 6f;
            float wx = _hmAddWarpX!.GetNoise(worldX, worldZ) * hmAddWarpAmp;
            float wz = _hmAddWarpZ!.GetNoise(worldX, worldZ) * hmAddWarpAmp;
            float addH = wg.GetHeight(worldX + wx, worldZ + wz);
            float dist = Mathf.Abs(worldY - addH);
            if (dist >= bandRadius) return 0f;
            return (bandRadius - dist) / bandRadius;
        }

        // ── Biome profile blending ────────────────────────────────────────────────
        // Samples the biome at 5 points (centre × 2, plus 4 cardinals at blendR).
        // Returns a weighted-average CaveProfile. Zero alloc: 5 dictionary lookups,
        // no collections allocated.
        static CaveProfile BlendProfile(float worldX, float worldZ, WorldGenerator wg)
        {
            const float blendR = 48f;
            var profs = _biomeProfiles; // snapshot volatile ref

            CaveProfile Get(Heightmap.Biome b) =>
                profs.TryGetValue(b, out var p) ? p : CaveProfile.Default;

            var p0 = Get(wg.GetBiome(worldX,         worldZ        ));
            var p1 = Get(wg.GetBiome(worldX,         worldZ        )); // centre × 2
            var p2 = Get(wg.GetBiome(worldX + blendR, worldZ        ));
            var p3 = Get(wg.GetBiome(worldX - blendR, worldZ        ));
            var p4 = Get(wg.GetBiome(worldX,          worldZ + blendR));
            var p5 = Get(wg.GetBiome(worldX,          worldZ - blendR));
            const float w = 1f / 6f;
            return new CaveProfile
            {
                strength   = (p0.strength   + p1.strength   + p2.strength   + p3.strength   + p4.strength   + p5.strength  ) * w,
                bandRadius = (p0.bandRadius + p1.bandRadius + p2.bandRadius + p3.bandRadius + p4.bandRadius + p5.bandRadius) * w,
                warpAmp    = (p0.warpAmp    + p1.warpAmp    + p2.warpAmp    + p3.warpAmp    + p4.warpAmp    + p5.warpAmp   ) * w,
                ditherAmp  = (p0.ditherAmp  + p1.ditherAmp  + p2.ditherAmp  + p3.ditherAmp  + p4.ditherAmp  + p5.ditherAmp ) * w,
            };
        }

        // ── Location cache ────────────────────────────────────────────────────────
        // Reads ZoneSystem.m_locationInstances once per world load. Called lazily
        // from Carve() on the first zone that fires after ZoneSystem is ready.
        static void BuildLocationCache()
        {
            if (_locationsCached) return;
            _locationsCached = true;
            _locationPositions.Clear();
            var zs = ZoneSystem.instance;
            if (zs == null || _locationInstancesField == null) return;
            var dict = _locationInstancesField.GetValue(zs);
            if (dict == null) return;
            var valsProp = dict.GetType().GetProperty("Values");
            if (valsProp?.GetValue(dict) is not System.Collections.IEnumerable vals) return;
            FieldInfo? mPos = null;
            foreach (var inst in vals)
            {
                mPos ??= inst?.GetType().GetField("m_pos");
                if (inst == null || mPos == null) continue;
                if (mPos.GetValue(inst) is Vector3 pos)
                    _locationPositions.Add(new Vector2(pos.x, pos.z));
            }
            Log.LogInfo($"[VoxCaves] Location cache built: {_locationPositions.Count} positions.");
        }

        // Returns a factor in [0,1]: 0 = fully inside a location's protection zone,
        // 1 = far from all locations. nearbyLocs is pre-filtered to this zone's vicinity.
        static float LocationFactor(float worldX, float worldZ, List<Vector2> nearbyLocs)
        {
            if (nearbyLocs.Count == 0) return 1f;
            float minDist = float.MaxValue;
            for (int i = 0; i < nearbyLocs.Count; i++)
            {
                float dx = worldX - nearbyLocs[i].x;
                float dz = worldZ - nearbyLocs[i].y;
                float d  = Mathf.Sqrt(dx * dx + dz * dz);
                if (d < minDist) minDist = d;
            }
            return Mathf.Clamp01((minDist - LocProtectR) / LocFadeR);
        }

        // ── Carve pass ────────────────────────────────────────────────────────────
        internal static void Carve(object field, object grid, int ox, int oz, int width, int yLo, int yHi)
        {
            if (_scale == null)
            {
                _scale = _scaleField != null ? (float)_scaleField.GetValue(null)! : 1f;
                Log.LogInfo($"[VoxCaves] s_scale resolved to {_scale}");
            }
            float s = _scale.Value;
            var   wg = WorldGenerator.instance;

            BuildLocationCache();

            // Pre-filter locations to this zone's vicinity so the per-column check is O(few).
            float zoneX = (ox + width * 0.5f) * s;
            float zoneZ = (oz + width * 0.5f) * s;
            float searchR = width * s * 0.5f + LocProtectR + LocFadeR;
            var nearbyLocs = new List<Vector2>();
            foreach (var loc in _locationPositions)
            {
                float dx = loc.x - zoneX, dz = loc.y - zoneZ;
                if (dx * dx + dz * dz < searchR * searchR)
                    nearbyLocs.Add(loc);
            }

            string zoneKey = $"{ox},{oz}";
            _zoneParams[zoneKey] = (field, grid, ox, oz, width, yLo, yHi);

            int carved = 0;
            var brickSet = new HashSet<(int, int, int)>();

            for (int ci = 0; ci < width; ci++)
            for (int ri = 0; ri < width; ri++)
            {
                int   gx     = ox + ci, gz = oz + ri;
                float worldX = gx * s,  worldZ = gz * s;
                float floorY = ComputeColumnFloor(worldX, worldZ, wg);

                // ── Mode B: warped-heightmap carve ────────────────────────────────
                // Per-column: compute the blended biome profile + location factor once,
                // then write a proper bilateral SDF (positive inside band, negative
                // outside) for every voxel in the column.
                if (_useHeightmapWarp && wg != null)
                {
                    InitNoise();
                    var prof = BlendProfile(worldX, worldZ, wg);
                    float locFac = LocationFactor(worldX, worldZ, nearbyLocs);

                    // Blend warp amplitude toward zero near locations ("no-warp blending").
                    float effectiveWarpAmp = prof.warpAmp * locFac;
                    float wx2    = _hmWarpX!.GetNoise(worldX, worldZ) * effectiveWarpAmp;
                    float wz2    = _hmWarpZ!.GetNoise(worldX, worldZ) * effectiveWarpAmp;
                    float warpedH = wg.GetHeight(worldX + wx2, worldZ + wz2)
                                  + _caveNoise!.GetNoise(worldX * 1.3f, worldZ * 1.3f) * prof.ditherAmp;
                    float hmC = warpedH + prof.bandRadius;
                    float hmF = warpedH - prof.bandRadius;

                    for (int gy = yLo; gy <= yHi; gy++)
                    {
                        float worldY  = gy * s;
                        float bandSDF = Mathf.Min(hmC - worldY, worldY - hmF);
                        float sdf     = Mathf.Min(bandSDF, worldY - floorY)
                                      * prof.strength * locFac;
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
                        sbyte v = (sbyte)Mathf.Clamp(d * 127f, 1f, 127f);
                        _set!(field, gx, gy, gz, v);
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
                        sbyte v = (sbyte)Mathf.Clamp(-d * 127f, -127f, -1f);
                        _set!(field, gx, gy, gz, v);
                        brickSet.Add((gx >> 4, gy >> 4, gz >> 4));
                        carved++;
                    }
                }
            }

            if (carved > 0)
                _pendingBricks[zoneKey] = brickSet;

            Log.LogInfo($"[VoxCaves] zone ({ox},{oz}) wg={wg != null} yLo={yLo} yHi={yHi} s={s:F3} carved={carved}");
        }

        // ── Hot-reload: re-run the full voxel pipeline on every loaded zone ─────────
        // VoxelizeColumns resets the baseline SDF; our postfix re-carves with the new
        // profiles and populates _pendingBricks[key]. We then drain that brick set into
        // EnqueueMesh, and call FinishSkinNow to synchronously rebuild each brick's mesh.
        // This mirrors what Voxheim does after player terrain edits.
        static void RecarveAll()
        {
            Log.LogInfo($"[VoxCaves] Hot-reload: re-voxelizing {_zoneParams.Count} zone(s).");
            if (_voxelizeColumnsMethod == null) return;
            bool canRemesh = _enqueueMeshMethod != null && _finishSkinNowMethod != null
                             && _int3Ctor != null;
            if (!canRemesh)
                Log.LogWarning("[VoxCaves] EnqueueMesh/FinishSkinNow unavailable — mesh rebuild skipped.");

            // Snapshot before iterating — Carve() writes back to _zoneParams during VoxelizeColumns.
            var snapshot = new List<(string key, (object field, object grid, int ox, int oz, int w, int yLo, int yHi) zp)>(_zoneParams.Count);
            foreach (var kv in _zoneParams) snapshot.Add((kv.Key, kv.Value));

            foreach (var (key, zp) in snapshot)
            {
                // Re-voxelize: Voxheim resets the field, our Carve() postfix re-carves.
                try
                {
                    _voxelizeColumnsMethod.Invoke(null, new object[]
                        { zp.field, zp.grid, zp.ox, zp.oz, 0, zp.w - 1, 0, zp.w - 1, zp.yLo, zp.yHi });
                }
                catch (Exception e) { Log.LogWarning($"[VoxCaves] Revoxelize {key}: {e.Message}"); continue; }

                if (!_zoneObjects.TryGetValue(key, out var zoneObj)) continue;
                if (!canRemesh) continue;

                // Drain the brick set that Carve() just populated.
                if (_pendingBricks.Remove(key, out var bricks))
                {
                    foreach (var (bx, by, bz) in bricks)
                    {
                        try
                        {
                            var i3 = _int3Ctor!.Invoke(new object[] { bx, by, bz });
                            _enqueueMeshMethod!.Invoke(zoneObj, new[] { i3 });
                        }
                        catch (Exception e) { Log.LogWarning($"[VoxCaves] EnqueueMesh {key}: {e.Message}"); break; }
                    }
                    Log.LogInfo($"[VoxCaves] Queued {bricks.Count} bricks for zone {key}.");
                }

                // Synchronously rebuild all queued bricks, then complete the zone.
                try { _finishSkinNowMethod!.Invoke(zoneObj, null); }
                catch (Exception e) { Log.LogWarning($"[VoxCaves] FinishSkinNow {key}: {e.Message}"); }
            }
        }

        // ── Placement height ──────────────────────────────────────────────────────
        internal static float FindCarvedSurfaceY(float worldX, float originalY, float worldZ)
        {
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

    [HarmonyPatch]
    static class VoxelizeColumnsPatch
    {
        static MethodBase? TargetMethod()
        {
            var t = Plugin.FindType("Voxheim.Core.HeightFieldVoxelizer", "Voxheim.Core");
            var m = t?.GetMethod("VoxelizeColumns", BindingFlags.Public | BindingFlags.Static);
            Plugin._voxelizeColumnsMethod = m;
            if (m == null) Plugin.Log.LogError("[VoxCaves] VoxelizeColumns not found.");
            return m;
        }

        static void Postfix(object field, object grid, int originX, int originZ,
                            int gx0, int gx1, int gz0, int gz1, int yLo, int yHi)
        {
            if (gx0 != 0 || gz0 != 0) return;
            try { Plugin.Carve(field, grid, originX, originZ, gx1 + 1, yLo, yHi); }
            catch (Exception e) { Plugin.Log.LogError($"[VoxCaves] Carve: {e.Message}"); }
        }
    }

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
            string key = $"{ox},{oz}";

            // Store zone object reference for hot-reload re-meshing.
            Plugin._zoneObjects[key] = __instance;

            if (!Plugin._pendingBricks.Remove(key, out var bricks)) return;
            var editedBricks = Plugin._editedBricksField.GetValue(__instance);
            if (editedBricks == null) return;
            Plugin._editedBricksAdd ??= editedBricks.GetType().GetMethod("Add");
            if (Plugin._editedBricksAdd == null) return;
            foreach (var (bx, by, bz) in bricks)
            {
                var i3 = Plugin._int3Ctor.Invoke(new object[] { bx, by, bz });
                Plugin._editedBricksAdd.Invoke(editedBricks, new[] { i3 });
            }
        }
    }

    [HarmonyPatch(typeof(ZNet), nameof(ZNet.SetServer))]
    static class WorldLoadPatch
    {
        static void Postfix()
        {
            Plugin.LoadBiomeConfig();
            Plugin._pendingBricks.Clear();
            Plugin._zoneObjects.Clear();
            Plugin._zoneParams.Clear();
            Plugin._locationPositions.Clear();
            Plugin._locationsCached = false;
            Plugin._caveNoise  = null;
            Plugin._warpX      = null;
            Plugin._warpZ      = null;
            Plugin._hmWarpX    = null;
            Plugin._hmWarpZ    = null;
            Plugin._hmAddWarpX = null;
            Plugin._hmAddWarpZ = null;
        }
    }

    [HarmonyPatch]
    static class PlaceVegetationPatch
    {
        static FieldInfo? _zdoPosField;

        static MethodBase? TargetMethod() =>
            typeof(ZoneSystem).GetMethod("PlaceVegetation",
                BindingFlags.NonPublic | BindingFlags.Instance);

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
