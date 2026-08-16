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
    struct CaveProfile
    {
        // ── Mode B: warped-heightmap carve ────────────────────────────────────────
        public float modeB_radius;      // half-thickness of carved band (world units); 0 = mode off
        public float modeB_warpAmp;         // XZ warp displacement (world units)
        public float modeB_warpFrequency;   // spatial frequency of XZ warp noise

        // ── Mode A: noise tunnel network ──────────────────────────────────────────
        public float modeA_width;         // noise threshold for tunnel interior (0–1); 0 = mode off
        public float modeA_frequency;     // spatial frequency of 3D tunnel noise
        public float modeA_warpAmp;        // 3D domain-warp amplitude XZ (world units)
        public float modeA_warpAmpY;       // 3D domain-warp amplitude Y (world units; 0=no Y warp)
        public float modeA_warpFrequency;  // spatial frequency of 3D warp noise
        public float modeA_vScale;         // Y scale before noise lookup (low=vertical trenches, high=horizontal tunnels)
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
        public float modeC_flare;          // widens feature toward top (positive) or narrows (negative)
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
        public float floorFade;        // metres above floorY over which carving tapers to zero (0=hard stop)

        public static readonly CaveProfile Default = new CaveProfile
        {
            blur_passes     = 0f,  perona_k = 0f, perona_lambda = 0.1f,
            modeB_radius          = 0f,     modeB_warpAmp           = 80f,
            modeB_warpFrequency   = 0.005f, modeB_vScale = 0.5f, modeB_noiseAmp = 0f, modeB_yOffset = 0f, modeB_twist = 0f,
            modeA_width         = 0f,   modeA_frequency   = 0.015f,
            modeA_warpAmp   = 10f,    modeA_warpAmpY = 0f, modeA_warpFrequency = 0.04f, modeA_vScale = 0.35f,
            modeA_widthVary = 0f,     modeA_widthFrequency = 0.003f,
            modeC_threshold     = 0f,     modeC_noiseAmp      = 0f,     modeC_frequency    = 0.015f,
            modeC_warpAmp      = 8f,     modeC_warpFrequency = 0.005f, modeC_vScale       = 0.4f,
            modeC_twist        = 0f,     modeC_twistVary     = 0f,     modeC_twistFrequency = 0.003f,
            modeC_ampVary      = 0f,     modeC_ampFrequency  = 0.003f, modeC_flare          = 0f,
            floorDepth      = 8f,
        };
    }

    [BepInPlugin("com.ctogle.procedural_terrain", "ProceduralTerrain", "0.1.0")]
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
        internal static bool _compressBake = false;

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
        internal static int   _fixpointMaxZones     = 8;     // EnsureFixpoint hop cap
        internal static float _coastFade           = 0f;     // world units above sea level over which Ocean inflates into blend (0=off)

        // ── SDF bake ─────────────────────────────────────────────────────────────
        static float _bakeRadius = 0f;
        static int   _bakeStep   = 8;
        // Staged: subsampled SDF written by Carve() when within bakeRadius.
        static readonly System.Collections.Concurrent.ConcurrentDictionary<(int, int), (int extLo, int bh, int bw, sbyte[] data)> _bakePending = new();
        // Loaded from disk: populated at world load; null if no valid file exists.
        internal static Dictionary<(int, int), (int extLo, int bh, int bw, sbyte[] data)>? _bakeData;
        static int _bakeFileStep = 8;
        static bool _bakeLoadAttempted = false;

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
        internal static FastNoiseLite? _warpY;          // Mode A: 3D warp Y (sy)
        internal static FastNoiseLite? _warpZ;          // Mode A: 3D warp Z
        internal static FastNoiseLite? _warpX2;         // Mode A: 3D warp X for field 2
        internal static FastNoiseLite? _warpY2;         // Mode A: 3D warp Y (sy) for field 2
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

        // ── Direct brick write (Option C fast path) ──────────────────────────────
        // Bypasses per-voxel VoxelField.Set() overhead: ~80 dict lookups per zone
        // instead of ~327K, plus direct sbyte[] writes into Brick._data.
        internal static Type?            _brickType;
        internal static FieldInfo?       _vfBricksField;       // VoxelField._bricks
        internal static FieldInfo?       _vfMaxBrickYField;    // VoxelField._maxBrickY
        internal static MethodInfo?      _vfSetFloorMethod;    // VoxelField.SetFloor(SolidFloorRule)
        internal static FieldInfo?       _brickDataField;      // Brick._data
        internal static Type?            _solidFloorRuleType;
        internal static ConstructorInfo? _solidFloorCtor;      // SolidFloorRule(int y,x0,x1,z0,z1)
        // Compiled delegate: _bricks[Int3(bx,by,bz)] = brick
        internal static Action<object, int, int, int, object>? _addBrick;
        // Set when prefix skips VoxelizeColumns so Phase 3 knows to set the floor rule
        internal static bool _voxelizeColumnsSkipped;

        // VoxelBounds fields — used to construct the changed-bounds arg for PokeNeighbors
        internal static Type?       _voxelBoundsType;
        internal static FieldInfo?  _vbMinField;
        internal static FieldInfo?  _vbMaxField;
        internal static FieldInfo?  _vbAnyField;
        internal static readonly Dictionary<string, int> _zoneMaxY  = new();
        internal static readonly Dictionary<string, int> _zoneExtHi = new(); // highest voxel Y ever written; used to size the reset range in hot-reload

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

        // Scratch buffers for floating-island BFS (Phase 1.5). Pre-allocated, resized on demand.
        [ThreadStatic] static bool[]? _visitedBuf;
        [ThreadStatic] static bool[]? _percolVisited;
        [ThreadStatic] static int[]?  _islandQueue;

        // wg.GetHeight is a pure function of (worldX, worldZ) for a given world seed.
        // Cache it keyed by integer coarse voxel coords — cleared on world load only.
        internal static readonly System.Collections.Concurrent.ConcurrentDictionary<long, float> _heightCache = new();

        // Per-zone SDF cache: filled by Phase 3, queried by FallbackDensityPatch.
        // Layout: data[(ci * bh + cy) * width + ri]  (ci=zoneX, ri=zoneZ, cy=Y-extLo)
        internal static readonly Dictionary<string, (int extLo, int bh, int width, sbyte[] data)> _sdfCache = new();

        // Per-zone surface height cache: filled after Phase 2, queried by GetWorldHeight patch.
        // Layout: surfH[cx * bw + cz] = world-Y of topmost solid voxel (NaN = no solid in column).
        // Includes overlap region so border columns near zone edges are correct.
        internal static readonly Dictionary<(int ox, int oz), (int bw, int extLo, float s, float[] surfH)> _surfHCache = new();

        // Int3 field accessors — used by FallbackDensityPatch to extract X/Y/Z from boxed struct.
        internal static FieldInfo? _int3FieldX, _int3FieldY, _int3FieldZ;

        // ── Cross-zone grounding face cache ──────────────────────────────────────
        // Indexed by (ox, oz) territory origin (same coordinates as _zoneParams).
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

        // Reused across Carve() calls to avoid per-zone GC pressure.
        [ThreadStatic] internal static float[]? _bufA;
        [ThreadStatic] internal static float[]? _bufB;
        // CR reconstruction temp buffers — main thread only, not [ThreadStatic].
        static float[] _crTemp1 = new float[72 * 150 * 36]; // [bw * nsy * nsz]
        static float[] _crTemp2 = new float[72 * 300 * 36]; // [bw * bh  * nsz]

        [ThreadStatic] internal static float[]? _bufC;
        // Dedicated buffer for ComputeZoneFaceCache so it never clobbers Carve()'s active buf.
        [ThreadStatic] internal static float[]? _precomputeBuf;

        [ThreadStatic] internal static float[]? _noiseBuf;
        [ThreadStatic] internal static float[]? _tunBuf;
        [ThreadStatic] internal static float[]? _tunBuf2;
        [ThreadStatic] internal static float[]? _tunWBuf;
        // 2D coarse bake (same XZ grid as _noiseBuf): [cx4*cw4 + cz4]
        [ThreadStatic] internal static float[]? _bk2dAddH;
        [ThreadStatic] internal static float[]? _bk2dLAmp;
        [ThreadStatic] internal static float[]? _bk2dMcS;
        [ThreadStatic] internal static float[]? _bk2dMcT;
        [ThreadStatic] internal static float[]? _bk2dMcFlare;
        [ThreadStatic] internal static float[]? _bk2dFlD;
        [ThreadStatic] internal static float[]? _bk2dFlFade;
        // 2D coarse bake — Mode B shape params
        [ThreadStatic] internal static float[]? _bk2dMbwf;
        [ThreadStatic] internal static float[]? _bk2dMbwa;
        [ThreadStatic] internal static float[]? _bk2dMbr;
        [ThreadStatic] internal static float[]? _bk2dMbvs;
        [ThreadStatic] internal static float[]? _bk2dMbna;
        [ThreadStatic] internal static float[]? _bk2dMbyo;
        [ThreadStatic] internal static float[]? _bk2dMbtwist;
        // 2D coarse bake — Mode A shape params
        [ThreadStatic] internal static float[]? _bk2dMawf;
        [ThreadStatic] internal static float[]? _bk2dMawa;
        [ThreadStatic] internal static float[]? _bk2dMaway;
        [ThreadStatic] internal static float[]? _bk2dMaf;
        [ThreadStatic] internal static float[]? _bk2dMavs;
        [ThreadStatic] internal static float[]? _bk2dMaw;
        [ThreadStatic] internal static float[]? _bk2dMawv;
        [ThreadStatic] internal static float[]? _bk2dMawvf;
        [ThreadStatic] internal static float[]? _bk2dMayo;
        // Full-res (bw×bw) — Mode B warped heightmap (wg.GetHeight after XZ warp)
        [ThreadStatic] internal static float[]? _bkWarpedH;

        static FieldInfo? _locationInstancesField;

        // ─────────────────────────────────────────────────────────────────────────

        void Awake()
        {
            Log = Logger;
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
            LoadBiomeConfig();
            WatchBiomeConfig();
            new Harmony("com.ctogle.procedural_terrain").PatchAll();
            new Terminal.ConsoleCommand("voxcaves_dump", "dump SDF + biome blend data for the next 9 zones processed to BepInEx/config/voxcaves_dump_*.json", _ =>
            {
                _dumpZonesRemaining = 9;
                Log.LogInfo("[ProceduralTerrain] Will dump next 9 zones to voxcaves_dump_*.json");
            });
            new Terminal.ConsoleCommand("voxcaves_probe", "voxcaves_probe [x z] — print biome blend breakdown + profile params at world pos (default: player pos)", args =>
            {
                try
                {
                    var wg = WorldGenerator.instance;
                    if (wg == null) { Log.LogWarning("[ProceduralTerrain] probe: WorldGenerator not ready."); return; }
                    float s = _scale is { } sv && sv > 0f ? sv : 1f;
                    float wx, wz;
                    if (args.Length >= 3 && float.TryParse(args[1], out float ax) && float.TryParse(args[2], out float az))
                        { wx = ax; wz = az; }
                    else
                    {
                        var player = Player.m_localPlayer;
                        if (player == null) { Log.LogWarning("[ProceduralTerrain] probe: no player; pass x z args."); return; }
                        wx = player.transform.position.x;
                        wz = player.transform.position.z;
                    }
                    Log.LogInfo($"[ProceduralTerrain] Probing biome blend at world ({wx:F1}, {wz:F1}):");
                    ProbeBlend(wx, wz, wg, s);
                }
                catch (Exception e) { Log.LogError($"[ProceduralTerrain] probe exception: {e}"); }
            });
            new Terminal.ConsoleCommand("voxcaves_bake", "voxcaves_bake [radius] — write baked SDF to disk for all staged zones (optionally filtered to radius)", args =>
            {
                float radius = _bakeRadius;
                if (args.Length >= 2 && float.TryParse(args[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float r))
                    radius = r;
                WriteBakeFile(radius);
            });
            Log.LogInfo("[ProceduralTerrain] Ready.");
        }

        internal static int _dumpZonesRemaining = 0;

        // Voxheim queue diagnostic — reflected once, logged periodically
        static System.Reflection.FieldInfo? _vhPendingZones;
        static System.Reflection.FieldInfo? _vhDeferredRemeshes;
        static System.Reflection.FieldInfo? _vhSZones;
        static System.Reflection.FieldInfo? _vhIsActive;
        static System.Reflection.FieldInfo? _vhIsPending;
        static int _diagFrame;

        static int _pollFrame;
        void Update()
        {
            if (_hotReloadPending)
            {
                _hotReloadPending = false;
                RecarveAll();
            }

            // Voxheim mesh queue diagnostic — log every ~10 s
            if (++_diagFrame % 600 == 0)
            {
                try
                {
                    if (_vhPendingZones == null || _vhDeferredRemeshes == null)
                    {
                        var flags = System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
                        var voxelZoneType = System.AppDomain.CurrentDomain.GetAssemblies()
                            .SelectMany(a => { try { return a.GetTypes(); } catch { return System.Array.Empty<System.Type>(); } })
                            .FirstOrDefault(t => t.Name == "VoxelZone");
                        if (voxelZoneType != null)
                        {
                            _vhPendingZones     = voxelZoneType.GetField("s_pendingZones",     System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
                            _vhDeferredRemeshes = voxelZoneType.GetField("s_deferredRemeshes", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
                            _vhSZones           = voxelZoneType.GetField("s_zones",            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
                            _vhIsActive         = voxelZoneType.GetField("_isActive",          System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                            _vhIsPending        = voxelZoneType.GetField("_pending",           System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                        }
                    }
                    if (_vhPendingZones != null && _vhDeferredRemeshes != null)
                    {
                        var pending  = _vhPendingZones.GetValue(null)    as System.Collections.ICollection;
                        var deferred = _vhDeferredRemeshes.GetValue(null) as System.Collections.ICollection;
                        int active = 0, pend = 0, stale = 0;
                        if (_vhSZones != null && _vhIsActive != null && _vhIsPending != null)
                        {
                            var szones = _vhSZones.GetValue(null) as System.Collections.IDictionary;
                            if (szones != null)
                                foreach (var v in szones.Values)
                                {
                                    bool ia = (bool)(_vhIsActive.GetValue(v) ?? false);
                                    bool ip = (bool)(_vhIsPending.GetValue(v) ?? false);
                                    if (ia) active++; else if (ip) pend++; else stale++;
                                }
                        }
                        Log.LogDebug($"[ProceduralTerrain] Voxheim queues: pending={pending?.Count ?? -1} deferred={deferred?.Count ?? -1} zones(active={active} pending={pend} stale={stale})");
                    }
                }
                catch (System.Exception e) { Log.LogWarning($"[ProceduralTerrain] diag error: {e.Message}"); }
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
            // PokeNeighbors(in VoxelBounds, bool) — tells adjacent active zones to remesh boundary bricks.
            // PokeTransitionNeighbors only iterates _editedBricks (player edits), so we call PokeNeighbors directly.
            _pokeNeighborsMethod = zoneT?.GetMethods(BindingFlags.NonPublic | BindingFlags.Instance)
                .FirstOrDefault(m => m.Name == "PokeNeighbors" && m.GetParameters().Length == 2
                                     && m.GetParameters()[1].ParameterType == typeof(bool));
            if (_enqueueMeshMethod   == null) Log.LogWarning("[ProceduralTerrain] EnqueueMesh not found.");
            if (_finishSkinNowMethod == null) Log.LogWarning("[ProceduralTerrain] FinishSkinNow not found.");
            if (_pokeNeighborsMethod == null) Log.LogWarning("[ProceduralTerrain] PokeNeighbors not found — neighbor seam repair disabled.");

            var boundsT = AppDomain.CurrentDomain.GetAssemblies()
                .SelectMany(a => { try { return a.GetTypes(); } catch { return Array.Empty<Type>(); } })
                .FirstOrDefault(t => t.FullName == "Voxheim.Core.VoxelBounds");
            _voxelBoundsType = boundsT;
            _vbMinField = boundsT?.GetField("Min", BindingFlags.Public | BindingFlags.Instance);
            _vbMaxField = boundsT?.GetField("Max", BindingFlags.Public | BindingFlags.Instance);
            _vbAnyField = boundsT?.GetField("Any", BindingFlags.Public | BindingFlags.Instance);
            if (_voxelBoundsType == null) Log.LogWarning("[ProceduralTerrain] VoxelBounds type not found — neighbor seam repair disabled.");

            _zoneOriginX         = zoneT?.GetField("_originX",
                BindingFlags.NonPublic | BindingFlags.Instance);
            _zoneOriginZ         = zoneT?.GetField("_originZ",
                BindingFlags.NonPublic | BindingFlags.Instance);
            _voxelZoneFieldField = zoneT?.GetField("_field",
                BindingFlags.NonPublic | BindingFlags.Instance);
            _yScanHiField        = zoneT?.GetField("_yScanHi",
                BindingFlags.NonPublic | BindingFlags.Instance);
            if (_zoneOriginX == null || _zoneOriginZ == null)
                Log.LogWarning("[ProceduralTerrain] Zone origin fields missing — per-brick SDF disabled.");
            if (_voxelZoneFieldField == null)
                Log.LogWarning("[ProceduralTerrain] VoxelZone._field not found — per-brick SDF disabled.");
            if (_yScanHiField == null)
                Log.LogWarning("[ProceduralTerrain] _yScanHi not found — Mode C ceiling extension disabled.");

            _locationInstancesField =
                typeof(ZoneSystem).GetField("m_locationInstances",
                    BindingFlags.NonPublic | BindingFlags.Instance) ??
                typeof(ZoneSystem).GetField("m_locationInstances",
                    BindingFlags.Public    | BindingFlags.Instance);
            if (_locationInstancesField == null)
                Log.LogWarning("[ProceduralTerrain] m_locationInstances not found — location protection off.");

            // ── Direct brick write reflection ─────────────────────────────────────
            _brickType        = FindType("Voxheim.Core.Brick", "Voxheim.Core");
            _solidFloorRuleType = FindType("Voxheim.Core.SolidFloorRule", "Voxheim.Core");
            _vfBricksField    = fieldT?.GetField("_bricks",    BindingFlags.NonPublic | BindingFlags.Instance);
            _vfMaxBrickYField = fieldT?.GetField("_maxBrickY", BindingFlags.NonPublic | BindingFlags.Instance);
            _vfSetFloorMethod = fieldT?.GetMethod("SetFloor",  BindingFlags.Public | BindingFlags.Instance);
            _brickDataField   = _brickType?.GetField("_data",  BindingFlags.NonPublic | BindingFlags.Instance);
            _solidFloorCtor   = _solidFloorRuleType?.GetConstructor(
                new[] { typeof(int), typeof(int), typeof(int), typeof(int), typeof(int) });

            if (_brickType != null && _vfBricksField != null && _int3Ctor != null && fieldT != null)
            {
                // Emit: void(object field, int bx, int by, int bz, object brick)
                //       → ((VoxelField)field)._bricks[new Int3(bx,by,bz)] = (Brick)brick
                var dictType = typeof(Dictionary<,>).MakeGenericType(int3T, _brickType);
                var setItem  = dictType.GetMethod("set_Item")!;
                var abDm = new DynamicMethod("VC_AddBrick", typeof(void),
                    new[] { typeof(object), typeof(int), typeof(int), typeof(int), typeof(object) },
                    typeof(Plugin).Module, skipVisibility: true);
                var abIl = abDm.GetILGenerator();
                abIl.Emit(OpCodes.Ldarg_0); abIl.Emit(OpCodes.Castclass, fieldT);
                abIl.Emit(OpCodes.Ldfld, _vfBricksField);
                abIl.Emit(OpCodes.Ldarg_1); abIl.Emit(OpCodes.Ldarg_2); abIl.Emit(OpCodes.Ldarg_3);
                abIl.Emit(OpCodes.Newobj, _int3Ctor);
                abIl.Emit(OpCodes.Ldarg_S, (byte)4); abIl.Emit(OpCodes.Castclass, _brickType);
                abIl.Emit(OpCodes.Callvirt, setItem);
                abIl.Emit(OpCodes.Ret);
                _addBrick = (Action<object, int, int, int, object>)
                    abDm.CreateDelegate(typeof(Action<object, int, int, int, object>));
                Log.LogInfo("[ProceduralTerrain] Direct brick write path ready.");
            }
            else Log.LogWarning("[ProceduralTerrain] Direct brick write unavailable — falling back to per-voxel Set.");

            _int3FieldX = int3T.GetField("X", BindingFlags.Public | BindingFlags.Instance)
                          ?? int3T.GetField("x", BindingFlags.Public | BindingFlags.Instance);
            _int3FieldY = int3T.GetField("Y", BindingFlags.Public | BindingFlags.Instance)
                          ?? int3T.GetField("y", BindingFlags.Public | BindingFlags.Instance);
            _int3FieldZ = int3T.GetField("Z", BindingFlags.Public | BindingFlags.Instance)
                          ?? int3T.GetField("z", BindingFlags.Public | BindingFlags.Instance);
            if (_int3FieldX == null || _int3FieldY == null || _int3FieldZ == null)
                Log.LogWarning("[ProceduralTerrain] Int3 X/Y/Z fields not found — FallbackDensity patch will use vanilla.");

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
            Path.Combine(BepInEx.Paths.ConfigPath, "procedural_terrain_biomes.cfg");

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
                    case "modea_warpampy":        p.modeA_warpAmpY        = v; break;
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
                    case "modec_flare":           p.modeC_flare           = v; break;
                    case "modec_yoffset":         p.modeC_yOffset         = v; break;
                    case "blur_passes":            p.blur_passes            = v; break;
                    case "perona_k":              p.perona_k              = v; break;
                    case "perona_lambda":         p.perona_lambda         = v; break;
                    case "floordepth":            p.floorDepth            = v; break;
                    case "floorfade":             p.floorFade             = v; break;
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
                        if (k == "fixpointmaxzones" && int.TryParse(v, out int fmz))
                            _fixpointMaxZones = Mathf.Max(1, fmz);
                        if (k == "coastfade" && float.TryParse(v, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float cf))
                            _coastFade = Mathf.Max(0f, cf);
                    }
                    continue;
                }
                if (name.Equals("Bake", StringComparison.OrdinalIgnoreCase))
                {
                    foreach (var (k, v) in kvs)
                    {
                        if (k == "bakeradius" && float.TryParse(v, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float br))
                            _bakeRadius = Mathf.Max(0f, br);
                        if (k == "bakestep" && int.TryParse(v, out int bs))
                            _bakeStep = Mathf.Clamp(bs, 1, 64);
                        if (k == "compressbake")
                            _compressBake = !(v == "0" || v == "false");
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
            Log.LogInfo($"[ProceduralTerrain] Profiles reloaded: {named.Count} named, {profiles.Count} biomes.");
            foreach (var kv in profiles)
            {
                var p = kv.Value;
                if (p.modeA_width > 0f || p.modeB_radius > 0f || p.modeC_noiseAmp > 0f)
                    Log.LogInfo($"[ProceduralTerrain]   {kv.Key}: mB_r={p.modeB_radius:F1} mA_w={p.modeA_width:F2} mC_amp={p.modeC_noiseAmp:F1}");
            }
        }

        static void WriteDefaultBiomeConfig(string path)
        {
            var sb = new System.Text.StringBuilder();

            sb.AppendLine("# ProceduralTerrain — edit and save to hot-reload in-game");
            sb.AppendLine("#");
            sb.AppendLine("# Define profiles with [Profile:name], assign to biomes with:");
            sb.AppendLine("#   profile = name");
            sb.AppendLine("# Params listed directly in a [BiomeName] section override the profile.");
            sb.AppendLine("#");
            sb.AppendLine("# ── Full parameter reference (copy to start a new profile) ─────────────");
            sb.AppendLine("# [Profile:my_profile]");
            sb.AppendLine("# blur_passes           = 0      # XZ Gaussian passes for seam smoothing (0=off, 1-3=light/heavy; Y never blurred)");
            sb.AppendLine("# floorDepth            = 8      # how far below heightmap modes A+B stop carving");
            sb.AppendLine("# floorFade             = 0      # world units above floorY over which carving tapers to zero (0=hard stop)");
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
            sb.AppendLine("# modeA_warpAmp         = 5      # 3D domain-warp displacement XZ (world units)");
            sb.AppendLine("# modeA_warpAmpY        = 0      # 3D domain-warp displacement Y/vertical (world units; 0=no Y bend)");
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
            Log.LogInfo($"[ProceduralTerrain] Wrote default biome config to {path}");
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
            Log.LogInfo($"[ProceduralTerrain] noise seed={seed} (uid={uid})");

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
            _warpY       = MakeNoise(15); // Mode A warp Y/sy (field 1)
            _warpY2      = MakeNoise(16); // Mode A warp Y/sy (field 2)
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

        // C1-continuous max — rounds the crease where two SDF fields meet, over a blend width of k.
        // Identical to max(a,b) when |a-b| > k; adds at most k/4 at the join centre.
        static float SmoothMax(float a, float b, float k)
        {
            float h = Mathf.Max(k - Mathf.Abs(a - b), 0f) / k;
            return Mathf.Max(a, b) + h * h * k * 0.25f;
        }

        // ── Shared biome-blend BFS primitives ────────────────────────────────────────

        // Multi-source BFS from all cells with non-zero fraction of biome slot.
        // Fills _biomeDist with hop-counts; call after _biomeFrac is populated.
        static void RunBiomeBFS(int slot, int pcw, int pgSize, int maxHops)
        {
            int qHead = 0, qTail = 0;
            for (int i = 0; i < pgSize; i++)
            {
                if (_biomeFrac![i * MaxBiomeSlots + slot] > 0f)
                    { _biomeDist![i] = 0; _biomeBfsQ![qTail++] = i; }
                else _biomeDist![i] = int.MaxValue;
            }
            while (qHead < qTail)
            {
                int cell = _biomeBfsQ![qHead++];
                int nd   = _biomeDist[cell] + 1;
                if (nd > maxHops) continue;
                int px = cell / pcw, pz = cell % pcw;
                if (px > 0)     { int n=cell-pcw; if (_biomeDist[n]>nd){_biomeDist[n]=nd;_biomeBfsQ[qTail++]=n;} }
                if (px < pcw-1) { int n=cell+pcw; if (_biomeDist[n]>nd){_biomeDist[n]=nd;_biomeBfsQ[qTail++]=n;} }
                if (pz > 0)     { int n=cell-1;   if (_biomeDist[n]>nd){_biomeDist[n]=nd;_biomeBfsQ[qTail++]=n;} }
                if (pz < pcw-1) { int n=cell+1;   if (_biomeDist[n]>nd){_biomeDist[n]=nd;_biomeBfsQ[qTail++]=n;} }
            }
        }

        // Accumulate one weighted profile contribution into acc, adding w to totW.
        static void AccumulateProfile(ref CaveProfile acc, ref float totW, in CaveProfile prof, float w)
        {
            totW += w;
            acc.modeB_radius         += w * prof.modeB_radius;
            acc.modeB_warpAmp        += w * prof.modeB_warpAmp;
            acc.modeB_warpFrequency  += w * prof.modeB_warpFrequency;
            acc.modeB_vScale         += w * prof.modeB_vScale;
            acc.modeB_noiseAmp       += w * prof.modeB_noiseAmp;
            acc.modeB_yOffset        += w * prof.modeB_yOffset;
            acc.modeB_twist          += w * prof.modeB_twist;
            acc.modeA_width          += w * prof.modeA_width;
            acc.modeA_frequency      += w * prof.modeA_frequency;
            acc.modeA_warpAmp        += w * prof.modeA_warpAmp;
            acc.modeA_warpAmpY       += w * prof.modeA_warpAmpY;
            acc.modeA_warpFrequency  += w * prof.modeA_warpFrequency;
            acc.modeA_vScale         += w * prof.modeA_vScale;
            acc.modeA_widthVary      += w * prof.modeA_widthVary;
            acc.modeA_widthFrequency += w * prof.modeA_widthFrequency;
            acc.modeA_yOffset        += w * prof.modeA_yOffset;
            acc.modeC_threshold      += w * prof.modeC_threshold;
            acc.modeC_noiseAmp       += w * prof.modeC_noiseAmp;
            acc.modeC_frequency      += w * prof.modeC_frequency;
            acc.modeC_warpAmp        += w * prof.modeC_warpAmp;
            acc.modeC_warpFrequency  += w * prof.modeC_warpFrequency;
            acc.modeC_vScale         += w * prof.modeC_vScale;
            acc.modeC_twist          += w * prof.modeC_twist;
            acc.modeC_twistVary      += w * prof.modeC_twistVary;
            acc.modeC_twistFrequency += w * prof.modeC_twistFrequency;
            acc.modeC_ampVary        += w * prof.modeC_ampVary;
            acc.modeC_ampFrequency   += w * prof.modeC_ampFrequency;
            acc.modeC_flare          += w * prof.modeC_flare;
            acc.modeC_yOffset        += w * prof.modeC_yOffset;
            acc.blur_passes          += w * prof.blur_passes;
            acc.perona_k             += w * prof.perona_k;
            acc.perona_lambda        += w * prof.perona_lambda;
            acc.floorDepth           += w * prof.floorDepth;
            acc.floorFade            += w * prof.floorFade;
        }

        static void NormalizeProfile(ref CaveProfile p, float totW)
        {
            if (totW <= 0f) return;
            float rcp = 1f / totW;
            p.modeB_radius         *= rcp; p.modeB_warpAmp       *= rcp;
            p.modeB_warpFrequency  *= rcp; p.modeB_vScale        *= rcp;
            p.modeB_noiseAmp       *= rcp; p.modeB_yOffset       *= rcp; p.modeB_twist *= rcp;
            p.modeA_width          *= rcp; p.modeA_frequency     *= rcp;
            p.modeA_warpAmp        *= rcp; p.modeA_warpAmpY      *= rcp; p.modeA_warpFrequency *= rcp;
            p.modeA_vScale         *= rcp; p.modeA_widthVary     *= rcp; p.modeA_widthFrequency *= rcp; p.modeA_yOffset *= rcp;
            p.modeC_threshold      *= rcp; p.modeC_noiseAmp      *= rcp;
            p.modeC_frequency      *= rcp; p.modeC_warpAmp       *= rcp;
            p.modeC_warpFrequency  *= rcp; p.modeC_vScale        *= rcp;
            p.modeC_twist          *= rcp; p.modeC_twistVary     *= rcp; p.modeC_twistFrequency *= rcp;
            p.modeC_ampVary        *= rcp; p.modeC_ampFrequency  *= rcp; p.modeC_flare *= rcp; p.modeC_yOffset *= rcp;
            p.blur_passes          *= rcp; p.perona_k            *= rcp;
            p.perona_lambda        *= rcp; p.floorDepth          *= rcp; p.floorFade *= rcp;
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
                RunBiomeBFS(slot, pcw, pgSize, maxHops);
                int distC = _biomeDist![center];
                if (distC > maxHops) continue;
                float t = distC * coarseStep / _biomeBlendRadius;
                float w = _biomeBlendSmooth ? 1f - t * t * (3f - 2f * t) : 1f - t;
                if (w <= 0f) continue;
                float frac = distC == 0 ? _biomeFrac![center * MaxBiomeSlots + slot] : 1f;
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
                RunBiomeBFS(slot, pcw, pgSize, maxHops);
                int distC = _biomeDist![center];
                if (distC > maxHops) continue;
                float tC = distC * coarseStep / _biomeBlendRadius;
                float w  = _biomeBlendSmooth ? 1f - tC * tC * (3f - 2f * tC) : 1f - tC;
                if (w <= 0f) continue;
                if (distC == 0) w *= _biomeFrac![center * MaxBiomeSlots + slot];
                if (w <= 0f) continue;
                var prof = profs.TryGetValue((Heightmap.Biome)biomeInt, out var p) ? p : CaveProfile.Default;
                AccumulateProfile(ref result, ref totW, in prof, w);
            }
            NormalizeProfile(ref result, totW);
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
                RunBiomeBFS(slot, pcw, pgSize, maxHops);

                // Accumulate weighted profile into each inner coarse cell.
                // At distance 0, scale by fractional membership so a cell that is 50% BF
                // contributes half as much as a fully-BF cell at the same distance.
                for (int cx4 = 0; cx4 < cw4; cx4++)
                for (int cz4 = 0; cz4 < cw4; cz4++)
                {
                    int pIdx = (cx4 + pad) * pcw + (cz4 + pad);
                    int distCells = _biomeDist![pIdx];
                    if (distCells > maxHops) continue;
                    float t = distCells * coarseStep / _biomeBlendRadius;
                    float w = _biomeBlendSmooth ? 1f - t * t * (3f - 2f * t) : 1f - t;
                    if (w <= 0f) continue;
                    // Scale by fractional membership at the seed cell (distance 0 only).
                    // For propagated cells (dist > 0) the full falloff weight applies as-is.
                    if (distCells == 0)
                        w *= _biomeFrac![pIdx * MaxBiomeSlots + slot];
                    if (w <= 0f) continue;
                    int idx = cx4 * cw4 + cz4;
                    ref var acc = ref _blendedProfs![idx];
                    AccumulateProfile(ref acc, ref _biomeTotW![idx], in prof, w);
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
                    ref var acc = ref _blendedProfs![idx];
                    AccumulateProfile(ref acc, ref _biomeTotW![idx], in oceanProf, coastW);
                }
            }

            // ── Step 3: Normalize ─────────────────────────────────────────────────────
            for (int idx = 0; idx < cellCount; idx++)
            {
                ref var p = ref _blendedProfs![idx];
                NormalizeProfile(ref p, _biomeTotW![idx]);
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
                Log.LogInfo($"[ProceduralTerrain] Location cache built: {_locationPositions.Count} positions — scheduling recarve.");
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
        // field==null means bake-only: run Phase 1+2, stage result, skip Phase 3 voxel writes.
        internal static void Carve(object? field, object? grid, int ox, int oz, int width, int yLo, int yHi)
        {
            if (_scale == null)
            {
                _scale = _scaleField != null ? (float)_scaleField.GetValue(null)! : 1f;
                Log.LogInfo($"[ProceduralTerrain] s_scale resolved to {_scale}");
            }
            float s      = _scale.Value;
            bool bakeOnly = field == null;
            var   wg = WorldGenerator.instance;

            if (!_bakeLoadAttempted) { _bakeLoadAttempted = true; LoadBakeFile(); }

            BuildLocationCache();

            float zoneX = (ox + width * 0.5f) * s;
            float zoneZ = (oz + width * 0.5f) * s;

            string zoneKey = $"{ox},{oz}";
            if (!bakeOnly) _zoneParams[zoneKey] = (field!, grid!, ox, oz, width, yLo, yHi);

            // If location data isn't ready yet, leave the zone vanilla.
            if (!bakeOnly && !_locationsCached)
            {
                Log.LogDebug($"[ProceduralTerrain] zone ({ox},{oz}) skip=no-locations");
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
            Log.LogDebug($"[ProceduralTerrain] zone ({ox},{oz}) biome={centerBiome} mB_r={centerProf.modeB_radius:F1} mA_w={centerProf.modeA_width:F2} mC_amp={centerProf.modeC_noiseAmp:F1} mC_flare={centerProf.modeC_flare:F3}");

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
            if (_bufC == null || _bufC.Length < bufSize) _bufC = new float[bufSize];
            float[] buf = _bufA, tmp = _bufB, orig = _bufC;

            bool bakeC = false, bakeA = false, anyMbActive = false;
            bool fromBake = _bakeData != null && TryPopulateFromBake(ox, oz, bw, bh, extLo, s, buf);
            long _t0 = 0, _t1 = 0, _t1b = 0, _t2 = 0, _tFix = 0;
            var _sw = System.Diagnostics.Stopwatch.StartNew();

            if (!fromBake)
            {
            InitNoise();

            int   cw4     = cw4Early;  // already computed above for BakeBlendedProfiles
            BakeSdfSetup(ox, oz, overlap, s, extLo, bh, bw, cw4, wg, nearbyLocs,
                out bakeC, out bakeA, out anyMbActive);

            _t0 = _sw.ElapsedMilliseconds;

            // ── Phase 1 ───────────────────────────────────────────────────────────
            FillPhase1(buf, bw, bh, extLo, s, ox, oz, overlap, bakeC, bakeA, anyMbActive, wg, cw4, nearbyLocs);
            _t1 = _sw.ElapsedMilliseconds;

            _t1b = _sw.ElapsedMilliseconds;

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

            // ── Phase 2: Taubin smoothing of the SDF buffer ──────────────────────────
            // Each iteration = one λ-step (forward blur) + one μ-step (backward blur).
            // λ=0.5, μ=-0.53: high-frequency noise damps while volume is preserved.
            // Two kernel modes via perona_k:
            //   perona_k = 0  → separable 5-tap Gaussian [1/16,4/16,6/16,4/16,1/16]
            //   perona_k > 0  → Perona-Malik anisotropic diffusion (edge-preserving)
            const float taubinLambda = 0.5f;
            const float taubinMu    = -0.53f;
            int   pbSz = bw * bh * bw;

            // Applies one Taubin half-step: smooths buf into tmp via kernel, then
            // writes buf[i] = orig[i] + weight*(smoothed[i] - orig[i]).
            void TaubinStep(float weight)
            {
                if (blurProf.perona_k > 0f)
                {
                    float invK2 = 1f / (blurProf.perona_k * blurProf.perona_k);
                    float lam   = blurProf.perona_lambda;
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
                }
                else
                {
                    // X pass: buf → tmp
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
                    // Y pass: buf → tmp
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
                }
                // Apply displacement scaled by weight relative to pre-step snapshot.
                for (int i = 0; i < pbSz; i++)
                    buf[i] = orig[i] + weight * (tmp[i] - orig[i]);
            }

            for (int pass = 0; pass < blurPasses; pass++)
            {
                Array.Copy(buf, orig, pbSz);
                TaubinStep(taubinLambda);   // forward: shrinks slightly
                Array.Copy(buf, orig, pbSz);
                TaubinStep(taubinMu);       // backward: restores volume
            }

            _t2 = _sw.ElapsedMilliseconds;

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
                    long _fixStart = _sw.ElapsedMilliseconds;
                    EnsureFixpoint(ox, oz, width, ungNX, ungPX, ungNZ, ungPZ);
                    _tFix = _sw.ElapsedMilliseconds - _fixStart;

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
                        percol      = new bool[4, 4],
                        percolValid = false, // Carve's BFS can't compute percol; ComputeZoneFaceCache does
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

                Log.LogDebug($"[ProceduralTerrain] island-fill zone ({ox},{oz}): pre={!needsFullPass} seeds={qTail} removed={removed}");
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

                Log.LogDebug($"[ProceduralTerrain] pocket-fill zone ({ox},{oz}): filled={filled}");
            }

            } // end if (!fromBake)

            // ── Bake staging: subsample buf[] into _bakePending ───────────────────────
            if (!fromBake && (bakeOnly || _bakeRadius > 0f))
            {
                if (bakeOnly || (ox * s) * (ox * s) + (oz * s) * (oz * s) <= _bakeRadius * _bakeRadius)
                    StageBakeEntry(ox, oz, bw, bh, extLo, s, buf);
            }

            // ── Surface height cache (for vegetation placement) ───────────────────────
            // Scan each XZ column top-down and record the world-Y of the first solid voxel.
            // Covers the full buffer including overlap so border trees land correctly.
            {
                var surfH = new float[bw * bw];
                for (int cx = 0; cx < bw; cx++)
                for (int cz = 0; cz < bw; cz++)
                {
                    float sh = float.NaN;
                    for (int cy = bh - 1; cy >= 0; cy--)
                    {
                        if (buf[(cx * bh + cy) * bw + cz] < 0f)
                        {
                            sh = (extLo + cy) * s;
                            break;
                        }
                    }
                    surfH[cx * bw + cz] = sh;
                }
                if (!_parallelBakeActive)
                    lock (_surfHCache) _surfHCache[(ox, oz)] = (bw, extLo, s, surfH);
            }

            // ── Phase 3: Write SDF directly to VoxelField via the 'field' parameter ────
            // Skipped when bakeOnly (field==null); only staging into _bakePending is needed.
            int carved = 0;
            int maxYC  = yHi;
            int maxYAny = yHi;
            if (!bakeOnly)
            {
            var transBytes  = new sbyte[bh];
            var transHasOvr = new bool[bh];
            var cacheData   = new sbyte[width * bh * width]; // (ci*bh + cy)*width + ri

            // Encode transition bytes and cacheData for all columns (common to both write paths)
            for (int ci = 0; ci < width; ci++)
            for (int ri = 0; ri < width; ri++)
            {
                int cx = ci + overlap, cz = ri + overlap;
                Array.Clear(transHasOvr, 0, bh);
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
                for (int cy = 0; cy < bh; cy++)
                {
                    float finalSDF = buf[(cx * bh + cy) * bw + cz];
                    sbyte v = transHasOvr[cy] ? transBytes[cy] : ToSDFByte(finalSDF, s);
                    cacheData[(ci * bh + cy) * width + ri] = v;
                    int gy = extLo + cy;
                    if (gy > maxYAny) maxYAny = gy;
                    if (v < 0 && gy > maxYC) maxYC = gy;
                }
            }

            // Fast path: write directly into Brick._data arrays (no per-voxel dict lookup)
            bool wroteViaBricks = _addBrick != null && _brickDataField != null &&
                WriteCacheDataToBricks(field!, ox, oz, width, extLo, bh, cacheData, _voxelizeColumnsSkipped);
            _voxelizeColumnsSkipped = false;

            if (!wroteViaBricks)
            {
                // Slow path: per-voxel VoxelField.Set via reflection delegate
                for (int ci = 0; ci < width; ci++)
                for (int ri = 0; ri < width; ri++)
                {
                    int gx = ox + ci, gz = oz + ri;
                    for (int gy = extLo; gy <= extHi; gy++)
                    {
                        int   cy      = gy - extLo;
                        sbyte newByte = cacheData[(ci * bh + cy) * width + ri];
                        sbyte curByte = _get!(field, gx, gy, gz);
                        if (newByte == curByte) continue;
                        _set!(field, gx, gy, gz, newByte);
                        carved++;
                    }
                }
            }

            if (!_parallelBakeActive) _sdfCache[zoneKey] = (extLo, bh, width, cacheData);

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
            } // end if (!bakeOnly)

            long _t3 = _sw.ElapsedMilliseconds;

            if (_dumpZonesRemaining > 0)
            {
                _dumpZonesRemaining--;
                DumpZoneData(ox, oz, overlap, s, wg, cw4Early, bw, bh, extLo, buf);
            }

            var _centerBiome = WorldGenerator.instance?.GetBiome((ox + width / 2) * (float)(_scale ?? 1f), (oz + width / 2) * (float)(_scale ?? 1f)) ?? Heightmap.Biome.None;
            Log.LogDebug($"[ProceduralTerrain] zone ({ox},{oz}) biome={_centerBiome} bakeC={bakeC} bakeA={bakeA} voxels={bw}x{bh}x{bw} fixHops={_lastFixpointHops} Ph0={_t0}ms Ph1={_t1-_t0}ms blur={_t1b-_t1}ms Ph2={_t2-_t1b}ms fix={_tFix}ms Ph3={_t3-_t2}ms total={_t3}ms carved={carved}");
        }

        // ── Direct brick write ────────────────────────────────────────────────────
        // Writes cacheData directly into VoxelField._bricks, bypassing per-voxel
        // VoxelField.Set() overhead. One dict lookup per brick (~80) instead of per
        // voxel (~327K). setFloor=true when VoxelizeColumns was skipped and the
        // field needs its floor rule initialized before bricks are added.
        static bool WriteCacheDataToBricks(object field, int ox, int oz, int width,
                                           int extLo, int bh, sbyte[] cacheData, bool setFloor)
        {
            if (_addBrick == null || _brickDataField == null || _brickType == null ||
                _vfMaxBrickYField == null) return false;

            if (setFloor)
            {
                if (_solidFloorCtor == null || _vfSetFloorMethod == null) return false;
                var floorRule = _solidFloorCtor.Invoke(
                    new object[] { extLo - 1, ox, ox + width - 1, oz, oz + width - 1 });
                _vfSetFloorMethod.Invoke(field, new object[] { floorRule });
            }

            int maxBrickY = (int)_vfMaxBrickYField.GetValue(field)!;

            int brickXMin = ox >> 4,          brickXMax = (ox + width - 1) >> 4;
            int brickYMin = extLo >> 4,       brickYMax = (extLo + bh - 1) >> 4;
            int brickZMin = oz >> 4,          brickZMax = (oz + width - 1) >> 4;

            for (int brickX = brickXMin; brickX <= brickXMax; brickX++)
            for (int brickY = brickYMin; brickY <= brickYMax; brickY++)
            for (int brickZ = brickZMin; brickZ <= brickZMax; brickZ++)
            {
                int vx0 = brickX << 4, vy0 = brickY << 4, vz0 = brickZ << 4;

                // Determine fill: below our range → solid (-127), otherwise air (+127)
                sbyte fill = (vy0 + 15 < extLo) ? (sbyte)-127 : (sbyte)127;
                var brick  = Activator.CreateInstance(_brickType, fill)!;
                var data   = new sbyte[4096];
                // Initialise data with fill
                if (fill != 0) { for (int i = 0; i < 4096; i++) data[i] = fill; }

                // Write the voxels that fall within our cacheData range
                for (int lx = 0; lx < 16; lx++)
                for (int lz = 0; lz < 16; lz++)
                {
                    int gx = vx0 + lx, gz = vz0 + lz;
                    if (gx < ox || gx >= ox + width || gz < oz || gz >= oz + width) continue;
                    int ci = gx - ox, ri = gz - oz;
                    for (int ly = 0; ly < 16; ly++)
                    {
                        int gy = vy0 + ly;
                        if (gy < extLo || gy >= extLo + bh) continue;
                        int cy = gy - extLo;
                        data[lx + 16 * (ly + 16 * lz)] = cacheData[(ci * bh + cy) * width + ri];
                    }
                }

                _brickDataField.SetValue(brick, data);
                _addBrick(field, brickX, brickY, brickZ, brick);
                if (brickY > maxBrickY) maxBrickY = brickY;
            }

            _vfMaxBrickYField.SetValue(field, maxBrickY);
            return true;
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
                Log.LogInfo($"[ProceduralTerrain] Dump written → {outPath}");
                Log.LogInfo($"[ProceduralTerrain] Dump → {outPath}");
            }
            catch (Exception ex) { Log.LogError($"[ProceduralTerrain] Dump failed: {ex.Message}"); }
        }

        // ── SDF bake helpers ─────────────────────────────────────────────────────

        static uint ComputeCfgHash()
        {
            string path = ConfigPath;
            if (!File.Exists(path)) return 0u;
            byte[] bytes = File.ReadAllBytes(path);
            uint hash = 2166136261u;
            foreach (byte b in bytes) hash = (hash ^ b) * 16777619u;
            return hash;
        }

        static string BakeFilePath(int seed, uint cfgHash) =>
            Path.Combine(BepInEx.Paths.ConfigPath, $"procedural_terrain_bake_{seed:X8}_{cfgHash:X8}.bin");

        static void LoadBakeFile()
        {
            var wg = WorldGenerator.instance;
            if (wg == null) { _bakeData = null; return; }
            int seed = wg.GetSeed();
            uint cfgHash = ComputeCfgHash();
            string path = BakeFilePath(seed, cfgHash);
            if (!File.Exists(path)) { _bakeData = null; return; }
            try
            {
                using var fs  = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                // Detect gzip by magic bytes (0x1f 0x8b) without consuming the stream.
                bool isGzip = fs.ReadByte() == 0x1f && fs.ReadByte() == 0x8b;
                fs.Seek(0, SeekOrigin.Begin);
                Stream inStream = isGzip ? new GZipStream(fs, CompressionMode.Decompress) : fs;
                using var br  = new BinaryReader(inStream);
                if (br.ReadUInt32() != 0x564F5846u) { Log.LogWarning("[ProceduralTerrain] bake: invalid magic."); _bakeData = null; return; }
                ushort ver = br.ReadUInt16();
                if (ver != 1 && ver != 2) { Log.LogWarning($"[ProceduralTerrain] bake: unsupported version {ver}."); _bakeData = null; return; }
                if (br.ReadUInt64() != (ulong)(uint)seed || br.ReadUInt32() != cfgHash)
                    { Log.LogWarning("[ProceduralTerrain] bake: seed/cfg mismatch."); _bakeData = null; return; }
                _bakeFileStep = br.ReadUInt16();
                int count = (int)br.ReadUInt32();
                var data = new Dictionary<(int, int), (int extLo, int bh, int bw, sbyte[] data)>(count);
                for (int i = 0; i < count; i++)
                {
                    int eox = br.ReadInt32(), eoz = br.ReadInt32(), extLo = br.ReadInt32();
                    int bh = br.ReadUInt16(), ebw = br.ReadUInt16();
                    int nsx = br.ReadInt32(), nsy = br.ReadInt32(), nsz = br.ReadInt32();
                    int len   = nsx * nsy * nsz;
                    var raw   = new sbyte[len];
                    var bytes = br.ReadBytes(len);
                    Buffer.BlockCopy(bytes, 0, raw, 0, len);
                    data[(eox, eoz)] = (extLo, bh, ebw, raw);
                }
                _bakeData = data;
                Log.LogInfo($"[ProceduralTerrain] Bake loaded: {count} zones, step={_bakeFileStep} from {path}");
            }
            catch (Exception ex) { Log.LogWarning($"[ProceduralTerrain] bake: load failed: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}"); _bakeData = null; }
        }

        static float CatmullRom(float p0, float p1, float p2, float p3, float t)
        {
            float t2 = t * t, t3 = t2 * t;
            return 0.5f * ((2*p1) + (-p0+p2)*t + (2*p0-5*p1+4*p2-p3)*t2 + (-p0+3*p1-3*p2+p3)*t3);
        }

        static bool TryPopulateFromBake(int ox, int oz, int bw, int bh, int extLo, float s, float[] buf)
        {
            if (_bakeData == null) { Log.LogInfo($"[ProceduralTerrain] bake miss ({ox},{oz}): no bake data loaded"); return false; }
            if (!_bakeData.TryGetValue((ox, oz), out var e)) { Log.LogInfo($"[ProceduralTerrain] bake miss ({ox},{oz}): zone not in file"); return false; }
            if (e.bw != bw) { Log.LogInfo($"[ProceduralTerrain] bake miss ({ox},{oz}): bw mismatch baked={e.bw} live={bw}"); return false; }
            int step = _bakeFileStep;
            int nsx  = (bw   + step - 1) / step;
            int nsy  = (e.bh + step - 1) / step;
            int nsz  = (bw   + step - 1) / step;
            sbyte[] raw = e.data;
            if (raw.Length != nsx * nsy * nsz) { Log.LogInfo($"[ProceduralTerrain] bake miss ({ox},{oz}): data length mismatch"); return false; }
            float scale = 32f * s / 127f;

            // Separable tricubic Catmull-Rom reconstruction: three axis passes through
            // temporary buffers. O(bw*bh*bw*4) reads vs O(bw*bh*bw*64) for naive 3D CR.
            // Pass 1: expand X — raw[xs,ys,zs] → temp1[cx,ys,zs]
            int t1Len = bw * nsy * nsz, t2Len = bw * bh * nsz;
            if (_crTemp1.Length < t1Len) _crTemp1 = new float[t1Len];
            if (_crTemp2.Length < t2Len) _crTemp2 = new float[t2Len];
            var temp1 = _crTemp1;
            var temp2 = _crTemp2;
            for (int ys = 0; ys < nsy; ys++)
            for (int zs = 0; zs < nsz; zs++)
            for (int cx = 0; cx < bw; cx++)
            {
                float xf = (float)cx / step;
                int xs = Math.Min((int)xf, nsx - 2); if (xs < 0) xs = 0;
                float tx = xf - xs;
                int xA = Math.Max(xs-1,0), xB=xs, xC=Math.Min(xs+1,nsx-1), xD=Math.Min(xs+2,nsx-1);
                temp1[(cx * nsy + ys) * nsz + zs] = CatmullRom(
                    raw[(xA*nsy+ys)*nsz+zs], raw[(xB*nsy+ys)*nsz+zs],
                    raw[(xC*nsy+ys)*nsz+zs], raw[(xD*nsy+ys)*nsz+zs], tx);
            }

            // Pass 2: expand Y — temp1[cx,ys,zs] → temp2[cx,cy,zs]
            for (int cx = 0; cx < bw; cx++)
            for (int zs = 0; zs < nsz; zs++)
            for (int cy = 0; cy < bh; cy++)
            {
                int bakCy = (extLo + cy) - e.extLo;
                if (bakCy < 0 || bakCy >= e.bh)
                {
                    temp2[(cx * bh + cy) * nsz + zs] = (bakCy < 0) ? -127f : 127f;
                    continue;
                }
                float yf = (float)bakCy / step;
                int ys = Math.Min((int)yf, nsy - 2); if (ys < 0) ys = 0;
                float ty = yf - ys;
                int yA=Math.Max(ys-1,0), yB=ys, yC=Math.Min(ys+1,nsy-1), yD=Math.Min(ys+2,nsy-1);
                temp2[(cx * bh + cy) * nsz + zs] = CatmullRom(
                    temp1[(cx*nsy+yA)*nsz+zs], temp1[(cx*nsy+yB)*nsz+zs],
                    temp1[(cx*nsy+yC)*nsz+zs], temp1[(cx*nsy+yD)*nsz+zs], ty);
            }

            // Pass 3: expand Z — temp2[cx,cy,zs] → buf[cx,cy,cz]
            for (int cx = 0; cx < bw; cx++)
            for (int cy = 0; cy < bh; cy++)
            for (int cz = 0; cz < bw; cz++)
            {
                float zf = (float)cz / step;
                int zs = Math.Min((int)zf, nsz - 2); if (zs < 0) zs = 0;
                float tz = zf - zs;
                int zA=Math.Max(zs-1,0), zB=zs, zC=Math.Min(zs+1,nsz-1), zD=Math.Min(zs+2,nsz-1);
                buf[(cx * bh + cy) * bw + cz] = CatmullRom(
                    temp2[(cx*bh+cy)*nsz+zA], temp2[(cx*bh+cy)*nsz+zB],
                    temp2[(cx*bh+cy)*nsz+zC], temp2[(cx*bh+cy)*nsz+zD], tz) * scale;
            }
            return true;
        }

        static void StageBakeEntry(int ox, int oz, int bw, int bh, int extLo, float s, float[] buf)
        {
            int step = _bakeStep;
            int nsx  = (bw + step - 1) / step;
            int nsy  = (bh + step - 1) / step;
            int nsz  = (bw + step - 1) / step;
            var raw  = new sbyte[nsx * nsy * nsz];
            for (int xs = 0; xs < nsx; xs++)
            for (int ys = 0; ys < nsy; ys++)
            for (int zs = 0; zs < nsz; zs++)
            {
                int cx = Math.Min(xs * step, bw - 1);
                int cy = Math.Min(ys * step, bh - 1);
                int cz = Math.Min(zs * step, bw - 1);
                raw[(xs * nsy + ys) * nsz + zs] = ToSDFByte(buf[(cx * bh + cy) * bw + cz], 32f * s);
            }
            _bakePending[(ox, oz)] = (extLo, bh, bw, raw);
        }

        static void WriteBakeFile(float radius)
        {
            var wg = WorldGenerator.instance;
            if (wg == null) { Log.LogWarning("[ProceduralTerrain] bake: WorldGenerator not ready."); return; }
            if (_scale == null) { Log.LogWarning("[ProceduralTerrain] bake: scale not resolved yet."); return; }
            float s = _scale.Value;
            int seed = wg.GetSeed();
            uint cfgHash = ComputeCfgHash();

            // Enumerate every zone origin within radius and ensure it's staged.
            const int zoneW = 64;
            int zoneR = radius > 0f ? (int)Math.Ceiling(radius / (zoneW * s)) : 0;

            // Valheim zone grid: zone at index 0 is centered at world origin,
            // so its XZ origin (lower-left corner) is at -zoneSize/2 = -32 world units.
            // In voxel coords: gridOffset = -(int)(32f / s), mod zoneW to get positive form.
            int gridOffset = ((-(int)(32f / s)) % zoneW + zoneW) % zoneW;
            Log.LogDebug($"[ProceduralTerrain] bake: zone grid offset={gridOffset}");

            // Discard all cached bake data so every zone gets re-carved fresh.
            _bakePending.Clear();
            _bakeData = null;

            // Collect zones to carve (not already loaded from file at current step).
            var toCarve = new List<(int ox, int oz)>();
            for (int dox = -zoneR; dox <= zoneR; dox++)
            for (int doz = -zoneR; doz <= zoneR; doz++)
            {
                int ox = dox * zoneW + gridOffset;
                int oz = doz * zoneW + gridOffset;
                float wx = ox * s, wz = oz * s;
                if (radius > 0f && wx * wx + wz * wz > radius * radius) continue;
                if (_bakeData != null && _bakeFileStep == _bakeStep && _bakeData.ContainsKey((ox, oz))) continue;
                toCarve.Add((ox, oz));
            }
            int threads = Math.Max(1, Environment.ProcessorCount - 1);
            Log.LogInfo($"[ProceduralTerrain] bake: {toCarve.Count} zones to carve (radius={radius:F0}m, threads={threads})...");
            var bakeSw = System.Diagnostics.Stopwatch.StartNew();
            int doneCount = 0;
            _parallelBakeActive = true;
            try
            {
                System.Threading.Tasks.Parallel.ForEach(toCarve,
                    new System.Threading.Tasks.ParallelOptions { MaxDegreeOfParallelism = threads },
                    zone =>
                    {
                        var (zox, zoz) = zone;
                        var (yLo, yHi) = ComputeYBounds(zox, zoz, zoneW, s, wg);
                        Carve(null, null, zox, zoz, zoneW, yLo, yHi);
                        int n = System.Threading.Interlocked.Increment(ref doneCount);
                        if (n % 50 == 0 || n == toCarve.Count)
                        {
                            float elapsed = bakeSw.ElapsedMilliseconds / 1000f;
                            float eta = elapsed > 0f ? (toCarve.Count - n) / (n / elapsed) : 0f;
                            Log.LogInfo($"[ProceduralTerrain] bake: {n}/{toCarve.Count} zones — {eta:F0}s remaining");
                        }
                    });
            }
            finally { _parallelBakeActive = false; }

            // Merge staged + existing bake data (staged takes priority).
            var toWrite = new Dictionary<(int, int), (int extLo, int bh, int bw, sbyte[] data)>();
            if (_bakeData != null)
                foreach (var kv in _bakeData) toWrite[kv.Key] = kv.Value;
            foreach (var kv in _bakePending)
                toWrite[kv.Key] = kv.Value;

            string path = BakeFilePath(seed, cfgHash);
            using var fs  = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
            Stream outStream = _compressBake ? new GZipStream(fs, System.IO.Compression.CompressionLevel.Optimal) : fs;
            using var bw2 = new BinaryWriter(outStream);
            bw2.Write(0x564F5846u);          // magic
            bw2.Write((ushort)(_compressBake ? 2 : 1)); // version: 1=raw, 2=gzip
            bw2.Write((ulong)(uint)seed);
            bw2.Write(cfgHash);
            bw2.Write((ushort)_bakeStep);
            // Only write entries whose raw data is consistent with the current step.
            // Stale entries (different step) are dropped so the file stays self-consistent.
            var validEntries = new List<((int, int) key, (int extLo, int bh, int bw, sbyte[] data) entry)>();
            foreach (var kv in toWrite)
            {
                var (extLo, bh, ebw, raw) = kv.Value;
                int nsx = (ebw + _bakeStep - 1) / _bakeStep;
                int nsy = (bh  + _bakeStep - 1) / _bakeStep;
                int nsz = (ebw + _bakeStep - 1) / _bakeStep;
                if (raw.Length != nsx * nsy * nsz)
                {
                    Log.LogInfo($"[ProceduralTerrain] bake: dropping zone {kv.Key} — raw size {raw.Length} != expected {nsx*nsy*nsz} (stale step?)");
                    continue;
                }
                validEntries.Add((kv.Key, kv.Value));
            }
            bw2.Write((uint)validEntries.Count);
            foreach (var (key, entry) in validEntries)
            {
                var (eox, eoz) = key;
                var (extLo, bh, ebw, raw) = entry;
                int nsx = (ebw + _bakeStep - 1) / _bakeStep;
                int nsy = (bh  + _bakeStep - 1) / _bakeStep;
                int nsz = (ebw + _bakeStep - 1) / _bakeStep;
                bw2.Write(eox); bw2.Write(eoz); bw2.Write(extLo);
                bw2.Write((ushort)bh); bw2.Write((ushort)ebw);
                bw2.Write(nsx); bw2.Write(nsy); bw2.Write(nsz);
                var bytes = new byte[raw.Length];
                Buffer.BlockCopy(raw, 0, bytes, 0, raw.Length);
                bw2.Write(bytes);
            }
            _bakeData = toWrite;
            _bakeFileStep = _bakeStep;
            _bakePending.Clear();
            Log.LogInfo($"[ProceduralTerrain] Bake written: {validEntries.Count} zones (dropped {toWrite.Count - validEntries.Count} stale), radius={radius:F0}m, step={_bakeStep} → {path}");
        }

        // ── Hot-reload: re-run the full voxel pipeline on every loaded zone ───────
        internal static void ResetWorldState()
        {
            LoadBiomeConfig();
            _bakePending.Clear();
            _bakeData = null;
            _bakeLoadAttempted = false;
            _zoneObjects.Clear();
            _zoneParams.Clear();
            _zoneMaxY.Clear();
            _zoneExtHi.Clear();
            _sdfCache.Clear();
            _heightCache.Clear();
            lock (_surfHCache) _surfHCache.Clear();
            _zoneFaceCache.Clear();
            _locationPositions.Clear();
            _locationsCached = false;
            _caveNoise       = null;
            _caveNoise2      = null;
            _warpX           = null;
            _warpY           = null;
            _warpZ           = null;
            _warpX2          = null;
            _warpY2          = null;
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
            Log.LogInfo($"[ProceduralTerrain] Hot-reload: re-voxelizing {_zoneParams.Count} zone(s).");
            // Clear all config-derived and geometry-derived caches so the re-voxelization
            // starts from a clean slate. _zoneParams and _zoneObjects are kept because
            // they are the iteration target — the zone objects and geometry metadata
            // (origin, width, yLo/yHi) are world-geometry facts, not profile-dependent.
            _sdfCache.Clear();
            _heightCache.Clear();
            lock (_surfHCache) _surfHCache.Clear();
            _zoneMaxY.Clear();
            _zoneExtHi.Clear();
            _zoneFaceCache.Clear();
            if (_voxelizeColumnsMethod == null) return;
            bool canRemesh = _enqueueMeshMethod != null && _finishSkinNowMethod != null && _int3Ctor != null;
            if (!canRemesh)
                Log.LogWarning("[ProceduralTerrain] EnqueueMesh/FinishSkinNow unavailable — mesh rebuild skipped.");

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
                catch (Exception e) { Log.LogWarning($"[ProceduralTerrain] Revoxelize {key}: {e.Message}"); }
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
                    catch (Exception e) { Log.LogWarning($"[ProceduralTerrain] EnqueueMesh {key}: {e.Message}"); break; }
                }
                Log.LogDebug($"[ProceduralTerrain] Queued {queued} bricks for zone {key}.");

                try { _finishSkinNowMethod!.Invoke(zoneObj, null); }
                catch (Exception e) { Log.LogWarning($"[ProceduralTerrain] FinishSkinNow {key}: {e.Message}"); }
            }
        }

        // ── Placement height ──────────────────────────────────────────────────────
        // Returns the world-Y of the highest solid surface at (worldX, worldZ),
        // or originalY if no cached surface exists or if it is lower than originalY.
        internal static float FindCarvedSurfaceY(float worldX, float originalY, float worldZ)
        {
            float s = _scale is { } v && v > 0f ? v
                    : _scaleField != null && _scaleField.GetValue(null) is float sf && sf > 0f ? sf
                    : 1f;
            const int overlap = 4, width = 64;
            int gx = Mathf.RoundToInt(worldX / s);
            int gz = Mathf.RoundToInt(worldZ / s);
            // Zone origin (in voxel coords) — same formula as Carve() line that derives ox.
            int ox = gx - ((gx % width + width) % width);
            int oz = gz - ((gz % width + width) % width);
            (int bw, int extLo, float zs, float[] surfH) entry;
            lock (_surfHCache)
                if (!_surfHCache.TryGetValue((ox, oz), out entry)) return originalY;
            int cx = Mathf.Clamp(gx - (ox - overlap), 0, entry.bw - 1);
            int cz = Mathf.Clamp(gz - (oz - overlap), 0, entry.bw - 1);
            float sh = entry.surfH[cx * entry.bw + cz];
            return float.IsNaN(sh) ? originalY : Mathf.Max(originalY, sh);
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
            if (_bk2dMcS     == null || _bk2dMcS.Length     < nb2Size) _bk2dMcS     = new float[nb2Size];
            if (_bk2dMcT     == null || _bk2dMcT.Length     < nb2Size) _bk2dMcT     = new float[nb2Size];
            if (_bk2dMcFlare == null || _bk2dMcFlare.Length < nb2Size) _bk2dMcFlare = new float[nb2Size];
            if (_bk2dFlD    == null || _bk2dFlD.Length    < nb2Size) _bk2dFlD    = new float[nb2Size];
            if (_bk2dFlFade == null || _bk2dFlFade.Length < nb2Size) _bk2dFlFade = new float[nb2Size];
            if (_bk2dMbwf  == null || _bk2dMbwf.Length  < nb2Size) _bk2dMbwf  = new float[nb2Size];
            if (_bk2dMbwa  == null || _bk2dMbwa.Length  < nb2Size) _bk2dMbwa  = new float[nb2Size];
            if (_bk2dMbr   == null || _bk2dMbr.Length   < nb2Size) _bk2dMbr   = new float[nb2Size];
            if (_bk2dMbvs  == null || _bk2dMbvs.Length  < nb2Size) _bk2dMbvs  = new float[nb2Size];
            if (_bk2dMbna  == null || _bk2dMbna.Length  < nb2Size) _bk2dMbna  = new float[nb2Size];
            if (_bk2dMbyo    == null || _bk2dMbyo.Length    < nb2Size) _bk2dMbyo    = new float[nb2Size];
            if (_bk2dMbtwist == null || _bk2dMbtwist.Length < nb2Size) _bk2dMbtwist = new float[nb2Size];
            if (_bk2dMawf  == null || _bk2dMawf.Length  < nb2Size) _bk2dMawf  = new float[nb2Size];
            if (_bk2dMawa  == null || _bk2dMawa.Length  < nb2Size) _bk2dMawa  = new float[nb2Size];
            if (_bk2dMaway == null || _bk2dMaway.Length < nb2Size) _bk2dMaway = new float[nb2Size];
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

                _bk2dMcS!    [idx2] = p4.modeC_noiseAmp;
                _bk2dMcT!    [idx2] = p4.modeC_threshold;
                _bk2dMcFlare![idx2] = p4.modeC_flare;
                _bk2dFlD!   [idx2] = p4.floorDepth;
                _bk2dFlFade![idx2] = p4.floorFade;
                _bk2dMbwf![idx2] = p4.modeB_warpFrequency;
                _bk2dMbwa![idx2] = p4.modeB_warpAmp;
                _bk2dMbr! [idx2] = p4.modeB_radius;
                _bk2dMbvs![idx2] = p4.modeB_vScale;
                _bk2dMbna![idx2] = p4.modeB_noiseAmp;
                _bk2dMbyo![idx2]    = p4.modeB_yOffset;
                _bk2dMbtwist![idx2] = p4.modeB_twist;
                _bk2dMawf![idx2] = p4.modeA_warpFrequency;
                _bk2dMawa![idx2] = p4.modeA_warpAmp;
                _bk2dMaway![idx2] = p4.modeA_warpAmpY;
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

        static (int yLo, int yHi) ComputeYBounds(int ox, int oz, int w, float s, WorldGenerator wg)
        {
            // Include the overlap region — same buffer footprint Voxheim scans for yLo/yHi.
            const int overlap = 4;
            float minH = float.MaxValue, maxH = float.MinValue;
            for (int gx = -overlap; gx <= w + overlap; gx++)
            for (int gz = -overlap; gz <= w + overlap; gz++)
            {
                float h = wg.GetHeight((ox + gx) * s, (oz + gz) * s) / s;
                if (h < minH) minH = h;
                if (h > maxH) maxH = h;
            }
            return ((int)Math.Floor(minH) - 2, (int)Math.Ceiling(maxH) + 2);
        }

        // On-demand fixpoint: follows percolation chains seeded by the faces where the
        // pre-pass BFS found ungrounded solid (ungNX/PX/NZ/PZ).
        // Only hops into a neighbor zone if the specific ungrounded blob at the shared face
        // percolates through that zone to another face (via percol[inFace, toFace]).
        // Unrelated ungrounded blobs in the neighbor do NOT trigger further expansion.
        static int _lastFixpointHops = 0; // set by EnsureFixpoint, read by Carve's final log

        // Face indices used by percol and hop logic: 0=NegX 1=PosX 2=NegZ 3=PosZ
        static readonly (int dx, int dz, int inFace)[] FaceHops = {
            (-1,  0, 1), // our NegX → neighbor's PosX
            ( 1,  0, 0), // our PosX → neighbor's NegX
            ( 0, -1, 3), // our NegZ → neighbor's PosZ
            ( 0,  1, 2), // our PosZ → neighbor's NegZ
        };

        static void EnsureFixpoint(int ox, int oz, int w,
                                   bool ungNX, bool ungPX, bool ungNZ, bool ungPZ)
        {
            float s  = _scale ?? 1f;
            var   wg = WorldGenerator.instance;
            if (wg == null) return;
            int FixpointMaxZones = _fixpointMaxZones;

            // Worklist items: (zone origin, incoming face index into that zone)
            var worklist = new Queue<(int ox, int oz, int inFace)>();
            var seen     = new HashSet<(int, int)> { (ox, oz) };
            int computed = 0;
            _lastFixpointHops = 0;

            bool[] ungFlags = { ungNX, ungPX, ungNZ, ungPZ };
            for (int f = 0; f < 4; f++)
            {
                if (!ungFlags[f]) continue;
                var (dx, dz, inF) = FaceHops[f];
                int nox = ox + dx * w, noz = oz + dz * w;
                if (seen.Add((nox, noz))) worklist.Enqueue((nox, noz, inF));
            }

            while (worklist.Count > 0)
            {
                if (computed >= FixpointMaxZones)
                {
                    Log.LogWarning($"[ProceduralTerrain] EnsureFixpoint ({ox},{oz}): hit cap ({FixpointMaxZones}), {worklist.Count} zones skipped");
                    break;
                }

                var (zox, zoz, inFace) = worklist.Dequeue();

                if (!_zoneFaceCache.TryGetValue((zox, zoz), out var existingNc) || !existingNc.percolValid)
                {
                    var (zyLo, zyHi) = ComputeYBounds(zox, zoz, w, s, wg);
                    ComputeZoneFaceCache(zox, zoz, w, zyLo, zyHi);
                    computed++;
                }

                if (!_zoneFaceCache.TryGetValue((zox, zoz), out var nc)) continue;

                // Expand only along faces the AB blob actually percolates to
                for (int toFace = 0; toFace < 4; toFace++)
                {
                    if (toFace == inFace) continue;
                    if (!nc.percol[inFace, toFace]) continue;
                    var (dx, dz, neighborInFace) = FaceHops[toFace];
                    int nnox = zox + dx * w, nnoz = zoz + dz * w;
                    if (seen.Add((nnox, nnoz))) worklist.Enqueue((nnox, nnoz, neighborInFace));
                }
            }

            _lastFixpointHops = computed;
            if (computed > 0)
                Log.LogInfo($"[ProceduralTerrain] EnsureFixpoint ({ox},{oz}): {computed} neighbor zone(s) computed");
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

            // Percolation BFSes: for each territory face, BFS through ungrounded solid within
            // the territory to discover which other faces the blob reaches.
            // _visitedBuf still holds grounding result here (cleared for air BFS below).
            // Face indices: 0=NegX 1=PosX 2=NegZ 3=PosZ
            var percol = new bool[4, 4];
            if (_percolVisited == null || _percolVisited.Length < bufSz2) _percolVisited = new bool[bufSz2];

            for (int fromFace = 0; fromFace < 4; fromFace++)
            {
                Array.Clear(_percolVisited, 0, bufSz2);
                qHead = 0; qTail = 0;
                bool hitNX = false, hitPX = false, hitNZ = false, hitPZ = false;
                int facesLeft = 3; // at most 3 other faces to hit

                // Seed: ungrounded solid at the source territory face
                if (fromFace <= 1) // X faces: iterate (cy, cz∈[terNegZ,terPosZ])
                {
                    int fCx = fromFace == 0 ? terNegX : terPosX;
                    for (int cy = 0; cy < bhZ; cy++)
                    for (int cz = terNegZ; cz <= terPosZ; cz++)
                    {
                        int idx = fCx * stX + cy * bwZ + cz;
                        if (buf[idx] < 0f && !_visitedBuf[idx] && !_percolVisited[idx])
                        { _percolVisited[idx] = true; _islandQueue![qTail++] = idx; }
                    }
                }
                else // Z faces: iterate (cy, cx∈[terNegX,terPosX])
                {
                    int fCz = fromFace == 2 ? terNegZ : terPosZ;
                    for (int cy = 0; cy < bhZ; cy++)
                    for (int cx = terNegX; cx <= terPosX; cx++)
                    {
                        int idx = cx * stX + cy * bwZ + fCz;
                        if (buf[idx] < 0f && !_visitedBuf[idx] && !_percolVisited[idx])
                        { _percolVisited[idx] = true; _islandQueue![qTail++] = idx; }
                    }
                }

                while (qHead < qTail && facesLeft > 0)
                {
                    int idx = _islandQueue![qHead++];
                    int pcx = idx / stX;
                    int rem = idx % stX;
                    int pcy = rem / bwZ;
                    int pcz = rem % bwZ;

                    if (pcx == terNegX && fromFace != 0 && !hitNX) { hitNX = true; facesLeft--; }
                    if (pcx == terPosX && fromFace != 1 && !hitPX) { hitPX = true; facesLeft--; }
                    if (pcz == terNegZ && fromFace != 2 && !hitNZ) { hitNZ = true; facesLeft--; }
                    if (pcz == terPosZ && fromFace != 3 && !hitPZ) { hitPZ = true; facesLeft--; }

                    int ni;
                    // Propagate through ungrounded solid, contained within territory
                    if (pcx > terNegX) { ni = idx - stX; if (!_percolVisited[ni] && buf[ni] < 0f && !_visitedBuf[ni]) { _percolVisited[ni] = true; _islandQueue[qTail++] = ni; } }
                    if (pcx < terPosX) { ni = idx + stX; if (!_percolVisited[ni] && buf[ni] < 0f && !_visitedBuf[ni]) { _percolVisited[ni] = true; _islandQueue[qTail++] = ni; } }
                    if (pcy > 0)       { ni = idx - bwZ; if (!_percolVisited[ni] && buf[ni] < 0f && !_visitedBuf[ni]) { _percolVisited[ni] = true; _islandQueue[qTail++] = ni; } }
                    if (pcy < bhZ-1)   { ni = idx + bwZ; if (!_percolVisited[ni] && buf[ni] < 0f && !_visitedBuf[ni]) { _percolVisited[ni] = true; _islandQueue[qTail++] = ni; } }
                    if (pcz > terNegZ) { ni = idx - 1;   if (!_percolVisited[ni] && buf[ni] < 0f && !_visitedBuf[ni]) { _percolVisited[ni] = true; _islandQueue[qTail++] = ni; } }
                    if (pcz < terPosZ) { ni = idx + 1;   if (!_percolVisited[ni] && buf[ni] < 0f && !_visitedBuf[ni]) { _percolVisited[ni] = true; _islandQueue[qTail++] = ni; } }
                }

                percol[fromFace, 0] = hitNX;
                percol[fromFace, 1] = hitPX;
                percol[fromFace, 2] = hitNZ;
                percol[fromFace, 3] = hitPZ;
            }
            cache.percol      = percol;
            cache.percolValid = true;

            _zoneFaceCache[(ox, oz)] = cache;
        }

        static void FillYColumn(float[] buf, int bw, int bh, int extLo, float s, int cx, int cz,
            float h, float addH, float profMcT, float profMcFlare, float localAmp, bool mcActive,
            float locFac, float floorY, float floorFade,
            float mbWarpedH, float mbR, float mbNA, float mbVS, float mbWF, float mbTwist,
            int bb00, int bb10, int bb01, int bb11, float bfx, float bfz,
            float mAwf, float mAwa, float mAway, float mAf, float mAvs, float mAw, float mAwv, float mAwvf,
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
                    float _ft = Mathf.Clamp01(relY / localAmp);
                    float effMcT = profMcT - profMcFlare * (4f * _ft * (1f - _ft));
                    float sdf = relY - (n - effMcT) * localAmp;
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
                        float wy  = _warpY! .GetNoise(worldX * mAwf, sy * mAwf, worldZ * mAwf) * mAway;
                        float wx2 = _warpX2!.GetNoise(worldX * mAwf, sy * mAwf, worldZ * mAwf) * mAwa;
                        float wz2 = _warpZ2!.GetNoise(worldX * mAwf, sy * mAwf, worldZ * mAwf) * mAwa;
                        float wy2 = _warpY2!.GetNoise(worldX * mAwf, sy * mAwf, worldZ * mAwf) * mAway;
                        na = _caveNoise! .GetNoise((worldX + wx)  * mAf, (sy + wy)  * mAf, (worldZ + wz)  * mAf);
                        nb = _caveNoise2!.GetNoise((worldX + wx2) * mAf, (sy + wy2) * mAf, (worldZ + wz2) * mAf);
                        wn = _widthNoise!.GetNoise(worldX * mAwvf, (worldY * mAvs) * mAwvf, worldZ * mAwvf);
                    }
                    float localWidth = Mathf.Max(mAw * (1f + mAwv * wn), 0.001f);
                    // L2 distance of two independent noise fields: intersection of two zero-crossing
                    // sheets = cylindrical tube network. Normalized by 1/sqrt(2) to keep dist in ~[0,1].
                    float dist      = Mathf.Sqrt(na * na + nb * nb) * 0.7071f;
                    float tunnelSDF = (localWidth - dist) / localWidth * 4f * s;
                    float sdf = Mathf.Min(tunnelSDF, worldY - floorY);
                    combined = SmoothMax(combined, sdf, 2f * s);
                }

                float _fade = floorFade > 0f ? Mathf.Clamp01((worldY - floorY) / floorFade) : 1f;
                buf[(cx * bh + cy) * bw + cz] = baseSDF + (combined - baseSDF) * locFac * _fade;
            }

            // Segment 2: inside Mode B range — B carves first, then C fills back in, then A punches through.
            for (int cy = cyMbLo; cy <= cyMbHi; cy++)
            {
                float worldY   = (extLo + cy) * s;
                float baseSDF  = worldY - h;
                float combined = baseSDF;

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

                if (mcActive)
                {
                    float relY = worldY - addH;
                    float n;
                    {
                        float n0 = _noiseBuf![bb00+cy] + (_noiseBuf[bb10+cy] - _noiseBuf![bb00+cy]) * bfx;
                        float n1 = _noiseBuf![bb01+cy] + (_noiseBuf[bb11+cy] - _noiseBuf![bb01+cy]) * bfx;
                        n = n0 + (n1-n0) * bfz;
                    }
                    float _ft = Mathf.Clamp01(relY / localAmp);
                    float effMcT = profMcT - profMcFlare * (4f * _ft * (1f - _ft));
                    float sdf = relY - (n - effMcT) * localAmp;
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
                        float wy  = _warpY! .GetNoise(worldX * mAwf, sy * mAwf, worldZ * mAwf) * mAway;
                        float wx2 = _warpX2!.GetNoise(worldX * mAwf, sy * mAwf, worldZ * mAwf) * mAwa;
                        float wz2 = _warpZ2!.GetNoise(worldX * mAwf, sy * mAwf, worldZ * mAwf) * mAwa;
                        float wy2 = _warpY2!.GetNoise(worldX * mAwf, sy * mAwf, worldZ * mAwf) * mAway;
                        na = _caveNoise! .GetNoise((worldX + wx)  * mAf, (sy + wy)  * mAf, (worldZ + wz)  * mAf);
                        nb = _caveNoise2!.GetNoise((worldX + wx2) * mAf, (sy + wy2) * mAf, (worldZ + wz2) * mAf);
                        wn = _widthNoise!.GetNoise(worldX * mAwvf, (worldY * mAvs) * mAwvf, worldZ * mAwvf);
                    }
                    float localWidth = Mathf.Max(mAw * (1f + mAwv * wn), 0.001f);
                    // L2 distance of two independent noise fields: intersection of two zero-crossing
                    // sheets = cylindrical tube network. Normalized by 1/sqrt(2) to keep dist in ~[0,1].
                    float dist      = Mathf.Sqrt(na * na + nb * nb) * 0.7071f;
                    float tunnelSDF = (localWidth - dist) / localWidth * 4f * s;
                    float sdf = Mathf.Min(tunnelSDF, worldY - floorY);
                    combined = SmoothMax(combined, sdf, 2f * s);
                }

                float _fade = floorFade > 0f ? Mathf.Clamp01((worldY - floorY) / floorFade) : 1f;
                buf[(cx * bh + cy) * bw + cz] = baseSDF + (combined - baseSDF) * locFac * _fade;
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
                    float _ft = Mathf.Clamp01(relY / localAmp);
                    float effMcT = profMcT - profMcFlare * (4f * _ft * (1f - _ft));
                    float sdf = relY - (n - effMcT) * localAmp;
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
                        float wy  = _warpY! .GetNoise(worldX * mAwf, sy * mAwf, worldZ * mAwf) * mAway;
                        float wx2 = _warpX2!.GetNoise(worldX * mAwf, sy * mAwf, worldZ * mAwf) * mAwa;
                        float wz2 = _warpZ2!.GetNoise(worldX * mAwf, sy * mAwf, worldZ * mAwf) * mAwa;
                        float wy2 = _warpY2!.GetNoise(worldX * mAwf, sy * mAwf, worldZ * mAwf) * mAway;
                        na = _caveNoise! .GetNoise((worldX + wx)  * mAf, (sy + wy)  * mAf, (worldZ + wz)  * mAf);
                        nb = _caveNoise2!.GetNoise((worldX + wx2) * mAf, (sy + wy2) * mAf, (worldZ + wz2) * mAf);
                        wn = _widthNoise!.GetNoise(worldX * mAwvf, (worldY * mAvs) * mAwvf, worldZ * mAwvf);
                    }
                    float localWidth = Mathf.Max(mAw * (1f + mAwv * wn), 0.001f);
                    // L2 distance of two independent noise fields: intersection of two zero-crossing
                    // sheets = cylindrical tube network. Normalized by 1/sqrt(2) to keep dist in ~[0,1].
                    float dist      = Mathf.Sqrt(na * na + nb * nb) * 0.7071f;
                    float tunnelSDF = (localWidth - dist) / localWidth * 4f * s;
                    float sdf = Mathf.Min(tunnelSDF, worldY - floorY);
                    combined = SmoothMax(combined, sdf, 2f * s);
                }

                float _fade = floorFade > 0f ? Mathf.Clamp01((worldY - floorY) / floorFade) : 1f;
                buf[(cx * bh + cy) * bw + cz] = baseSDF + (combined - baseSDF) * locFac * _fade;
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

                float profMcT    = BL(_bk2dMcT!,     b200, b210, b201, b211, bfx, bfz);
                float profMcFlare= BL(_bk2dMcFlare!, b200, b210, b201, b211, bfx, bfz);
                float profFlD    = BL(_bk2dFlD!,    b200, b210, b201, b211, bfx, bfz);
                float profFlFade = BL(_bk2dFlFade!, b200, b210, b201, b211, bfx, bfz);
                float addH     = bakeC ? BL(_bk2dAddH!, b200, b210, b201, b211, bfx, bfz) : 0f;
                float localAmp = bakeC ? BL(_bk2dLAmp!, b200, b210, b201, b211, bfx, bfz) : 0f;
                float h;
                if (wg == null) { h = 0f; }
                else
                {
                    long hKey = (long)gx << 32 | (uint)gz;
                    if (!_heightCache.TryGetValue(hKey, out h))
                    {
                        h = wg.GetHeight(worldX, worldZ);
                        if (!_parallelBakeActive) _heightCache[hKey] = h;
                    }
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

                float mAwf=0f, mAwa=0f, mAway=0f, mAf=0f, mAvs=0f, mAw=0f, mAwv=0f, mAwvf=0f;
                if (bakeA)
                {
                    mAwf  = BL(_bk2dMawf!,  b200, b210, b201, b211, bfx, bfz);
                    mAwa  = BL(_bk2dMawa!,  b200, b210, b201, b211, bfx, bfz);
                    mAway = BL(_bk2dMaway!, b200, b210, b201, b211, bfx, bfz);
                    mAf   = BL(_bk2dMaf!,   b200, b210, b201, b211, bfx, bfz);
                    mAvs  = BL(_bk2dMavs!,  b200, b210, b201, b211, bfx, bfz);
                    mAw   = BL(_bk2dMaw!,   b200, b210, b201, b211, bfx, bfz);
                    mAwv  = BL(_bk2dMawv!,  b200, b210, b201, b211, bfx, bfz);
                    mAwvf = BL(_bk2dMawvf!, b200, b210, b201, b211, bfx, bfz);
                }

                long _tb = System.Diagnostics.Stopwatch.GetTimestamp();
                _setupTicks += _tb - _ta;
                FillYColumn(buf, bw, bh, extLo, s, cx, cz,
                    h, addH, profMcT, profMcFlare, localAmp, localAmp > 0f && bakeC,
                    locFac, floorY, profFlFade,
                    mbWarpedH, mbR, mbNA, mbVS, mbWF, mbTwist,
                    bb00, bb10, bb01, bb11, bfx, bfz,
                    mAwf, mAwa, mAway, mAf, mAvs, mAw, mAwv, mAwvf,
                    worldX, worldZ, bakeC, bakeA);
                _yloopTicks += System.Diagnostics.Stopwatch.GetTimestamp() - _tb;
            }
            Log.LogInfo($"[ProceduralTerrain] Ph1 breakdown: setup={_setupTicks*1000/_freq}ms yloop={_yloopTicks*1000/_freq}ms");
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
            if (m == null) Plugin.Log.LogError("[ProceduralTerrain] VoxelizeColumns not found.");
            return m;
        }

        // Skip VoxelizeColumns entirely for bake-hit zones: saves ~5ms of wasted
        // vanilla SDF computation; Phase 3 will set the floor rule and create bricks.
        static bool Prefix(object field, object grid, int originX, int originZ,
                            int gx0, int gx1, int gz0, int gz1, int yLo, int yHi)
        {
            if (gx0 != 0 || gz0 != 0) return true;
            if (Plugin._addBrick == null) return true;
            if (Plugin._bakeData == null || !Plugin._bakeData.ContainsKey((originX, originZ))) return true;
            Plugin._voxelizeColumnsSkipped = true;
            return false; // skip VoxelizeColumns
        }

        static void Postfix(object field, object grid, int originX, int originZ,
                            int gx0, int gx1, int gz0, int gz1, int yLo, int yHi)
        {
            if (gx0 != 0 || gz0 != 0) return;
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            try { Plugin.Carve(field, grid, originX, originZ, gx1 + 1, yLo, yHi); }
            catch (Exception e) { Plugin.Log.LogError($"[ProceduralTerrain] Carve: {e.Message}"); }
            long ms = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000L / System.Diagnostics.Stopwatch.Frequency;
            Plugin.Log.LogInfo($"[ProceduralTerrain] postfix ({originX},{originZ}) wall={ms}ms");
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
            catch (Exception ex) { Plugin.Log.LogWarning($"[ProceduralTerrain] PokeNeighbors threw: {ex.Message}"); }
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
            lock (Plugin._surfHCache) Plugin._surfHCache.Remove((ox, oz));
        }
    }

    // After Voxheim's authority system installs a snapshot or delta, re-establish caves:
    //   1. Write our SDF into the field (replaces vanilla baseline in the authority data)
    //   2. Re-apply the authority bricks on top (player edits win over caves)
    // For delta: removed bricks are already gone (floor-rule default), so our SDF fills them.
    //            upserted bricks are re-applied after our SDF.
    static class AuthorityInstallHelper
    {
        // Cached reflection for VoxelSnapshotBrick — resolved on first use
        static PropertyInfo? _brickCoordProp;
        static PropertyInfo? _brickDensProp;

        static void EnsureBrickProps(object brick)
        {
            if (_brickCoordProp != null) return;
            var t = brick.GetType();
            _brickCoordProp = t.GetProperty("Coordinate");
            _brickDensProp  = t.GetProperty("Densities");
        }

        // Write our cached SDF (or re-run Carve if cache is cold) into field.
        static void WriteSDF(object field, int ox, int oz)
        {
            if (Plugin._get == null || Plugin._set == null) return;
            string zoneKey = $"{ox},{oz}";
            if (Plugin._sdfCache.TryGetValue(zoneKey, out var c))
            {
                var (extLo, bh, width, cacheData) = c;
                for (int ci = 0; ci < width; ci++)
                for (int ri = 0; ri < width; ri++)
                {
                    int gx = ox + ci, gz = oz + ri;
                    for (int cy = 0; cy < bh; cy++)
                    {
                        sbyte v = cacheData[(ci * bh + cy) * width + ri];
                        if (Plugin._get(field, gx, extLo + cy, gz) != v)
                            Plugin._set(field, gx, extLo + cy, gz, v);
                    }
                }
            }
            else if (Plugin._zoneParams.TryGetValue(zoneKey, out var zp))
            {
                Plugin._zoneParams[zoneKey] = (field, zp.grid, zp.ox, zp.oz, zp.w, zp.yLo, zp.yHi);
                Plugin.Carve(field, zp.grid, zp.ox, zp.oz, zp.w, zp.yLo, zp.yHi);
            }
        }

        // Write authority brick densities back into field so player edits win over our SDF.
        static void WriteBricks(object field, System.Collections.IEnumerable bricks)
        {
            if (Plugin._set == null || Plugin._int3FieldX == null ||
                Plugin._int3FieldY == null || Plugin._int3FieldZ == null) return;
            foreach (object brick in bricks)
            {
                EnsureBrickProps(brick);
                object? coord     = _brickCoordProp?.GetValue(brick);
                var     densities = _brickDensProp?.GetValue(brick) as IReadOnlyList<sbyte>;
                if (coord == null || densities == null) continue;
                int bx = (int)Plugin._int3FieldX.GetValue(coord)! << 4;
                int by = (int)Plugin._int3FieldY!.GetValue(coord)! << 4;
                int bz = (int)Plugin._int3FieldZ!.GetValue(coord)! << 4;
                for (int x = 0; x < 16; x++)
                for (int y = 0; y < 16; y++)
                for (int z = 0; z < 16; z++)
                    Plugin._set(field, bx + x, by + y, bz + z, densities[x + 16 * (y + 16 * z)]);
            }
        }

        static (object? field, int ox, int oz) GetZoneState(object zoneInstance)
        {
            if (Plugin._zoneOriginX == null || Plugin._zoneOriginZ == null ||
                Plugin._voxelZoneFieldField == null) return (null, 0, 0);
            int ox = (int)Plugin._zoneOriginX.GetValue(zoneInstance)!;
            int oz = (int)Plugin._zoneOriginZ.GetValue(zoneInstance)!;
            return (Plugin._voxelZoneFieldField.GetValue(zoneInstance), ox, oz);
        }

        internal static void OnSnapshot(object zoneInstance, object? snapshotObj)
        {
            var (field, ox, oz) = GetZoneState(zoneInstance);
            if (field == null) return;
            WriteSDF(field, ox, oz);
            // Re-apply authority bricks (player edits) on top of our SDF
            var bricks = GetBricksFromSnapshot(snapshotObj);
            if (bricks != null) WriteBricks(field, bricks);
        }

        internal static void OnDelta(object zoneInstance, object? patchObj)
        {
            var (field, ox, oz) = GetZoneState(zoneInstance);
            if (field == null) return;
            WriteSDF(field, ox, oz);
            // Re-apply only the upserted bricks (removed bricks are now floor-default, our SDF already covers them)
            var upserted = GetProp(patchObj, "UpsertedBricks") as System.Collections.IEnumerable;
            if (upserted != null) WriteBricks(field, upserted);
        }

        static System.Collections.IEnumerable? GetBricksFromSnapshot(object? snapshotObj)
        {
            var fieldDoc = GetProp(snapshotObj, "Field");
            return GetProp(fieldDoc, "Bricks") as System.Collections.IEnumerable;
        }

        static object? GetProp(object? obj, string name) =>
            obj?.GetType().GetProperty(name)?.GetValue(obj);
    }

    [HarmonyPatch]
    static class AuthoritySnapshotPatch
    {
        static MethodBase? TargetMethod()
        {
            var t = Plugin.FindType("Voxheim.Unity.VoxelZone", "Voxheim");
            var m = t?.GetMethod("TryInstallAuthoritySnapshot",
                BindingFlags.NonPublic | BindingFlags.Instance);
            if (m == null) Plugin.Log.LogWarning("[ProceduralTerrain] TryInstallAuthoritySnapshot not found — caves may disappear in player-edited zones.");
            return m;
        }
        static void Postfix(object __instance, bool __result, object __0)
        {
            if (__result) AuthorityInstallHelper.OnSnapshot(__instance, __0);
        }
    }

    [HarmonyPatch]
    static class AuthorityDeltaPatch
    {
        static MethodBase? TargetMethod()
        {
            var t = Plugin.FindType("Voxheim.Unity.VoxelZone", "Voxheim");
            var m = t?.GetMethod("TryInstallAuthorityDelta",
                BindingFlags.NonPublic | BindingFlags.Instance);
            if (m == null) Plugin.Log.LogWarning("[ProceduralTerrain] TryInstallAuthorityDelta not found — caves may disappear after live terrain edits.");
            return m;
        }
        static void Postfix(object __instance, bool __result, object __1)
        {
            if (__result) AuthorityInstallHelper.OnDelta(__instance, __1);
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

    [HarmonyPatch]
    static class PlaceVegetationPatch
    {
        static MethodBase? TargetMethod() =>
            typeof(ZoneSystem).GetMethod("PlaceVegetation",
                BindingFlags.NonPublic | BindingFlags.Instance);

        static bool Prefix() => !Plugin._disableVegetation;
    }

    [HarmonyPatch]
    static class SpawnLocationPatch
    {
        static MethodBase? TargetMethod() =>
            typeof(ZoneSystem).GetMethod("SpawnLocation",
                BindingFlags.NonPublic | BindingFlags.Instance);

        // Correct Y before any ZDOs are created.
        // GetGroundData uses a physics raycast which fails in SpawnMode.Ghost (no collider).
        static void Prefix(ref Vector3 pos)
        {
            var wg = WorldGenerator.instance;
            if (wg == null) return;
            float vanillaY = wg.GetHeight(pos.x, pos.z);
            pos.y = Plugin.FindCarvedSurfaceY(pos.x, vanillaY, pos.z);
        }
    }
}
