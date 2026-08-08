using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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

        // ── Per-brick SDF cache ───────────────────────────────────────────────────
        internal static ConstructorInfo? _int3Ctor;
        internal static FieldInfo?       _zoneOriginX;
        internal static FieldInfo?       _zoneOriginZ;
        internal static FieldInfo?       _yScanHiField;
        internal static FieldInfo?       _voxelZoneFieldField;  // VoxelZone._field → VoxelField

        // VoxelBounds fields — used to construct the changed-bounds arg for PokeNeighbors
        internal static Type?       _voxelBoundsType;
        internal static FieldInfo?  _vbMinField;
        internal static FieldInfo?  _vbMaxField;
        internal static FieldInfo?  _vbAnyField;
        internal static readonly Dictionary<string, int> _zoneMaxY  = new();
        internal static readonly Dictionary<string, int> _zoneExtHi = new(); // highest voxel Y ever written; used to size the reset range in hot-reload

        // BlendProfile memoization: keyed by (gx4 << 32 | (uint)gz4) — coarse voxel coords.
        // Cleared on world load and config reload; persists through RecarveAll (config unchanged).
        internal static readonly Dictionary<long, CaveProfile> _profileCache = new();

        // Scratch buffers for floating-island BFS (Phase 1.5). Pre-allocated, resized on demand.
        static bool[]? _visitedBuf;
        static int[]?  _islandQueue;

        // wg.GetHeight is a pure function of (worldX, worldZ) for a given world seed.
        // Cache it keyed by integer coarse voxel coords — cleared on world load only.
        internal static readonly Dictionary<long, float> _heightCache = new();

        // Per-zone SDF cache: filled by Phase 3, queried by FallbackDensityPatch.
        // Layout: data[(ci * bh + cy) * width + ri]  (ci=zoneX, ri=zoneZ, cy=Y-extLo)
        internal static readonly Dictionary<string, (int extLo, int bh, int width, sbyte[] data)> _sdfCache = new();

        // Int3 field accessors — used by FallbackDensityPatch to extract X/Y/Z from boxed struct.
        internal static FieldInfo? _int3FieldX, _int3FieldY, _int3FieldZ;

        // Reused across Carve() calls to avoid per-zone GC pressure.
        internal static float[]? _bufA;
        internal static float[]? _bufB;

        internal static float[]? _noiseBuf;  // coarse Mode C bake: [cx4*(cw4*bh) + cz4*bh + cy]
        internal static float[]? _tunBuf;   // coarse Mode A: tunnel noise (after warp)
        internal static float[]? _tunWBuf;  // coarse Mode A: width modulation noise
        // 2D coarse bake (same XZ grid as _noiseBuf): [cx4*cw4 + cz4]
        internal static float[]? _bk2dAddH;  // Mode C anchor height
        internal static float[]? _bk2dLAmp;  // Mode C local amplitude
        internal static float[]? _bk2dMcS;   // modeC_strength
        internal static float[]? _bk2dMcT;   // modeC_threshold
        internal static float[]? _bk2dFlD;   // floorDepth
        internal static float[]? _bk2dMbS;   // modeB_strength
        internal static float[]? _bk2dMaS;   // modeA_strength
        // 2D coarse bake — Mode B shape params
        internal static float[]? _bk2dMbwf;  // modeB_warpFrequency
        internal static float[]? _bk2dMbwa;  // modeB_warpAmp
        internal static float[]? _bk2dMbr;   // modeB_radius
        internal static float[]? _bk2dMbdf;  // modeB_ditherFrequency
        internal static float[]? _bk2dMbda;  // modeB_ditherAmp
        internal static float[]? _bk2dMbls;  // modeB_layerSpacing
        internal static float[]? _bk2dMbl;   // modeB_layers
        // 2D coarse bake — Mode A shape params
        internal static float[]? _bk2dMawf;  // modeA_warpFrequency
        internal static float[]? _bk2dMawa;  // modeA_warpAmp
        internal static float[]? _bk2dMaf;   // modeA_frequency
        internal static float[]? _bk2dMavs;  // modeA_vScale
        internal static float[]? _bk2dMaw;   // modeA_width
        internal static float[]? _bk2dMawv;  // modeA_widthVary
        internal static float[]? _bk2dMawvf; // modeA_widthFrequency
        // Full-res (bw×bw) — Mode B warped heightmap (wg.GetHeight after XZ warp, before dither)
        internal static float[]? _bkWarpedH;

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

            _enqueueMeshMethod   = zoneT?.GetMethod("EnqueueMesh",
                BindingFlags.NonPublic | BindingFlags.Instance);
            _finishSkinNowMethod = zoneT?.GetMethod("FinishSkinNow",
                BindingFlags.NonPublic | BindingFlags.Instance);
            // PokeNeighbors(in VoxelBounds, bool) — tells adjacent active zones to remesh boundary bricks.
            // PokeTransitionNeighbors only iterates _editedBricks (player edits), so we call PokeNeighbors directly.
            _pokeNeighborsMethod = zoneT?.GetMethods(BindingFlags.NonPublic | BindingFlags.Instance)
                .FirstOrDefault(m => m.Name == "PokeNeighbors" && m.GetParameters().Length == 2
                                     && m.GetParameters()[1].ParameterType == typeof(bool));
            if (_enqueueMeshMethod   == null) Log.LogWarning("[VoxCaves] EnqueueMesh not found.");
            if (_finishSkinNowMethod == null) Log.LogWarning("[VoxCaves] FinishSkinNow not found.");
            if (_pokeNeighborsMethod == null) Log.LogWarning("[VoxCaves] PokeNeighbors not found — neighbor seam repair disabled.");

            var boundsT = AppDomain.CurrentDomain.GetAssemblies()
                .SelectMany(a => { try { return a.GetTypes(); } catch { return Array.Empty<Type>(); } })
                .FirstOrDefault(t => t.FullName == "Voxheim.Core.VoxelBounds");
            _voxelBoundsType = boundsT;
            _vbMinField = boundsT?.GetField("Min", BindingFlags.Public | BindingFlags.Instance);
            _vbMaxField = boundsT?.GetField("Max", BindingFlags.Public | BindingFlags.Instance);
            _vbAnyField = boundsT?.GetField("Any", BindingFlags.Public | BindingFlags.Instance);
            if (_voxelBoundsType == null) Log.LogWarning("[VoxCaves] VoxelBounds type not found — neighbor seam repair disabled.");

            _zoneOriginX         = zoneT?.GetField("_originX",
                BindingFlags.NonPublic | BindingFlags.Instance);
            _zoneOriginZ         = zoneT?.GetField("_originZ",
                BindingFlags.NonPublic | BindingFlags.Instance);
            _voxelZoneFieldField = zoneT?.GetField("_field",
                BindingFlags.NonPublic | BindingFlags.Instance);
            _yScanHiField        = zoneT?.GetField("_yScanHi",
                BindingFlags.NonPublic | BindingFlags.Instance);
            if (_zoneOriginX == null || _zoneOriginZ == null)
                Log.LogWarning("[VoxCaves] Zone origin fields missing — per-brick SDF disabled.");
            if (_voxelZoneFieldField == null)
                Log.LogWarning("[VoxCaves] VoxelZone._field not found — per-brick SDF disabled.");
            if (_yScanHiField == null)
                Log.LogWarning("[VoxCaves] _yScanHi not found — Mode C ceiling extension disabled.");

            _locationInstancesField =
                typeof(ZoneSystem).GetField("m_locationInstances",
                    BindingFlags.NonPublic | BindingFlags.Instance) ??
                typeof(ZoneSystem).GetField("m_locationInstances",
                    BindingFlags.Public    | BindingFlags.Instance);
            if (_locationInstancesField == null)
                Log.LogWarning("[VoxCaves] m_locationInstances not found — location protection off.");

            _int3FieldX = int3T.GetField("X", BindingFlags.Public | BindingFlags.Instance)
                          ?? int3T.GetField("x", BindingFlags.Public | BindingFlags.Instance);
            _int3FieldY = int3T.GetField("Y", BindingFlags.Public | BindingFlags.Instance)
                          ?? int3T.GetField("y", BindingFlags.Public | BindingFlags.Instance);
            _int3FieldZ = int3T.GetField("Z", BindingFlags.Public | BindingFlags.Instance)
                          ?? int3T.GetField("z", BindingFlags.Public | BindingFlags.Instance);
            if (_int3FieldX == null || _int3FieldY == null || _int3FieldZ == null)
                Log.LogWarning("[VoxCaves] Int3 X/Y/Z fields not found — FallbackDensity patch will use vanilla.");

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
            _profileCache.Clear();
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
            foreach (var kv in profiles)
            {
                var p = kv.Value;
                if (p.modeA_strength > 0f || p.modeB_strength > 0f || p.modeC_strength > 0f)
                    Log.LogInfo($"[VoxCaves]   {kv.Key}: mB={p.modeB_strength:F2} mA={p.modeA_strength:F2} mC={p.modeC_strength:F2}");
            }
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

        // ── Biome profile blending ────────────────────────────────────────────────
        static CaveProfile BlendProfile(float worldX, float worldZ, WorldGenerator wg)
        {
            const float blendR = 48f;
            var profs = _biomeProfiles;

            CaveProfile Get(Heightmap.Biome b) =>
                profs.TryGetValue(b, out var p) ? p : CaveProfile.Default;

            // 3×3 Gaussian kernel (σ = blendR).
            // Cardinals at distance blendR  → weight exp(-1) ≈ 0.368.
            // Diagonals at distance blendR√2 → weight exp(-2) ≈ 0.135.
            // Normalized so all 9 weights sum to 1.
            const float wC = 0.33191f, wK = 0.12212f, wD = 0.04491f;

            var p0  = Get(wg.GetBiome(worldX,          worldZ         ));
            var pE  = Get(wg.GetBiome(worldX + blendR, worldZ         ));
            var pW  = Get(wg.GetBiome(worldX - blendR, worldZ         ));
            var pN  = Get(wg.GetBiome(worldX,          worldZ + blendR));
            var pS  = Get(wg.GetBiome(worldX,          worldZ - blendR));
            var pNE = Get(wg.GetBiome(worldX + blendR, worldZ + blendR));
            var pNW = Get(wg.GetBiome(worldX - blendR, worldZ + blendR));
            var pSE = Get(wg.GetBiome(worldX + blendR, worldZ - blendR));
            var pSW = Get(wg.GetBiome(worldX - blendR, worldZ - blendR));

            float K(float c, float e, float w, float n, float s,
                    float ne, float nw, float se, float sw) =>
                c*wC + (e+w+n+s)*wK + (ne+nw+se+sw)*wD;

            return new CaveProfile
            {
                modeB_strength       = K(p0.modeB_strength,       pE.modeB_strength,       pW.modeB_strength,       pN.modeB_strength,       pS.modeB_strength,       pNE.modeB_strength,       pNW.modeB_strength,       pSE.modeB_strength,       pSW.modeB_strength      ),
                modeB_radius         = K(p0.modeB_radius,         pE.modeB_radius,         pW.modeB_radius,         pN.modeB_radius,         pS.modeB_radius,         pNE.modeB_radius,         pNW.modeB_radius,         pSE.modeB_radius,         pSW.modeB_radius        ),
                modeB_warpAmp        = K(p0.modeB_warpAmp,        pE.modeB_warpAmp,        pW.modeB_warpAmp,        pN.modeB_warpAmp,        pS.modeB_warpAmp,        pNE.modeB_warpAmp,        pNW.modeB_warpAmp,        pSE.modeB_warpAmp,        pSW.modeB_warpAmp       ),
                modeB_warpFrequency  = K(p0.modeB_warpFrequency,  pE.modeB_warpFrequency,  pW.modeB_warpFrequency,  pN.modeB_warpFrequency,  pS.modeB_warpFrequency,  pNE.modeB_warpFrequency,  pNW.modeB_warpFrequency,  pSE.modeB_warpFrequency,  pSW.modeB_warpFrequency ),
                modeB_ditherAmp      = K(p0.modeB_ditherAmp,      pE.modeB_ditherAmp,      pW.modeB_ditherAmp,      pN.modeB_ditherAmp,      pS.modeB_ditherAmp,      pNE.modeB_ditherAmp,      pNW.modeB_ditherAmp,      pSE.modeB_ditherAmp,      pSW.modeB_ditherAmp     ),
                modeB_ditherFrequency= K(p0.modeB_ditherFrequency,pE.modeB_ditherFrequency,pW.modeB_ditherFrequency,pN.modeB_ditherFrequency,pS.modeB_ditherFrequency,pNE.modeB_ditherFrequency,pNW.modeB_ditherFrequency,pSE.modeB_ditherFrequency,pSW.modeB_ditherFrequency),
                modeB_layers         = K(p0.modeB_layers,         pE.modeB_layers,         pW.modeB_layers,         pN.modeB_layers,         pS.modeB_layers,         pNE.modeB_layers,         pNW.modeB_layers,         pSE.modeB_layers,         pSW.modeB_layers        ),
                modeB_layerSpacing   = K(p0.modeB_layerSpacing,   pE.modeB_layerSpacing,   pW.modeB_layerSpacing,   pN.modeB_layerSpacing,   pS.modeB_layerSpacing,   pNE.modeB_layerSpacing,   pNW.modeB_layerSpacing,   pSE.modeB_layerSpacing,   pSW.modeB_layerSpacing  ),
                modeA_strength       = K(p0.modeA_strength,       pE.modeA_strength,       pW.modeA_strength,       pN.modeA_strength,       pS.modeA_strength,       pNE.modeA_strength,       pNW.modeA_strength,       pSE.modeA_strength,       pSW.modeA_strength      ),
                modeA_width          = K(p0.modeA_width,          pE.modeA_width,          pW.modeA_width,          pN.modeA_width,          pS.modeA_width,          pNE.modeA_width,          pNW.modeA_width,          pSE.modeA_width,          pSW.modeA_width         ),
                modeA_frequency      = K(p0.modeA_frequency,      pE.modeA_frequency,      pW.modeA_frequency,      pN.modeA_frequency,      pS.modeA_frequency,      pNE.modeA_frequency,      pNW.modeA_frequency,      pSE.modeA_frequency,      pSW.modeA_frequency     ),
                modeA_warpAmp        = K(p0.modeA_warpAmp,        pE.modeA_warpAmp,        pW.modeA_warpAmp,        pN.modeA_warpAmp,        pS.modeA_warpAmp,        pNE.modeA_warpAmp,        pNW.modeA_warpAmp,        pSE.modeA_warpAmp,        pSW.modeA_warpAmp       ),
                modeA_warpFrequency  = K(p0.modeA_warpFrequency,  pE.modeA_warpFrequency,  pW.modeA_warpFrequency,  pN.modeA_warpFrequency,  pS.modeA_warpFrequency,  pNE.modeA_warpFrequency,  pNW.modeA_warpFrequency,  pSE.modeA_warpFrequency,  pSW.modeA_warpFrequency ),
                modeA_vScale         = K(p0.modeA_vScale,         pE.modeA_vScale,         pW.modeA_vScale,         pN.modeA_vScale,         pS.modeA_vScale,         pNE.modeA_vScale,         pNW.modeA_vScale,         pSE.modeA_vScale,         pSW.modeA_vScale        ),
                modeA_widthVary      = K(p0.modeA_widthVary,      pE.modeA_widthVary,      pW.modeA_widthVary,      pN.modeA_widthVary,      pS.modeA_widthVary,      pNE.modeA_widthVary,      pNW.modeA_widthVary,      pSE.modeA_widthVary,      pSW.modeA_widthVary     ),
                modeA_widthFrequency = K(p0.modeA_widthFrequency, pE.modeA_widthFrequency, pW.modeA_widthFrequency, pN.modeA_widthFrequency, pS.modeA_widthFrequency, pNE.modeA_widthFrequency, pNW.modeA_widthFrequency, pSE.modeA_widthFrequency, pSW.modeA_widthFrequency),
                modeC_strength       = K(p0.modeC_strength,       pE.modeC_strength,       pW.modeC_strength,       pN.modeC_strength,       pS.modeC_strength,       pNE.modeC_strength,       pNW.modeC_strength,       pSE.modeC_strength,       pSW.modeC_strength      ),
                modeC_threshold      = K(p0.modeC_threshold,      pE.modeC_threshold,      pW.modeC_threshold,      pN.modeC_threshold,      pS.modeC_threshold,      pNE.modeC_threshold,      pNW.modeC_threshold,      pSE.modeC_threshold,      pSW.modeC_threshold     ),
                modeC_noiseAmp       = K(p0.modeC_noiseAmp,       pE.modeC_noiseAmp,       pW.modeC_noiseAmp,       pN.modeC_noiseAmp,       pS.modeC_noiseAmp,       pNE.modeC_noiseAmp,       pNW.modeC_noiseAmp,       pSE.modeC_noiseAmp,       pSW.modeC_noiseAmp      ),
                modeC_frequency      = K(p0.modeC_frequency,      pE.modeC_frequency,      pW.modeC_frequency,      pN.modeC_frequency,      pS.modeC_frequency,      pNE.modeC_frequency,      pNW.modeC_frequency,      pSE.modeC_frequency,      pSW.modeC_frequency     ),
                modeC_warpAmp        = K(p0.modeC_warpAmp,        pE.modeC_warpAmp,        pW.modeC_warpAmp,        pN.modeC_warpAmp,        pS.modeC_warpAmp,        pNE.modeC_warpAmp,        pNW.modeC_warpAmp,        pSE.modeC_warpAmp,        pSW.modeC_warpAmp       ),
                modeC_warpFrequency  = K(p0.modeC_warpFrequency,  pE.modeC_warpFrequency,  pW.modeC_warpFrequency,  pN.modeC_warpFrequency,  pS.modeC_warpFrequency,  pNE.modeC_warpFrequency,  pNW.modeC_warpFrequency,  pSE.modeC_warpFrequency,  pSW.modeC_warpFrequency ),
                modeC_vScale         = K(p0.modeC_vScale,         pE.modeC_vScale,         pW.modeC_vScale,         pN.modeC_vScale,         pS.modeC_vScale,         pNE.modeC_vScale,         pNW.modeC_vScale,         pSE.modeC_vScale,         pSW.modeC_vScale        ),
                modeC_twist          = K(p0.modeC_twist,          pE.modeC_twist,          pW.modeC_twist,          pN.modeC_twist,          pS.modeC_twist,          pNE.modeC_twist,          pNW.modeC_twist,          pSE.modeC_twist,          pSW.modeC_twist         ),
                modeC_twistVary      = K(p0.modeC_twistVary,      pE.modeC_twistVary,      pW.modeC_twistVary,      pN.modeC_twistVary,      pS.modeC_twistVary,      pNE.modeC_twistVary,      pNW.modeC_twistVary,      pSE.modeC_twistVary,      pSW.modeC_twistVary     ),
                modeC_twistFrequency = K(p0.modeC_twistFrequency, pE.modeC_twistFrequency, pW.modeC_twistFrequency, pN.modeC_twistFrequency, pS.modeC_twistFrequency, pNE.modeC_twistFrequency, pNW.modeC_twistFrequency, pSE.modeC_twistFrequency, pSW.modeC_twistFrequency),
                modeC_ampVary        = K(p0.modeC_ampVary,        pE.modeC_ampVary,        pW.modeC_ampVary,        pN.modeC_ampVary,        pS.modeC_ampVary,        pNE.modeC_ampVary,        pNW.modeC_ampVary,        pSE.modeC_ampVary,        pSW.modeC_ampVary       ),
                modeC_ampFrequency   = K(p0.modeC_ampFrequency,   pE.modeC_ampFrequency,   pW.modeC_ampFrequency,   pN.modeC_ampFrequency,   pS.modeC_ampFrequency,   pNE.modeC_ampFrequency,   pNW.modeC_ampFrequency,   pSE.modeC_ampFrequency,   pSW.modeC_ampFrequency  ),
                blur_passes          = K(p0.blur_passes,          pE.blur_passes,          pW.blur_passes,          pN.blur_passes,          pS.blur_passes,          pNE.blur_passes,          pNW.blur_passes,          pSE.blur_passes,          pSW.blur_passes         ),
                floorDepth           = K(p0.floorDepth,           pE.floorDepth,           pW.floorDepth,           pN.floorDepth,           pS.floorDepth,           pNE.floorDepth,           pNW.floorDepth,           pSE.floorDepth,           pSW.floorDepth          ),
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

            float zoneX = (ox + width * 0.5f) * s;
            float zoneZ = (oz + width * 0.5f) * s;

            string zoneKey = $"{ox},{oz}";
            _zoneParams[zoneKey] = (field, grid, ox, oz, width, yLo, yHi);

            // If location data isn't ready yet, leave the zone vanilla.
            if (!_locationsCached)
            {
                Log.LogInfo($"[VoxCaves] zone ({ox},{oz}) skip=no-locations");
                return;
            }

            // Sample centre profile once for structural params (blur, Mode C margin).
            var centerProf = wg != null ? BlendProfile(zoneX, zoneZ, wg) : CaveProfile.Default;
            int blurPasses = (int)centerProf.blur_passes;

            var centerBiome = wg?.GetBiome(zoneX, zoneZ) ?? Heightmap.Biome.None;

            // All modes off: leave Voxheim's VoxelField untouched — vanilla terrain is already correct.
            if (centerProf.modeA_strength <= 0f && centerProf.modeB_strength <= 0f && centerProf.modeC_strength <= 0f)
            {
                Log.LogInfo($"[VoxCaves] zone ({ox},{oz}) skip=all-off biome={centerBiome}");
                return;
            }

            Log.LogInfo($"[VoxCaves] zone ({ox},{oz}) biome={centerBiome} mB={centerProf.modeB_strength:F2} mA={centerProf.modeA_strength:F2} mC={centerProf.modeC_strength:F2}");

            const int overlap = 4;

            // Build nearby-location list for the location-protection fade.
            // Done after the early exit so vanilla zones pay zero cost.
            float searchR = width * s * 0.5f + LocProtectR + LocFadeR;
            var nearbyLocs = new List<Vector2>();
            foreach (var loc in _locationPositions)
            {
                float dx = loc.x - zoneX, dz = loc.y - zoneZ;
                if (dx * dx + dz * dz < searchR * searchR)
                    nearbyLocs.Add(loc);
            }

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

            var _sw = System.Diagnostics.Stopwatch.StartNew();

            // ── Phase 0: Bake Mode C noise at 1/4 XZ, full Y ────────────────────────
            // modeC_frequency = 0.008 → 1 cycle per 125 voxels; Nyquist allows a sample
            // every 62 voxels, so 4-voxel coarse XZ is well within the faithful range.
            // Layout: _noiseBuf[cx4 * (cw4*bh) + cz4 * bh + cy] — cy-major per (cx4,cz4)
            // so the Y inner loop reads four sequential streams, prefetcher-friendly.
            int   cw4     = (bw + 3) / 4;
            bool  bakeC   = centerProf.modeC_strength > 0f && wg != null;
            bool  bakeA   = centerProf.modeA_strength > 0f && wg != null;
            int   nb4Size = cw4 * cw4 * bh;
            int   nb2Size = cw4 * cw4;
            if (_noiseBuf  == null || _noiseBuf.Length  < nb4Size) _noiseBuf  = new float[nb4Size];
            if (_tunBuf    == null || _tunBuf.Length    < nb4Size) _tunBuf    = new float[nb4Size];
            if (_tunWBuf   == null || _tunWBuf.Length   < nb4Size) _tunWBuf   = new float[nb4Size];
            if (_bk2dAddH  == null || _bk2dAddH.Length  < nb2Size) _bk2dAddH  = new float[nb2Size];
            if (_bk2dLAmp  == null || _bk2dLAmp.Length  < nb2Size) _bk2dLAmp  = new float[nb2Size];
            if (_bk2dMcS   == null || _bk2dMcS.Length   < nb2Size) _bk2dMcS   = new float[nb2Size];
            if (_bk2dMcT   == null || _bk2dMcT.Length   < nb2Size) _bk2dMcT   = new float[nb2Size];
            if (_bk2dFlD   == null || _bk2dFlD.Length   < nb2Size) _bk2dFlD   = new float[nb2Size];
            if (_bk2dMbS   == null || _bk2dMbS.Length   < nb2Size) _bk2dMbS   = new float[nb2Size];
            if (_bk2dMaS   == null || _bk2dMaS.Length   < nb2Size) _bk2dMaS   = new float[nb2Size];
            if (_bk2dMbwf  == null || _bk2dMbwf.Length  < nb2Size) _bk2dMbwf  = new float[nb2Size];
            if (_bk2dMbwa  == null || _bk2dMbwa.Length  < nb2Size) _bk2dMbwa  = new float[nb2Size];
            if (_bk2dMbr   == null || _bk2dMbr.Length   < nb2Size) _bk2dMbr   = new float[nb2Size];
            if (_bk2dMbdf  == null || _bk2dMbdf.Length  < nb2Size) _bk2dMbdf  = new float[nb2Size];
            if (_bk2dMbda  == null || _bk2dMbda.Length  < nb2Size) _bk2dMbda  = new float[nb2Size];
            if (_bk2dMbls  == null || _bk2dMbls.Length  < nb2Size) _bk2dMbls  = new float[nb2Size];
            if (_bk2dMbl   == null || _bk2dMbl.Length   < nb2Size) _bk2dMbl   = new float[nb2Size];
            if (_bk2dMawf  == null || _bk2dMawf.Length  < nb2Size) _bk2dMawf  = new float[nb2Size];
            if (_bk2dMawa  == null || _bk2dMawa.Length  < nb2Size) _bk2dMawa  = new float[nb2Size];
            if (_bk2dMaf   == null || _bk2dMaf.Length   < nb2Size) _bk2dMaf   = new float[nb2Size];
            if (_bk2dMavs  == null || _bk2dMavs.Length  < nb2Size) _bk2dMavs  = new float[nb2Size];
            if (_bk2dMaw   == null || _bk2dMaw.Length   < nb2Size) _bk2dMaw   = new float[nb2Size];
            if (_bk2dMawv  == null || _bk2dMawv.Length  < nb2Size) _bk2dMawv  = new float[nb2Size];
            if (_bk2dMawvf == null || _bk2dMawvf.Length < nb2Size) _bk2dMawvf = new float[nb2Size];
            // 2D param bake — always runs so Phase 1 can use bilinear interpolation
            // instead of calling BlendProfile per column (saves ~9600 GetBiome calls/zone).
            bool anyMbActive = false;
            for (int cx4 = 0; cx4 < cw4; cx4++)
            for (int cz4 = 0; cz4 < cw4; cz4++)
            {
                int   cx4w  = Math.Min(cx4 * 4, bw - 1);
                int   cz4w  = Math.Min(cz4 * 4, bw - 1);
                int   gx4   = ox - overlap + cx4w, gz4 = oz - overlap + cz4w;
                float wx4   = gx4 * s, wz4 = gz4 * s;
                long  pKey  = (long)gx4 << 32 | (uint)gz4;
                if (!Plugin._profileCache.TryGetValue(pKey, out var p4))
                {
                    p4 = wg != null ? BlendProfile(wx4, wz4, wg) : CaveProfile.Default;
                    if (wg != null) Plugin._profileCache[pKey] = p4;
                }
                int   idx2  = cx4 * cw4 + cz4;

                _bk2dMcS! [idx2] = p4.modeC_strength;
                _bk2dMcT! [idx2] = p4.modeC_threshold;
                _bk2dFlD! [idx2] = p4.floorDepth;
                _bk2dMbS! [idx2] = p4.modeB_strength;
                _bk2dMaS! [idx2] = p4.modeA_strength;
                _bk2dMbwf![idx2] = p4.modeB_warpFrequency;
                _bk2dMbwa![idx2] = p4.modeB_warpAmp;
                _bk2dMbr! [idx2] = p4.modeB_radius;
                _bk2dMbdf![idx2] = p4.modeB_ditherFrequency;
                _bk2dMbda![idx2] = p4.modeB_ditherAmp;
                _bk2dMbls![idx2] = p4.modeB_layerSpacing;
                _bk2dMbl! [idx2] = p4.modeB_layers;
                _bk2dMawf![idx2] = p4.modeA_warpFrequency;
                _bk2dMawa![idx2] = p4.modeA_warpAmp;
                _bk2dMaf! [idx2] = p4.modeA_frequency;
                _bk2dMavs![idx2] = p4.modeA_vScale;
                _bk2dMaw! [idx2] = p4.modeA_width;
                _bk2dMawv![idx2] = p4.modeA_widthVary;
                _bk2dMawvf![idx2]= p4.modeA_widthFrequency;
                if (p4.modeB_strength > 0f) anyMbActive = true;

                // Mode C anchor quantities — only needed when modeC is active.
                if (bakeC && p4.modeC_strength > 0f)
                {
                    float awf4  = p4.modeC_warpFrequency;
                    float awx4  = _hmAddWarpX!.GetNoise(wx4 * awf4, wz4 * awf4) * p4.modeC_warpAmp;
                    float awz4  = _hmAddWarpZ!.GetNoise(wx4 * awf4, wz4 * awf4) * p4.modeC_warpAmp;
                    float addH4 = wg!.GetHeight(wx4 + awx4, wz4 + awz4);
                    float avf4  = p4.modeC_ampFrequency;
                    _bk2dAddH![idx2] = addH4;
                    _bk2dLAmp![idx2] = Mathf.Max(p4.modeC_noiseAmp * (1f + p4.modeC_ampVary
                                           * _addVaryNoise!.GetNoise(wx4 * avf4, wz4 * avf4)), 0f);

                    // 3D Mode C noise bake.
                    float ts4 = 1f + p4.modeC_twistVary
                        * _twistVaryNoise!.GetNoise(wx4 * p4.modeC_twistFrequency, wz4 * p4.modeC_twistFrequency);
                    float af4 = p4.modeC_frequency, vs4 = p4.modeC_vScale, tw4 = p4.modeC_twist;
                    int baseIdx = cx4 * cw4 * bh + cz4 * bh;
                    for (int cy = 0; cy < bh; cy++)
                    {
                        float wy4  = (extLo + cy) * s;
                        float sy4  = (wy4 - addH4) * vs4;
                        float ang4 = wy4 * tw4 * ts4;
                        float cos4 = Mathf.Cos(ang4), sin4 = Mathf.Sin(ang4);
                        float tx4  = wx4 * cos4 - wz4 * sin4;
                        float tz4  = wx4 * sin4 + wz4 * cos4;
                        _noiseBuf[baseIdx + cy] = _addNoise!.GetNoise(tx4 * af4, sy4 * af4, tz4 * af4);
                    }
                }
                else if (bakeC)
                {
                    int baseIdx = cx4 * cw4 * bh + cz4 * bh;
                    for (int cy = 0; cy < bh; cy++) _noiseBuf[baseIdx + cy] = 0f;
                    _bk2dAddH![idx2] = 0f;
                    _bk2dLAmp![idx2] = 0f;
                }

                // Mode A 3D noise bake — same 1/4 XZ layout as Mode C.
                // Bakes warped tunnel noise + width noise per (coarse XZ, full Y).
                // Phase 1 bilinear-interpolates instead of computing 4× 3D noise per voxel.
                if (bakeA && p4.modeA_strength > 0f)
                {
                    float mawf4  = _bk2dMawf! [idx2];
                    float mawa4  = _bk2dMawa! [idx2];
                    float maf4   = _bk2dMaf!  [idx2];
                    float mavs4  = _bk2dMavs! [idx2];
                    float mawvf4 = _bk2dMawvf![idx2];
                    int baseIdxA = cx4 * cw4 * bh + cz4 * bh;
                    for (int cy = 0; cy < bh; cy++)
                    {
                        float sy4  = (extLo + cy) * s * mavs4;
                        float awx4 = _warpX!.GetNoise(wx4 * mawf4, sy4 * mawf4, wz4 * mawf4) * mawa4;
                        float awz4 = _warpZ!.GetNoise(wx4 * mawf4, sy4 * mawf4, wz4 * mawf4) * mawa4;
                        _tunBuf! [baseIdxA + cy] = _caveNoise!.GetNoise((wx4 + awx4) * maf4, sy4 * maf4, (wz4 + awz4) * maf4);
                        _tunWBuf![baseIdxA + cy] = _widthNoise!.GetNoise(wx4 * mawvf4, sy4 * mawvf4, wz4 * mawvf4);
                    }
                }
                else if (bakeA)
                {
                    int baseIdxA = cx4 * cw4 * bh + cz4 * bh;
                    for (int cy = 0; cy < bh; cy++) { _tunBuf![baseIdxA + cy] = 0f; _tunWBuf![baseIdxA + cy] = 0f; }
                }
            }
            // Full-res Mode B warpedH bake — eliminates wg.GetHeight from Phase 1.
            // Skipped entirely when Mode B is inactive, so no overhead in the common case.
            if (anyMbActive && wg != null)
            {
                int fullSz = bw * bw;
                if (_bkWarpedH == null || _bkWarpedH.Length < fullSz) _bkWarpedH = new float[fullSz];
                for (int cx = 0; cx < bw; cx++)
                for (int cz = 0; cz < bw; cz++)
                {
                    float wx = (ox - overlap + cx) * s, wz = (oz - overlap + cz) * s;
                    int cx4  = cx / 4, cz4  = cz / 4;
                    int cx4b = Math.Min(cx4 + 1, cw4 - 1), cz4b = Math.Min(cz4 + 1, cw4 - 1);
                    float bfx2 = (cx & 3) * 0.25f, bfz2 = (cz & 3) * 0.25f;
                    int b00 = cx4 * cw4 + cz4, b10 = cx4b * cw4 + cz4;
                    int b01 = cx4 * cw4 + cz4b, b11 = cx4b * cw4 + cz4b;
                    static float BL2(float[] a, int i00, int i10, int i01, int i11, float fx, float fz) =>
                        Mathf.Lerp(Mathf.Lerp(a[i00], a[i10], fx),
                                   Mathf.Lerp(a[i01], a[i11], fx), fz);
                    float wf    = BL2(_bk2dMbwf!, b00, b10, b01, b11, bfx2, bfz2);
                    float wa    = BL2(_bk2dMbwa!, b00, b10, b01, b11, bfx2, bfz2);
                    float locF2 = LocationFactor(wx, wz, nearbyLocs);
                    float dx    = _hmWarpX!.GetNoise(wx * wf, wz * wf) * (wa * locF2);
                    float dz2   = _hmWarpZ!.GetNoise(wx * wf, wz * wf) * (wa * locF2);
                    _bkWarpedH![cx * bw + cz] = wg.GetHeight(wx + dx, wz + dz2);
                }
            }

            long _t0 = _sw.ElapsedMilliseconds;

            // ── Phase 1 ───────────────────────────────────────────────────────────
            FillPhase1(buf, bw, bh, extLo, s, ox, oz, overlap, bakeC, bakeA, wg, cw4, nearbyLocs);
            long _t1 = _sw.ElapsedMilliseconds;

            // ── Phase 1.5: Floating-island removal ───────────────────────────────────
            // BFS from all solid voxels at cy=0 (always below terrain). Any solid
            // voxel not reachable is a floating island; set it to air before blur so
            // the blur pass feathers the newly-exposed edges.
            if (bakeC)
            {
                int islandBufSize = bw * bh * bw;
                int stride_x      = bh * bw;
                if (_visitedBuf  == null || _visitedBuf.Length  < islandBufSize) _visitedBuf  = new bool[islandBufSize];
                if (_islandQueue == null || _islandQueue.Length < islandBufSize) _islandQueue = new int[islandBufSize];
                Array.Clear(_visitedBuf, 0, islandBufSize);

                int qHead = 0, qTail = 0;

                for (int cx = 0; cx < bw; cx++)
                for (int cz = 0; cz < bw; cz++)
                {
                    int idx = cx * stride_x + cz; // cy=0: (cx*bh+0)*bw+cz
                    if (buf[idx] < 0f && !_visitedBuf[idx])
                    { _visitedBuf[idx] = true; _islandQueue[qTail++] = idx; }
                }

                while (qHead < qTail)
                {
                    int idx = _islandQueue[qHead++];
                    int cx  = idx / stride_x;
                    int rem = idx % stride_x;
                    int cy  = rem / bw;
                    int cz  = rem % bw;
                    int ni;
                    if (cx > 0)    { ni = idx - stride_x; if (!_visitedBuf[ni] && buf[ni] < 0f) { _visitedBuf[ni] = true; _islandQueue[qTail++] = ni; } }
                    if (cx < bw-1) { ni = idx + stride_x; if (!_visitedBuf[ni] && buf[ni] < 0f) { _visitedBuf[ni] = true; _islandQueue[qTail++] = ni; } }
                    if (cy > 0)    { ni = idx - bw;        if (!_visitedBuf[ni] && buf[ni] < 0f) { _visitedBuf[ni] = true; _islandQueue[qTail++] = ni; } }
                    if (cy < bh-1) { ni = idx + bw;        if (!_visitedBuf[ni] && buf[ni] < 0f) { _visitedBuf[ni] = true; _islandQueue[qTail++] = ni; } }
                    if (cz > 0)    { ni = idx - 1;         if (!_visitedBuf[ni] && buf[ni] < 0f) { _visitedBuf[ni] = true; _islandQueue[qTail++] = ni; } }
                    if (cz < bw-1) { ni = idx + 1;         if (!_visitedBuf[ni] && buf[ni] < 0f) { _visitedBuf[ni] = true; _islandQueue[qTail++] = ni; } }
                }

                int removed = 0;
                for (int i = 0; i < islandBufSize; i++)
                    if (!_visitedBuf[i] && buf[i] < 0f) { buf[i] = s; removed++; }

                if (removed > 0)
                    Log.LogDebug($"[VoxCaves] island-fill removed {removed} voxels in zone ({ox},{oz})");
            }
            long _t1b = _sw.ElapsedMilliseconds;

            // ── Phase 2: Separable 3D Gaussian blur [0.25, 0.5, 0.25] ──────────────
            // Each pass: X buf→tmp, Z tmp→buf, Y buf→tmp→buf. Result always in buf.
            // blur_passes=0 skips entirely; Phase 3 reads buf directly.
            // blurPasses sampled at zone centre before Phase 1.
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
                // Y pass: buf → tmp → buf
                for (int cx = 0; cx < bw; cx++)
                for (int cy = 0; cy < bh; cy++)
                for (int cz = 0; cz < bw; cz++)
                {
                    int i = (cx * bh + cy) * bw + cz;
                    tmp[i] = (cy > 0 && cy < bh - 1)
                        ? buf[(cx*bh+(cy-1))*bw+cz] * 0.25f + buf[i] * 0.5f + buf[(cx*bh+(cy+1))*bw+cz] * 0.25f
                        : buf[i];
                }
                for (int cx = 0; cx < bw; cx++)
                for (int cy = 0; cy < bh; cy++)
                for (int cz = 0; cz < bw; cz++)
                {
                    int i = (cx * bh + cy) * bw + cz;
                    buf[i] = tmp[i];
                }
            }

            long _t2 = _sw.ElapsedMilliseconds;

            // ── Phase 3: Write SDF directly to VoxelField via the 'field' parameter ────
            // Writing to the same object VoxelizeColumns used (not _voxelZoneFieldField)
            // ensures RebuildBrick reads the correct SDF regardless of Voxheim's internal
            // field architecture.  FinishSkinNow is NOT called; bricks mesh async.
            int carved = 0;
            int maxYC  = yHi;
            var transBytes  = new sbyte[bh];
            var transHasOvr = new bool[bh];
            var cacheData   = new sbyte[width * bh * width]; // (ci*bh + cy)*width + ri

            for (int ci = 0; ci < width; ci++)
            for (int ri = 0; ri < width; ri++)
            {
                int cx = ci + overlap, cz = ri + overlap;
                int gx = ox + ci,      gz = oz + ri;

                Array.Clear(transHasOvr, 0, bh);
                {
                    float av = buf[(cx * bh + (bh - 1)) * bw + cz];
                    for (int cy = bh - 2; cy >= 0; cy--)
                    {
                        float bv = buf[(cx * bh + cy) * bw + cz];
                        bool topSurf = av > 0f && bv <= 0f;
                        bool botSurf = av <= 0f && bv > 0f;
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
                    cacheData[(ci * bh + cy) * width + ri] = newByte;
                    sbyte curByte  = _get!(field, gx, gy, gz);
                    if (newByte == curByte) continue;
                    _set!(field, gx, gy, gz, newByte);
                    if (newByte < 0 && gy > maxYC) maxYC = gy;
                    carved++;
                }
            }
            _sdfCache[zoneKey] = (extLo, bh, width, cacheData);

            if (maxYC > yHi)
            {
                if (!_zoneMaxY.TryGetValue(zoneKey, out int prevMaxY) || maxYC > prevMaxY)
                    _zoneMaxY[zoneKey] = maxYC;
                int hiMark = maxYC + 8;
                if (!_zoneExtHi.TryGetValue(zoneKey, out int prevHiMark) || hiMark > prevHiMark)
                    _zoneExtHi[zoneKey] = hiMark;
            }

            if (_disableVegetation) _pendingVegCleanup = true;

            long _t3 = _sw.ElapsedMilliseconds;
            Log.LogInfo($"[VoxCaves] zone ({ox},{oz}) bakeC={bakeC} bw={bw} bh={bh} Ph0={_t0}ms Ph1={_t1-_t0}ms Ph1b={_t1b-_t1}ms Ph2={_t2-_t1b}ms Ph3={_t3-_t2}ms total={_t3}ms carved={carved}");
        }

        // ── Hot-reload: re-run the full voxel pipeline on every loaded zone ───────
        static void RecarveAll()
        {
            Log.LogInfo($"[VoxCaves] Hot-reload: re-voxelizing {_zoneParams.Count} zone(s).");
            _sdfCache.Clear();
            if (_voxelizeColumnsMethod == null) return;
            bool canRemesh = _enqueueMeshMethod != null && _finishSkinNowMethod != null && _int3Ctor != null;
            if (!canRemesh)
                Log.LogWarning("[VoxCaves] EnqueueMesh/FinishSkinNow unavailable — mesh rebuild skipped.");

            var snapshot = new List<(string key, (object field, object grid, int ox, int oz, int w, int yLo, int yHi) zp)>(_zoneParams.Count);
            foreach (var kv in _zoneParams) snapshot.Add((kv.Key, kv.Value));

            // ── Pass 1: update all VoxelFields before touching any meshes ─────────
            // Processing all fields first means cross-zone boundary bricks in Pass 2
            // read current data from every neighbor, eliminating seam ordering issues.
            foreach (var (key, zp) in snapshot)
            {
                int extLo = zp.yLo - 24;
                // Start from previously stored high-water mark so turning Mode C off
                // still resets the voxels it previously wrote above yHi.
                int extHi = _zoneExtHi.TryGetValue(key, out int hiMark) ? hiMark : zp.yHi + 24;
                {
                    float sRc = _scale is { } sv && sv > 0f ? sv : 1f;
                    var wgRc  = WorldGenerator.instance;
                    if (wgRc != null)
                    {
                        float cxRc = (zp.ox + zp.w * 0.5f) * sRc;
                        float czRc = (zp.oz + zp.w * 0.5f) * sRc;
                        var cpRc   = BlendProfile(cxRc, czRc, wgRc);
                        if (cpRc.modeC_strength > 0f)
                        {
                            float maxAmpRc  = cpRc.modeC_noiseAmp * Mathf.Max(1f, 1f + cpRc.modeC_ampVary);
                            float maxExtMRc = Mathf.Max(0f, 1f - cpRc.modeC_threshold) * maxAmpRc + cpRc.modeC_warpAmp;
                            int newHi = zp.yHi + Mathf.Max(24, (int)Mathf.Ceil(maxExtMRc / sRc) + 8);
                            if (newHi > extHi) extHi = newHi;
                        }
                    }
                }
                try
                {
                    _voxelizeColumnsMethod.Invoke(null, new object[]
                        { zp.field, zp.grid, zp.ox, zp.oz, 0, zp.w - 1, 0, zp.w - 1, extLo, extHi });
                }
                catch (Exception e) { Log.LogWarning($"[VoxCaves] Revoxelize {key}: {e.Message}"); }
            }

            // ── Between passes: extend _yScanHi for zones where Mode C raised terrain ──
            // CompletePatch only runs on fresh zone load, so hot-reload must do this itself.
            if (_yScanHiField != null)
            {
                foreach (var (key, _) in snapshot)
                {
                    if (!_zoneMaxY.TryGetValue(key, out int maxY)) continue;
                    _zoneMaxY.Remove(key);
                    if (!_zoneObjects.TryGetValue(key, out var zo)) continue;
                    if (zo is UnityEngine.Object u && !u) continue;
                    int cur = (int)_yScanHiField.GetValue(zo)!;
                    if (maxY > cur) _yScanHiField.SetValue(zo, maxY);
                }
            }

            if (!canRemesh) return;

            // ── Pass 2: rebuild meshes now that all VoxelFields are current ────────
            foreach (var (key, zp) in snapshot)
            {
                if (!_zoneObjects.TryGetValue(key, out var zoneObj)) continue;
                if (zoneObj is UnityEngine.Object uObj && !uObj) continue;

                int extLo = zp.yLo - 24;
                int extHi = _zoneExtHi.TryGetValue(key, out int hiMark2) ? hiMark2 : zp.yHi + 24;

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

        static void FillYColumn(float[] buf, int bw, int bh, int extLo, float s, int cx, int cz,
            float h, float addH, float profMcS, float profMcT, float localAmp, bool mcActive,
            float locFac, float floorY, float profMbS, float profMaS,
            int layerCount, float[] hmC, float[] hmF,
            int bb00, int bb10, int bb01, int bb11, float bfx, float bfz,
            float mAwf, float mAwa, float mAf, float mAvs, float mAw, float mAwv, float mAwvf,
            float worldX, float worldZ, bool bakeC, bool bakeA)
        {
            float hmC0 = layerCount > 0 ? hmC[0] : 0f;
            float hmF0 = layerCount > 0 ? hmF[0] : 0f;

            // Mode B bandSDF is positive only when worldY is inside the band [hmF,hmC] and above floorY.
            // Precompute the cy range where Mode B can contribute; skip it everywhere else.
            int cyMbLo = bh, cyMbHi = -1;
            if (layerCount > 0)
            {
                float yLo = float.MaxValue, yHi = float.MinValue;
                for (int li = 0; li < layerCount; li++)
                {
                    float lo = hmF[li] > floorY ? hmF[li] : floorY;
                    float hi = hmC[li];
                    if (hi > lo) { if (lo < yLo) yLo = lo; if (hi > yHi) yHi = hi; }
                }
                if (yHi > yLo)
                {
                    cyMbLo = Math.Max(0,    (int)(yLo / s) - extLo);
                    cyMbHi = Math.Min(bh-1, (int)(yHi / s) - extLo + 1);
                }
            }

            // Segment 1: below Mode B range — Mode C + Mode A only.
            for (int cy = 0; cy < cyMbLo; cy++)
            {
                float worldY   = (extLo + cy) * s;
                float baseSDF  = worldY - h;
                float combined = baseSDF;

                if (mcActive)
                {
                    float relY = worldY - addH;
                    float n;
                    {
                        float n0 = _noiseBuf![bb00+cy] + (_noiseBuf[bb10+cy] - _noiseBuf![bb00+cy]) * bfx;
                        float n1 = _noiseBuf![bb01+cy] + (_noiseBuf[bb11+cy] - _noiseBuf![bb01+cy]) * bfx;
                        n = n0 + (n1-n0) * bfz;
                    }
                    float sdf = (relY - (n - profMcT) * localAmp) * profMcS;
                    combined = combined < sdf ? combined : sdf;
                }

                if (profMaS > 0f)
                {
                    float n, wn;
                    if (bakeA)
                    {
                        float n0 = _tunBuf![bb00+cy] + (_tunBuf[bb10+cy] - _tunBuf[bb00+cy]) * bfx;
                        float n1 = _tunBuf![bb01+cy] + (_tunBuf[bb11+cy] - _tunBuf[bb01+cy]) * bfx;
                        n  = n0 + (n1 - n0) * bfz;
                        float w0 = _tunWBuf![bb00+cy] + (_tunWBuf[bb10+cy] - _tunWBuf[bb00+cy]) * bfx;
                        float w1 = _tunWBuf![bb01+cy] + (_tunWBuf[bb11+cy] - _tunWBuf[bb01+cy]) * bfx;
                        wn = w0 + (w1 - w0) * bfz;
                    }
                    else
                    {
                        float sy = worldY * mAvs;
                        float wx = _warpX!.GetNoise(worldX * mAwf, sy * mAwf, worldZ * mAwf) * mAwa;
                        float wz = _warpZ!.GetNoise(worldX * mAwf, sy * mAwf, worldZ * mAwf) * mAwa;
                        n  = _caveNoise!.GetNoise((worldX + wx) * mAf, sy * mAf, (worldZ + wz) * mAf);
                        wn = _widthNoise!.GetNoise(worldX * mAwvf, (worldY * mAvs) * mAwvf, worldZ * mAwvf);
                    }
                    float localWidth = Mathf.Max(mAw * (1f + mAwv * wn), 0.001f);
                    float tunnelSDF = (localWidth - Mathf.Abs(n)) / localWidth * 4f * s;
                    float sdf = Mathf.Min(tunnelSDF, worldY - floorY) * profMaS;
                    combined = combined > sdf ? combined : sdf;
                }

                buf[(cx * bh + cy) * bw + cz] = baseSDF + (combined - baseSDF) * locFac;
            }

            // Segment 2: inside Mode B range — Mode B + Mode C + Mode A.
            for (int cy = cyMbLo; cy <= cyMbHi; cy++)
            {
                float worldY   = (extLo + cy) * s;
                float baseSDF  = worldY - h;
                float combined = baseSDF;

                {
                    float ca = hmC0 - worldY, cb = worldY - hmF0;
                    float bandSDF = ca < cb ? ca : cb;
                    for (int li = 1; li < layerCount; li++)
                    {
                        float la = hmC[li] - worldY, lb = worldY - hmF[li];
                        float layerSDF = la < lb ? la : lb;
                        if (layerSDF > bandSDF) bandSDF = layerSDF;
                    }
                    float fd = worldY - floorY;
                    float sdf = (bandSDF < fd ? bandSDF : fd) * profMbS;
                    if (sdf > combined) combined = sdf;
                }

                if (mcActive)
                {
                    float relY = worldY - addH;
                    float n;
                    {
                        float n0 = _noiseBuf![bb00+cy] + (_noiseBuf[bb10+cy] - _noiseBuf![bb00+cy]) * bfx;
                        float n1 = _noiseBuf![bb01+cy] + (_noiseBuf[bb11+cy] - _noiseBuf![bb01+cy]) * bfx;
                        n = n0 + (n1-n0) * bfz;
                    }
                    float sdf = (relY - (n - profMcT) * localAmp) * profMcS;
                    combined = combined < sdf ? combined : sdf;
                }

                if (profMaS > 0f)
                {
                    float n, wn;
                    if (bakeA)
                    {
                        float n0 = _tunBuf![bb00+cy] + (_tunBuf[bb10+cy] - _tunBuf[bb00+cy]) * bfx;
                        float n1 = _tunBuf![bb01+cy] + (_tunBuf[bb11+cy] - _tunBuf[bb01+cy]) * bfx;
                        n  = n0 + (n1 - n0) * bfz;
                        float w0 = _tunWBuf![bb00+cy] + (_tunWBuf[bb10+cy] - _tunWBuf[bb00+cy]) * bfx;
                        float w1 = _tunWBuf![bb01+cy] + (_tunWBuf[bb11+cy] - _tunWBuf[bb01+cy]) * bfx;
                        wn = w0 + (w1 - w0) * bfz;
                    }
                    else
                    {
                        float sy = worldY * mAvs;
                        float wx = _warpX!.GetNoise(worldX * mAwf, sy * mAwf, worldZ * mAwf) * mAwa;
                        float wz = _warpZ!.GetNoise(worldX * mAwf, sy * mAwf, worldZ * mAwf) * mAwa;
                        n  = _caveNoise!.GetNoise((worldX + wx) * mAf, sy * mAf, (worldZ + wz) * mAf);
                        wn = _widthNoise!.GetNoise(worldX * mAwvf, (worldY * mAvs) * mAwvf, worldZ * mAwvf);
                    }
                    float localWidth = Mathf.Max(mAw * (1f + mAwv * wn), 0.001f);
                    float tunnelSDF = (localWidth - Mathf.Abs(n)) / localWidth * 4f * s;
                    float sdf = Mathf.Min(tunnelSDF, worldY - floorY) * profMaS;
                    combined = combined > sdf ? combined : sdf;
                }

                buf[(cx * bh + cy) * bw + cz] = baseSDF + (combined - baseSDF) * locFac;
            }

            // Segment 3: above Mode B range — Mode C + Mode A only (identical body to Segment 1).
            int cy3Start = cyMbHi < cyMbLo ? cyMbLo : cyMbHi + 1;
            for (int cy = cy3Start; cy < bh; cy++)
            {
                float worldY   = (extLo + cy) * s;
                float baseSDF  = worldY - h;
                float combined = baseSDF;

                if (mcActive)
                {
                    float relY = worldY - addH;
                    float n;
                    {
                        float n0 = _noiseBuf![bb00+cy] + (_noiseBuf[bb10+cy] - _noiseBuf![bb00+cy]) * bfx;
                        float n1 = _noiseBuf![bb01+cy] + (_noiseBuf[bb11+cy] - _noiseBuf![bb01+cy]) * bfx;
                        n = n0 + (n1-n0) * bfz;
                    }
                    float sdf = (relY - (n - profMcT) * localAmp) * profMcS;
                    combined = combined < sdf ? combined : sdf;
                }

                if (profMaS > 0f)
                {
                    float n, wn;
                    if (bakeA)
                    {
                        float n0 = _tunBuf![bb00+cy] + (_tunBuf[bb10+cy] - _tunBuf[bb00+cy]) * bfx;
                        float n1 = _tunBuf![bb01+cy] + (_tunBuf[bb11+cy] - _tunBuf[bb01+cy]) * bfx;
                        n  = n0 + (n1 - n0) * bfz;
                        float w0 = _tunWBuf![bb00+cy] + (_tunWBuf[bb10+cy] - _tunWBuf[bb00+cy]) * bfx;
                        float w1 = _tunWBuf![bb01+cy] + (_tunWBuf[bb11+cy] - _tunWBuf[bb01+cy]) * bfx;
                        wn = w0 + (w1 - w0) * bfz;
                    }
                    else
                    {
                        float sy = worldY * mAvs;
                        float wx = _warpX!.GetNoise(worldX * mAwf, sy * mAwf, worldZ * mAwf) * mAwa;
                        float wz = _warpZ!.GetNoise(worldX * mAwf, sy * mAwf, worldZ * mAwf) * mAwa;
                        n  = _caveNoise!.GetNoise((worldX + wx) * mAf, sy * mAf, (worldZ + wz) * mAf);
                        wn = _widthNoise!.GetNoise(worldX * mAwvf, (worldY * mAvs) * mAwvf, worldZ * mAwvf);
                    }
                    float localWidth = Mathf.Max(mAw * (1f + mAwv * wn), 0.001f);
                    float tunnelSDF = (localWidth - Mathf.Abs(n)) / localWidth * 4f * s;
                    float sdf = Mathf.Min(tunnelSDF, worldY - floorY) * profMaS;
                    combined = combined > sdf ? combined : sdf;
                }

                buf[(cx * bh + cy) * bw + cz] = baseSDF + (combined - baseSDF) * locFac;
            }
        }

        // ── Per-point SDF for FallbackDensity patch ───────────────────────────────
        // Computes our SDF for a single voxel (absolute voxel coordinates).
        // Lookup into the SDF cache baked by Phase 3 of Carve().
        // Returns false on cache miss (zone not yet carved) → caller falls through to vanilla.
        internal static bool TryComputePointSDF(int vx, int vy, int vz, out sbyte result)
        {
            result = sbyte.MaxValue;
            // Zone origin: floor-divide by 64
            int ox = vx - ((vx % 64 + 64) % 64);
            int oz = vz - ((vz % 64 + 64) % 64);
            if (!_sdfCache.TryGetValue($"{ox},{oz}", out var c)) return false;
            int ci = vx - ox, ri = vz - oz, cy = vy - c.extLo;
            if (ci < 0 || ci >= c.width || ri < 0 || ri >= c.width || cy < 0 || cy >= c.bh)
                return false;
            result = c.data[(ci * c.bh + cy) * c.width + ri];
            return true;
        }

        // Extracted into its own method so Mono JITs it independently from Carve().
        // Carve() grew too large for Mono's optimized JIT path, causing 8× slowdown in the hot loop.
        // Profile parameters come from the always-baked 2D coarse arrays via bilinear interpolation,
        // replacing the previous per-column BlendProfile (9600 GetBiome/zone).
        // h (terrain surface height) must be exact per-column — coarse interpolation of h
        // produces visible terrain artifacts because baseSDF = worldY - h drives the surface position.
        static void FillPhase1(float[] buf, int bw, int bh, int extLo, float s,
            int ox, int oz, int overlap, bool bakeC, bool bakeA, WorldGenerator? wg, int cw4,
            List<Vector2> nearbyLocs)
        {
            long _setupTicks = 0L, _yloopTicks = 0L;
            long _freq = System.Diagnostics.Stopwatch.Frequency;
            float[] hmC = new float[16], hmF = new float[16];
            for (int cx = 0; cx < bw; cx++)
            for (int cz = 0; cz < bw; cz++)
            {
                long _ta = System.Diagnostics.Stopwatch.GetTimestamp();
                int   gx     = ox - overlap + cx, gz = oz - overlap + cz;
                float worldX = gx * s,            worldZ = gz * s;

                int cx4  = cx / 4, cz4  = cz / 4;
                int cx4b = Math.Min(cx4 + 1, cw4 - 1);
                int cz4b = Math.Min(cz4 + 1, cw4 - 1);
                float bfx = (cx & 3) * 0.25f;
                float bfz = (cz & 3) * 0.25f;
                int b200 =  cx4  * cw4 + cz4,  b210 =  cx4b * cw4 + cz4;
                int b201 =  cx4  * cw4 + cz4b, b211 =  cx4b * cw4 + cz4b;
                int bb00 = b200 * bh, bb10 = b210 * bh, bb01 = b201 * bh, bb11 = b211 * bh;

                static float BL(float[] a, int i00, int i10, int i01, int i11, float fx, float fz) =>
                    Mathf.Lerp(Mathf.Lerp(a[i00], a[i10], fx), Mathf.Lerp(a[i01], a[i11], fx), fz);

                float profMcS  = BL(_bk2dMcS!, b200, b210, b201, b211, bfx, bfz);
                float profMcT  = BL(_bk2dMcT!, b200, b210, b201, b211, bfx, bfz);
                float profFlD  = BL(_bk2dFlD!, b200, b210, b201, b211, bfx, bfz);
                float profMbS  = BL(_bk2dMbS!, b200, b210, b201, b211, bfx, bfz);
                float profMaS  = BL(_bk2dMaS!, b200, b210, b201, b211, bfx, bfz);
                float addH     = bakeC ? BL(_bk2dAddH!, b200, b210, b201, b211, bfx, bfz) : 0f;
                float localAmp = bakeC ? BL(_bk2dLAmp!, b200, b210, b201, b211, bfx, bfz) : 0f;
                float h;
                if (wg == null) { h = 0f; }
                else
                {
                    long hKey = (long)gx << 32 | (uint)gz;
                    if (!_heightCache.TryGetValue(hKey, out h))
                        _heightCache[hKey] = h = wg.GetHeight(worldX, worldZ);
                }
                float floorY   = h - profFlD;
                float locFac   = LocationFactor(worldX, worldZ, nearbyLocs);

                int layerCount = 0;
                if (profMbS > 0f)
                {
                    float df        = BL(_bk2dMbdf!, b200, b210, b201, b211, bfx, bfz);
                    float da        = BL(_bk2dMbda!, b200, b210, b201, b211, bfx, bfz);
                    float warpedH   = _bkWarpedH![cx * bw + cz]
                                    + _ditherNoise!.GetNoise(worldX * df, worldZ * df) * da;
                    float mbR       = BL(_bk2dMbr!,  b200, b210, b201, b211, bfx, bfz);
                    float mbLs      = BL(_bk2dMbls!, b200, b210, b201, b211, bfx, bfz);
                    float mbLayersF = BL(_bk2dMbl!,  b200, b210, b201, b211, bfx, bfz);
                    layerCount = Mathf.Max(1, Mathf.RoundToInt(mbLayersF));
                    for (int li = 0; li < layerCount; li++)
                    {
                        float lh = warpedH - li * mbLs;
                        hmC[li] = lh + mbR;
                        hmF[li] = lh - mbR;
                    }
                }

                float mAwf=0f, mAwa=0f, mAf=0f, mAvs=0f, mAw=0f, mAwv=0f, mAwvf=0f;
                if (profMaS > 0f)
                {
                    mAwf  = BL(_bk2dMawf!,  b200, b210, b201, b211, bfx, bfz);
                    mAwa  = BL(_bk2dMawa!,  b200, b210, b201, b211, bfx, bfz);
                    mAf   = BL(_bk2dMaf!,   b200, b210, b201, b211, bfx, bfz);
                    mAvs  = BL(_bk2dMavs!,  b200, b210, b201, b211, bfx, bfz);
                    mAw   = BL(_bk2dMaw!,   b200, b210, b201, b211, bfx, bfz);
                    mAwv  = BL(_bk2dMawv!,  b200, b210, b201, b211, bfx, bfz);
                    mAwvf = BL(_bk2dMawvf!, b200, b210, b201, b211, bfx, bfz);
                }

                long _tb = System.Diagnostics.Stopwatch.GetTimestamp();
                _setupTicks += _tb - _ta;
                FillYColumn(buf, bw, bh, extLo, s, cx, cz,
                    h, addH, profMcS, profMcT, localAmp, profMcS > 0f && bakeC,
                    locFac, floorY, profMbS, profMaS,
                    layerCount, hmC, hmF,
                    bb00, bb10, bb01, bb11, bfx, bfz,
                    mAwf, mAwa, mAf, mAvs, mAw, mAwv, mAwvf,
                    worldX, worldZ, bakeC, bakeA);
                _yloopTicks += System.Diagnostics.Stopwatch.GetTimestamp() - _tb;
            }
            Log.LogInfo($"[VoxCaves] Ph1 breakdown: setup={_setupTicks*1000/_freq}ms yloop={_yloopTicks*1000/_freq}ms");
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
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            try { Plugin.Carve(field, grid, originX, originZ, gx1 + 1, yLo, yHi); }
            catch (Exception e) { Plugin.Log.LogError($"[VoxCaves] Carve: {e.Message}"); }
            long ms = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000L / System.Diagnostics.Stopwatch.Frequency;
            Plugin.Log.LogInfo($"[VoxCaves] postfix ({originX},{originZ}) wall={ms}ms");
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

        // After Complete() sets _isActive=true, poke neighbors so their boundary
        // bricks rebuild using this zone's carved _field instead of FallbackDensity or stale data.
        // PokeNeighbors(in VoxelBounds, bool defer) finds all active adjacent zones and enqueues
        // a deferred remesh of the bricks overlapping the supplied bounds.
        static void Postfix(object __instance)
        {
            if (Plugin._pokeNeighborsMethod == null) return;
            if (Plugin._voxelBoundsType == null || Plugin._vbMinField == null
                || Plugin._vbMaxField == null || Plugin._vbAnyField == null) return;
            if (Plugin._zoneOriginX == null || Plugin._zoneOriginZ == null) return;
            if (Plugin._int3Ctor == null) return;

            int ox = (int)Plugin._zoneOriginX.GetValue(__instance)!;
            int oz = (int)Plugin._zoneOriginZ.GetValue(__instance)!;
            if (!Plugin._zoneParams.TryGetValue($"{ox},{oz}", out var zp)) return;

            // Build VoxelBounds covering the full carved voxel extent of this zone.
            // Neighbors only need bricks that touch this zone's edge, but covering the
            // full zone is safe and ensures we catch Mode C terrain raised above yHi.
            object minV = Plugin._int3Ctor.Invoke(new object[] { zp.ox,          zp.yLo - 32, zp.oz });
            object maxV = Plugin._int3Ctor.Invoke(new object[] { zp.ox + zp.w - 1, zp.yHi + 128, zp.oz + zp.w - 1 });
            object bounds = Activator.CreateInstance(Plugin._voxelBoundsType);
            Plugin._vbMinField.SetValue(bounds, minV);
            Plugin._vbMaxField.SetValue(bounds, maxV);
            Plugin._vbAnyField.SetValue(bounds, true);

            try { Plugin._pokeNeighborsMethod.Invoke(__instance, new object[] { bounds, true }); }
            catch (Exception ex) { Plugin.Log.LogWarning($"[VoxCaves] PokeNeighbors threw: {ex.Message}"); }
        }
    }

    [HarmonyPatch(typeof(ZNet), nameof(ZNet.SetServer))]
    static class WorldLoadPatch
    {
        static void Postfix()
        {
            Plugin.LoadBiomeConfig();
            Plugin._pendingVegCleanup = true; // run cliff + disableVegetation cleanup after world loads
            Plugin._zoneObjects.Clear();
            Plugin._zoneParams.Clear();
            Plugin._zoneMaxY.Clear();
            Plugin._zoneExtHi.Clear();
            Plugin._sdfCache.Clear();
            Plugin._profileCache.Clear();
            Plugin._heightCache.Clear();
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
            if (m == null) Plugin.Log.LogWarning("[VoxCaves] FallbackDensity not found — cross-zone boundary density will be vanilla.");
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
