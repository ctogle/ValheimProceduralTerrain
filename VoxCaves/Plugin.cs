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
    struct CaveProfile
    {
        // ── Mode B: warped-heightmap carve ────────────────────────────────────────
        public float modeB_strength;        // 0=no carving, 1=full
        public float modeB_radius;      // half-thickness of carved band (world units)
        public float modeB_warpAmp;         // XZ warp displacement (world units)
        public float modeB_warpFrequency;   // spatial frequency of XZ warp noise
        public float modeB_ditherAmp;       // per-column surface-noise offset (world units)
        public float modeB_ditherFrequency; // spatial frequency of dither noise

        // ── Mode A: noise tunnel network ──────────────────────────────────────────
        public float modeA_strength;      // 0=disabled, 1=full
        public float modeA_width;         // noise threshold for tunnel interior (0–1)
        public float modeA_frequency;     // spatial frequency of 3D tunnel noise
        public float modeA_warpAmp;        // 3D domain-warp amplitude (world units)
        public float modeA_warpFrequency;  // spatial frequency of 3D warp noise
        public float modeA_vScale;         // Y scale before noise lookup (low=horizontal, high=vertical)
        public float modeA_widthVary;      // amplitude of spatial width modulation (0=uniform, 1=2× variation)
        public float modeA_widthFrequency; // spatial frequency of width modulation noise

        // ── Mode C: 3D density field raised above heightmap ──────────────────────
        public float modeC_strength;       // 0=disabled, 1=full
        public float modeC_threshold;      // noise threshold for solid interior (-1..1, like modeA_width)
        public float modeC_noiseAmp;       // SDF scale — higher = stronger surface gradient
        public float modeC_frequency;      // spatial frequency of 3D density noise
        public float modeC_warpAmp;        // XZ anchor warp (world units — keep small)
        public float modeC_warpFrequency;  // spatial frequency of XZ anchor warp
        public float modeC_vScale;         // Y scale before noise lookup (higher = more overhangs/cliffs)
        public float modeC_twist;           // XZ rotation rate per world unit Y (radians/m)
        public float modeC_twistVary;      // amplitude of per-XZ noise-modulated twist scale (0=uniform)
        public float modeC_twistFrequency; // spatial frequency of twist variation noise
        public float modeC_ampVary;        // amplitude of spatial noise-amplitude modulation (0=uniform)
        public float modeC_ampFrequency;   // spatial frequency of amplitude modulation noise

        // ── Shared ────────────────────────────────────────────────────────────────
        public float blur_passes;      // XZ Gaussian smoothing passes (0=off; Y never blurred)
        public float floorDepth;       // metres below terrain surface where carving stops
        public float modeB_layers;       // number of stacked Mode B bands (1 = current behaviour)
        public float modeB_layerSpacing; // vertical gap between band centres (world units)

        public static readonly CaveProfile Default = new CaveProfile
        {
            blur_passes     = 0f,
            modeB_strength        = 0f,     modeB_radius          = 6f,    modeB_warpAmp           = 80f,
            modeB_warpFrequency   = 0.005f, modeB_ditherAmp           = 0.45f, modeB_ditherFrequency   = 0.0195f,
            modeA_strength  = 0f,     modeA_width         = 0.18f, modeA_frequency   = 0.015f,
            modeA_warpAmp   = 10f,    modeA_warpFrequency = 0.04f, modeA_vScale       = 0.35f,
            modeA_widthVary = 0f,     modeA_widthFrequency = 0.003f,
            modeC_strength     = 0f,     modeC_threshold     = 0f,     modeC_noiseAmp      = 10f,    modeC_frequency    = 0.015f,
            modeC_warpAmp      = 8f,     modeC_warpFrequency = 0.005f, modeC_vScale       = 0.4f,
            modeC_twist        = 0f,     modeC_twistVary     = 0f,     modeC_twistFrequency = 0.003f,
            modeC_ampVary      = 0f,     modeC_ampFrequency  = 0.003f,
            floorDepth      = 8f,     modeB_layers          = 1f,    modeB_layerSpacing  = 15f,
        };
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

        // ── Debug flags ───────────────────────────────────────────────────────────
        internal static volatile bool _disableVegetation;
        internal static volatile bool _pendingVegCleanup;

        // ── Biome profiles ────────────────────────────────────────────────────────
        static volatile Dictionary<string, CaveProfile> _namedProfiles
            = new Dictionary<string, CaveProfile>(StringComparer.OrdinalIgnoreCase);
        static volatile Dictionary<Heightmap.Biome, CaveProfile> _biomeProfiles
            = BuildDefaultProfiles();
        static volatile bool _hotReloadPending;
        static DateTime _configLastWrite = DateTime.MinValue;

        // ── Location protection ───────────────────────────────────────────────────
        internal static readonly List<Vector2> _locationPositions = new();
        internal static bool _locationsCached;
        const float LocProtectR = 30f;
        const float LocFadeR    = 40f;

        // ── Zone tracking for hot-reload ──────────────────────────────────────────
        internal static readonly Dictionary<string, (object field, object grid, int ox, int oz, int w, int yLo, int yHi)>
            _zoneParams = new();
        internal static readonly Dictionary<string, object> _zoneObjects = new();
        internal static MethodInfo? _completeMethod;
        internal static MethodInfo? _afterFieldEditMethod;
        internal static MethodInfo? _enqueueMeshMethod;
        internal static MethodInfo? _finishSkinNowMethod;
        internal static MethodInfo? _pokeNeighborsMethod;
        internal static MethodInfo? _voxelizeColumnsMethod;

        // ── Noise ─────────────────────────────────────────────────────────────────
        // All noise objects use frequency=1.0; call sites scale coordinates by
        // the blended per-biome frequency so blended frequencies interpolate cleanly.
        internal static FastNoiseLite? _caveNoise;     // Mode A: 3D tunnel noise
        internal static FastNoiseLite? _ditherNoise;   // Mode B: per-column dither
        internal static FastNoiseLite? _warpX;         // Mode A: 3D warp X
        internal static FastNoiseLite? _warpZ;         // Mode A: 3D warp Z
        internal static FastNoiseLite? _hmWarpX;       // Mode B: XZ warp X
        internal static FastNoiseLite? _hmWarpZ;       // Mode B: XZ warp Z
        internal static FastNoiseLite? _hmAddWarpX;    // Mode C: anchor warp X
        internal static FastNoiseLite? _hmAddWarpZ;    // Mode C: anchor warp Z
        internal static FastNoiseLite? _addNoise;        // Mode C: 3D density field
        internal static FastNoiseLite? _addVaryNoise;   // Mode C: amplitude modulation
        internal static FastNoiseLite? _widthNoise;     // Mode A: tunnel width modulation
        internal static FastNoiseLite? _twistVaryNoise; // Mode C: twist scale modulation

        // ── Seam fix ──────────────────────────────────────────────────────────────
        internal static ConstructorInfo? _int3Ctor;
        internal static FieldInfo?       _zoneOriginX;
        internal static FieldInfo?       _zoneOriginZ;
        internal static FieldInfo?       _editedBricksField;
        internal static FieldInfo?       _yScanHiField;
        internal static MethodInfo?      _editedBricksAdd;
        internal static readonly Dictionary<string, HashSet<(int, int, int)>> _pendingBricks = new();
        internal static readonly Dictionary<string, int> _zoneMaxY = new();

        // Reused across Carve() calls to avoid per-zone GC pressure.
        internal static float[]? _bufA;
        internal static float[]? _bufB;

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

        static int _pollFrame;
        static int _vegCleanupDelay;
        void Update()
        {
            if (_pendingVegCleanup && ZNetScene.instance != null && ZoneSystem.instance != null)
            {
                // Wait ~3 s after conditions are met so voxel meshes are built before the physics check
                if (++_vegCleanupDelay > 90)
                {
                    _pendingVegCleanup = false;
                    _vegCleanupDelay   = 0;
                    CleanupVegetation();
                }
            }
            else _vegCleanupDelay = 0;

            if (_hotReloadPending)
            {
                _hotReloadPending = false;
                RecarveAll();
                _pendingVegCleanup = true;
            }

            // Fast-poll while waiting for location data (zone modifications are deferred
            // until locations are known), then settle to a slow poll for config changes.
            int pollEvery = _locationsCached ? 300 : 10;
            if (++_pollFrame % pollEvery != 0) return;

            if (!_locationsCached)
                BuildLocationCache(); // sets _hotReloadPending → RecarveAll once locations arrive

            var t = File.GetLastWriteTimeUtc(ConfigPath);
            if (t == _configLastWrite) return;
            _configLastWrite = t;
            LoadBiomeConfig();
            RecarveAll();
            _pendingVegCleanup = true; // remove vegetation that may now be floating after terrain reset
        }

        static void CleanupVegetation()
        {
            var zns = ZNetScene.instance;
            var zs  = ZoneSystem.instance;
            if (zns == null || zs == null) return;
            var vegNames = new HashSet<string>();
            foreach (var veg in zs.m_vegetation)
                if (veg.m_prefab != null) vegNames.Add(veg.m_prefab.name);
            if (vegNames.Count == 0) return;
            int count = 0;
            foreach (var nv in UnityEngine.Object.FindObjectsOfType<ZNetView>())
            {
                if (nv == null) continue;
                string nm = nv.gameObject.name;
                if (nm.EndsWith("(Clone)")) nm = nm.Substring(0, nm.Length - 7).TrimEnd();
                if (!vegNames.Contains(nm)) continue;

                if (!_disableVegetation)
                {
                    // Keep vegetation that has solid ground within 1.5 m directly below —
                    // removes trees on carved cliff faces where no horizontal surface exists.
                    Vector3 pos = nv.transform.position;
                    if (Physics.Raycast(pos + Vector3.up * 0.5f, Vector3.down, 2f))
                        continue;
                }

                zns.Destroy(nv.gameObject); count++;
            }
            Log.LogInfo($"[VoxCaves] Removed {count} vegetation objects.");
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

            _afterFieldEditMethod = zoneT?.GetMethod("AfterFieldEdit",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

            _enqueueMeshMethod   = zoneT?.GetMethod("EnqueueMesh",
                BindingFlags.NonPublic | BindingFlags.Instance);
            _finishSkinNowMethod = zoneT?.GetMethod("FinishSkinNow",
                BindingFlags.NonPublic | BindingFlags.Instance);
            _pokeNeighborsMethod = zoneT?.GetMethod("PokeTransitionNeighbors",
                BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance)
                ?? zoneT?.GetMethod("PokeNeighbors",
                BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
            if (_enqueueMeshMethod   == null) Log.LogWarning("[VoxCaves] EnqueueMesh not found.");
            if (_finishSkinNowMethod == null) Log.LogWarning("[VoxCaves] FinishSkinNow not found.");
            if (_pokeNeighborsMethod == null) Log.LogWarning("[VoxCaves] PokeTransitionNeighbors/PokeNeighbors not found — seam fix on hot-reload disabled.");

            _zoneOriginX       = zoneT?.GetField("_originX",
                BindingFlags.NonPublic | BindingFlags.Instance);
            _zoneOriginZ       = zoneT?.GetField("_originZ",
                BindingFlags.NonPublic | BindingFlags.Instance);
            _editedBricksField = zoneT?.GetField("_editedBricks",
                BindingFlags.NonPublic | BindingFlags.Instance);
            _yScanHiField      = zoneT?.GetField("_yScanHi",
                BindingFlags.NonPublic | BindingFlags.Instance);
            _completeMethod    = zoneT?.GetMethod("Complete",
                BindingFlags.NonPublic | BindingFlags.Instance);
            if (_zoneOriginX == null || _zoneOriginZ == null || _editedBricksField == null)
                Log.LogWarning("[VoxCaves] Seam-fix fields missing.");
            if (_yScanHiField == null)
                Log.LogWarning("[VoxCaves] _yScanHi not found — Mode C ceiling extension disabled.");

            _locationInstancesField =
                typeof(ZoneSystem).GetField("m_locationInstances",
                    BindingFlags.NonPublic | BindingFlags.Instance) ??
                typeof(ZoneSystem).GetField("m_locationInstances",
                    BindingFlags.Public    | BindingFlags.Instance);
            if (_locationInstancesField == null)
                Log.LogWarning("[VoxCaves] m_locationInstances not found — location protection off.");

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
        static CaveProfile P(
            float s, float r, float wa, float wf, float da, float df,
            float ts = 0f, float tw = 0.18f, float tf = 0.015f,
            float twa = 10f, float twf = 0.04f, float tvs = 0.35f,
            float twv = 0f, float twvf = 0.003f,
            float addS = 0f,  float addTh = 0f,     float addNa = 10f,    float addF  = 0.015f,
            float addWa = 8f, float addWf = 0.005f, float addVs = 0.4f,
            float addTw = 0f, float addTv = 0f,  float addTvf = 0.003f,
            float addAv = 0f, float addAf = 0.003f,
            float fd = 8f, float bl = 1f, float bls = 15f) =>
            new CaveProfile
            {
                modeB_strength = s, modeB_radius = r, modeB_warpAmp = wa, modeB_warpFrequency = wf,
                modeB_ditherAmp = da, modeB_ditherFrequency = df,
                modeA_strength = ts, modeA_width = tw, modeA_frequency = tf,
                modeA_warpAmp = twa, modeA_warpFrequency = twf, modeA_vScale = tvs,
                modeA_widthVary = twv, modeA_widthFrequency = twvf,
                modeC_strength = addS, modeC_threshold = addTh, modeC_noiseAmp = addNa, modeC_frequency = addF,
                modeC_warpAmp = addWa, modeC_warpFrequency = addWf, modeC_vScale = addVs,
                modeC_twist = addTw, modeC_twistVary = addTv, modeC_twistFrequency = addTvf,
                modeC_ampVary = addAv, modeC_ampFrequency = addAf,
                floorDepth = fd, modeB_layers = bl, modeB_layerSpacing = bls,
            };

        static Dictionary<Heightmap.Biome, CaveProfile> BuildDefaultProfiles() =>
            new Dictionary<Heightmap.Biome, CaveProfile>
            {
                [Heightmap.Biome.Meadows]     = CaveProfile.Default,
                [Heightmap.Biome.BlackForest] = CaveProfile.Default,
                [Heightmap.Biome.Swamp]       = CaveProfile.Default,
                [Heightmap.Biome.Mountain]    = CaveProfile.Default,
                [Heightmap.Biome.Plains]      = CaveProfile.Default,
                [Heightmap.Biome.Mistlands]   = CaveProfile.Default,
                [Heightmap.Biome.AshLands]    = CaveProfile.Default,
                [Heightmap.Biome.DeepNorth]   = CaveProfile.Default,
                [Heightmap.Biome.Ocean]       = CaveProfile.Default,
                [Heightmap.Biome.None]        = CaveProfile.Default,
            };

        // ── Config file ───────────────────────────────────────────────────────────
        static string ConfigPath =>
            Path.Combine(BepInEx.Paths.ConfigPath, "voxcaves_biomes.cfg");

        // Apply a flat key→value list on top of a base CaveProfile (struct copy).
        // Unrecognised or non-numeric values are silently skipped so callers can
        // pass raw section lines without pre-filtering (e.g. "profile = name").
        static CaveProfile ApplyProfile(
            IEnumerable<(string key, string val)> kvs, CaveProfile p)
        {
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            foreach (var (key, val) in kvs)
            {
                if (!float.TryParse(val, System.Globalization.NumberStyles.Float, inv, out float v))
                    continue;
                switch (key)
                {
                    case "modeb_strength":        p.modeB_strength        = v; break;
                    case "modeb_radius":          p.modeB_radius          = v; break;
                    case "modeb_warpamp":         p.modeB_warpAmp         = v; break;
                    case "modeb_warpfrequency":   p.modeB_warpFrequency   = v; break;
                    case "modeb_ditheramp":       p.modeB_ditherAmp       = v; break;
                    case "modeb_ditherfrequency": p.modeB_ditherFrequency = v; break;
                    case "modeb_layers":          p.modeB_layers          = v; break;
                    case "modeb_layerspacing":    p.modeB_layerSpacing    = v; break;
                    case "modea_strength":        p.modeA_strength        = v; break;
                    case "modea_width":           p.modeA_width           = v; break;
                    case "modea_frequency":       p.modeA_frequency       = v; break;
                    case "modea_warpamp":         p.modeA_warpAmp         = v; break;
                    case "modea_warpfrequency":   p.modeA_warpFrequency   = v; break;
                    case "modea_vscale":          p.modeA_vScale          = v; break;
                    case "modea_widthvary":       p.modeA_widthVary       = v; break;
                    case "modea_widthfrequency":  p.modeA_widthFrequency  = v; break;
                    case "modec_strength":        p.modeC_strength        = v; break;
                    case "modec_threshold":       p.modeC_threshold       = v; break;
                    case "modec_noiseamp":        p.modeC_noiseAmp        = v; break;
                    case "modec_frequency":       p.modeC_frequency       = v; break;
                    case "modec_warpamp":         p.modeC_warpAmp         = v; break;
                    case "modec_warpfrequency":   p.modeC_warpFrequency   = v; break;
                    case "modec_vscale":          p.modeC_vScale          = v; break;
                    case "modec_twist":           p.modeC_twist           = v; break;
                    case "modec_twistvary":       p.modeC_twistVary       = v; break;
                    case "modec_twistfrequency":  p.modeC_twistFrequency  = v; break;
                    case "modec_ampvary":         p.modeC_ampVary         = v; break;
                    case "modec_ampfrequency":    p.modeC_ampFrequency    = v; break;
                    case "blur_passes":            p.blur_passes            = v; break;
                    case "floordepth":            p.floorDepth            = v; break;
                }
            }
            return p;
        }

        internal static void LoadBiomeConfig()
        {
            string path = ConfigPath;
            if (!File.Exists(path))
            {
                WriteDefaultBiomeConfig(path);
                _biomeProfiles = BuildDefaultProfiles();
                return;
            }

            // Collect all sections into raw key-value lists for two-pass resolution.
            var sections = new List<(string name, List<(string key, string val)> kvs)>();
            List<(string, string)>? cur = null;
            foreach (string raw in File.ReadAllLines(path))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line[0] == '#') continue;
                if (line[0] == '[')
                {
                    cur = new List<(string, string)>();
                    sections.Add((line.Trim('[', ']').Trim(), cur));
                    continue;
                }
                if (cur == null) continue;
                int eq = line.IndexOf('=');
                if (eq < 0) continue;
                cur.Add((line.Substring(0, eq).Trim().ToLowerInvariant(),
                         line.Substring(eq + 1).Trim()));
            }

            // Pass 1: named profiles  [Profile:name]
            var named = new Dictionary<string, CaveProfile>(StringComparer.OrdinalIgnoreCase);
            foreach (var (name, kvs) in sections)
            {
                if (!name.StartsWith("Profile:", StringComparison.OrdinalIgnoreCase)) continue;
                string profName = name.Substring(8).Trim();
                named[profName] = ApplyProfile(kvs, CaveProfile.Default);
            }

            // Pass 2: debug + biome assignments
            var profiles = BuildDefaultProfiles();
            foreach (var (name, kvs) in sections)
            {
                if (name.Equals("Debug", StringComparison.OrdinalIgnoreCase))
                {
                    foreach (var (k, v) in kvs)
                    {
                        if (k == "disablevegetation")
                        {
                            _disableVegetation = v == "1" || v == "true";
                            if (_disableVegetation) _pendingVegCleanup = true;
                        }
                    }
                    continue;
                }
                if (!Enum.TryParse<Heightmap.Biome>(name, out var biome)) continue;

                // Resolve base: named profile if specified, else existing default.
                CaveProfile baseProf = profiles.TryGetValue(biome, out var def) ? def : CaveProfile.Default;
                foreach (var (k, v) in kvs)
                    if (k == "profile" && named.TryGetValue(v, out var np)) { baseProf = np; break; }

                profiles[biome] = ApplyProfile(kvs, baseProf);
            }

            _namedProfiles = named;
            _biomeProfiles = profiles;
            Log.LogInfo($"[VoxCaves] Profiles reloaded: {named.Count} named, {profiles.Count} biomes.");
        }

        static void WriteDefaultBiomeConfig(string path)
        {
            var sb = new System.Text.StringBuilder();

            sb.AppendLine("# VoxCaves — edit and save to hot-reload in-game");
            sb.AppendLine("#");
            sb.AppendLine("# Define profiles with [Profile:name], assign to biomes with:");
            sb.AppendLine("#   profile = name");
            sb.AppendLine("# Params listed directly in a [BiomeName] section override the profile.");
            sb.AppendLine("#");
            sb.AppendLine("# ── Full parameter reference (copy to start a new profile) ─────────────");
            sb.AppendLine("# [Profile:my_profile]");
            sb.AppendLine("# blur_passes           = 0      # XZ Gaussian passes for seam smoothing (0=off, 1-3=light/heavy; Y never blurred)");
            sb.AppendLine("# floorDepth            = 8      # how far below heightmap modes A+B stop carving");
            sb.AppendLine("# # Mode B — carves a band of air around the heightmap surface");
            sb.AppendLine("# modeB_strength        = 0      # 0=off 1=full");
            sb.AppendLine("# modeB_radius          = 6      # half-thickness of carved band (world units)");
            sb.AppendLine("# modeB_warpAmp         = 80     # XZ warp displacement (world units)");
            sb.AppendLine("# modeB_warpFrequency   = 0.005  # spatial frequency of XZ warp");
            sb.AppendLine("# modeB_ditherAmp       = 0.45   # per-column surface jitter amplitude");
            sb.AppendLine("# modeB_ditherFrequency = 0.0195 # spatial frequency of surface jitter");
            sb.AppendLine("# modeB_layers          = 1      # stacked cave band count");
            sb.AppendLine("# modeB_layerSpacing    = 15     # vertical gap between band centres");
            sb.AppendLine("# # Mode A — 3D noise tunnel network (punches through Mode C solid)");
            sb.AppendLine("# modeA_strength        = 0      # 0=off 1=full");
            sb.AppendLine("# modeA_width           = 0.5    # tunnel size (noise threshold 0-1)");
            sb.AppendLine("# modeA_frequency       = 0.02   # spatial frequency of tunnel noise");
            sb.AppendLine("# modeA_warpAmp         = 5      # 3D domain-warp displacement");
            sb.AppendLine("# modeA_warpFrequency   = 0.02   # spatial frequency of domain warp");
            sb.AppendLine("# modeA_vScale          = 0.1    # Y stretch (low=flat, high=vertical)");
            sb.AppendLine("# modeA_widthVary       = 1      # tunnel-width spatial variation (0=uniform)");
            sb.AppendLine("# modeA_widthFrequency  = 0.02   # spatial frequency of width variation");
            sb.AppendLine("# # Mode C — 3D density field raising solid terrain above the heightmap");
            sb.AppendLine("# modeC_strength        = 0      # 0=off 1=full");
            sb.AppendLine("# modeC_threshold       = 0      # iso-surface offset (-1..1; 0=half solid)");
            sb.AppendLine("# modeC_noiseAmp        = 10     # height of raised terrain (world units)");
            sb.AppendLine("# modeC_frequency       = 0.015  # spatial frequency of 3D density noise");
            sb.AppendLine("# modeC_warpAmp         = 8      # XZ anchor-point warp displacement");
            sb.AppendLine("# modeC_warpFrequency   = 0.005  # spatial frequency of anchor warp");
            sb.AppendLine("# modeC_vScale          = 0.4    # Y stretch (higher = more overhangs/cliffs)");
            sb.AppendLine("# modeC_twist           = 0      # XZ rotation per world-unit Y (radians/m)");
            sb.AppendLine("# modeC_twistVary       = 0      # per-XZ twist-scale variation (0=uniform)");
            sb.AppendLine("# modeC_twistFrequency  = 0.003  # spatial frequency of twist variation");
            sb.AppendLine("# modeC_ampVary         = 0      # height variation amplitude (0=uniform)");
            sb.AppendLine("# modeC_ampFrequency    = 0.003  # spatial frequency of height variation");
            sb.AppendLine("# ─────────────────────────────────────────────────────────────────────────");
            sb.AppendLine();
            sb.AppendLine("[Debug]");
            sb.AppendLine("disableVegetation = false");
            sb.AppendLine();

            sb.AppendLine("[Profile:vanilla_heightmap]");
            sb.AppendLine("blur_passes    = 0");
            sb.AppendLine("modeB_strength = 0");
            sb.AppendLine("modeA_strength = 0");
            sb.AppendLine("modeC_strength = 0");
            sb.AppendLine();

            // ── Biome assignments — all default to vanilla_heightmap ──────────────
            var biomes = new[]
            {
                Heightmap.Biome.Meadows, Heightmap.Biome.BlackForest, Heightmap.Biome.Swamp,
                Heightmap.Biome.Mountain, Heightmap.Biome.Plains, Heightmap.Biome.Mistlands,
                Heightmap.Biome.AshLands, Heightmap.Biome.DeepNorth, Heightmap.Biome.Ocean,
            };
            foreach (var biome in biomes)
            {
                sb.AppendLine($"[{biome}]");
                sb.AppendLine("profile = vanilla_heightmap");
                sb.AppendLine();
            }

            File.WriteAllText(path, sb.ToString());
            Log.LogInfo($"[VoxCaves] Wrote default biome config to {path}");
        }

        static void WatchBiomeConfig()
        {
            _configLastWrite = File.GetLastWriteTimeUtc(ConfigPath);
        }

        // ── Noise initialisation ──────────────────────────────────────────────────
        // All objects at frequency=1.0; call sites scale coordinates explicitly so
        // blended per-biome frequencies interpolate correctly across biome boundaries.
        static void InitNoise()
        {
            if (_caveNoise != null) return;
            long uid = ZNet.instance?.GetWorldUID() ?? 0L;
            int seed = uid != 0L ? (int)(uid * 2654435761L) : 12345;
            Log.LogInfo($"[VoxCaves] noise seed={seed} (uid={uid})");

            FastNoiseLite MakeNoise(int offset)
            {
                var n = new FastNoiseLite(seed + offset);
                n.SetNoiseType(FastNoiseLite.NoiseType.OpenSimplex2);
                n.SetFrequency(1f);
                return n;
            }

            _caveNoise   = MakeNoise(0); // Mode A tunnel
            _ditherNoise = MakeNoise(1); // Mode B dither
            _warpX       = MakeNoise(2); // Mode A warp X
            _warpZ       = MakeNoise(3); // Mode A warp Z
            _hmWarpX     = MakeNoise(4); // Mode B warp X
            _hmWarpZ     = MakeNoise(5); // Mode B warp Z
            _hmAddWarpX  = MakeNoise(6); // Mode C anchor warp X
            _hmAddWarpZ  = MakeNoise(7); // Mode C anchor warp Z
            _widthNoise  = MakeNoise(8);  // Mode A width modulation
            _addNoise       = MakeNoise(9);  // Mode C 3D density field
            _addVaryNoise   = MakeNoise(10); // Mode C amplitude modulation
            _twistVaryNoise = MakeNoise(11); // Mode C twist scale modulation
        }

        // ── Per-column analytic floor ─────────────────────────────────────────────
        static sbyte ToSDFByte(float sdf, float s)
        {
            // Match Density.FromSdf exactly: clamp to ±1 voxel then round to nearest sbyte.
            // No snap — snapping collapsed sub-voxel fractions to ±12 on flat tops,
            // making adjacent columns round to the same interpolated height = terracing.
            return (sbyte)(int)Math.Round(Mathf.Clamp(sdf / s, -1f, 1f) * 127f);
        }

        internal static float ComputeColumnFloor(float worldX, float worldZ, WorldGenerator? wg, float depth = 8f) =>
            wg != null ? wg.GetHeight(worldX, worldZ) - depth : 20f;

        // ── Mode A density (noise tunnel network) ─────────────────────────────────
        static float TunnelDensity(float worldX, float worldY, float worldZ,
                                   float floorY, in CaveProfile prof)
        {
            if (prof.modeA_strength <= 0f || worldY <= floorY) return 0f;
            float sy  = worldY * prof.modeA_vScale;
            float twf = prof.modeA_warpFrequency;
            float wx  = _warpX!.GetNoise(worldX * twf, sy * twf, worldZ * twf) * prof.modeA_warpAmp;
            float wz  = _warpZ!.GetNoise(worldX * twf, sy * twf, worldZ * twf) * prof.modeA_warpAmp;
            float tf  = prof.modeA_frequency;
            float n   = _caveNoise!.GetNoise((worldX + wx) * tf, sy * tf, (worldZ + wz) * tf);
            float absN = Mathf.Abs(n);
            if (absN >= prof.modeA_width) return 0f;
            return (prof.modeA_width - absN) / prof.modeA_width * prof.modeA_strength;
        }

        // ── Mode C density sample (used for blend previews / future gizmos) ─────
        static float AddDensity(float worldX, float worldY, float worldZ,
                                WorldGenerator? wg, in CaveProfile prof)
        {
            if (prof.modeC_strength <= 0f || wg == null) return 0f;
            float wf  = prof.modeB_warpFrequency;
            float wx  = _hmAddWarpX!.GetNoise(worldX * wf, worldZ * wf) * prof.modeC_warpAmp;
            float wz  = _hmAddWarpZ!.GetNoise(worldX * wf, worldZ * wf) * prof.modeC_warpAmp;
            float addH = wg.GetHeight(worldX + wx, worldZ + wz);
            float relY = worldY - addH;
            float sy   = relY * prof.modeC_vScale;
            float angle = worldY * prof.modeC_twist;
            float cos = Mathf.Cos(angle), sin = Mathf.Sin(angle);
            float tx = worldX * cos - worldZ * sin, tz = worldX * sin + worldZ * cos;
            float n  = _addNoise!.GetNoise(tx * prof.modeC_frequency, sy * prof.modeC_frequency,
                                           tz * prof.modeC_frequency);
            float sdf = relY - (n - prof.modeC_threshold) * prof.modeC_noiseAmp;
            return sdf < 0f ? -sdf * prof.modeC_strength : 0f;
        }

        // ── Biome profile blending ────────────────────────────────────────────────
        static CaveProfile BlendProfile(float worldX, float worldZ, WorldGenerator wg)
        {
            const float blendR = 48f;
            var profs = _biomeProfiles;

            CaveProfile Get(Heightmap.Biome b) =>
                profs.TryGetValue(b, out var p) ? p : CaveProfile.Default;

            var p0 = Get(wg.GetBiome(worldX,          worldZ         ));
            var p1 = Get(wg.GetBiome(worldX,          worldZ         )); // centre × 2
            var p2 = Get(wg.GetBiome(worldX + blendR, worldZ         ));
            var p3 = Get(wg.GetBiome(worldX - blendR, worldZ         ));
            var p4 = Get(wg.GetBiome(worldX,          worldZ + blendR));
            var p5 = Get(wg.GetBiome(worldX,          worldZ - blendR));
            const float w = 1f / 6f;
            return new CaveProfile
            {
                modeB_strength            = (p0.modeB_strength            + p1.modeB_strength            + p2.modeB_strength            + p3.modeB_strength            + p4.modeB_strength            + p5.modeB_strength           ) * w,
                modeB_radius          = (p0.modeB_radius          + p1.modeB_radius          + p2.modeB_radius          + p3.modeB_radius          + p4.modeB_radius          + p5.modeB_radius         ) * w,
                modeB_warpAmp             = (p0.modeB_warpAmp             + p1.modeB_warpAmp             + p2.modeB_warpAmp             + p3.modeB_warpAmp             + p4.modeB_warpAmp             + p5.modeB_warpAmp            ) * w,
                modeB_warpFrequency       = (p0.modeB_warpFrequency       + p1.modeB_warpFrequency       + p2.modeB_warpFrequency       + p3.modeB_warpFrequency       + p4.modeB_warpFrequency       + p5.modeB_warpFrequency      ) * w,
                modeB_ditherAmp           = (p0.modeB_ditherAmp           + p1.modeB_ditherAmp           + p2.modeB_ditherAmp           + p3.modeB_ditherAmp           + p4.modeB_ditherAmp           + p5.modeB_ditherAmp          ) * w,
                modeB_ditherFrequency     = (p0.modeB_ditherFrequency     + p1.modeB_ditherFrequency     + p2.modeB_ditherFrequency     + p3.modeB_ditherFrequency     + p4.modeB_ditherFrequency     + p5.modeB_ditherFrequency    ) * w,
                modeA_strength      = (p0.modeA_strength      + p1.modeA_strength      + p2.modeA_strength      + p3.modeA_strength      + p4.modeA_strength      + p5.modeA_strength     ) * w,
                modeA_width         = (p0.modeA_width         + p1.modeA_width         + p2.modeA_width         + p3.modeA_width         + p4.modeA_width         + p5.modeA_width        ) * w,
                modeA_frequency     = (p0.modeA_frequency     + p1.modeA_frequency     + p2.modeA_frequency     + p3.modeA_frequency     + p4.modeA_frequency     + p5.modeA_frequency    ) * w,
                modeA_warpAmp       = (p0.modeA_warpAmp       + p1.modeA_warpAmp       + p2.modeA_warpAmp       + p3.modeA_warpAmp       + p4.modeA_warpAmp       + p5.modeA_warpAmp      ) * w,
                modeA_warpFrequency = (p0.modeA_warpFrequency + p1.modeA_warpFrequency + p2.modeA_warpFrequency + p3.modeA_warpFrequency + p4.modeA_warpFrequency + p5.modeA_warpFrequency) * w,
                modeA_vScale        = (p0.modeA_vScale        + p1.modeA_vScale        + p2.modeA_vScale        + p3.modeA_vScale        + p4.modeA_vScale        + p5.modeA_vScale       ) * w,
                modeA_widthVary     = (p0.modeA_widthVary     + p1.modeA_widthVary     + p2.modeA_widthVary     + p3.modeA_widthVary     + p4.modeA_widthVary     + p5.modeA_widthVary    ) * w,
                modeA_widthFrequency= (p0.modeA_widthFrequency+ p1.modeA_widthFrequency+ p2.modeA_widthFrequency+ p3.modeA_widthFrequency+ p4.modeA_widthFrequency+ p5.modeA_widthFrequency) * w,
                modeC_strength         = (p0.modeC_strength         + p1.modeC_strength         + p2.modeC_strength         + p3.modeC_strength         + p4.modeC_strength         + p5.modeC_strength        ) * w,
                modeC_threshold        = (p0.modeC_threshold        + p1.modeC_threshold        + p2.modeC_threshold        + p3.modeC_threshold        + p4.modeC_threshold        + p5.modeC_threshold       ) * w,
                modeC_noiseAmp         = (p0.modeC_noiseAmp         + p1.modeC_noiseAmp         + p2.modeC_noiseAmp         + p3.modeC_noiseAmp         + p4.modeC_noiseAmp         + p5.modeC_noiseAmp        ) * w,
                modeC_frequency        = (p0.modeC_frequency        + p1.modeC_frequency        + p2.modeC_frequency        + p3.modeC_frequency        + p4.modeC_frequency        + p5.modeC_frequency       ) * w,
                modeC_warpAmp          = (p0.modeC_warpAmp          + p1.modeC_warpAmp          + p2.modeC_warpAmp          + p3.modeC_warpAmp          + p4.modeC_warpAmp          + p5.modeC_warpAmp         ) * w,
                modeC_warpFrequency    = (p0.modeC_warpFrequency    + p1.modeC_warpFrequency    + p2.modeC_warpFrequency    + p3.modeC_warpFrequency    + p4.modeC_warpFrequency    + p5.modeC_warpFrequency   ) * w,
                modeC_vScale           = (p0.modeC_vScale           + p1.modeC_vScale           + p2.modeC_vScale           + p3.modeC_vScale           + p4.modeC_vScale           + p5.modeC_vScale          ) * w,
                modeC_twist            = (p0.modeC_twist            + p1.modeC_twist            + p2.modeC_twist            + p3.modeC_twist            + p4.modeC_twist            + p5.modeC_twist           ) * w,
                modeC_twistVary        = (p0.modeC_twistVary        + p1.modeC_twistVary        + p2.modeC_twistVary        + p3.modeC_twistVary        + p4.modeC_twistVary        + p5.modeC_twistVary       ) * w,
                modeC_twistFrequency   = (p0.modeC_twistFrequency   + p1.modeC_twistFrequency   + p2.modeC_twistFrequency   + p3.modeC_twistFrequency   + p4.modeC_twistFrequency   + p5.modeC_twistFrequency  ) * w,
                modeC_ampVary          = (p0.modeC_ampVary          + p1.modeC_ampVary          + p2.modeC_ampVary          + p3.modeC_ampVary          + p4.modeC_ampVary          + p5.modeC_ampVary         ) * w,
                modeC_ampFrequency     = (p0.modeC_ampFrequency     + p1.modeC_ampFrequency     + p2.modeC_ampFrequency     + p3.modeC_ampFrequency     + p4.modeC_ampFrequency     + p5.modeC_ampFrequency    ) * w,
                blur_passes         = (p0.blur_passes         + p1.blur_passes         + p2.blur_passes         + p3.blur_passes         + p4.blur_passes         + p5.blur_passes        ) * w,
                floorDepth          = (p0.floorDepth          + p1.floorDepth          + p2.floorDepth          + p3.floorDepth          + p4.floorDepth          + p5.floorDepth         ) * w,
                modeB_layers          = (p0.modeB_layers          + p1.modeB_layers          + p2.modeB_layers          + p3.modeB_layers          + p4.modeB_layers          + p5.modeB_layers         ) * w,
                modeB_layerSpacing    = (p0.modeB_layerSpacing    + p1.modeB_layerSpacing    + p2.modeB_layerSpacing    + p3.modeB_layerSpacing    + p4.modeB_layerSpacing    + p5.modeB_layerSpacing   ) * w,
            };
        }

        // ── Location cache ────────────────────────────────────────────────────────
        static void BuildLocationCache()
        {
            if (_locationsCached) return;
            var zs = ZoneSystem.instance;
            if (zs == null || _locationInstancesField == null) return;
            _locationPositions.Clear();
            var dict = _locationInstancesField.GetValue(zs);
            if (dict == null) return;
            var valsProp = dict.GetType().GetProperty("Values");
            if (valsProp?.GetValue(dict) is not System.Collections.IEnumerable vals) return;
            FieldInfo? mPos = null;
            foreach (var inst in vals)
            {
                mPos ??= inst?.GetType().GetField("m_position");
                if (inst == null || mPos == null) continue;
                if (mPos.GetValue(inst) is Vector3 pos)
                    _locationPositions.Add(new Vector2(pos.x, pos.z));
            }
            // Don't mark cached until we actually find locations — they may not be placed yet on first zone load.
            // When we first get locations, trigger a full recarve so any zones already written without
            // location data (e.g. spawn zone) get their protection radius applied retroactively.
            if (_locationPositions.Count > 0)
            {
                _locationsCached  = true;
                _hotReloadPending = true;
                Log.LogInfo($"[VoxCaves] Location cache built: {_locationPositions.Count} positions — scheduling recarve.");
            }
        }

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
        // Buffer-based 3D density pipeline:
        //   Phase 1 — fill float buffer with combined SDF from all modes using
        //             world-space density functions (continuous across boundaries).
        //             A 4-voxel XZ overlap region provides valid neighbor values
        //             so the blur can blend naturally across zone seams.
        //   Phase 2 — separable 3D Gaussian blur [0.25, 0.5, 0.25] for surface
        //             smoothing and floating-island fade.
        //   Phase 3 — write changed voxels to VoxelField; track dirty bricks.
        //
        // Combination order: base → max(B) → min(C) → max(A).
        // Mode A (tunnels) runs last so it punches through Mode C solid.
        internal static void Carve(object field, object grid, int ox, int oz, int width, int yLo, int yHi)
        {
            if (_scale == null)
            {
                _scale = _scaleField != null ? (float)_scaleField.GetValue(null)! : 1f;
                Log.LogInfo($"[VoxCaves] s_scale resolved to {_scale}");
            }
            float s  = _scale.Value;
            var   wg = WorldGenerator.instance;

            BuildLocationCache();

            float zoneX   = (ox + width * 0.5f) * s;
            float zoneZ   = (oz + width * 0.5f) * s;
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

            // If location data isn't ready yet, leave the zone vanilla.
            // Update() polls every 10 frames and triggers a full recarve (with correct
            // protection already applied) once ZoneSystem has placed locations.
            if (!_locationsCached) return;

            const int overlap = 4;

            // Sample centre profile once for structural params (blur, Mode C margin).
            var centerProf = wg != null ? BlendProfile(zoneX, zoneZ, wg) : CaveProfile.Default;
            int blurPasses = (int)centerProf.blur_passes;

            // Mode C can raise terrain well above the vanilla ceiling (yHi).
            // Compute the maximum possible Mode C top in voxels above yHi so Phase 3
            // writes the full solid column and the mesher gets a real gradient at the surface.
            // Without this, voxels above extHi stay as vanilla air (+127), causing a hard
            // -127→+127 cliff at the cutoff that forces the surface to snap to extHi + 0.5.
            int hiMargin = 24;
            if (centerProf.modeC_strength > 0f)
            {
                float maxAmp  = centerProf.modeC_noiseAmp * Mathf.Max(1f, 1f + centerProf.modeC_ampVary);
                float maxExtM = Mathf.Max(0f, 1f - centerProf.modeC_threshold) * maxAmp
                              + centerProf.modeC_warpAmp; // warp shifts addH up by at most warpAmp
                hiMargin = Mathf.Max(24, (int)Mathf.Ceil(maxExtM / s) + 8);
            }

            int extLo   = yLo - 24, extHi = yHi + hiMargin;
            int bw      = width + 2 * overlap;   // buffer XZ width (same for X and Z)
            int bh      = extHi - extLo + 1;     // buffer Y height
            int bufSize = bw * bh * bw;

            if (_bufA == null || _bufA.Length < bufSize) _bufA = new float[bufSize];
            if (_bufB == null || _bufB.Length < bufSize) _bufB = new float[bufSize];
            float[] buf = _bufA, tmp = _bufB;

            InitNoise();

            // ── Phase 1 ───────────────────────────────────────────────────────────
            for (int cx = 0; cx < bw; cx++)
            for (int cz = 0; cz < bw; cz++)
            {
                int   gx     = ox - overlap + cx, gz = oz - overlap + cz;
                float worldX = gx * s,            worldZ = gz * s;
                var   prof   = wg != null ? BlendProfile(worldX, worldZ, wg) : CaveProfile.Default;
                float locFac = LocationFactor(worldX, worldZ, nearbyLocs);
                float floorY = ComputeColumnFloor(worldX, worldZ, wg, prof.floorDepth);
                float h      = wg != null ? wg.GetHeight(worldX, worldZ) : 0f;

                // Mode B: per-column precompute (XZ-only quantities).
                int     layerCount = 0;
                float[] hmC = Array.Empty<float>(), hmF = Array.Empty<float>();
                if (prof.modeB_strength > 0f && wg != null)
                {
                    float wf    = prof.modeB_warpFrequency;
                    float ewAmp = prof.modeB_warpAmp * locFac;
                    float wx2   = _hmWarpX!.GetNoise(worldX * wf, worldZ * wf) * ewAmp;
                    float wz2   = _hmWarpZ!.GetNoise(worldX * wf, worldZ * wf) * ewAmp;
                    float df    = prof.modeB_ditherFrequency;
                    float warpedH = wg.GetHeight(worldX + wx2, worldZ + wz2)
                                  + _ditherNoise!.GetNoise(worldX * df, worldZ * df) * prof.modeB_ditherAmp;
                    layerCount = Mathf.Max(1, Mathf.RoundToInt(prof.modeB_layers));
                    hmC = new float[layerCount]; hmF = new float[layerCount];
                    for (int li = 0; li < layerCount; li++)
                    {
                        float lh = warpedH - li * prof.modeB_layerSpacing;
                        hmC[li] = lh + prof.modeB_radius;
                        hmF[li] = lh - prof.modeB_radius;
                    }
                }

                // Mode C: per-column anchor height + amplitude.
                float addH = 0f, localAmp = 0f;
                if (prof.modeC_strength > 0f && wg != null)
                {
                    float awf = prof.modeC_warpFrequency;
                    float awx = _hmAddWarpX!.GetNoise(worldX * awf, worldZ * awf) * prof.modeC_warpAmp;
                    float awz = _hmAddWarpZ!.GetNoise(worldX * awf, worldZ * awf) * prof.modeC_warpAmp;
                    addH      = wg.GetHeight(worldX + awx, worldZ + awz);
                    float avf = prof.modeC_ampFrequency;
                    localAmp  = Mathf.Max(prof.modeC_noiseAmp * (1f + prof.modeC_ampVary
                        * _addVaryNoise!.GetNoise(worldX * avf, worldZ * avf)), 0f);
                }

                for (int cy = 0; cy < bh; cy++)
                {
                    float worldY  = (extLo + cy) * s;
                    float baseSDF = worldY - h;    // base SDF: positive = air, negative = solid
                    float combined = baseSDF;

                    // Mode B: warped-heightmap band — air wins.
                    if (layerCount > 0)
                    {
                        float bandSDF = Mathf.Min(hmC[0] - worldY, worldY - hmF[0]);
                        for (int li = 1; li < layerCount; li++)
                            bandSDF = Mathf.Max(bandSDF, Mathf.Min(hmC[li] - worldY, worldY - hmF[li]));
                        float sdf = Mathf.Min(bandSDF, worldY - floorY) * prof.modeB_strength;
                        combined = Mathf.Max(combined, sdf);
                    }

                    // Mode C: 3D density field above heightmap — solid wins.
                    if (prof.modeC_strength > 0f && wg != null)
                    {
                        float relY  = worldY - addH;
                        float sy    = relY * prof.modeC_vScale;
                        float tvf   = prof.modeC_twistFrequency;
                        float twistScale = 1f + prof.modeC_twistVary
                            * _twistVaryNoise!.GetNoise(worldX * tvf, worldZ * tvf);
                        float angle = worldY * prof.modeC_twist * twistScale;
                        float cos   = Mathf.Cos(angle), sin = Mathf.Sin(angle);
                        float tx    = worldX * cos - worldZ * sin;
                        float tz    = worldX * sin + worldZ * cos;
                        float af    = prof.modeC_frequency;
                        float n     = _addNoise!.GetNoise(tx * af, sy * af, tz * af);
                        float sdf   = (relY - (n - prof.modeC_threshold) * localAmp) * prof.modeC_strength;
                        combined = Mathf.Min(combined, sdf);
                    }

                    // Mode A: noise tunnel network — air wins, punches through Mode C.
                    if (prof.modeA_strength > 0f)
                    {
                        float twf = prof.modeA_warpFrequency;
                        float tf  = prof.modeA_frequency;
                        float sy  = worldY * prof.modeA_vScale;
                        float wx  = _warpX!.GetNoise(worldX * twf, sy * twf, worldZ * twf) * prof.modeA_warpAmp;
                        float wz  = _warpZ!.GetNoise(worldX * twf, sy * twf, worldZ * twf) * prof.modeA_warpAmp;
                        float n   = _caveNoise!.GetNoise((worldX + wx) * tf, sy * tf, (worldZ + wz) * tf);
                        float wf2 = prof.modeA_widthFrequency;
                        float localWidth = Mathf.Max(prof.modeA_width * (1f + prof.modeA_widthVary
                            * _widthNoise!.GetNoise(worldX * wf2, sy * wf2, worldZ * wf2)), 0.001f);
                        float tunnelSDF = (localWidth - Mathf.Abs(n)) / localWidth * 4f * s;
                        float sdf = Mathf.Min(tunnelSDF, worldY - floorY) * prof.modeA_strength;
                        combined = Mathf.Max(combined, sdf);
                    }

                    // Fade all modifications to zero near protected locations.
                    // Lerping combined → baseSDF ensures locFac=0 leaves terrain completely
                    // unmodified, avoiding the max(baseSDF, 0) underground-carve bug that
                    // scaling individual SDFs by locFac produces.
                    buf[(cx * bh + cy) * bw + cz] = Mathf.Lerp(baseSDF, combined, locFac);
                }
            }

            // ── Phase 2: XZ-only Gaussian blur (Y never blurred — avoids terracing) ─
            // Each pass: X buf→tmp, Z tmp→buf. Result always lands in buf.
            // blur_passes=0 skips entirely; Phase 3 reads buf directly.
            // blurPasses sampled at zone centre before Phase 1
            for (int pass = 0; pass < blurPasses; pass++)
            {
                // X pass: buf → tmp
                for (int cx = 0; cx < bw; cx++)
                for (int cy = 0; cy < bh; cy++)
                for (int cz = 0; cz < bw; cz++)
                {
                    int i = (cx * bh + cy) * bw + cz;
                    tmp[i] = (cx > 0 && cx < bw - 1)
                        ? buf[((cx-1)*bh+cy)*bw+cz] * 0.25f + buf[i] * 0.5f + buf[((cx+1)*bh+cy)*bw+cz] * 0.25f
                        : buf[i];
                }
                // Z pass: tmp → buf
                for (int cx = 0; cx < bw; cx++)
                for (int cy = 0; cy < bh; cy++)
                for (int cz = 0; cz < bw; cz++)
                {
                    int i = (cx * bh + cy) * bw + cz;
                    buf[i] = (cz > 0 && cz < bw - 1)
                        ? tmp[(cx*bh+cy)*bw+(cz-1)] * 0.25f + tmp[i] * 0.5f + tmp[(cx*bh+cy)*bw+(cz+1)] * 0.25f
                        : tmp[i];
                }
            }

            // ── Phase 3: Write to VoxelField ──────────────────────────────────────
            int carved  = 0;
            int maxYC   = yHi;
            var brickSet = new HashSet<(int, int, int)>();

            // Per-column override arrays for surface-transition voxels.
            // Mode C's twist parameter rotates noise sampling coords with worldY, which
            // creates a large effective Y-gradient: |d(SDF)/dy| >> 1 voxel near any
            // non-zero XZ position. ToSDFByte then clamps both sides of every surface
            // pair to ±127, and marching cubes always interpolates to 0.5 → staircase.
            // Fix: at every sign change in buf, compute t = -belowVal/(aboveVal-belowVal).
            // This ratio is scale-invariant: correct even when both values are ±50 m.
            // Then write round(-t*127)/round((1-t)*127) (or the mirror for a base-of-solid
            // crossing) so the mesher gets the exact sub-voxel position for every surface,
            // including secondary ones (floating masses, cave ceilings inside Mode C).
            var transBytes  = new sbyte[bh];
            var transHasOvr = new bool[bh];

            for (int ci = 0; ci < width; ci++)
            for (int ri = 0; ri < width; ri++)
            {
                int cx = ci + overlap, cz = ri + overlap;
                int gx = ox + ci,       gz = oz + ri;

                Array.Clear(transHasOvr, 0, bh);
                {
                    float av = buf[(cx * bh + (bh - 1)) * bw + cz];
                    for (int cy = bh - 2; cy >= 0; cy--)
                    {
                        float bv = buf[(cx * bh + cy) * bw + cz];
                        bool topSurf = av > 0f && bv <= 0f; // air above, solid below
                        bool botSurf = av <= 0f && bv > 0f; // solid above, air below
                        if (topSurf || botSurf)
                        {
                            float d = av - bv;
                            float t = Mathf.Clamp01(d != 0f ? (-bv) / d : 0.5f);
                            sbyte lo, hi;
                            if (topSurf) { lo = (sbyte)(int)Math.Round(-t * 127f);  hi = (sbyte)(int)Math.Round((1f - t) * 127f);  }
                            else         { lo = (sbyte)(int)Math.Round(t * 127f);   hi = (sbyte)(int)Math.Round(-(1f - t) * 127f); }
                            transBytes[cy]     = lo; transHasOvr[cy]     = true;
                            transBytes[cy + 1] = hi; transHasOvr[cy + 1] = true;
                        }
                        av = bv;
                    }
                }

                for (int gy = extLo; gy <= extHi; gy++)
                {
                    int   cy       = gy - extLo;
                    float finalSDF = buf[(cx * bh + cy) * bw + cz];
                    sbyte newByte  = transHasOvr[cy] ? transBytes[cy] : ToSDFByte(finalSDF, s);
                    sbyte curByte  = _get!(field, gx, gy, gz);
                    if (newByte == curByte) continue;
                    _set!(field, gx, gy, gz, newByte);
                    brickSet.Add((gx >> 4, gy >> 4, gz >> 4));
                    if (newByte < 0 && gy > maxYC) maxYC = gy;
                    carved++;
                }
            }

            if (maxYC > yHi)
            {
                if (!_zoneMaxY.TryGetValue(zoneKey, out int prevMaxY) || maxYC > prevMaxY)
                    _zoneMaxY[zoneKey] = maxYC;
            }

            _pendingBricks[zoneKey] = brickSet;

            // Each zone activation may spawn new vegetation — schedule a deferred sweep.
            // Triggered per-zone rather than on a timer to avoid a destroy/respawn fight.
            if (_disableVegetation) _pendingVegCleanup = true;

            Log.LogInfo($"[VoxCaves] zone ({ox},{oz}) yLo={yLo} yHi={yHi} carved={carved} bricks={brickSet.Count}");
        }

        // ── Hot-reload: re-run the full voxel pipeline on every loaded zone ───────
        static void RecarveAll()
        {
            Log.LogInfo($"[VoxCaves] Hot-reload: re-voxelizing {_zoneParams.Count} zone(s).");
            if (_voxelizeColumnsMethod == null) return;
            bool canRemesh = _enqueueMeshMethod != null && _finishSkinNowMethod != null
                             && _int3Ctor != null;
            if (!canRemesh)
                Log.LogWarning("[VoxCaves] EnqueueMesh/FinishSkinNow unavailable — mesh rebuild skipped.");

            var snapshot = new List<(string key, (object field, object grid, int ox, int oz, int w, int yLo, int yHi) zp)>(_zoneParams.Count);
            foreach (var kv in _zoneParams) snapshot.Add((kv.Key, kv.Value));

            foreach (var (key, zp) in snapshot)
            {
                // Reset range must cover the full Mode C extension (same formula as Carve()).
                int extLo = zp.yLo - 24;
                int hiMarginRc = 24;
                {
                    float sRc = _scale is { } sv && sv > 0f ? sv : 1f;
                    var wgRc  = WorldGenerator.instance;
                    if (wgRc != null && sRc > 0f)
                    {
                        float cxRc = (zp.ox + zp.w * 0.5f) * sRc;
                        float czRc = (zp.oz + zp.w * 0.5f) * sRc;
                        var cpRc   = BlendProfile(cxRc, czRc, wgRc);
                        if (cpRc.modeC_strength > 0f)
                        {
                            float maxAmpRc  = cpRc.modeC_noiseAmp * Mathf.Max(1f, 1f + cpRc.modeC_ampVary);
                            float maxExtMRc = Mathf.Max(0f, 1f - cpRc.modeC_threshold) * maxAmpRc
                                           + cpRc.modeC_warpAmp;
                            hiMarginRc = Mathf.Max(24, (int)Mathf.Ceil(maxExtMRc / sRc) + 8);
                        }
                    }
                }
                int extHi = zp.yHi + hiMarginRc;
                try
                {
                    _voxelizeColumnsMethod.Invoke(null, new object[]
                        { zp.field, zp.grid, zp.ox, zp.oz, 0, zp.w - 1, 0, zp.w - 1, extLo, extHi });
                }
                catch (Exception e) { Log.LogWarning($"[VoxCaves] Revoxelize {key}: {e.Message}"); continue; }

                if (!_zoneObjects.TryGetValue(key, out var zoneObj)) continue;
                // Skip zones whose MonoBehaviour has been destroyed (fell out of active radius).
                if (zoneObj is UnityEngine.Object uObj && !uObj) continue;
                if (!canRemesh) continue;

                // Enqueue the full extended brick range so all modified voxels get remeshed.
                _pendingBricks.Remove(key, out _);
                int bx0 = zp.ox >> 4, bx1 = (zp.ox + zp.w - 1) >> 4;
                int bz0 = zp.oz >> 4, bz1 = (zp.oz + zp.w - 1) >> 4;
                int by0 = extLo >> 4, by1 = extHi >> 4;
                int queued = 0;
                for (int bx = bx0; bx <= bx1; bx++)
                for (int by = by0; by <= by1; by++)
                for (int bz = bz0; bz <= bz1; bz++)
                {
                    try
                    {
                        var i3 = _int3Ctor!.Invoke(new object[] { bx, by, bz });
                        _enqueueMeshMethod!.Invoke(zoneObj, new[] { i3 });
                        queued++;
                    }
                    catch (Exception e) { Log.LogWarning($"[VoxCaves] EnqueueMesh {key}: {e.Message}"); break; }
                }
                Log.LogInfo($"[VoxCaves] Queued {queued} bricks for zone {key}.");

                try { _finishSkinNowMethod!.Invoke(zoneObj, null); }
                catch (Exception e) { Log.LogWarning($"[VoxCaves] FinishSkinNow {key}: {e.Message}"); }

                // Tell neighboring zones to update their boundary meshes so seams clear too.
                if (_pokeNeighborsMethod != null)
                {
                    try { _pokeNeighborsMethod.Invoke(zoneObj, null); }
                    catch (Exception e) { Log.LogWarning($"[VoxCaves] PokeNeighbors {key}: {e.Message}"); }
                }
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
            var prof = wg != null ? BlendProfile(worldX, worldZ, wg) : CaveProfile.Default;
            float y = originalY;
            for (int i = 0; i < 256; i++, y -= s)
                if (TunnelDensity(worldX, y, worldZ, floorY, prof) <= 0f) return y;
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

            Plugin._zoneObjects[key] = __instance;

            // Expand _yScanHi so SurfaceEnvelope finds Mode C solid written above the original yHi
            if (Plugin._yScanHiField != null && Plugin._zoneMaxY.TryGetValue(key, out int maxY))
            {
                int cur = (int)Plugin._yScanHiField.GetValue(__instance)!;
                if (maxY > cur) Plugin._yScanHiField.SetValue(__instance, maxY);
                Plugin._zoneMaxY.Remove(key);
            }

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
            Plugin._pendingVegCleanup = true; // run cliff + disableVegetation cleanup after world loads
            Plugin._pendingBricks.Clear();
            Plugin._zoneObjects.Clear();
            Plugin._zoneParams.Clear();
            Plugin._zoneMaxY.Clear();
            Plugin._locationPositions.Clear();
            Plugin._locationsCached = false;
            Plugin._caveNoise    = null;
            Plugin._ditherNoise  = null;
            Plugin._warpX        = null;
            Plugin._warpZ        = null;
            Plugin._hmWarpX      = null;
            Plugin._hmWarpZ      = null;
            Plugin._hmAddWarpX   = null;
            Plugin._hmAddWarpZ   = null;
            Plugin._addNoise        = null;
            Plugin._addVaryNoise    = null;
            Plugin._widthNoise      = null;
            Plugin._twistVaryNoise  = null;
        }
    }

    [HarmonyPatch]
    static class PlaceVegetationPatch
    {
        static FieldInfo? _zdoPosField;

        static MethodBase? TargetMethod() =>
            typeof(ZoneSystem).GetMethod("PlaceVegetation",
                BindingFlags.NonPublic | BindingFlags.Instance);

        static bool Prefix() => !Plugin._disableVegetation;

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
