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
        public float modeB_radius;      // half-thickness of carved band (world units); 0 = mode off
        public float modeB_warpAmp;         // XZ warp displacement (world units)
        public float modeB_warpFrequency;   // spatial frequency of XZ warp noise

        // ── Mode A: noise tunnel network ──────────────────────────────────────────
        public float modeA_width;         // noise threshold for tunnel interior (0–1); 0 = mode off
        public float modeA_frequency;     // spatial frequency of 3D tunnel noise
        public float modeA_warpAmp;        // 3D domain-warp amplitude (world units)
        public float modeA_warpFrequency;  // spatial frequency of 3D warp noise
        public float modeA_vScale;         // Y scale before noise lookup (low=horizontal, high=vertical)
        public float modeA_widthVary;      // amplitude of spatial width modulation (0=uniform, 1=2× variation)
        public float modeA_widthFrequency; // spatial frequency of width modulation noise
        public float modeA_yOffset;        // anchor tunnel noise this many units below heightmap (0=absolute world Y)

        // ── Mode C: 3D density field raised above heightmap ──────────────────────
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
        public float modeC_yOffset;        // shift density field anchor this many units below the heightmap (0=surface)

        public float modeB_vScale;      // Y scale when sampling 3D wall-noise (low=flat, high=vertical)
        public float modeB_noiseAmp;   // amplitude of 3D SDF perturbation on Mode B walls (world units; 0=off)
        public float modeB_yOffset;    // push band centre this many units below the terrain surface (0=on surface; >mbR = enclosed cave)
        public float modeB_twist;      // XZ rotation of wall noise per world unit Y (radians/m)

        // ── Shared ────────────────────────────────────────────────────────────────
        public float blur_passes;      // smoothing iterations (0=off)
        public float perona_k;         // Perona-Malik edge threshold (0=Gaussian; >0=PM diffusion)
        public float perona_lambda;    // PM diffusion rate per iteration (must be < 1/6; default 0.1)
        public float floorDepth;       // metres below terrain surface where carving stops

        public static readonly CaveProfile Default = new CaveProfile
        {
            blur_passes     = 0f,  perona_k = 0f, perona_lambda = 0.1f,
            modeB_radius          = 0f,     modeB_warpAmp           = 80f,
            modeB_warpFrequency   = 0.005f, modeB_vScale = 0.5f, modeB_noiseAmp = 0f, modeB_yOffset = 0f, modeB_twist = 0f,
            modeA_width         = 0f,   modeA_frequency   = 0.015f,
            modeA_warpAmp   = 10f,    modeA_warpFrequency = 0.04f, modeA_vScale       = 0.35f,
            modeA_widthVary = 0f,     modeA_widthFrequency = 0.003f,
            modeC_threshold     = 0f,     modeC_noiseAmp      = 0f,     modeC_frequency    = 0.015f,
            modeC_warpAmp      = 8f,     modeC_warpFrequency = 0.005f, modeC_vScale       = 0.4f,
            modeC_twist        = 0f,     modeC_twistVary     = 0f,     modeC_twistFrequency = 0.003f,
            modeC_ampVary      = 0f,     modeC_ampFrequency  = 0.003f,
            floorDepth      = 8f,
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
        internal static bool  _protectLocations    = true;
        internal static bool  _islandRemoval       = true;
        internal static bool  _pocketRemoval       = true;
        internal static float _biomeBlendRadius    = 192f;
        internal static bool  _biomeBlendSmooth    = true;   // false=linear, true=smoothstep
        internal static int   _transitionBlurPasses = 3;     // min blur passes for any active zone
        internal static float _coastFade           = 0f;     // world units above sea level over which Ocean inflates into blend (0=off)
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
        internal static FastNoiseLite? _caveNoise;      // Mode A: 3D tunnel noise field 1
        internal static FastNoiseLite? _caveNoise2;     // Mode A: 3D tunnel noise field 2 (tubes = intersection of two zero-crossing sheets)
        internal static FastNoiseLite? _warpX;          // Mode A: 3D warp X
        internal static FastNoiseLite? _warpZ;          // Mode A: 3D warp Z
        internal static FastNoiseLite? _warpX2;         // Mode A: 3D warp X for field 2
        internal static FastNoiseLite? _warpZ2;         // Mode A: 3D warp Z for field 2
        internal static FastNoiseLite? _hmWarpX;       // Mode B: XZ warp X
        internal static FastNoiseLite? _hmWarpZ;       // Mode B: XZ warp Z
        internal static FastNoiseLite? _hmAddWarpX;    // Mode C: anchor warp X
        internal static FastNoiseLite? _hmAddWarpZ;    // Mode C: anchor warp Z
        internal static FastNoiseLite? _addNoise;        // Mode C: 3D density field
        internal static FastNoiseLite? _addVaryNoise;   // Mode C: amplitude modulation
        internal static FastNoiseLite? _widthNoise;      // Mode A: tunnel width modulation
        internal static FastNoiseLite? _twistVaryNoise; // Mode C: twist scale modulation
        internal static FastNoiseLite? _mbNoise;        // Mode B: 3D wall perturbation

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

        // Profile cache — cleared on config reload; no longer populated (kept for compat with any external readers).
        internal static readonly Dictionary<long, CaveProfile> _profileCache = new();

        // Scratch buffers for BFS biome blend.
        // _biomeFrac: supersampled fractional biome weights per padded coarse cell.
        //   Layout: [px * pcw + pz] * MaxBiomeSlots + slotIdx
        //   Slot mapping built fresh each zone (biomeSlot dict in BakeBlendedProfiles).
        const int SuperN        = 2;   // NxN sub-samples per coarse cell (2→4 samples, 9x smoother boundary)
        const int MaxBiomeSlots = 16;  // Valheim has ~9 biomes; 16 is safe headroom
        static float[]?       _biomeFrac    = null;  // [pgSize * MaxBiomeSlots]
        static int[]?         _biomeDist    = null;  // BFS distance map scratch (per-biome)
        static int[]?         _biomeBfsQ    = null;  // BFS queue scratch
        static float[]?       _biomeTotW    = null;  // per-cell weight accumulator
        static CaveProfile[]? _blendedProfs = null;  // blended profile at each coarse cell

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

        // ── Cross-zone grounding face cache ──────────────────────────────────────
        // Populated by PrecomputeFaceCaches() before the first Carve() call.
        // Indexed by (ox, oz) territory origin (same coordinates as _zoneParams).
        // Each face array is [bh * bw] bytes: 0=air, 1=grounded solid.
        // Faces are at the territory boundary: negX=cx=overlap, posX=cx=overlap+w-1, etc.
        internal struct ZoneFaceCache {
            public int extLo, bh, bw;
            public bool[] negX,    posX,    negZ,    posZ;    // [bh * bw]; true = grounded solid
            public bool[] negXAir, posXAir, negZAir, posZAir; // [bh * bw]; true = air connected to boundary
            // True if that face has ungrounded solid touching an uncached neighbor —
            // EnsureFixpoint uses these to decide whether to expand the worklist.
            public bool needsNegX, needsPosX, needsNegZ, needsPosZ;
        }
        internal static readonly Dictionary<(int, int), ZoneFaceCache> _zoneFaceCache = new();

        // Reused across Carve() calls to avoid per-zone GC pressure.
        internal static float[]? _bufA;
        internal static float[]? _bufB;
        // Dedicated buffer for ComputeZoneFaceCache so it never clobbers Carve()'s active buf.
        internal static float[]? _precomputeBuf;

        internal static float[]? _noiseBuf;  // coarse Mode C bake: [cx4*(cw4*bh) + cz4*bh + cy]
        internal static float[]? _tunBuf;   // coarse Mode A: tunnel noise field 1 (after warp)
        internal static float[]? _tunBuf2;  // coarse Mode A: tunnel noise field 2 (after warp)
        internal static float[]? _tunWBuf;  // coarse Mode A: width modulation noise
        // 2D coarse bake (same XZ grid as _noiseBuf): [cx4*cw4 + cz4]
        internal static float[]? _bk2dAddH;  // Mode C anchor height
        internal static float[]? _bk2dLAmp;  // Mode C local amplitude
        internal static float[]? _bk2dMcS;   // modeC_noiseAmp (post-bake, for bakeC gate)
        internal static float[]? _bk2dMcT;   // modeC_threshold
        internal static float[]? _bk2dFlD;   // floorDepth
        // 2D coarse bake — Mode B shape params
        internal static float[]? _bk2dMbwf;  // modeB_warpFrequency
        internal static float[]? _bk2dMbwa;  // modeB_warpAmp
        internal static float[]? _bk2dMbr;   // modeB_radius
        internal static float[]? _bk2dMbvs;  // modeB_vScale
        internal static float[]? _bk2dMbna;  // modeB_noiseAmp
        internal static float[]? _bk2dMbyo;  // modeB_yOffset
        internal static float[]? _bk2dMbtwist; // modeB_twist
        // 2D coarse bake — terrain geometry (used by Mode A to orient noise in surface space)
        // 2D coarse bake — Mode A shape params
        internal static float[]? _bk2dMawf;  // modeA_warpFrequency
        internal static float[]? _bk2dMawa;  // modeA_warpAmp
        internal static float[]? _bk2dMaf;   // modeA_frequency
        internal static float[]? _bk2dMavs;  // modeA_vScale
        internal static float[]? _bk2dMaw;   // modeA_width
        internal static float[]? _bk2dMawv;  // modeA_widthVary
        internal static float[]? _bk2dMawvf; // modeA_widthFrequency
        internal static float[]? _bk2dMayo;  // modeA_yOffset
        // Full-res (bw×bw) — Mode B warped heightmap (wg.GetHeight after XZ warp)
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
            new Terminal.ConsoleCommand("voxcaves_dump", "dump SDF + biome blend data for the next 9 zones processed to BepInEx/config/voxcaves_dump_*.json", _ =>
            {
                _dumpZonesRemaining = 9;
                Log.LogInfo("[VoxCaves] Will dump next 9 zones to voxcaves_dump_*.json");
            });
            new Terminal.ConsoleCommand("voxcaves_probe", "voxcaves_probe [x z] — print biome blend breakdown + profile params at world pos (default: player pos)", args =>
            {
                try
                {
                    var wg = WorldGenerator.instance;
                    if (wg == null) { Log.LogWarning("[VoxCaves] probe: WorldGenerator not ready."); return; }
                    float s = _scale is { } sv && sv > 0f ? sv : 1f;
                    float wx, wz;
                    if (args.Length >= 3 && float.TryParse(args[1], out float ax) && float.TryParse(args[2], out float az))
                        { wx = ax; wz = az; }
                    else
                    {
                        var player = Player.m_localPlayer;
                        if (player == null) { Log.LogWarning("[VoxCaves] probe: no player; pass x z args."); return; }
                        wx = player.transform.position.x;
                        wz = player.transform.position.z;
                    }
                    Log.LogInfo($"[VoxCaves] Probing biome blend at world ({wx:F1}, {wz:F1}):");
                    ProbeBlend(wx, wz, wg, s);
                }
                catch (Exception e) { Log.LogError($"[VoxCaves] probe exception: {e}"); }
            });
            Log.LogInfo("[VoxCaves] Ready.");
        }

        internal static int _dumpZonesRemaining = 0;

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
                    case "modeb_strength":        break; // removed — use modeB_radius=0 to disable
                    case "modeb_radius":          p.modeB_radius          = v; break;
                    case "modeb_warpamp":         p.modeB_warpAmp         = v; break;
                    case "modeb_warpfrequency":   p.modeB_warpFrequency   = v; break;
                    case "modeb_vscale":          p.modeB_vScale          = v; break;
                    case "modeb_noiseamp":        p.modeB_noiseAmp        = v; break;
                    case "modeb_yoffset":         p.modeB_yOffset         = v; break;
                    case "modeb_twist":           p.modeB_twist           = v; break;
                    case "modea_strength":        break; // removed — use modeA_width=0 to disable
                    case "modea_width":           p.modeA_width           = v; break;
                    case "modea_frequency":       p.modeA_frequency       = v; break;
                    case "modea_warpamp":         p.modeA_warpAmp         = v; break;
                    case "modea_warpfrequency":   p.modeA_warpFrequency   = v; break;
                    case "modea_vscale":          p.modeA_vScale          = v; break;
                    case "modea_widthvary":       p.modeA_widthVary       = v; break;
                    case "modea_widthfrequency":  p.modeA_widthFrequency  = v; break;
                    case "modea_yoffset":         p.modeA_yOffset         = v; break;
                    case "modec_strength":        break; // removed — use modeC_noiseAmp=0 to disable
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
                    case "modec_yoffset":         p.modeC_yOffset         = v; break;
                    case "blur_passes":            p.blur_passes            = v; break;
                    case "perona_k":              p.perona_k              = v; break;
                    case "perona_lambda":         p.perona_lambda         = v; break;
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
                var rawVal = line.Substring(eq + 1);
                var hashIdx = rawVal.IndexOf('#');
                if (hashIdx >= 0) rawVal = rawVal.Substring(0, hashIdx);
                cur.Add((line.Substring(0, eq).Trim().ToLowerInvariant(), rawVal.Trim()));
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
                        if (k == "protectlocations")
                            _protectLocations = !(v == "0" || v == "false");
                        if (k == "islandremoval")
                            _islandRemoval = !(v == "0" || v == "false");
                        if (k == "pocketremoval")
                            _pocketRemoval = !(v == "0" || v == "false");
                        if (k == "biomeblendradius" && float.TryParse(v, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float bbr))
                            _biomeBlendRadius = Mathf.Max(1f, bbr);
                        if (k == "biomeblendsmooth")
                            _biomeBlendSmooth = !(v == "0" || v == "false");
                        if (k == "transitionblurpasses" && int.TryParse(v, out int tbp))
                            _transitionBlurPasses = Mathf.Max(0, tbp);
                        if (k == "coastfade" && float.TryParse(v, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float cf))
                            _coastFade = Mathf.Max(0f, cf);
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
                if (p.modeA_width > 0f || p.modeB_radius > 0f || p.modeC_noiseAmp > 0f)
                    Log.LogInfo($"[VoxCaves]   {kv.Key}: mB_r={p.modeB_radius:F1} mA_w={p.modeA_width:F2} mC_amp={p.modeC_noiseAmp:F1}");
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
            sb.AppendLine("# modeB_vScale          = 0.5    # Y scale for 3D wall-noise (low=flat, high=vertical)");
            sb.AppendLine("# modeB_noiseAmp        = 0      # 3D SDF perturbation on band walls (world units; 0=off)");
            sb.AppendLine("# modeB_yOffset         = 0      # push band centre N units below surface (0=trench; >mbR=enclosed cave)");
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
            sb.AppendLine("protectLocations  = true   # false = carve through location areas (use with SpawnLocation Y-snap patch)");
            sb.AppendLine("islandRemoval     = true   # false = disable floating-island BFS removal");
            sb.AppendLine("pocketRemoval     = true   # false = disable sealed underground pocket BFS fill");
            sb.AppendLine("biomeBlendRadius  = 192    # world units over which biome profiles blend at boundaries");
            sb.AppendLine("coastFade         = 0      # world units above sea level (30) over which Ocean inflates into blend; 0=off");
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

            _caveNoise   = MakeNoise(0);  // Mode A tunnel field 1
            _caveNoise2  = MakeNoise(1);  // Mode A tunnel field 2
            _warpX       = MakeNoise(2);  // Mode A warp X (field 1)
            _warpZ       = MakeNoise(3);  // Mode A warp Z (field 1)
            _warpX2      = MakeNoise(13); // Mode A warp X (field 2)
            _warpZ2      = MakeNoise(14); // Mode A warp Z (field 2)
            _hmWarpX     = MakeNoise(4); // Mode B warp X
            _hmWarpZ     = MakeNoise(5); // Mode B warp Z
            _hmAddWarpX  = MakeNoise(6); // Mode C anchor warp X
            _hmAddWarpZ  = MakeNoise(7); // Mode C anchor warp Z
            _widthNoise  = MakeNoise(8);  // Mode A width modulation
            _addNoise       = MakeNoise(9);  // Mode C 3D density field
            _addVaryNoise   = MakeNoise(10); // Mode C amplitude modulation
            _twistVaryNoise = MakeNoise(11); // Mode C twist scale modulation
            _mbNoise        = MakeNoise(12); // Mode B 3D wall perturbation
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
            if (prof.modeA_width <= 0f || worldY <= floorY) return 0f;
            float sy  = worldY * prof.modeA_vScale;
            float twf = prof.modeA_warpFrequency;
            float wx  = _warpX! .GetNoise(worldX * twf, sy * twf, worldZ * twf) * prof.modeA_warpAmp;
            float wz  = _warpZ! .GetNoise(worldX * twf, sy * twf, worldZ * twf) * prof.modeA_warpAmp;
            float wx2 = _warpX2!.GetNoise(worldX * twf, sy * twf, worldZ * twf) * prof.modeA_warpAmp;
            float wz2 = _warpZ2!.GetNoise(worldX * twf, sy * twf, worldZ * twf) * prof.modeA_warpAmp;
            float tf  = prof.modeA_frequency;
            float na  = _caveNoise! .GetNoise((worldX + wx)  * tf, sy * tf, (worldZ + wz)  * tf);
            float nb  = _caveNoise2!.GetNoise((worldX + wx2) * tf, sy * tf, (worldZ + wz2) * tf);
            float dist = Mathf.Sqrt(na * na + nb * nb) * 0.7071f;
            if (dist >= prof.modeA_width) return 0f;
            return (prof.modeA_width - dist) / prof.modeA_width;
        }

        // ── voxcaves_probe: biome blend breakdown logged to console ──────────────────
        static void ProbeBlend(float worldX, float worldZ, WorldGenerator wg, float s)
        {
            var   profs      = _biomeProfiles;
            float coarseStep = 4f * s;
            int   pad        = (int)(_biomeBlendRadius / coarseStep) + 2;
            int   pcw        = 1 + 2 * pad;
            int   pgSize     = pcw * pcw;
            int   maxHops    = (int)(_biomeBlendRadius / coarseStep) + 1;
            int   center     = pad * pcw + pad;

            int fracStride = pgSize * MaxBiomeSlots;
            if (_biomeFrac == null || _biomeFrac.Length < fracStride) _biomeFrac = new float[fracStride];
            Array.Clear(_biomeFrac, 0, fracStride);
            if (_biomeDist == null || _biomeDist.Length < pgSize) _biomeDist = new int[pgSize];
            if (_biomeBfsQ == null || _biomeBfsQ.Length < pgSize) _biomeBfsQ = new int[pgSize];

            var biomeSlot = new Dictionary<int, int>(MaxBiomeSlots);
            float subWeight = 1f / (SuperN * SuperN);
            for (int px = 0; px < pcw; px++)
            for (int pz = 0; pz < pcw; pz++)
            {
                float cx = worldX + (px - pad) * coarseStep;
                float cz = worldZ + (pz - pad) * coarseStep;
                int cellBase = (px * pcw + pz) * MaxBiomeSlots;
                for (int si = 0; si < SuperN; si++)
                for (int sk = 0; sk < SuperN; sk++)
                {
                    int b = (int)wg.GetBiome(cx + (si + 0.5f) * coarseStep / SuperN,
                                             cz + (sk + 0.5f) * coarseStep / SuperN);
                    if (!biomeSlot.TryGetValue(b, out int slot))
                    {
                        if (biomeSlot.Count >= MaxBiomeSlots) continue;
                        slot = biomeSlot.Count;
                        biomeSlot[b] = slot;
                    }
                    _biomeFrac[cellBase + slot] += subWeight;
                }
            }

            // Center cell fractions (what fraction of sub-samples at this exact cell are each biome)
            var sb = new System.Text.StringBuilder();
            sb.Append($"  center cell fractions: ");
            foreach (var (biomeInt, slot) in biomeSlot)
            {
                float f = _biomeFrac[center * MaxBiomeSlots + slot];
                if (f > 0f) sb.Append($"{(Heightmap.Biome)biomeInt}={f:F2} ");
            }
            Log.LogInfo(sb.ToString());

            float totW = 0f;
            var contributions = new List<(string name, int distCells, float frac, float w, CaveProfile prof)>();

            foreach (var (biomeInt, slot) in biomeSlot)
            {
                int qHead = 0, qTail = 0;
                for (int i = 0; i < pgSize; i++)
                {
                    if (_biomeFrac[i * MaxBiomeSlots + slot] > 0f)
                        { _biomeDist[i] = 0; _biomeBfsQ[qTail++] = i; }
                    else _biomeDist[i] = int.MaxValue;
                }
                while (qHead < qTail)
                {
                    int cell = _biomeBfsQ[qHead++];
                    int nd   = _biomeDist[cell] + 1;
                    if (nd > maxHops) continue;
                    int px = cell / pcw, pz = cell % pcw;
                    if (px > 0)     { int n=cell-pcw; if (_biomeDist[n]>nd){_biomeDist[n]=nd;_biomeBfsQ[qTail++]=n;} }
                    if (px < pcw-1) { int n=cell+pcw; if (_biomeDist[n]>nd){_biomeDist[n]=nd;_biomeBfsQ[qTail++]=n;} }
                    if (pz > 0)     { int n=cell-1;   if (_biomeDist[n]>nd){_biomeDist[n]=nd;_biomeBfsQ[qTail++]=n;} }
                    if (pz < pcw-1) { int n=cell+1;   if (_biomeDist[n]>nd){_biomeDist[n]=nd;_biomeBfsQ[qTail++]=n;} }
                }
                int distC = _biomeDist[center];
                if (distC > maxHops) continue;
                float t = distC * coarseStep / _biomeBlendRadius;
                float w = _biomeBlendSmooth ? 1f - t * t * (3f - 2f * t) : 1f - t;
                if (w <= 0f) continue;
                float frac = distC == 0 ? _biomeFrac[center * MaxBiomeSlots + slot] : 1f;
                w *= frac;
                if (w <= 0f) continue;
                totW += w;
                var prof = profs.TryGetValue((Heightmap.Biome)biomeInt, out var p) ? p : CaveProfile.Default;
                contributions.Add(($"{(Heightmap.Biome)biomeInt}", distC, frac, w, prof));
            }

            if (totW <= 0f) { Log.LogInfo("  no biome contribution (totW=0)"); return; }

            Log.LogInfo($"  blendRadius={_biomeBlendRadius} coarseStep={coarseStep:F1} maxHops={maxHops}  totW={totW:F3}");
            foreach (var (name, distCells, frac, w, prof) in contributions)
            {
                float normW = w / totW;
                float worldDist = distCells * coarseStep;
                Log.LogInfo($"  [{name}]  dist={worldDist:F0}wu ({distCells} hops)  seedFrac={frac:F2}  weight={w:F3}  normW={normW:P0}  mcAmp={prof.modeC_noiseAmp:F1}  mbR={prof.modeB_radius:F1}  blur={prof.blur_passes:F0}");
            }

            // Blended result
            float rcp = 1f / totW;
            float blendMcAmp = contributions.Sum(c => c.w * c.prof.modeC_noiseAmp) * rcp;
            float blendMbR   = contributions.Sum(c => c.w * c.prof.modeB_radius)   * rcp;
            float blendBlur  = contributions.Sum(c => c.w * c.prof.blur_passes)    * rcp;
            float blendFloor = contributions.Sum(c => c.w * c.prof.floorDepth)     * rcp;
            Log.LogInfo($"  → blended: mcAmp={blendMcAmp:F2}  mbRadius={blendMbR:F2}  blur={blendBlur:F1}  floorDepth={blendFloor:F1}");
        }

        // ── Point-query biome blend — same supersampled fractional approach as BakeBlendedProfiles ──
        // Used by callers that need a blend for a single world point (e.g. hot-reload ceiling estimate).
        static CaveProfile PointBlendProfile(float worldX, float worldZ, WorldGenerator wg, float s)
        {
            var   profs      = _biomeProfiles;
            float coarseStep = 4f * s;
            int   pad        = (int)(_biomeBlendRadius / coarseStep) + 2;
            int   pcw        = 1 + 2 * pad;
            int   pgSize     = pcw * pcw;
            int   maxHops    = (int)(_biomeBlendRadius / coarseStep) + 1;
            int   center     = pad * pcw + pad;

            int fracStride = pgSize * MaxBiomeSlots;
            if (_biomeFrac == null || _biomeFrac.Length < fracStride) _biomeFrac = new float[fracStride];
            Array.Clear(_biomeFrac, 0, fracStride);
            if (_biomeDist == null || _biomeDist.Length < pgSize) _biomeDist = new int[pgSize];
            if (_biomeBfsQ == null || _biomeBfsQ.Length < pgSize) _biomeBfsQ = new int[pgSize];

            var biomeSlot = new Dictionary<int, int>(MaxBiomeSlots);
            float subWeight = 1f / (SuperN * SuperN);
            for (int px = 0; px < pcw; px++)
            for (int pz = 0; pz < pcw; pz++)
            {
                float cx = worldX + (px - pad) * coarseStep;
                float cz = worldZ + (pz - pad) * coarseStep;
                int cellBase = (px * pcw + pz) * MaxBiomeSlots;
                for (int si = 0; si < SuperN; si++)
                for (int sk = 0; sk < SuperN; sk++)
                {
                    int b = (int)wg.GetBiome(cx + (si + 0.5f) * coarseStep / SuperN,
                                             cz + (sk + 0.5f) * coarseStep / SuperN);
                    if (!biomeSlot.TryGetValue(b, out int slot))
                    {
                        if (biomeSlot.Count >= MaxBiomeSlots) continue;
                        slot = biomeSlot.Count;
                        biomeSlot[b] = slot;
                    }
                    _biomeFrac[cellBase + slot] += subWeight;
                }
            }

            var   result = new CaveProfile();
            float totW   = 0f;

            foreach (var (biomeInt, slot) in biomeSlot)
            {
                int qHead = 0, qTail = 0;
                for (int i = 0; i < pgSize; i++)
                {
                    if (_biomeFrac[i * MaxBiomeSlots + slot] > 0f)
                        { _biomeDist[i] = 0; _biomeBfsQ[qTail++] = i; }
                    else _biomeDist[i] = int.MaxValue;
                }
                while (qHead < qTail)
                {
                    int cell = _biomeBfsQ[qHead++];
                    int nd   = _biomeDist[cell] + 1;
                    if (nd > maxHops) continue;
                    int px = cell / pcw, pz = cell % pcw;
                    if (px > 0)     { int n=cell-pcw; if (_biomeDist[n]>nd){_biomeDist[n]=nd;_biomeBfsQ[qTail++]=n;} }
                    if (px < pcw-1) { int n=cell+pcw; if (_biomeDist[n]>nd){_biomeDist[n]=nd;_biomeBfsQ[qTail++]=n;} }
                    if (pz > 0)     { int n=cell-1;   if (_biomeDist[n]>nd){_biomeDist[n]=nd;_biomeBfsQ[qTail++]=n;} }
                    if (pz < pcw-1) { int n=cell+1;   if (_biomeDist[n]>nd){_biomeDist[n]=nd;_biomeBfsQ[qTail++]=n;} }
                }
                int distC = _biomeDist[center];
                if (distC > maxHops) continue;
                float tC = distC * coarseStep / _biomeBlendRadius;
                float w  = _biomeBlendSmooth ? 1f - tC * tC * (3f - 2f * tC) : 1f - tC;
                if (w <= 0f) continue;
                if (distC == 0) w *= _biomeFrac[center * MaxBiomeSlots + slot];
                if (w <= 0f) continue;
                totW += w;
                var prof = profs.TryGetValue((Heightmap.Biome)biomeInt, out var p) ? p : CaveProfile.Default;
                result.modeB_radius         += w*prof.modeB_radius;    result.modeB_warpAmp       += w*prof.modeB_warpAmp;
                result.modeB_warpFrequency  += w*prof.modeB_warpFrequency; result.modeB_vScale    += w*prof.modeB_vScale;
                result.modeB_noiseAmp       += w*prof.modeB_noiseAmp;  result.modeB_yOffset       += w*prof.modeB_yOffset;
                result.modeB_twist          += w*prof.modeB_twist;
                result.modeA_width          += w*prof.modeA_width;     result.modeA_frequency     += w*prof.modeA_frequency;
                result.modeA_warpAmp        += w*prof.modeA_warpAmp;   result.modeA_warpFrequency += w*prof.modeA_warpFrequency;
                result.modeA_vScale         += w*prof.modeA_vScale;    result.modeA_widthVary     += w*prof.modeA_widthVary;
                result.modeA_widthFrequency += w*prof.modeA_widthFrequency;
                result.modeA_yOffset        += w*prof.modeA_yOffset;
                result.modeC_threshold      += w*prof.modeC_threshold; result.modeC_noiseAmp      += w*prof.modeC_noiseAmp;
                result.modeC_frequency      += w*prof.modeC_frequency; result.modeC_warpAmp       += w*prof.modeC_warpAmp;
                result.modeC_warpFrequency  += w*prof.modeC_warpFrequency; result.modeC_vScale    += w*prof.modeC_vScale;
                result.modeC_twist          += w*prof.modeC_twist;     result.modeC_twistVary     += w*prof.modeC_twistVary;
                result.modeC_twistFrequency += w*prof.modeC_twistFrequency; result.modeC_ampVary  += w*prof.modeC_ampVary;
                result.modeC_ampFrequency   += w*prof.modeC_ampFrequency;
                result.modeC_yOffset        += w*prof.modeC_yOffset;
                result.blur_passes          += w*prof.blur_passes;     result.perona_k            += w*prof.perona_k;
                result.perona_lambda        += w*prof.perona_lambda;   result.floorDepth          += w*prof.floorDepth;
            }
            if (totW > 0f)
            {
                float rcp = 1f / totW;
                result.modeB_radius*=rcp; result.modeB_warpAmp*=rcp; result.modeB_warpFrequency*=rcp;
                result.modeB_vScale*=rcp; result.modeB_noiseAmp*=rcp; result.modeB_yOffset*=rcp; result.modeB_twist*=rcp;
                result.modeA_width*=rcp; result.modeA_frequency*=rcp; result.modeA_warpAmp*=rcp;
                result.modeA_warpFrequency*=rcp; result.modeA_vScale*=rcp; result.modeA_widthVary*=rcp;
                result.modeA_widthFrequency*=rcp; result.modeA_yOffset*=rcp;
                result.modeC_threshold*=rcp; result.modeC_noiseAmp*=rcp; result.modeC_frequency*=rcp;
                result.modeC_warpAmp*=rcp; result.modeC_warpFrequency*=rcp; result.modeC_vScale*=rcp;
                result.modeC_twist*=rcp; result.modeC_twistVary*=rcp; result.modeC_twistFrequency*=rcp;
                result.modeC_ampVary*=rcp; result.modeC_ampFrequency*=rcp; result.modeC_yOffset*=rcp;
                result.blur_passes*=rcp; result.perona_k*=rcp; result.perona_lambda*=rcp; result.floorDepth*=rcp;
            }
            return result;
        }

        // ── Distance-based biome weight pipeline ─────────────────────────────────────
        // For each coarse grid cell, computes the world-space distance to each distinct
        // biome via BFS on the padded sample grid, then derives blend weights from that
        // distance.  Weight = max(0, 1 - dist/blendRadius), normalized so all biome
        // weights sum to 1.  This gives exact 50/50 at any biome boundary, pure 1.0
        // far inside a biome, and a predictable linear falloff of width blendRadius.
        static void BakeBlendedProfiles(int cw4, int ox, int oz, int overlap, float s, WorldGenerator wg)
        {
            var profs = _biomeProfiles;
            CaveProfile GetP(Heightmap.Biome b) =>
                profs.TryGetValue(b, out var p) ? p : CaveProfile.Default;

            float coarseStep = 4f * s;
            int pad    = (int)(_biomeBlendRadius / coarseStep) + 2;
            int pcw    = cw4 + 2 * pad;
            int pgSize = pcw * pcw;
            int maxHops = (int)(_biomeBlendRadius / coarseStep) + 1;

            // ── Step 1: supersample GetBiome — SuperN×SuperN calls per coarse cell ──
            // Each cell gets fractional membership per biome rather than a single hard label.
            // This eliminates the aliasing artifact where one zone shows more BF influence
            // than its neighbor simply because its single sample fell inside a thin biome finger.
            int fracStride = pgSize * MaxBiomeSlots;
            if (_biomeFrac == null || _biomeFrac.Length < fracStride) _biomeFrac = new float[fracStride];
            Array.Clear(_biomeFrac, 0, fracStride);

            var biomeSlot = new Dictionary<int, int>(MaxBiomeSlots);
            float subWeight = 1f / (SuperN * SuperN);
            for (int px = 0; px < pcw; px++)
            for (int pz = 0; pz < pcw; pz++)
            {
                int gx0 = ox - overlap + (px - pad) * 4;
                int gz0 = oz - overlap + (pz - pad) * 4;
                int cellBase = (px * pcw + pz) * MaxBiomeSlots;
                for (int si = 0; si < SuperN; si++)
                for (int sk = 0; sk < SuperN; sk++)
                {
                    float wx = (gx0 + (si + 0.5f) * 4f / SuperN) * s;
                    float wz = (gz0 + (sk + 0.5f) * 4f / SuperN) * s;
                    int b = (int)wg.GetBiome(wx, wz);
                    if (!biomeSlot.TryGetValue(b, out int slot))
                    {
                        if (biomeSlot.Count >= MaxBiomeSlots) continue;
                        slot = biomeSlot.Count;
                        biomeSlot[b] = slot;
                    }
                    _biomeFrac[cellBase + slot] += subWeight;
                }
            }

            // ── Step 2: per-biome BFS distance transform + accumulate ────────────────
            int cellCount = cw4 * cw4;
            if (_blendedProfs == null || _blendedProfs.Length < cellCount)
                _blendedProfs = new CaveProfile[cellCount];
            if (_biomeTotW == null || _biomeTotW.Length < cellCount)
                _biomeTotW = new float[cellCount];
            for (int i = 0; i < cellCount; i++) { _blendedProfs[i] = new CaveProfile(); _biomeTotW[i] = 0f; }

            if (_biomeDist == null || _biomeDist.Length < pgSize) _biomeDist = new int[pgSize];
            if (_biomeBfsQ == null || _biomeBfsQ.Length < pgSize) _biomeBfsQ = new int[pgSize];

            foreach (var (biomeInt, slot) in biomeSlot)
            {
                var prof = GetP((Heightmap.Biome)biomeInt);

                // Multi-source BFS: seed from every cell that has any fraction of this biome.
                int qHead = 0, qTail = 0;
                for (int i = 0; i < pgSize; i++)
                {
                    if (_biomeFrac[i * MaxBiomeSlots + slot] > 0f)
                        { _biomeDist[i] = 0; _biomeBfsQ[qTail++] = i; }
                    else _biomeDist[i] = int.MaxValue;
                }
                while (qHead < qTail)
                {
                    int cell = _biomeBfsQ[qHead++];
                    int nd   = _biomeDist[cell] + 1;
                    if (nd > maxHops) continue;
                    int px = cell / pcw, pz = cell % pcw;
                    if (px > 0)     { int n = cell - pcw; if (_biomeDist[n] > nd) { _biomeDist[n] = nd; _biomeBfsQ[qTail++] = n; } }
                    if (px < pcw-1) { int n = cell + pcw; if (_biomeDist[n] > nd) { _biomeDist[n] = nd; _biomeBfsQ[qTail++] = n; } }
                    if (pz > 0)     { int n = cell - 1;   if (_biomeDist[n] > nd) { _biomeDist[n] = nd; _biomeBfsQ[qTail++] = n; } }
                    if (pz < pcw-1) { int n = cell + 1;   if (_biomeDist[n] > nd) { _biomeDist[n] = nd; _biomeBfsQ[qTail++] = n; } }
                }

                // Accumulate weighted profile into each inner coarse cell.
                // At distance 0, scale by fractional membership so a cell that is 50% BF
                // contributes half as much as a fully-BF cell at the same distance.
                for (int cx4 = 0; cx4 < cw4; cx4++)
                for (int cz4 = 0; cz4 < cw4; cz4++)
                {
                    int pIdx = (cx4 + pad) * pcw + (cz4 + pad);
                    int distCells = _biomeDist[pIdx];
                    if (distCells > maxHops) continue;
                    float t = distCells * coarseStep / _biomeBlendRadius;
                    float w = _biomeBlendSmooth ? 1f - t * t * (3f - 2f * t) : 1f - t;
                    if (w <= 0f) continue;
                    // Scale by fractional membership at the seed cell (distance 0 only).
                    // For propagated cells (dist > 0) the full falloff weight applies as-is.
                    if (distCells == 0)
                        w *= _biomeFrac[pIdx * MaxBiomeSlots + slot];
                    if (w <= 0f) continue;
                    int idx = cx4 * cw4 + cz4;
                    ref var acc = ref _blendedProfs[idx];
                    _biomeTotW[idx]           += w;
                    acc.modeB_radius          += w * prof.modeB_radius;
                    acc.modeB_warpAmp         += w * prof.modeB_warpAmp;
                    acc.modeB_warpFrequency   += w * prof.modeB_warpFrequency;
                    acc.modeB_vScale          += w * prof.modeB_vScale;
                    acc.modeB_noiseAmp        += w * prof.modeB_noiseAmp;
                    acc.modeB_yOffset         += w * prof.modeB_yOffset;
                    acc.modeB_twist           += w * prof.modeB_twist;
                    acc.modeA_width           += w * prof.modeA_width;
                    acc.modeA_frequency       += w * prof.modeA_frequency;
                    acc.modeA_warpAmp         += w * prof.modeA_warpAmp;
                    acc.modeA_warpFrequency   += w * prof.modeA_warpFrequency;
                    acc.modeA_vScale          += w * prof.modeA_vScale;
                    acc.modeA_widthVary       += w * prof.modeA_widthVary;
                    acc.modeA_widthFrequency  += w * prof.modeA_widthFrequency;
                    acc.modeA_yOffset         += w * prof.modeA_yOffset;
                    acc.modeC_threshold       += w * prof.modeC_threshold;
                    acc.modeC_noiseAmp        += w * prof.modeC_noiseAmp;
                    acc.modeC_frequency       += w * prof.modeC_frequency;
                    acc.modeC_warpAmp         += w * prof.modeC_warpAmp;
                    acc.modeC_warpFrequency   += w * prof.modeC_warpFrequency;
                    acc.modeC_vScale          += w * prof.modeC_vScale;
                    acc.modeC_twist           += w * prof.modeC_twist;
                    acc.modeC_twistVary       += w * prof.modeC_twistVary;
                    acc.modeC_twistFrequency  += w * prof.modeC_twistFrequency;
                    acc.modeC_ampVary         += w * prof.modeC_ampVary;
                    acc.modeC_ampFrequency    += w * prof.modeC_ampFrequency;
                    acc.modeC_yOffset         += w * prof.modeC_yOffset;
                    acc.blur_passes           += w * prof.blur_passes;
                    acc.perona_k              += w * prof.perona_k;
                    acc.perona_lambda         += w * prof.perona_lambda;
                    acc.floorDepth            += w * prof.floorDepth;
                }
            }

            // ── Step 2b: Height-based Ocean inflation ─────────────────────────────────
            // Injects extra Ocean-profile weight wherever the heightmap is within coastFade
            // world units of sea level (30f).  This suppresses carving in shallow coastal
            // areas regardless of what biome label the map assigns there.
            if (_coastFade > 0f)
            {
                const float SeaLevel = 30f;
                var oceanProf = GetP(Heightmap.Biome.Ocean);
                for (int cx4 = 0; cx4 < cw4; cx4++)
                for (int cz4 = 0; cz4 < cw4; cz4++)
                {
                    float wx = (ox - overlap + cx4 * 4 + 2) * s;
                    float wz = (oz - overlap + cz4 * 4 + 2) * s;
                    float h  = wg.GetHeight(wx, wz);
                    float coastW = 1f - Mathf.Clamp01((h - SeaLevel) / _coastFade);
                    if (coastW <= 0f) continue;
                    int idx = cx4 * cw4 + cz4;
                    ref var acc = ref _blendedProfs[idx];
                    _biomeTotW[idx]           += coastW;
                    acc.modeB_radius          += coastW * oceanProf.modeB_radius;
                    acc.modeB_warpAmp         += coastW * oceanProf.modeB_warpAmp;
                    acc.modeB_warpFrequency   += coastW * oceanProf.modeB_warpFrequency;
                    acc.modeB_vScale          += coastW * oceanProf.modeB_vScale;
                    acc.modeB_noiseAmp        += coastW * oceanProf.modeB_noiseAmp;
                    acc.modeB_yOffset         += coastW * oceanProf.modeB_yOffset;
                    acc.modeB_twist           += coastW * oceanProf.modeB_twist;
                    acc.modeA_width           += coastW * oceanProf.modeA_width;
                    acc.modeA_frequency       += coastW * oceanProf.modeA_frequency;
                    acc.modeA_warpAmp         += coastW * oceanProf.modeA_warpAmp;
                    acc.modeA_warpFrequency   += coastW * oceanProf.modeA_warpFrequency;
                    acc.modeA_vScale          += coastW * oceanProf.modeA_vScale;
                    acc.modeA_widthVary       += coastW * oceanProf.modeA_widthVary;
                    acc.modeA_widthFrequency  += coastW * oceanProf.modeA_widthFrequency;
                    acc.modeA_yOffset         += coastW * oceanProf.modeA_yOffset;
                    acc.modeC_threshold       += coastW * oceanProf.modeC_threshold;
                    acc.modeC_noiseAmp        += coastW * oceanProf.modeC_noiseAmp;
                    acc.modeC_frequency       += coastW * oceanProf.modeC_frequency;
                    acc.modeC_warpAmp         += coastW * oceanProf.modeC_warpAmp;
                    acc.modeC_warpFrequency   += coastW * oceanProf.modeC_warpFrequency;
                    acc.modeC_vScale          += coastW * oceanProf.modeC_vScale;
                    acc.modeC_twist           += coastW * oceanProf.modeC_twist;
                    acc.modeC_twistVary       += coastW * oceanProf.modeC_twistVary;
                    acc.modeC_twistFrequency  += coastW * oceanProf.modeC_twistFrequency;
                    acc.modeC_ampVary         += coastW * oceanProf.modeC_ampVary;
                    acc.modeC_ampFrequency    += coastW * oceanProf.modeC_ampFrequency;
                    acc.modeC_yOffset         += coastW * oceanProf.modeC_yOffset;
                    acc.blur_passes           += coastW * oceanProf.blur_passes;
                    acc.perona_k              += coastW * oceanProf.perona_k;
                    acc.perona_lambda         += coastW * oceanProf.perona_lambda;
                    acc.floorDepth            += coastW * oceanProf.floorDepth;
                }
            }

            // ── Step 3: Normalize ─────────────────────────────────────────────────────
            for (int idx = 0; idx < cellCount; idx++)
            {
                float wt = _biomeTotW[idx];
                if (wt <= 0f) continue;
                float rcp = 1f / wt;
                ref var p = ref _blendedProfs[idx];
                p.modeB_radius          *= rcp; p.modeB_warpAmp       *= rcp;
                p.modeB_warpFrequency   *= rcp; p.modeB_vScale        *= rcp;
                p.modeB_noiseAmp        *= rcp; p.modeB_yOffset       *= rcp; p.modeB_twist *= rcp;
                p.modeA_width           *= rcp; p.modeA_frequency     *= rcp;
                p.modeA_warpAmp         *= rcp; p.modeA_warpFrequency *= rcp;
                p.modeA_vScale          *= rcp; p.modeA_widthVary     *= rcp; p.modeA_widthFrequency *= rcp; p.modeA_yOffset *= rcp;
                p.modeC_threshold       *= rcp; p.modeC_noiseAmp      *= rcp;
                p.modeC_frequency       *= rcp; p.modeC_warpAmp       *= rcp;
                p.modeC_warpFrequency   *= rcp; p.modeC_vScale        *= rcp;
                p.modeC_twist           *= rcp; p.modeC_twistVary     *= rcp; p.modeC_twistFrequency *= rcp;
                p.modeC_ampVary         *= rcp; p.modeC_ampFrequency  *= rcp; p.modeC_yOffset *= rcp;
                p.blur_passes           *= rcp; p.perona_k            *= rcp;
                p.perona_lambda         *= rcp; p.floorDepth          *= rcp;
            }
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
            if (!_protectLocations || nearbyLocs.Count == 0) return 1f;
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

            const int overlap = 4;
            int bwEarly = width + 2 * overlap;
            int cw4Early = (bwEarly + 3) / 4;

            // Run the distance-based biome blend pass before hiMargin so all subsequent
            // profile reads use the same representation — no old kernel anywhere.
            if (wg != null) BakeBlendedProfiles(cw4Early, ox, oz, overlap, s, wg);

            // Read center and corners directly from _blendedProfs.
            CaveProfile ProfAt(int cx4, int cz4) =>
                (_blendedProfs != null && cx4 * cw4Early + cz4 < _blendedProfs.Length)
                    ? _blendedProfs[cx4 * cw4Early + cz4] : CaveProfile.Default;
            int lastC = cw4Early - 1;
            var centerProf = ProfAt(cw4Early / 2, cw4Early / 2);

            var centerBiome = wg?.GetBiome(zoneX, zoneZ) ?? Heightmap.Biome.None;
            Log.LogInfo($"[VoxCaves] zone ({ox},{oz}) biome={centerBiome} mB_r={centerProf.modeB_radius:F1} mA_w={centerProf.modeA_width:F2} mC_amp={centerProf.modeC_noiseAmp:F1}");

            // Build nearby-location list for the location-protection fade.
            float searchR = width * s * 0.5f + LocProtectR + LocFadeR;
            var nearbyLocs = new List<Vector2>();
            foreach (var loc in _locationPositions)
            {
                float dx = loc.x - zoneX, dz = loc.y - zoneZ;
                if (dx * dx + dz * dz < searchR * searchR)
                    nearbyLocs.Add(loc);
            }

            // Mode C can raise terrain above yHi — compute headroom from blended corners.
            int hiMargin = 24;
            {
                float maxExtM = 0f;
                void CheckProf(CaveProfile cp) {
                    if (cp.modeC_noiseAmp <= 0f) return;
                    float a = cp.modeC_noiseAmp * Mathf.Max(1f, 1f + cp.modeC_ampVary);
                    float e = Mathf.Max(0f, 1f - cp.modeC_threshold) * a + cp.modeC_warpAmp;
                    if (e > maxExtM) maxExtM = e;
                }
                CheckProf(centerProf);
                CheckProf(ProfAt(0,       0      )); CheckProf(ProfAt(lastC, 0      ));
                CheckProf(ProfAt(0,       lastC  )); CheckProf(ProfAt(lastC, lastC  ));
                if (maxExtM > 0f)
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

            int   cw4     = cw4Early;  // already computed above for BakeBlendedProfiles
            BakeSdfSetup(ox, oz, overlap, s, extLo, bh, bw, cw4, wg, nearbyLocs,
                out bool bakeC, out bool bakeA, out bool anyMbActive);

            long _t0 = _sw.ElapsedMilliseconds;

            // ── Phase 1 ───────────────────────────────────────────────────────────
            FillPhase1(buf, bw, bh, extLo, s, ox, oz, overlap, bakeC, bakeA, anyMbActive, wg, cw4, nearbyLocs);
            long _t1 = _sw.ElapsedMilliseconds;

            long _t1b = _sw.ElapsedMilliseconds;

            // Read blur params from the blended center coarse cell so the distance-based
            // weights govern blur behaviour, not the old 5×5 kernel centerProf.
            CaveProfile blurProf = centerProf;
            if (_blendedProfs != null)
            {
                int cCx = cw4 / 2, cCz = cw4 / 2;
                blurProf = _blendedProfs[cCx * cw4 + cCz];
            }
            // Ceil so any nonzero blended blur_passes activates at least 1 pass.
            // RoundToInt would give 0 for values like 0.3 in transition zones that
            // still have active Mode C terrain and need smoothing.
            int blurPasses = (bakeC || bakeA || anyMbActive)
                ? Mathf.Max(Mathf.CeilToInt(blurProf.blur_passes), _transitionBlurPasses)
                : 0;

            // ── Phase 2: Blur / smooth the SDF buffer ────────────────────────────────
            // Two modes selectable per profile via perona_k:
            //   perona_k = 0  → separable 5-tap Gaussian [1/16,4/16,6/16,4/16,1/16]
            //   perona_k > 0  → Perona-Malik anisotropic diffusion (preserves edges)
            if (blurProf.perona_k > 0f)
            {
                // Perona-Malik: d_new = d + λ * Σ exp(-(Δ/K)²) * Δ  for 6 face neighbours.
                // Smooths homogeneous regions aggressively; slows at SDF gradients ≥ K.
                float invK2 = 1f / (blurProf.perona_k * blurProf.perona_k);
                float lam   = blurProf.perona_lambda;
                int   pbSz  = bw * bh * bw;
                for (int pass = 0; pass < blurPasses; pass++)
                {
                    for (int cx = 0; cx < bw; cx++)
                    for (int cy = 0; cy < bh; cy++)
                    for (int cz = 0; cz < bw; cz++)
                    {
                        int   i    = (cx * bh + cy) * bw + cz;
                        float v    = buf[i], flux = 0f;
                        if (cx > 0)    { float d = buf[((cx-1)*bh+cy)*bw+cz] - v; flux += Mathf.Exp(-d*d*invK2) * d; }
                        if (cx < bw-1) { float d = buf[((cx+1)*bh+cy)*bw+cz] - v; flux += Mathf.Exp(-d*d*invK2) * d; }
                        if (cy > 0)    { float d = buf[(cx*bh+(cy-1))*bw+cz] - v; flux += Mathf.Exp(-d*d*invK2) * d; }
                        if (cy < bh-1) { float d = buf[(cx*bh+(cy+1))*bw+cz] - v; flux += Mathf.Exp(-d*d*invK2) * d; }
                        if (cz > 0)    { float d = buf[(cx*bh+cy)*bw+(cz-1)] - v; flux += Mathf.Exp(-d*d*invK2) * d; }
                        if (cz < bw-1) { float d = buf[(cx*bh+cy)*bw+(cz+1)] - v; flux += Mathf.Exp(-d*d*invK2) * d; }
                        tmp[i] = v + lam * flux;
                    }
                    Array.Copy(tmp, buf, pbSz);
                }
            }
            else
            for (int pass = 0; pass < blurPasses; pass++)
            {
                // X pass: buf → tmp (branch hoisted to cx level)
                for (int cx = 0; cx < bw; cx++)
                {
                    bool full = cx >= 2 && cx <= bw - 3;
                    for (int cy = 0; cy < bh; cy++)
                    for (int cz = 0; cz < bw; cz++)
                    {
                        int i = (cx * bh + cy) * bw + cz;
                        tmp[i] = full
                            ? buf[((cx-2)*bh+cy)*bw+cz]*0.0625f + buf[((cx-1)*bh+cy)*bw+cz]*0.25f
                              + buf[i]*0.375f
                              + buf[((cx+1)*bh+cy)*bw+cz]*0.25f + buf[((cx+2)*bh+cy)*bw+cz]*0.0625f
                            : buf[i];
                    }
                }
                // Z pass: tmp → buf
                for (int cx = 0; cx < bw; cx++)
                for (int cy = 0; cy < bh; cy++)
                for (int cz = 0; cz < bw; cz++)
                {
                    int i = (cx * bh + cy) * bw + cz;
                    buf[i] = (cz >= 2 && cz <= bw - 3)
                        ? tmp[(cx*bh+cy)*bw+(cz-2)]*0.0625f + tmp[(cx*bh+cy)*bw+(cz-1)]*0.25f
                          + tmp[i]*0.375f
                          + tmp[(cx*bh+cy)*bw+(cz+1)]*0.25f + tmp[(cx*bh+cy)*bw+(cz+2)]*0.0625f
                        : tmp[i];
                }
                // Y pass: buf → tmp → buf
                for (int cx = 0; cx < bw; cx++)
                for (int cy = 0; cy < bh; cy++)
                for (int cz = 0; cz < bw; cz++)
                {
                    int i = (cx * bh + cy) * bw + cz;
                    tmp[i] = (cy >= 2 && cy <= bh - 3)
                        ? buf[(cx*bh+(cy-2))*bw+cz]*0.0625f + buf[(cx*bh+(cy-1))*bw+cz]*0.25f
                          + buf[i]*0.375f
                          + buf[(cx*bh+(cy+1))*bw+cz]*0.25f + buf[(cx*bh+(cy+2))*bw+cz]*0.0625f
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

            // ── Phase 2.5: Floating-island removal (after blur) ───────────────────────
            // BFS from cy=0 (bedrock); any solid voxel unreachable from the bottom is a
            // floating island.  Cross-zone grounding uses neighbor face caches, computed
            // on-demand by EnsureFixpoint only when the pre-pass finds ungrounded blobs
            // touching a zone face — saving the neighbor computation in the common case.
            if (_islandRemoval) {
                int islandBufSize = bw * bh * bw;
                int stride_x      = bh * bw;
                int terNegX25 = overlap;
                int terPosX25 = overlap + width - 1;
                int terNegZ25 = overlap;
                int terPosZ25 = overlap + width - 1;
                if (_visitedBuf  == null || _visitedBuf.Length  < islandBufSize) _visitedBuf  = new bool[islandBufSize];
                if (_islandQueue == null || _islandQueue.Length < islandBufSize) _islandQueue = new int[islandBufSize];
                Array.Clear(_visitedBuf, 0, islandBufSize);

                int qHead = 0, qTail = 0;

                // ── Pre-pass BFS: cy=0 seeds only ────────────────────────────────────
                for (int cx = 0; cx < bw; cx++)
                for (int cz = 0; cz < bw; cz++)
                {
                    int idx = cx * stride_x + cz; // cy=0
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

                // Check which territory faces have ungrounded solid after the pre-pass.
                bool ungNX = false, ungPX = false, ungNZ = false, ungPZ = false;
                for (int cy = 0; cy < bh; cy++)
                for (int cz = 0; cz < bw; cz++)
                {
                    int iNX = terNegX25 * stride_x + cy * bw + cz;
                    int iPX = terPosX25 * stride_x + cy * bw + cz;
                    if (!ungNX && buf[iNX] < 0f && !_visitedBuf[iNX]) ungNX = true;
                    if (!ungPX && buf[iPX] < 0f && !_visitedBuf[iPX]) ungPX = true;
                }
                for (int cy = 0; cy < bh; cy++)
                for (int cx = 0; cx < bw; cx++)
                {
                    int iNZ = cx * stride_x + cy * bw + terNegZ25;
                    int iPZ = cx * stride_x + cy * bw + terPosZ25;
                    if (!ungNZ && buf[iNZ] < 0f && !_visitedBuf[iNZ]) ungNZ = true;
                    if (!ungPZ && buf[iPZ] < 0f && !_visitedBuf[iPZ]) ungPZ = true;
                }

                // ── Full BFS with neighbor caches (only when needed) ─────────────────
                // If any face has ungrounded solid, fetch the relevant neighbor caches
                // and re-run the BFS with those additional seeds.
                bool needsFullPass = ungNX || ungPX || ungNZ || ungPZ;
                if (needsFullPass)
                {
                    EnsureFixpoint(ox, oz, width, ungNX, ungPX, ungNZ, ungPZ);

                    Array.Clear(_visitedBuf, 0, islandBufSize);
                    qHead = 0; qTail = 0;

                    // cy=0 seeds
                    for (int cx = 0; cx < bw; cx++)
                    for (int cz = 0; cz < bw; cz++)
                    {
                        int idx = cx * stride_x + cz;
                        if (buf[idx] < 0f && !_visitedBuf[idx])
                        { _visitedBuf[idx] = true; _islandQueue[qTail++] = idx; }
                    }

                    // Neighbor face cache seeds
                    void SeedFromCache(bool[]? nFace, int nExtLo, int nBh, int nBw,
                                       int ourCx, bool xFace)
                    {
                        if (nFace == null) return;
                        for (int cperp = 0; cperp < bw; cperp++)
                        for (int cy = 0; cy < bh; cy++)
                        {
                            int worldY = extLo + cy;
                            int ncy    = worldY - nExtLo;
                            if (ncy < 0 || ncy >= nBh) continue;
                            int nIdx = ncy * nBw + cperp;
                            if (nIdx >= nFace.Length || !nFace[nIdx]) continue;
                            int bufIdx = xFace
                                ? ourCx * stride_x + cy * bw + cperp
                                : cperp * stride_x + cy * bw + ourCx;
                            if (buf[bufIdx] < 0f && !_visitedBuf[bufIdx])
                            { _visitedBuf[bufIdx] = true; _islandQueue[qTail++] = bufIdx; }
                        }
                    }

                    if (_zoneFaceCache.TryGetValue((ox - width, oz), out var negXC25))
                        SeedFromCache(negXC25.posX, negXC25.extLo, negXC25.bh, negXC25.bw, terNegX25, true);
                    if (_zoneFaceCache.TryGetValue((ox + width, oz), out var posXC25))
                        SeedFromCache(posXC25.negX, posXC25.extLo, posXC25.bh, posXC25.bw, terPosX25, true);
                    if (_zoneFaceCache.TryGetValue((ox, oz - width), out var negZC25))
                        SeedFromCache(negZC25.posZ, negZC25.extLo, negZC25.bh, negZC25.bw, terNegZ25, false);
                    if (_zoneFaceCache.TryGetValue((ox, oz + width), out var posZC25))
                        SeedFromCache(posZC25.negZ, posZC25.extLo, posZC25.bh, posZC25.bw, terPosZ25, false);

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
                }

                // Store A's face cache from the final BFS result.
                {
                    int faceSz25 = bh * bw;
                    var ac = new ZoneFaceCache {
                        extLo = extLo, bh = bh, bw = bw,
                        negX    = new bool[faceSz25], posX    = new bool[faceSz25],
                        negZ    = new bool[faceSz25], posZ    = new bool[faceSz25],
                        negXAir = new bool[faceSz25], posXAir = new bool[faceSz25],
                        negZAir = new bool[faceSz25], posZAir = new bool[faceSz25],
                    };
                    for (int cy = 0; cy < bh; cy++)
                    for (int cz = 0; cz < bw; cz++)
                    {
                        int fIdx = cy * bw + cz;
                        ac.negX[fIdx] = _visitedBuf[terNegX25 * stride_x + cy * bw + cz];
                        ac.posX[fIdx] = _visitedBuf[terPosX25 * stride_x + cy * bw + cz];
                    }
                    for (int cy = 0; cy < bh; cy++)
                    for (int cx = 0; cx < bw; cx++)
                    {
                        int fIdx = cy * bw + cx;
                        ac.negZ[fIdx] = _visitedBuf[cx * stride_x + cy * bw + terNegZ25];
                        ac.posZ[fIdx] = _visitedBuf[cx * stride_x + cy * bw + terPosZ25];
                    }
                    _zoneFaceCache[(ox, oz)] = ac;
                }

                int removed = 0;
                for (int i = 0; i < islandBufSize; i++)
                    if (!_visitedBuf[i] && buf[i] < 0f) { buf[i] = s; removed++; }

                Log.LogInfo($"[VoxCaves] island-fill zone ({ox},{oz}): pre={!needsFullPass} seeds={qTail} removed={removed}");
            }

            // ── Phase 2.6: Sealed underground pocket removal ──────────────────────────
            // BFS from all boundary air voxels (top face + 4 side faces).
            // Any air voxel unreachable from the boundary is a sealed pocket → fill solid.
            // Seeding from side faces treats zone-edge caves as open (conservative).
            if (_pocketRemoval) {
                Array.Clear(_visitedBuf!, 0, bw * bh * bw);
                int stride_x = bh * bw;
                int qHead = 0, qTail = 0;

                void TryEnqueueAir(int i)
                {
                    if (!_visitedBuf![i] && buf[i] >= 0f) { _visitedBuf[i] = true; _islandQueue![qTail++] = i; }
                }

                // Top face (cy = bh-1)
                for (int cx = 0; cx < bw; cx++)
                for (int cz = 0; cz < bw; cz++)
                    TryEnqueueAir(cx * stride_x + (bh - 1) * bw + cz);

                // Side faces (cx=0, cx=bw-1, cz=0, cz=bw-1)
                for (int cy = 0; cy < bh; cy++)
                for (int cz = 0; cz < bw; cz++)
                {
                    TryEnqueueAir(0          * stride_x + cy * bw + cz);
                    TryEnqueueAir((bw - 1)   * stride_x + cy * bw + cz);
                }
                for (int cx = 0; cx < bw; cx++)
                for (int cy = 0; cy < bh; cy++)
                {
                    TryEnqueueAir(cx * stride_x + cy * bw + 0);
                    TryEnqueueAir(cx * stride_x + cy * bw + (bw - 1));
                }

                while (qHead < qTail)
                {
                    int idx = _islandQueue![qHead++];
                    int cx  = idx / stride_x;
                    int rem = idx % stride_x;
                    int cy  = rem / bw;
                    int cz  = rem % bw;
                    int ni;
                    if (cx > 0)    { ni = idx - stride_x; if (!_visitedBuf![ni] && buf[ni] >= 0f) { _visitedBuf[ni] = true; _islandQueue[qTail++] = ni; } }
                    if (cx < bw-1) { ni = idx + stride_x; if (!_visitedBuf![ni] && buf[ni] >= 0f) { _visitedBuf[ni] = true; _islandQueue[qTail++] = ni; } }
                    if (cy > 0)    { ni = idx - bw;        if (!_visitedBuf![ni] && buf[ni] >= 0f) { _visitedBuf[ni] = true; _islandQueue[qTail++] = ni; } }
                    if (cy < bh-1) { ni = idx + bw;        if (!_visitedBuf![ni] && buf[ni] >= 0f) { _visitedBuf[ni] = true; _islandQueue[qTail++] = ni; } }
                    if (cz > 0)    { ni = idx - 1;         if (!_visitedBuf![ni] && buf[ni] >= 0f) { _visitedBuf[ni] = true; _islandQueue[qTail++] = ni; } }
                    if (cz < bw-1) { ni = idx + 1;         if (!_visitedBuf![ni] && buf[ni] >= 0f) { _visitedBuf[ni] = true; _islandQueue[qTail++] = ni; } }
                }

                int filled = 0;
                int total  = bw * bh * bw;
                for (int i = 0; i < total; i++)
                    if (!_visitedBuf![i] && buf[i] >= 0f) { buf[i] = -s; filled++; }

                Log.LogInfo($"[VoxCaves] pocket-fill zone ({ox},{oz}): filled={filled}");
            }

            // ── Phase 3: Write SDF directly to VoxelField via the 'field' parameter ────
            // Writing to the same object VoxelizeColumns used (not _voxelZoneFieldField)
            // ensures RebuildBrick reads the correct SDF regardless of Voxheim's internal
            // field architecture.  FinishSkinNow is NOT called; bricks mesh async.
            int carved = 0;
            int maxYC  = yHi;
            int maxYAny = yHi; // highest Y touched by any write (solid or air); sizes hot-reload reset range
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
                    if (gy > maxYAny) maxYAny = gy;
                    if (newByte < 0 && gy > maxYC) maxYC = gy;
                    carved++;
                }
            }
            _sdfCache[zoneKey] = (extLo, bh, width, cacheData);

            // _zoneMaxY tracks highest solid voxel (for _yScanHi extension on mode C terrain)
            // _zoneExtHi tracks highest ANY write (solid or air) so hot-reload VoxelizeColumns
            // range covers mode B's air voxels written above yHi
            if (maxYC > yHi)
            {
                if (!_zoneMaxY.TryGetValue(zoneKey, out int prevMaxY) || maxYC > prevMaxY)
                    _zoneMaxY[zoneKey] = maxYC;
            }
            int anyHiMark = Math.Max(maxYC, maxYAny);
            if (anyHiMark > yHi)
            {
                int hiMark = anyHiMark + 8;
                if (!_zoneExtHi.TryGetValue(zoneKey, out int prevHiMark) || hiMark > prevHiMark)
                    _zoneExtHi[zoneKey] = hiMark;
            }

            if (_disableVegetation) _pendingVegCleanup = true;

            long _t3 = _sw.ElapsedMilliseconds;

            if (_dumpZonesRemaining > 0)
            {
                _dumpZonesRemaining--;
                DumpZoneData(ox, oz, overlap, s, wg, cw4Early, bw, bh, extLo, buf);
            }

            Log.LogInfo($"[VoxCaves] zone ({ox},{oz}) bakeC={bakeC} bw={bw} bh={bh} Ph0={_t0}ms Ph1={_t1-_t0}ms Ph1b={_t1b-_t1}ms Ph2={_t2-_t1b}ms Ph3={_t3-_t2}ms total={_t3}ms carved={carved}");
        }

        // ── Zone data dump (voxcaves_dump command) ───────────────────────────────
        static void DumpZoneData(int ox, int oz, int overlap, float s, WorldGenerator wg,
                                  int cw4, int bw, int bh, int extLo, float[] buf)
        {
            try
            {
                float coarseStep = 4f * s;
                int pad = (int)(_biomeBlendRadius / coarseStep) + 2;
                int pcw = cw4 + 2 * pad;

                var sb = new System.Text.StringBuilder();
                sb.AppendLine("{");
                sb.AppendLine($"  \"zone\": {{ \"ox\": {ox}, \"oz\": {oz}, \"overlap\": {overlap}, \"s\": {s}, \"bw\": {bw}, \"bh\": {bh}, \"extLo\": {extLo}, \"cw4\": {cw4}, \"blendRadius\": {_biomeBlendRadius} }},");

                // ── Biome grid (padded) — dominant biome per cell from _biomeFrac ──
                sb.Append("  \"biomeGrid\": { \"pcw\": "); sb.Append(pcw); sb.AppendLine(",");
                sb.Append("    \"data\": [");
                // _biomeFrac is [pgSize * MaxBiomeSlots]; we emit the dominant slot index (not biome enum)
                // as a rough visual aid — slot 0 = first biome seen, etc.
                if (_biomeFrac != null)
                {
                    for (int i = 0; i < pcw * pcw; i++)
                    {
                        if (i > 0) sb.Append(',');
                        int dom = 0; float domF = 0f;
                        for (int sl = 0; sl < MaxBiomeSlots; sl++)
                        {
                            float f = _biomeFrac[i * MaxBiomeSlots + sl];
                            if (f > domF) { domF = f; dom = sl; }
                        }
                        sb.Append(dom);
                    }
                }
                sb.AppendLine("] },");

                // ── Blended profiles per coarse cell ────────────────────────────
                sb.AppendLine("  \"blendedProfs\": [");
                for (int cx4 = 0; cx4 < cw4; cx4++)
                for (int cz4 = 0; cz4 < cw4; cz4++)
                {
                    int idx = cx4 * cw4 + cz4;
                    bool last = (cx4 == cw4 - 1 && cz4 == cw4 - 1);
                    float wx = (ox - overlap + cx4 * 4) * s, wz = (oz - overlap + cz4 * 4) * s;
                    int pIdx2 = (cx4 + pad) * pcw + (cz4 + pad);
                    int biomeRaw = -1;
                    if (_biomeFrac != null) { float domF2=0f; for(int sl=0;sl<MaxBiomeSlots;sl++){float f=_biomeFrac[pIdx2*MaxBiomeSlots+sl];if(f>domF2){domF2=f;biomeRaw=sl;}} }
                    float totW  = (_biomeTotW    != null && idx < _biomeTotW.Length)    ? _biomeTotW[idx]    : -1f;
                    float mcAmp = (_blendedProfs != null && idx < _blendedProfs.Length) ? _blendedProfs[idx].modeC_noiseAmp : -1f;
                    float mbR2  = (_blendedProfs != null && idx < _blendedProfs.Length) ? _blendedProfs[idx].modeB_radius   : -1f;
                    float blur  = (_blendedProfs != null && idx < _blendedProfs.Length) ? _blendedProfs[idx].blur_passes    : -1f;
                    float addH  = (_bk2dAddH    != null && idx < _bk2dAddH.Length)     ? _bk2dAddH[idx]    : -1f;
                    float bk2dS = (_bk2dMcS     != null && idx < _bk2dMcS.Length)      ? _bk2dMcS[idx]     : -1f;
                    sb.AppendLine($"    {{\"cx4\":{cx4},\"cz4\":{cz4},\"wx\":{wx:F1},\"wz\":{wz:F1},\"biome\":{biomeRaw},\"totW\":{totW:F3},\"mcAmp\":{mcAmp:F2},\"mbR\":{mbR2:F2},\"blur\":{blur:F2},\"addH\":{addH:F1},\"bk2dMcAmp\":{bk2dS:F3}}}{(last ? "" : ",")}");
                }
                sb.AppendLine("  ],");

                // ── SDF cross-section: fixed Z at zone centre, all X and Y ──────
                int midZ = bw / 2;
                sb.AppendLine($"  \"sdfSlice\": {{ \"fixedBufZ\": {midZ}, \"worldZ\": {(oz * s + (midZ - overlap) * s):F1},");
                sb.Append("    \"data\": [");  // rows = cx (X), cols = cy (Y)
                for (int cx = 0; cx < bw; cx++)
                {
                    if (cx > 0) sb.Append(',');
                    sb.Append('[');
                    for (int cy = 0; cy < bh; cy++)
                    {
                        if (cy > 0) sb.Append(',');
                        float v = buf[(cx * bh + cy) * bw + midZ];
                        sb.Append(v.ToString("F3"));
                    }
                    sb.Append(']');
                }
                sb.AppendLine("] }");
                sb.AppendLine("}");

                string outPath = Path.Combine(BepInEx.Paths.ConfigPath, $"voxcaves_dump_{ox}_{oz}.json");
                File.WriteAllText(outPath, sb.ToString());
                Log.LogInfo($"[VoxCaves] Dump written → {outPath}");
                Log.LogInfo($"[VoxCaves] Dump → {outPath}");
            }
            catch (Exception ex) { Log.LogError($"[VoxCaves] Dump failed: {ex.Message}"); }
        }

        // ── Hot-reload: re-run the full voxel pipeline on every loaded zone ───────
        internal static void ResetWorldState()
        {
            LoadBiomeConfig();
            _pendingVegCleanup = true;
            _zoneObjects.Clear();
            _zoneParams.Clear();
            _zoneMaxY.Clear();
            _zoneExtHi.Clear();
            _sdfCache.Clear();
            _profileCache.Clear();
            _heightCache.Clear();
            _zoneFaceCache.Clear();
            _locationPositions.Clear();
            _locationsCached = false;
            _caveNoise       = null;
            _caveNoise2      = null;
            _warpX           = null;
            _warpZ           = null;
            _warpX2          = null;
            _warpZ2          = null;
            _hmWarpX         = null;
            _hmWarpZ         = null;
            _hmAddWarpX      = null;
            _hmAddWarpZ      = null;
            _addNoise        = null;
            _addVaryNoise    = null;
            _widthNoise      = null;
            _twistVaryNoise  = null;
            _mbNoise         = null;
        }

        static void RecarveAll()
        {
            Log.LogInfo($"[VoxCaves] Hot-reload: re-voxelizing {_zoneParams.Count} zone(s).");
            // Clear all config-derived and geometry-derived caches so the re-voxelization
            // starts from a clean slate. _zoneParams and _zoneObjects are kept because
            // they are the iteration target — the zone objects and geometry metadata
            // (origin, width, yLo/yHi) are world-geometry facts, not profile-dependent.
            _sdfCache.Clear();
            _profileCache.Clear();
            _heightCache.Clear();
            _zoneMaxY.Clear();
            _zoneExtHi.Clear();
            _zoneFaceCache.Clear();
            if (_voxelizeColumnsMethod == null) return;
            bool canRemesh = _enqueueMeshMethod != null && _finishSkinNowMethod != null && _int3Ctor != null;
            if (!canRemesh)
                Log.LogWarning("[VoxCaves] EnqueueMesh/FinishSkinNow unavailable — mesh rebuild skipped.");

            var snapshot = new List<(string key, (object field, object grid, int ox, int oz, int w, int yLo, int yHi) zp)>(_zoneParams.Count);
            foreach (var kv in _zoneParams) snapshot.Add((kv.Key, kv.Value));

            // ── Pre-pass: reset _yScanHi to baseline so it accurately reflects the new config ──
            // We only ever raise _yScanHi, so stale high values from a previous profile
            // (e.g., mode B active → now off) would leave Voxheim scanning above the new
            // terrain ceiling, producing mesh artifacts. Reset to yHi now; the between-passes
            // step will re-extend it if mode C raises terrain above that.
            if (_yScanHiField != null)
            {
                foreach (var (key, zp) in snapshot)
                {
                    if (!_zoneObjects.TryGetValue(key, out var zo)) continue;
                    if (zo is UnityEngine.Object u && !u) continue;
                    _yScanHiField.SetValue(zo, zp.yHi);
                }
            }

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
                        var cpRc   = PointBlendProfile(cxRc, czRc, wgRc, sRc);
                        if (cpRc.modeC_noiseAmp > 0f)
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
            var prof = wg != null ? PointBlendProfile(worldX, worldZ, wg, s) : CaveProfile.Default;
            float y = originalY;
            for (int i = 0; i < 256; i++, y -= s)
                if (TunnelDensity(worldX, y, worldZ, floorY, prof) <= 0f) return y;
            return y;
        }

        // ── BakeSdfSetup: Phase 0 noise bake, extracted for reuse in precompute pass ─
        // Caller must have already run BakeBlendedProfiles so _blendedProfs is current.
        // Populates all _bk2d*, _noiseBuf, _tunBuf*, _tunWBuf, _bkWarpedH statics.
        static void BakeSdfSetup(
            int ox, int oz, int overlap, float s, int extLo, int bh, int bw, int cw4,
            WorldGenerator? wg, List<Vector2> nearbyLocs,
            out bool bakeC, out bool bakeA, out bool anyMbActive)
        {
            bakeC = false; bakeA = false; anyMbActive = false;
            int nb4Size = cw4 * cw4 * bh;
            int nb2Size = cw4 * cw4;
            if (_noiseBuf  == null || _noiseBuf.Length  < nb4Size) _noiseBuf  = new float[nb4Size];
            if (_tunBuf    == null || _tunBuf.Length    < nb4Size) _tunBuf    = new float[nb4Size];
            if (_tunBuf2   == null || _tunBuf2.Length   < nb4Size) _tunBuf2   = new float[nb4Size];
            if (_tunWBuf   == null || _tunWBuf.Length   < nb4Size) _tunWBuf   = new float[nb4Size];
            if (_bk2dAddH  == null || _bk2dAddH.Length  < nb2Size) _bk2dAddH  = new float[nb2Size];
            if (_bk2dLAmp  == null || _bk2dLAmp.Length  < nb2Size) _bk2dLAmp  = new float[nb2Size];
            if (_bk2dMcS   == null || _bk2dMcS.Length   < nb2Size) _bk2dMcS   = new float[nb2Size];
            if (_bk2dMcT   == null || _bk2dMcT.Length   < nb2Size) _bk2dMcT   = new float[nb2Size];
            if (_bk2dFlD   == null || _bk2dFlD.Length   < nb2Size) _bk2dFlD   = new float[nb2Size];
            if (_bk2dMbwf  == null || _bk2dMbwf.Length  < nb2Size) _bk2dMbwf  = new float[nb2Size];
            if (_bk2dMbwa  == null || _bk2dMbwa.Length  < nb2Size) _bk2dMbwa  = new float[nb2Size];
            if (_bk2dMbr   == null || _bk2dMbr.Length   < nb2Size) _bk2dMbr   = new float[nb2Size];
            if (_bk2dMbvs  == null || _bk2dMbvs.Length  < nb2Size) _bk2dMbvs  = new float[nb2Size];
            if (_bk2dMbna  == null || _bk2dMbna.Length  < nb2Size) _bk2dMbna  = new float[nb2Size];
            if (_bk2dMbyo    == null || _bk2dMbyo.Length    < nb2Size) _bk2dMbyo    = new float[nb2Size];
            if (_bk2dMbtwist == null || _bk2dMbtwist.Length < nb2Size) _bk2dMbtwist = new float[nb2Size];
            if (_bk2dMawf  == null || _bk2dMawf.Length  < nb2Size) _bk2dMawf  = new float[nb2Size];
            if (_bk2dMawa  == null || _bk2dMawa.Length  < nb2Size) _bk2dMawa  = new float[nb2Size];
            if (_bk2dMaf   == null || _bk2dMaf.Length   < nb2Size) _bk2dMaf   = new float[nb2Size];
            if (_bk2dMavs  == null || _bk2dMavs.Length  < nb2Size) _bk2dMavs  = new float[nb2Size];
            if (_bk2dMaw   == null || _bk2dMaw.Length   < nb2Size) _bk2dMaw   = new float[nb2Size];
            if (_bk2dMawv  == null || _bk2dMawv.Length  < nb2Size) _bk2dMawv  = new float[nb2Size];
            if (_bk2dMawvf == null || _bk2dMawvf.Length < nb2Size) _bk2dMawvf = new float[nb2Size];
            if (_bk2dMayo  == null || _bk2dMayo.Length  < nb2Size) _bk2dMayo  = new float[nb2Size];

            if (_blendedProfs != null)
            {
                for (int i = 0; i < nb2Size && i < _blendedProfs.Length; i++)
                {
                    if (_blendedProfs[i].modeC_noiseAmp > 0f) bakeC = true;
                    if (_blendedProfs[i].modeA_width    > 0f) bakeA = true;
                    if (_blendedProfs[i].modeB_radius   > 0f) anyMbActive = true;
                    if (bakeC && bakeA && anyMbActive) break;
                }
            }

            for (int cx4 = 0; cx4 < cw4; cx4++)
            for (int cz4 = 0; cz4 < cw4; cz4++)
            {
                int   cx4w  = Math.Min(cx4 * 4, bw - 1);
                int   cz4w  = Math.Min(cz4 * 4, bw - 1);
                int   gx4   = ox - overlap + cx4w, gz4 = oz - overlap + cz4w;
                float wx4   = gx4 * s, wz4 = gz4 * s;
                int   idx2  = cx4 * cw4 + cz4;
                var   p4    = (wg != null && _blendedProfs != null && idx2 < _blendedProfs.Length)
                              ? _blendedProfs[idx2] : CaveProfile.Default;

                _bk2dMcS! [idx2] = p4.modeC_noiseAmp;
                _bk2dMcT! [idx2] = p4.modeC_threshold;
                _bk2dFlD! [idx2] = p4.floorDepth;
                _bk2dMbwf![idx2] = p4.modeB_warpFrequency;
                _bk2dMbwa![idx2] = p4.modeB_warpAmp;
                _bk2dMbr! [idx2] = p4.modeB_radius;
                _bk2dMbvs![idx2] = p4.modeB_vScale;
                _bk2dMbna![idx2] = p4.modeB_noiseAmp;
                _bk2dMbyo![idx2]    = p4.modeB_yOffset;
                _bk2dMbtwist![idx2] = p4.modeB_twist;
                _bk2dMawf![idx2] = p4.modeA_warpFrequency;
                _bk2dMawa![idx2] = p4.modeA_warpAmp;
                _bk2dMaf! [idx2] = p4.modeA_frequency;
                _bk2dMavs![idx2] = p4.modeA_vScale;
                _bk2dMaw! [idx2] = p4.modeA_width;
                _bk2dMawv![idx2] = p4.modeA_widthVary;
                _bk2dMawvf![idx2]= p4.modeA_widthFrequency;
                _bk2dMayo! [idx2]= p4.modeA_yOffset;

                if (bakeC && p4.modeC_noiseAmp > 0f)
                {
                    float awf4  = p4.modeC_warpFrequency;
                    float awx4  = _hmAddWarpX!.GetNoise(wx4 * awf4, wz4 * awf4) * p4.modeC_warpAmp;
                    float awz4  = _hmAddWarpZ!.GetNoise(wx4 * awf4, wz4 * awf4) * p4.modeC_warpAmp;
                    float addH4    = wg!.GetHeight(wx4 + awx4, wz4 + awz4) - p4.modeC_yOffset;
                    float avf4  = p4.modeC_ampFrequency;
                    _bk2dAddH![idx2] = addH4;
                    _bk2dLAmp![idx2] = Mathf.Max(p4.modeC_noiseAmp * (1f + p4.modeC_ampVary
                                           * _addVaryNoise!.GetNoise(wx4 * avf4, wz4 * avf4)), 0f);
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
                    _bk2dAddH![idx2] = wg!.GetHeight(wx4, wz4);
                    _bk2dLAmp![idx2] = 0f;
                    int baseIdx = cx4 * cw4 * bh + cz4 * bh;
                    for (int cy = 0; cy < bh; cy++) _noiseBuf[baseIdx + cy] = 0f;
                }

                if (bakeA && p4.modeA_width > 0f)
                {
                    float mawf4  = _bk2dMawf! [idx2];
                    float mawa4  = _bk2dMawa! [idx2];
                    float maf4   = _bk2dMaf!  [idx2];
                    float mavs4  = _bk2dMavs! [idx2];
                    float mawvf4 = _bk2dMawvf![idx2];
                    float mayo4  = p4.modeA_yOffset;
                    float addHA4 = mayo4 > 0f ? wg!.GetHeight(wx4, wz4) : 0f;
                    int baseIdxA = cx4 * cw4 * bh + cz4 * bh;
                    for (int cy = 0; cy < bh; cy++)
                    {
                        float sy4   = ((extLo + cy) * s - addHA4 + mayo4) * mavs4;
                        float awx4  = _warpX! .GetNoise(wx4 * mawf4, sy4 * mawf4, wz4 * mawf4) * mawa4;
                        float awz4  = _warpZ! .GetNoise(wx4 * mawf4, sy4 * mawf4, wz4 * mawf4) * mawa4;
                        float awx4b = _warpX2!.GetNoise(wx4 * mawf4, sy4 * mawf4, wz4 * mawf4) * mawa4;
                        float awz4b = _warpZ2!.GetNoise(wx4 * mawf4, sy4 * mawf4, wz4 * mawf4) * mawa4;
                        _tunBuf! [baseIdxA + cy] = _caveNoise! .GetNoise((wx4 + awx4)  * maf4, sy4 * maf4, (wz4 + awz4)  * maf4);
                        _tunBuf2![baseIdxA + cy] = _caveNoise2!.GetNoise((wx4 + awx4b) * maf4, sy4 * maf4, (wz4 + awz4b) * maf4);
                        _tunWBuf![baseIdxA + cy] = _widthNoise!.GetNoise(wx4 * mawvf4, sy4 * mawvf4, wz4 * mawvf4);
                    }
                }
                else if (bakeA)
                {
                    int baseIdxA = cx4 * cw4 * bh + cz4 * bh;
                    for (int cy = 0; cy < bh; cy++) { _tunBuf![baseIdxA + cy] = 0f; _tunBuf2![baseIdxA + cy] = 0f; _tunWBuf![baseIdxA + cy] = 0f; }
                }
            }

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
                    float yo    = BL2(_bk2dMbyo!, b00, b10, b01, b11, bfx2, bfz2);
                    float locF2 = LocationFactor(wx, wz, nearbyLocs);
                    float dx    = _hmWarpX!.GetNoise(wx * wf, wz * wf) * (wa * locF2);
                    float dz2   = _hmWarpZ!.GetNoise(wx * wf, wz * wf) * (wa * locF2);
                    _bkWarpedH![cx * bw + cz] = wg.GetHeight(wx + dx, wz + dz2) - yo;
                }
            }
        }

        // ── Cross-zone face cache helpers ─────────────────────────────────────────

        static int CountGroundedFaceVoxels(ZoneFaceCache c)
        {
            int n = 0;
            if (c.negX != null) foreach (var b in c.negX) if (b) n++;
            if (c.posX != null) foreach (var b in c.posX) if (b) n++;
            if (c.negZ != null) foreach (var b in c.negZ) if (b) n++;
            if (c.posZ != null) foreach (var b in c.posZ) if (b) n++;
            return n;
        }

        static (int yLo, int yHi) ComputeYBounds(int ox, int oz, int w, float s, WorldGenerator wg)
        {
            float minH = float.MaxValue, maxH = float.MinValue;
            for (int gx = 0; gx <= w; gx++)
            for (int gz = 0; gz <= w; gz++)
            {
                float h = wg.GetHeight((ox + gx) * s, (oz + gz) * s) / s;
                if (h < minH) minH = h;
                if (h > maxH) maxH = h;
            }
            return ((int)Math.Floor(minH) - 2, (int)Math.Ceiling(maxH) + 2);
        }

        // On-demand fixpoint: seeds with the faces that the pre-pass BFS found to have
        // ungrounded solid (ungNX/PX/NZ/PZ).  Expands outward via chain hops until every
        // zone in the worklist is terminal (no ungrounded solid touching an uncached face).
        // Re-queues zones whose caches improved so multi-hop chains resolve correctly.
        const int FixpointMaxZones = 64; // safety cap; logged if hit

        static void EnsureFixpoint(int ox, int oz, int w,
                                   bool ungNX, bool ungPX, bool ungNZ, bool ungPZ)
        {
            float s  = _scale ?? 1f;
            var   wg = WorldGenerator.instance;
            if (wg == null) return;

            var worklist = new Queue<(int ox, int oz)>();
            var seen     = new HashSet<(int, int)> { (ox, oz) };
            int computed = 0;

            void MaybeEnqueue(int nox, int noz, bool faceUngrounded)
            {
                if (!faceUngrounded) return;
                if (_zoneFaceCache.ContainsKey((nox, noz))) return;
                if (!seen.Add((nox, noz))) return;
                worklist.Enqueue((nox, noz));
            }

            MaybeEnqueue(ox - w, oz, ungNX);
            MaybeEnqueue(ox + w, oz, ungPX);
            MaybeEnqueue(ox, oz - w, ungNZ);
            MaybeEnqueue(ox, oz + w, ungPZ);

            while (worklist.Count > 0)
            {
                if (computed >= FixpointMaxZones)
                {
                    Log.LogWarning($"[VoxCaves] EnsureFixpoint ({ox},{oz}): hit cap ({FixpointMaxZones}), {worklist.Count} zones skipped");
                    break;
                }

                var (zox, zoz) = worklist.Dequeue();

                int beforeCount = _zoneFaceCache.TryGetValue((zox, zoz), out var oldC)
                    ? CountGroundedFaceVoxels(oldC) : -1;

                var (zyLo, zyHi) = ComputeYBounds(zox, zoz, w, s, wg);
                ComputeZoneFaceCache(zox, zoz, w, zyLo, zyHi);
                computed++;

                if (!_zoneFaceCache.TryGetValue((zox, zoz), out var nc)) continue;
                int afterCount = CountGroundedFaceVoxels(nc);
                bool improved  = afterCount > beforeCount;

                void HopIfNeeded(bool needsFlag, int nnox, int nnoz)
                {
                    if (!needsFlag) return;
                    if (_zoneFaceCache.ContainsKey((nnox, nnoz))) return;
                    if (!seen.Add((nnox, nnoz))) return;
                    worklist.Enqueue((nnox, nnoz));
                }
                HopIfNeeded(nc.needsNegX, zox - w, zoz);
                HopIfNeeded(nc.needsPosX, zox + w, zoz);
                HopIfNeeded(nc.needsNegZ, zox, zoz - w);
                HopIfNeeded(nc.needsPosZ, zox, zoz + w);

                if (improved)
                {
                    void RequeueIfCached(int nnox, int nnoz)
                    {
                        if ((nnox, nnoz) == (ox, oz)) return;
                        if (!_zoneFaceCache.ContainsKey((nnox, nnoz))) return;
                        if (!seen.Add((nnox, nnoz))) return;
                        worklist.Enqueue((nnox, nnoz));
                    }
                    RequeueIfCached(zox - w, zoz);
                    RequeueIfCached(zox + w, zoz);
                    RequeueIfCached(zox, zoz - w);
                    RequeueIfCached(zox, zoz + w);
                }
            }

            if (computed > 0)
                Log.LogInfo($"[VoxCaves] EnsureFixpoint ({ox},{oz}): {computed} neighbor zone(s) computed");
        }

        // ── Cross-zone face cache precompute ──────────────────────────────────────
        // Runs SDF+BFS for one zone, using any already-available neighbor face caches
        // as extra grounding seeds.  Populates _zoneFaceCache[(ox,oz)].
        static void ComputeZoneFaceCache(int ox, int oz, int w, int yLo, int yHi)
        {
            const int overlap = 4;
            float s  = _scale ?? 1f;
            var   wg = WorldGenerator.instance;

            int bwZ = w + 2 * overlap;
            int cw4 = (bwZ + 3) / 4;

            if (wg != null) BakeBlendedProfiles(cw4, ox, oz, overlap, s, wg);

            // Compute hiMargin from blended corner profiles (mirrors Carve logic)
            int hiMargin = 24;
            if (_blendedProfs != null)
            {
                float maxExtM = 0f;
                int   lastC   = cw4 - 1;
                void CheckCP(int ci, int cj) {
                    int idx = ci * cw4 + cj;
                    if (idx >= _blendedProfs.Length) return;
                    var cp = _blendedProfs[idx];
                    if (cp.modeC_noiseAmp <= 0f) return;
                    float a = cp.modeC_noiseAmp * Mathf.Max(1f, 1f + cp.modeC_ampVary);
                    float e = Mathf.Max(0f, 1f - cp.modeC_threshold) * a + cp.modeC_warpAmp;
                    if (e > maxExtM) maxExtM = e;
                }
                CheckCP(cw4/2, cw4/2);
                CheckCP(0, 0); CheckCP(lastC, 0); CheckCP(0, lastC); CheckCP(lastC, lastC);
                if (maxExtM > 0f) hiMargin = Mathf.Max(24, (int)Mathf.Ceil(maxExtM / s) + 8);
            }

            int extLo  = yLo - 24;
            int extHi  = yHi + hiMargin;
            int bhZ    = extHi - extLo + 1;
            int bufSz  = bwZ * bhZ * bwZ;

            if (_precomputeBuf == null || _precomputeBuf.Length < bufSz) _precomputeBuf = new float[bufSz];
            float[] buf = _precomputeBuf;

            InitNoise();

            // Build nearby-location list (same as Carve)
            float zoneX   = (ox + w * 0.5f) * s;
            float zoneZ   = (oz + w * 0.5f) * s;
            float searchR = w * s * 0.5f + LocProtectR + LocFadeR;
            var   nearby  = new List<Vector2>();
            foreach (var loc in _locationPositions)
            {
                float dx = loc.x - zoneX, dz = loc.y - zoneZ;
                if (dx * dx + dz * dz < searchR * searchR) nearby.Add(loc);
            }

            BakeSdfSetup(ox, oz, overlap, s, extLo, bhZ, bwZ, cw4, wg, nearby,
                out bool bakeC, out bool bakeA, out bool anyMbActive);
            FillPhase1(buf, bwZ, bhZ, extLo, s, ox, oz, overlap, bakeC, bakeA, anyMbActive, wg, cw4, nearby);

            // BFS: seed from cy=0 (bedrock) + neighbor-grounded face voxels
            int stX   = bhZ * bwZ;    // stride in X
            int bufSz2 = bwZ * bhZ * bwZ;
            if (_visitedBuf  == null || _visitedBuf.Length  < bufSz2) _visitedBuf  = new bool[bufSz2];
            if (_islandQueue == null || _islandQueue.Length < bufSz2) _islandQueue = new int[bufSz2];
            Array.Clear(_visitedBuf, 0, bufSz2);
            int qHead = 0, qTail = 0;

            // Seed 1: cy=0 across full buffer
            for (int cx = 0; cx < bwZ; cx++)
            for (int cz = 0; cz < bwZ; cz++)
            {
                int idx = cx * stX + cz; // cy=0
                if (buf[idx] < 0f && !_visitedBuf[idx])
                { _visitedBuf[idx] = true; _islandQueue[qTail++] = idx; }
            }

            // Seed 2: voxels adjacent to a neighbor's grounded face
            // Face alignment: both zones share the same bw, same cz/cy coordinate space
            // (territories tile without gap; cy matched via world-Y = extLo + cy).
            void SeedFromNeighborFace(bool[]? nFace, int nExtLo, int nBh, int nBw,
                                      int ourCx, bool iterateCz)
            {
                if (nFace == null) return;
                int maxCperp = iterateCz ? bwZ : bwZ; // both axes use bwZ
                for (int cperp = 0; cperp < maxCperp; cperp++)
                for (int cy = 0; cy < bhZ; cy++)
                {
                    int worldY = extLo + cy;
                    int ncy    = worldY - nExtLo;
                    if (ncy < 0 || ncy >= nBh) continue;
                    int nIdx = ncy * nBw + cperp;
                    if (nIdx >= nFace.Length || !nFace[nIdx]) continue;
                    int bufIdx = iterateCz
                        ? ourCx * stX + cy * bwZ + cperp   // X face: (ourCx, cy, cperp=cz)
                        : cperp * stX + cy * bwZ + ourCx;  // Z face: (cperp=cx, cy, ourCz)
                    if (buf[bufIdx] < 0f && !_visitedBuf[bufIdx])
                    { _visitedBuf[bufIdx] = true; _islandQueue[qTail++] = bufIdx; }
                }
            }

            int terNegX = overlap;
            int terPosX = overlap + w - 1;
            int terNegZ = overlap;
            int terPosZ = overlap + w - 1;

            if (_zoneFaceCache.TryGetValue((ox - w, oz), out var negXN))
                SeedFromNeighborFace(negXN.posX, negXN.extLo, negXN.bh, negXN.bw, terNegX, true);
            if (_zoneFaceCache.TryGetValue((ox + w, oz), out var posXN))
                SeedFromNeighborFace(posXN.negX, posXN.extLo, posXN.bh, posXN.bw, terPosX, true);
            if (_zoneFaceCache.TryGetValue((ox, oz - w), out var negZN))
                SeedFromNeighborFace(negZN.posZ, negZN.extLo, negZN.bh, negZN.bw, terNegZ, false);
            if (_zoneFaceCache.TryGetValue((ox, oz + w), out var posZN))
                SeedFromNeighborFace(posZN.negZ, posZN.extLo, posZN.bh, posZN.bw, terPosZ, false);

            // BFS flood
            while (qHead < qTail)
            {
                int idx = _islandQueue[qHead++];
                int cx  = idx / stX;
                int rem = idx % stX;
                int cy  = rem / bwZ;
                int cz  = rem % bwZ;
                int ni;
                if (cx > 0)     { ni = idx - stX;  if (!_visitedBuf[ni] && buf[ni] < 0f) { _visitedBuf[ni] = true; _islandQueue[qTail++] = ni; } }
                if (cx < bwZ-1) { ni = idx + stX;  if (!_visitedBuf[ni] && buf[ni] < 0f) { _visitedBuf[ni] = true; _islandQueue[qTail++] = ni; } }
                if (cy > 0)     { ni = idx - bwZ;  if (!_visitedBuf[ni] && buf[ni] < 0f) { _visitedBuf[ni] = true; _islandQueue[qTail++] = ni; } }
                if (cy < bhZ-1) { ni = idx + bwZ;  if (!_visitedBuf[ni] && buf[ni] < 0f) { _visitedBuf[ni] = true; _islandQueue[qTail++] = ni; } }
                if (cz > 0)     { ni = idx - 1;    if (!_visitedBuf[ni] && buf[ni] < 0f) { _visitedBuf[ni] = true; _islandQueue[qTail++] = ni; } }
                if (cz < bwZ-1) { ni = idx + 1;    if (!_visitedBuf[ni] && buf[ni] < 0f) { _visitedBuf[ni] = true; _islandQueue[qTail++] = ni; } }
            }

            // Build solid face cache from BFS result
            int faceSz = bhZ * bwZ;
            var cache  = new ZoneFaceCache {
                extLo = extLo, bh = bhZ, bw = bwZ,
                negX    = new bool[faceSz], posX    = new bool[faceSz],
                negZ    = new bool[faceSz], posZ    = new bool[faceSz],
                negXAir = new bool[faceSz], posXAir = new bool[faceSz],
                negZAir = new bool[faceSz], posZAir = new bool[faceSz],
            };
            for (int cy = 0; cy < bhZ; cy++)
            for (int cz = 0; cz < bwZ; cz++)
            {
                int fIdx = cy * bwZ + cz;
                cache.negX[fIdx] = _visitedBuf[terNegX * stX + cy * bwZ + cz];
                cache.posX[fIdx] = _visitedBuf[terPosX * stX + cy * bwZ + cz];
            }
            for (int cy = 0; cy < bhZ; cy++)
            for (int cx = 0; cx < bwZ; cx++)
            {
                int fIdx = cy * bwZ + cx;
                cache.negZ[fIdx] = _visitedBuf[cx * stX + cy * bwZ + terNegZ];
                cache.posZ[fIdx] = _visitedBuf[cx * stX + cy * bwZ + terPosZ];
            }

            // Air-connectivity BFS: seed from buffer boundary air, flood through air.
            // Records which territory face voxels connect to the open world —
            // used to prevent pocket removal from filling caves that exit through a neighbor.
            Array.Clear(_visitedBuf, 0, bufSz2);
            qHead = 0; qTail = 0;

            void TryEnqueueAirSeed(int idx) {
                if (!_visitedBuf[idx] && buf[idx] >= 0f)
                { _visitedBuf[idx] = true; _islandQueue![qTail++] = idx; }
            }

            // Top face (cy = bhZ-1)
            for (int cx = 0; cx < bwZ; cx++)
            for (int cz = 0; cz < bwZ; cz++)
                TryEnqueueAirSeed(cx * stX + (bhZ - 1) * bwZ + cz);
            // Buffer side faces (cx=0, cx=bwZ-1, cz=0, cz=bwZ-1), all cy
            for (int cy = 0; cy < bhZ; cy++)
            for (int cz = 0; cz < bwZ; cz++)
            {
                TryEnqueueAirSeed(0          * stX + cy * bwZ + cz);
                TryEnqueueAirSeed((bwZ - 1)  * stX + cy * bwZ + cz);
            }
            for (int cx = 0; cx < bwZ; cx++)
            for (int cy = 0; cy < bhZ; cy++)
            {
                TryEnqueueAirSeed(cx * stX + cy * bwZ + 0);
                TryEnqueueAirSeed(cx * stX + cy * bwZ + (bwZ - 1));
            }

            while (qHead < qTail)
            {
                int idx = _islandQueue[qHead++];
                int cx  = idx / stX;
                int rem = idx % stX;
                int cy  = rem / bwZ;
                int cz  = rem % bwZ;
                int ni;
                if (cx > 0)     { ni = idx - stX;  if (!_visitedBuf[ni] && buf[ni] >= 0f) { _visitedBuf[ni] = true; _islandQueue[qTail++] = ni; } }
                if (cx < bwZ-1) { ni = idx + stX;  if (!_visitedBuf[ni] && buf[ni] >= 0f) { _visitedBuf[ni] = true; _islandQueue[qTail++] = ni; } }
                if (cy > 0)     { ni = idx - bwZ;  if (!_visitedBuf[ni] && buf[ni] >= 0f) { _visitedBuf[ni] = true; _islandQueue[qTail++] = ni; } }
                if (cy < bhZ-1) { ni = idx + bwZ;  if (!_visitedBuf[ni] && buf[ni] >= 0f) { _visitedBuf[ni] = true; _islandQueue[qTail++] = ni; } }
                if (cz > 0)     { ni = idx - 1;    if (!_visitedBuf[ni] && buf[ni] >= 0f) { _visitedBuf[ni] = true; _islandQueue[qTail++] = ni; } }
                if (cz < bwZ-1) { ni = idx + 1;    if (!_visitedBuf[ni] && buf[ni] >= 0f) { _visitedBuf[ni] = true; _islandQueue[qTail++] = ni; } }
            }

            for (int cy = 0; cy < bhZ; cy++)
            for (int cz = 0; cz < bwZ; cz++)
            {
                int fIdx = cy * bwZ + cz;
                cache.negXAir[fIdx] = _visitedBuf[terNegX * stX + cy * bwZ + cz];
                cache.posXAir[fIdx] = _visitedBuf[terPosX * stX + cy * bwZ + cz];
            }
            for (int cy = 0; cy < bhZ; cy++)
            for (int cx = 0; cx < bwZ; cx++)
            {
                int fIdx = cy * bwZ + cx;
                cache.negZAir[fIdx] = _visitedBuf[cx * stX + cy * bwZ + terNegZ];
                cache.posZAir[fIdx] = _visitedBuf[cx * stX + cy * bwZ + terPosZ];
            }

            // Set needs* flags: face has ungrounded solid AND the corresponding neighbor is uncached.
            // EnsureFixpoint uses these to expand the worklist toward zones that could resolve the blob.
            bool anyNegXUng = false, anyPosXUng = false, anyNegZUng = false, anyPosZUng = false;
            for (int cy = 0; cy < bhZ; cy++)
            for (int cz = 0; cz < bwZ; cz++)
            {
                int iNX = terNegX * stX + cy * bwZ + cz;
                int iPX = terPosX * stX + cy * bwZ + cz;
                if (!anyNegXUng && buf[iNX] < 0f && !_visitedBuf[iNX]) anyNegXUng = true;
                if (!anyPosXUng && buf[iPX] < 0f && !_visitedBuf[iPX]) anyPosXUng = true;
            }
            for (int cy = 0; cy < bhZ; cy++)
            for (int cx = 0; cx < bwZ; cx++)
            {
                int iNZ = cx * stX + cy * bwZ + terNegZ;
                int iPZ = cx * stX + cy * bwZ + terPosZ;
                if (!anyNegZUng && buf[iNZ] < 0f && !_visitedBuf[iNZ]) anyNegZUng = true;
                if (!anyPosZUng && buf[iPZ] < 0f && !_visitedBuf[iPZ]) anyPosZUng = true;
            }
            cache.needsNegX = anyNegXUng && !_zoneFaceCache.ContainsKey((ox - w, oz));
            cache.needsPosX = anyPosXUng && !_zoneFaceCache.ContainsKey((ox + w, oz));
            cache.needsNegZ = anyNegZUng && !_zoneFaceCache.ContainsKey((ox, oz - w));
            cache.needsPosZ = anyPosZUng && !_zoneFaceCache.ContainsKey((ox, oz + w));

            _zoneFaceCache[(ox, oz)] = cache;
        }

        // Worklist fixpoint: runs ComputeZoneFaceCache for all known zones in FIFO order.
        // Each zone is re-queued until fully resolved (all 4 neighbors cached).
        // Terminates when a full pass produces no new resolutions.
        static void PrecomputeFaceCaches()
        {
            if (_zoneParams.Count == 0) return;
            _zoneFaceCache.Clear();

            var queue = new Queue<(int ox, int oz, int w, int yLo, int yHi)>();
            foreach (var kv in _zoneParams)
                queue.Enqueue((kv.Value.ox, kv.Value.oz, kv.Value.w, kv.Value.yLo, kv.Value.yHi));

            int total     = queue.Count;
            int totalDone = 0;
            var sw        = System.Diagnostics.Stopwatch.StartNew();
            Log.LogInfo($"[VoxCaves] PrecomputeFaceCaches: starting {total} zones");

            while (queue.Count > 0)
            {
                int passSize   = queue.Count;
                int passResolved = 0;

                for (int i = 0; i < passSize; i++)
                {
                    var (ox, oz, w, yLo, yHi) = queue.Dequeue();
                    int beforeCount = _zoneFaceCache.TryGetValue((ox, oz), out var oldCachePC)
                        ? CountGroundedFaceVoxels(oldCachePC) : -1;
                    ComputeZoneFaceCache(ox, oz, w, yLo, yHi);
                    int afterCount = _zoneFaceCache.TryGetValue((ox, oz), out var newCachePC)
                        ? CountGroundedFaceVoxels(newCachePC) : -1;
                    bool resolved = !(newCachePC.needsNegX || newCachePC.needsPosX ||
                                      newCachePC.needsNegZ || newCachePC.needsPosZ);
                    bool improved = afterCount > beforeCount;
                    if (resolved || !improved)
                    {
                        totalDone++;
                        passResolved++;
                    }
                    else
                    {
                        queue.Enqueue((ox, oz, w, yLo, yHi));
                    }
                }

                Log.LogInfo($"[VoxCaves] PrecomputeFaceCaches: pass complete — resolved {passResolved}/{passSize}, total {totalDone}/{total}, {sw.ElapsedMilliseconds}ms elapsed");

                if (passResolved == 0)
                {
                    // Fixpoint: remaining zones have mutually unresolvable ambiguity (circular
                    // floating-island chains with no bedrock anywhere in the loop).
                    // Mark them done with their current (partial) face cache — Carve() will
                    // cull the genuinely floating blobs during its own island removal pass.
                    Log.LogInfo($"[VoxCaves] PrecomputeFaceCaches: fixpoint — {queue.Count} zones remain ambiguous, treating as floating");
                    break;
                }
            }

            Log.LogInfo($"[VoxCaves] PrecomputeFaceCaches: done in {sw.ElapsedMilliseconds}ms, {totalDone}/{total} fully resolved");
        }

        static void FillYColumn(float[] buf, int bw, int bh, int extLo, float s, int cx, int cz,
            float h, float addH, float profMcT, float localAmp, bool mcActive,
            float locFac, float floorY,
            float mbWarpedH, float mbR, float mbNA, float mbVS, float mbWF, float mbTwist,
            int bb00, int bb10, int bb01, int bb11, float bfx, float bfz,
            float mAwf, float mAwa, float mAf, float mAvs, float mAw, float mAwv, float mAwvf,
            float worldX, float worldZ, bool bakeC, bool bakeA)
        {
            // Mode B bandSDF is positive only inside the band [mbWarpedH-mbR, mbWarpedH+mbR] and above floorY.
            // Expand the range by mbNA to account for the 3D noise perturbation.
            int cyMbLo = bh, cyMbHi = -1;
            if (mbR > 0f)
            {
                float yLo = mbWarpedH - mbR - mbNA;
                if (yLo < floorY) yLo = floorY;
                float yHi = mbWarpedH + mbR + mbNA;
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
                    float sdf = relY - (n - profMcT) * localAmp;
                    combined = combined < sdf ? combined : sdf;
                }

                if (mAw > 0f)
                {
                    float na, nb, wn;
                    if (bakeA)
                    {
                        float t0 = _tunBuf![bb00+cy]  + (_tunBuf[bb10+cy]  - _tunBuf[bb00+cy])  * bfx;
                        float t1 = _tunBuf![bb01+cy]  + (_tunBuf[bb11+cy]  - _tunBuf[bb01+cy])  * bfx;
                        na = t0 + (t1 - t0) * bfz;
                        float t0b = _tunBuf2![bb00+cy] + (_tunBuf2[bb10+cy] - _tunBuf2[bb00+cy]) * bfx;
                        float t1b = _tunBuf2![bb01+cy] + (_tunBuf2[bb11+cy] - _tunBuf2[bb01+cy]) * bfx;
                        nb = t0b + (t1b - t0b) * bfz;
                        float w0 = _tunWBuf![bb00+cy] + (_tunWBuf[bb10+cy] - _tunWBuf[bb00+cy]) * bfx;
                        float w1 = _tunWBuf![bb01+cy] + (_tunWBuf[bb11+cy] - _tunWBuf[bb01+cy]) * bfx;
                        wn = w0 + (w1 - w0) * bfz;
                    }
                    else
                    {
                        float sy  = worldY * mAvs;
                        float wx  = _warpX! .GetNoise(worldX * mAwf, sy * mAwf, worldZ * mAwf) * mAwa;
                        float wz  = _warpZ! .GetNoise(worldX * mAwf, sy * mAwf, worldZ * mAwf) * mAwa;
                        float wx2 = _warpX2!.GetNoise(worldX * mAwf, sy * mAwf, worldZ * mAwf) * mAwa;
                        float wz2 = _warpZ2!.GetNoise(worldX * mAwf, sy * mAwf, worldZ * mAwf) * mAwa;
                        na = _caveNoise! .GetNoise((worldX + wx)  * mAf, sy * mAf, (worldZ + wz)  * mAf);
                        nb = _caveNoise2!.GetNoise((worldX + wx2) * mAf, sy * mAf, (worldZ + wz2) * mAf);
                        wn = _widthNoise!.GetNoise(worldX * mAwvf, (worldY * mAvs) * mAwvf, worldZ * mAwvf);
                    }
                    float localWidth = Mathf.Max(mAw * (1f + mAwv * wn), 0.001f);
                    // L2 distance of two independent noise fields: intersection of two zero-crossing
                    // sheets = cylindrical tube network. Normalized by 1/sqrt(2) to keep dist in ~[0,1].
                    float dist      = Mathf.Sqrt(na * na + nb * nb) * 0.7071f;
                    float tunnelSDF = (localWidth - dist) / localWidth * 4f * s;
                    float sdf = Mathf.Min(tunnelSDF, worldY - floorY);
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

                if (mcActive)
                {
                    float relY = worldY - addH;
                    float n;
                    {
                        float n0 = _noiseBuf![bb00+cy] + (_noiseBuf[bb10+cy] - _noiseBuf![bb00+cy]) * bfx;
                        float n1 = _noiseBuf![bb01+cy] + (_noiseBuf[bb11+cy] - _noiseBuf![bb01+cy]) * bfx;
                        n = n0 + (n1-n0) * bfz;
                    }
                    float sdf = relY - (n - profMcT) * localAmp;
                    combined = combined < sdf ? combined : sdf;
                }

                {
                    float bandSDF = mbR - Mathf.Abs(worldY - mbWarpedH);
                    if (mbNA > 0f)
                    {
                        float sy = worldY * mbVS;
                        float nx = worldX, nz = worldZ;
                        if (mbTwist != 0f)
                        {
                            float ang = worldY * mbTwist;
                            float c = Mathf.Cos(ang), ss = Mathf.Sin(ang);
                            nx = worldX * c - worldZ * ss;
                            nz = worldX * ss + worldZ * c;
                        }
                        bandSDF += _mbNoise!.GetNoise(nx * mbWF, sy * mbWF, nz * mbWF) * mbNA;
                    }
                    float fd = worldY - floorY;
                    float sdf = bandSDF < fd ? bandSDF : fd;
                    if (sdf > combined) combined = sdf;
                }

                if (mAw > 0f)
                {
                    float na, nb, wn;
                    if (bakeA)
                    {
                        float t0 = _tunBuf![bb00+cy]  + (_tunBuf[bb10+cy]  - _tunBuf[bb00+cy])  * bfx;
                        float t1 = _tunBuf![bb01+cy]  + (_tunBuf[bb11+cy]  - _tunBuf[bb01+cy])  * bfx;
                        na = t0 + (t1 - t0) * bfz;
                        float t0b = _tunBuf2![bb00+cy] + (_tunBuf2[bb10+cy] - _tunBuf2[bb00+cy]) * bfx;
                        float t1b = _tunBuf2![bb01+cy] + (_tunBuf2[bb11+cy] - _tunBuf2[bb01+cy]) * bfx;
                        nb = t0b + (t1b - t0b) * bfz;
                        float w0 = _tunWBuf![bb00+cy] + (_tunWBuf[bb10+cy] - _tunWBuf[bb00+cy]) * bfx;
                        float w1 = _tunWBuf![bb01+cy] + (_tunWBuf[bb11+cy] - _tunWBuf[bb01+cy]) * bfx;
                        wn = w0 + (w1 - w0) * bfz;
                    }
                    else
                    {
                        float sy  = worldY * mAvs;
                        float wx  = _warpX! .GetNoise(worldX * mAwf, sy * mAwf, worldZ * mAwf) * mAwa;
                        float wz  = _warpZ! .GetNoise(worldX * mAwf, sy * mAwf, worldZ * mAwf) * mAwa;
                        float wx2 = _warpX2!.GetNoise(worldX * mAwf, sy * mAwf, worldZ * mAwf) * mAwa;
                        float wz2 = _warpZ2!.GetNoise(worldX * mAwf, sy * mAwf, worldZ * mAwf) * mAwa;
                        na = _caveNoise! .GetNoise((worldX + wx)  * mAf, sy * mAf, (worldZ + wz)  * mAf);
                        nb = _caveNoise2!.GetNoise((worldX + wx2) * mAf, sy * mAf, (worldZ + wz2) * mAf);
                        wn = _widthNoise!.GetNoise(worldX * mAwvf, (worldY * mAvs) * mAwvf, worldZ * mAwvf);
                    }
                    float localWidth = Mathf.Max(mAw * (1f + mAwv * wn), 0.001f);
                    // L2 distance of two independent noise fields: intersection of two zero-crossing
                    // sheets = cylindrical tube network. Normalized by 1/sqrt(2) to keep dist in ~[0,1].
                    float dist      = Mathf.Sqrt(na * na + nb * nb) * 0.7071f;
                    float tunnelSDF = (localWidth - dist) / localWidth * 4f * s;
                    float sdf = Mathf.Min(tunnelSDF, worldY - floorY);
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
                    float sdf = relY - (n - profMcT) * localAmp;
                    combined = combined < sdf ? combined : sdf;
                }

                if (mAw > 0f)
                {
                    float na, nb, wn;
                    if (bakeA)
                    {
                        float t0 = _tunBuf![bb00+cy]  + (_tunBuf[bb10+cy]  - _tunBuf[bb00+cy])  * bfx;
                        float t1 = _tunBuf![bb01+cy]  + (_tunBuf[bb11+cy]  - _tunBuf[bb01+cy])  * bfx;
                        na = t0 + (t1 - t0) * bfz;
                        float t0b = _tunBuf2![bb00+cy] + (_tunBuf2[bb10+cy] - _tunBuf2[bb00+cy]) * bfx;
                        float t1b = _tunBuf2![bb01+cy] + (_tunBuf2[bb11+cy] - _tunBuf2[bb01+cy]) * bfx;
                        nb = t0b + (t1b - t0b) * bfz;
                        float w0 = _tunWBuf![bb00+cy] + (_tunWBuf[bb10+cy] - _tunWBuf[bb00+cy]) * bfx;
                        float w1 = _tunWBuf![bb01+cy] + (_tunWBuf[bb11+cy] - _tunWBuf[bb01+cy]) * bfx;
                        wn = w0 + (w1 - w0) * bfz;
                    }
                    else
                    {
                        float sy  = worldY * mAvs;
                        float wx  = _warpX! .GetNoise(worldX * mAwf, sy * mAwf, worldZ * mAwf) * mAwa;
                        float wz  = _warpZ! .GetNoise(worldX * mAwf, sy * mAwf, worldZ * mAwf) * mAwa;
                        float wx2 = _warpX2!.GetNoise(worldX * mAwf, sy * mAwf, worldZ * mAwf) * mAwa;
                        float wz2 = _warpZ2!.GetNoise(worldX * mAwf, sy * mAwf, worldZ * mAwf) * mAwa;
                        na = _caveNoise! .GetNoise((worldX + wx)  * mAf, sy * mAf, (worldZ + wz)  * mAf);
                        nb = _caveNoise2!.GetNoise((worldX + wx2) * mAf, sy * mAf, (worldZ + wz2) * mAf);
                        wn = _widthNoise!.GetNoise(worldX * mAwvf, (worldY * mAvs) * mAwvf, worldZ * mAwvf);
                    }
                    float localWidth = Mathf.Max(mAw * (1f + mAwv * wn), 0.001f);
                    // L2 distance of two independent noise fields: intersection of two zero-crossing
                    // sheets = cylindrical tube network. Normalized by 1/sqrt(2) to keep dist in ~[0,1].
                    float dist      = Mathf.Sqrt(na * na + nb * nb) * 0.7071f;
                    float tunnelSDF = (localWidth - dist) / localWidth * 4f * s;
                    float sdf = Mathf.Min(tunnelSDF, worldY - floorY);
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
            int ox, int oz, int overlap, bool bakeC, bool bakeA, bool anyMbActive, WorldGenerator? wg, int cw4,
            List<Vector2> nearbyLocs)
        {
            long _setupTicks = 0L, _yloopTicks = 0L;
            long _freq = System.Diagnostics.Stopwatch.Frequency;
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

                float profMcT  = BL(_bk2dMcT!, b200, b210, b201, b211, bfx, bfz);
                float profFlD  = BL(_bk2dFlD!, b200, b210, b201, b211, bfx, bfz);
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

                float mbWarpedH = 0f, mbR = 0f, mbNA = 0f, mbVS = 0f, mbWF = 0f, mbTwist = 0f;
                if (anyMbActive)
                {
                    mbR       = BL(_bk2dMbr!,     b200, b210, b201, b211, bfx, bfz);
                    mbWarpedH = mbR > 0f ? _bkWarpedH![cx * bw + cz] : 0f;
                    mbNA      = BL(_bk2dMbna!,    b200, b210, b201, b211, bfx, bfz);
                    mbVS      = BL(_bk2dMbvs!,    b200, b210, b201, b211, bfx, bfz);
                    mbWF      = BL(_bk2dMbwf!,    b200, b210, b201, b211, bfx, bfz);
                    mbTwist   = BL(_bk2dMbtwist!, b200, b210, b201, b211, bfx, bfz);
                }

                float mAwf=0f, mAwa=0f, mAf=0f, mAvs=0f, mAw=0f, mAwv=0f, mAwvf=0f;
                if (bakeA)
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
                    h, addH, profMcT, localAmp, localAmp > 0f && bakeC,
                    locFac, floorY,
                    mbWarpedH, mbR, mbNA, mbVS, mbWF, mbTwist,
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
        static void Postfix() => Plugin.ResetWorldState();
    }

    // ZNet.Shutdown fires on logout / return to main menu in the same process.
    // Without this, _heightCache and all zone dictionaries survive into the next
    // world load, feeding the new world stale geometry from the previous seed.
    [HarmonyPatch(typeof(ZNet), nameof(ZNet.Shutdown))]
    static class WorldUnloadPatch
    {
        static void Prefix() => Plugin.ResetWorldState();
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

    [HarmonyPatch]
    static class SpawnLocationPatch
    {
        static MethodBase? TargetMethod() =>
            typeof(ZoneSystem).GetMethod("SpawnLocation",
                BindingFlags.NonPublic | BindingFlags.Instance);

        // Correct Y before any ZDOs are created.
        // GetGroundData uses a physics raycast which fails in SpawnMode.Ghost (no collider).
        // For Mode B terrain the carved cave is underground; the surface stays at wg.GetHeight.
        // Mode C support will require a proper surface scan — update this when Mode C is used.
        static void Prefix(ref Vector3 pos)
        {
            var wg = WorldGenerator.instance;
            if (wg == null) return;
            pos.y = wg.GetHeight(pos.x, pos.z);
        }
    }
}
