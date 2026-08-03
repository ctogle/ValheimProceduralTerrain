using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Reflection.Emit;
using UnityEngine;
using YamlDotNet.Serialization;

namespace VoxCaves
{
    [BepInPlugin("com.ctogle.voxcaves", "VoxCaves", "0.1.0")]
    [BepInDependency("com.bogey.voxheim", BepInDependency.DependencyFlags.SoftDependency)]
    public class Plugin : BaseUnityPlugin
    {
        internal static ManualLogSource Log = null!;
        internal static VoxCavesConfig Cfg = VoxCavesConfig.Default();

        static bool _voxheimPresent;
        internal static MethodInfo? _getXYZ;
        internal static MethodInfo? _setXYZ;
        internal static ConstructorInfo? _int3Ctor;
        internal static readonly HashSet<string> _carved = new();
        internal static int _seedOffset;

        internal static FieldInfo? _zoneField;
        internal static FieldInfo? _zoneOriginX;
        internal static FieldInfo? _zoneOriginZ;
        internal static FieldInfo? _zoneWidth;
        internal static FieldInfo? _zoneYScanLo;
        internal static FieldInfo? _zoneYScanHi;
        internal static FieldInfo? _zoneScaleField; // s_scale: world units per voxel

        // Compiled delegates: replace per-voxel MethodInfo.Invoke (which allocates
        // object[] + boxes every arg) with direct IL calls — zero GC pressure.
        internal static Func<object, int, int, int, sbyte>?    _getVoxel;
        internal static Action<object, int, int, int, sbyte>?  _setVoxel;

        // Seam-fix reflection — all three must be non-null for CompletePatch to work.
        internal static MethodInfo? _pokeNeighbors;
        internal static MethodInfo? _boundsInclude;
        internal static Type?       _boundsType;

        // Coarse surface height cache: populated in CarveNoiseTube from the SH grid,
        // consumed by GetWorldHeightPatch. Key is ZoneSystem.GetZone().
        internal static readonly Dictionary<Vector2i, ZoneSurface> _surfaceCache = new();
        // Subset of _carved: only zones where CarveNoiseTube/DRG wrote at least one voxel.
        // Used to skip PokeNeighbors for unmodified zones.
        internal static readonly HashSet<string> _actuallyCarved = new();
        internal static Plugin? instance;

        internal struct ZoneSurface
        {
            public float originX, originZ; // world pos of coarse grid cell [0,0]
            public float cellSize;         // world units per grid step (STEP * voxelScale)
            public int   cols, rows;       // nx, nz dimensions of the coarse grid
            public float[] heights;        // [xi * rows + zi] → world Y of surface
        }

        void Awake()
        {
            instance = this;
            Log = Logger;
            LoadConfig();
            _voxheimPresent = BepInEx.Bootstrap.Chainloader.PluginInfos.ContainsKey("com.bogey.voxheim");
            if (!_voxheimPresent) { Log.LogInfo("[VoxCaves] Voxheim not present — disabled."); return; }
            if (!ResolveReflection()) { Log.LogError("[VoxCaves] Reflection failed — disabled."); return; }
            new Harmony("com.ctogle.voxcaves").PatchAll();
            Log.LogInfo($"[VoxCaves] Loaded. Mode={Cfg.Mode}");
        }

        static void LoadConfig()
        {
            string path = Path.Combine(Paths.ConfigPath, "voxcaves.yaml");
            if (!File.Exists(path))
            {
                File.WriteAllText(path, new SerializerBuilder().Build().Serialize(VoxCavesConfig.Default()));
                return;
            }
            try { Cfg = new DeserializerBuilder().IgnoreUnmatchedProperties().Build().Deserialize<VoxCavesConfig>(File.ReadAllText(path)) ?? VoxCavesConfig.Default(); }
            catch (Exception e) { Log.LogWarning($"[VoxCaves] Config error: {e.Message}"); }
        }

        internal static Type? FindType(string fullName, string asmHint)
        {
            var t = Type.GetType($"{fullName}, {asmHint}");
            if (t != null) return t;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                if (asm.GetName().Name == asmHint)
                {
                    t = asm.GetType(fullName);
                    if (t != null) return t;
                }
            return null;
        }

        static bool ResolveReflection()
        {
            try
            {
                var fieldType = FindType("Voxheim.Core.VoxelField", "Voxheim.Core");
                var int3Type  = FindType("Voxheim.Core.Int3",       "Voxheim.Core");
                var zoneType  = FindType("Voxheim.Unity.VoxelZone", "Voxheim");
                if (fieldType == null || int3Type == null || zoneType == null) return false;

                var pub  = BindingFlags.Public    | BindingFlags.Instance;
                var priv = BindingFlags.NonPublic | BindingFlags.Instance;
                _int3Ctor    = int3Type.GetConstructor(new[] { typeof(int), typeof(int), typeof(int) });
                _getXYZ      = fieldType.GetMethod("Get", pub, null, new[] { int3Type }, null);
                _setXYZ      = fieldType.GetMethod("Set", pub, null, new[] { int3Type, typeof(sbyte) }, null);
                _zoneField   = zoneType.GetField("_field",   priv);
                _zoneOriginX = zoneType.GetField("_originX", priv);
                _zoneOriginZ = zoneType.GetField("_originZ", priv);
                _zoneWidth   = zoneType.GetField("_width",   priv);
                _zoneYScanLo = zoneType.GetField("_yScanLo", priv);
                _zoneYScanHi   = zoneType.GetField("_yScanHi", priv);
                _zoneScaleField = zoneType.GetField("s_scale",  BindingFlags.NonPublic | BindingFlags.Static);
                if (_zoneScaleField == null) Log.LogWarning("[VoxCaves] s_scale not found — defaulting to 0.5");

                // Seam fix: poke neighbors after we've carved so they re-mesh their
                // boundary bricks using our now-populated field.
                _boundsType    = FindType("Voxheim.Core.VoxelBounds", "Voxheim.Core");
                _boundsInclude = _boundsType?.GetMethod("Include", pub, null, new[] { int3Type }, null);
                _pokeNeighbors = zoneType.GetMethod("PokeNeighbors", priv);
                if (_pokeNeighbors == null || _boundsInclude == null)
                    Log.LogWarning("[VoxCaves] PokeNeighbors/VoxelBounds not found — zone seams won't self-heal");

                // Compile DynamicMethod delegates so the inner voxel loop does zero
                // heap allocation per voxel: IL creates Int3 on the stack via newobj
                // (value-type newobj is stack-allocated) then calls Get/Set directly.
                if (_int3Ctor != null && _getXYZ != null && _setXYZ != null)
                {
                    var getDm = new DynamicMethod("VoxCaves_Get", typeof(sbyte),
                        new[] { typeof(object), typeof(int), typeof(int), typeof(int) },
                        typeof(Plugin).Module, skipVisibility: true);
                    var gil = getDm.GetILGenerator();
                    gil.Emit(OpCodes.Ldarg_0); gil.Emit(OpCodes.Castclass, fieldType);
                    gil.Emit(OpCodes.Ldarg_1); gil.Emit(OpCodes.Ldarg_2); gil.Emit(OpCodes.Ldarg_3);
                    gil.Emit(OpCodes.Newobj, _int3Ctor);
                    gil.Emit(OpCodes.Callvirt, _getXYZ);
                    gil.Emit(OpCodes.Ret);
                    _getVoxel = (Func<object, int, int, int, sbyte>)getDm.CreateDelegate(
                        typeof(Func<object, int, int, int, sbyte>));

                    var setDm = new DynamicMethod("VoxCaves_Set", typeof(void),
                        new[] { typeof(object), typeof(int), typeof(int), typeof(int), typeof(sbyte) },
                        typeof(Plugin).Module, skipVisibility: true);
                    var sil = setDm.GetILGenerator();
                    sil.Emit(OpCodes.Ldarg_0); sil.Emit(OpCodes.Castclass, fieldType);
                    sil.Emit(OpCodes.Ldarg_1); sil.Emit(OpCodes.Ldarg_2); sil.Emit(OpCodes.Ldarg_3);
                    sil.Emit(OpCodes.Newobj, _int3Ctor);
                    sil.Emit(OpCodes.Ldarg_S, (byte)4);
                    sil.Emit(OpCodes.Callvirt, _setXYZ);
                    sil.Emit(OpCodes.Ret);
                    _setVoxel = (Action<object, int, int, int, sbyte>)setDm.CreateDelegate(
                        typeof(Action<object, int, int, int, sbyte>));
                }

                bool ok = _int3Ctor != null && _getXYZ != null && _setXYZ != null
                       && _zoneField != null && _zoneOriginX != null && _zoneOriginZ != null
                       && _zoneWidth != null && _zoneYScanLo != null && _zoneYScanHi != null
                       && _getVoxel != null && _setVoxel != null;
                Log.LogInfo($"[VoxCaves] Reflection ok={ok}, seamFix={_pokeNeighbors != null && _boundsInclude != null}");
                return ok;
            }
            catch (Exception e) { Log.LogError($"[VoxCaves] Reflection exception: {e.Message}"); return false; }
        }

        internal static void OnWorldLoad()
        {
            _carved.Clear();
            _actuallyCarved.Clear();
            _surfaceCache.Clear();
            long uid = ZNet.instance?.GetWorldUID() ?? 0L;
            _seedOffset = (int)(uid * 2654435761L);
            Log.LogInfo($"[VoxCaves] World loaded, seed offset={_seedOffset}.");
        }

        // ── Shared entry point ────────────────────────────────────────────────────

        internal static void ProcessZone(object field, int ox, int oz, int gx0, int gx1, int gz0, int gz1, int yLo, int yHi)
        {
            if (!Cfg.Enabled) return;
            int cols = gx1 - gx0 + 1, rows = gz1 - gz0 + 1;
            float[,] colMask = BuildColumnMask(ox, oz, gx0, gz0, cols, rows);
            int modCount = Cfg.Mode == "DRG"
                ? CarveDRG(field, ox, oz, gx0, gz0, cols, rows, yLo, yHi, colMask)
                : CarveNoiseTube(field, ox, oz, gx0, gz0, cols, rows, yLo, yHi, colMask);
            if (modCount > 0) { _actuallyCarved.Add($"{ox},{oz}"); Log.LogDebug($"[VoxCaves] Zone {ox},{oz}: carved {modCount} voxels"); }
        }

        // Per-column suppression mask [0=blocked, 1=free].
        // Blocks carving inside any world location's exterior radius, fading over
        // LocationProtectBuffer world units. Same mask applies to the additive pass later.
        static float[,] BuildColumnMask(int ox, int oz, int gx0, int gz0, int cols, int rows)
        {
            var mask = new float[cols, rows];
            float s   = _zoneScaleField != null ? (float)_zoneScaleField.GetValue(null)! : 0.5f;
            float buf = Mathf.Max(1f, Cfg.LocationProtectBuffer);

            // Extract location data once — avoids allocating an IDictionary enumerator
            // per column (4096 times) and unboxing LocationInstance on every iteration.
            var locs = new List<(float x, float z, float r)>();
            var zs = ZoneSystem.instance;
            if (zs != null && zs.m_locationInstances.Count > 0)
            {
                var instances = (System.Collections.IDictionary)zs.m_locationInstances;
                foreach (System.Collections.DictionaryEntry entry in instances)
                {
                    var loc = (ZoneSystem.LocationInstance)entry.Value!;
                    if (loc.m_location == null) continue;
                    locs.Add((loc.m_position.x, loc.m_position.z, loc.m_location.m_exteriorRadius));
                }
            }

            // Cull locations whose influence can't reach this zone (AABB check).
            // Without this, large worlds have 1000+ locations × 16384 columns = millions
            // of sqrt/smoothstep calls per zone.
            float zxMin = (ox + gx0) * s,        zxMax = (ox + gx0 + cols) * s;
            float zzMin = (oz + gz0) * s,        zzMax = (oz + gz0 + rows) * s;
            var zoneLocs = locs.FindAll(l =>
                l.x + l.r + buf > zxMin && l.x - l.r - buf < zxMax &&
                l.z + l.r + buf > zzMin && l.z - l.r - buf < zzMax);

            for (int ci = 0; ci < cols; ci++)
            for (int ri = 0; ri < rows; ri++)
            {
                if (zoneLocs.Count == 0) { mask[ci, ri] = 1f; continue; }
                float wx = (ox + gx0 + ci) * s;
                float wz = (oz + gz0 + ri) * s;
                float m = 1f;
                foreach (var (lx, lz, lr) in zoneLocs)
                {
                    float dx = wx - lx, dz = wz - lz;
                    float dist = Mathf.Sqrt(dx * dx + dz * dz);
                    float fade = Mathf.SmoothStep(0f, 1f, (dist - lr) / buf);
                    if (fade < m) m = fade;
                }
                mask[ci, ri] = m;
            }
            return mask;
        }

        // ── Mode A: two-field noise tube ─────────────────────────────────────────
        //
        // cave_sdf = min(TW - |n1|, TW - |n2|) / TW
        // Positive where both noise fields are near their zero crossings → tube-shaped air.
        // No depth fade: caves carve wherever noise says so, including the surface and
        // above-ground faces. Guard is SDF union — only write when we'd make a voxel
        // more air-like than it already is (cave_sdf > existing).

        static int CarveNoiseTube(object field, int ox, int oz, int gx0, int gz0, int cols, int rows, int yLo, int yHi, float[,] colMask)
        {
            int modCount = 0;
            const int STEP = 8;

            float s      = _zoneScaleField != null ? (float)_zoneScaleField.GetValue(null)! : 0.5f;
            float freq   = Cfg.Frequency;
            float vs     = Cfg.VerticalScale;
            float amp    = Cfg.NoiseAmplitude;
            float maxD   = Cfg.MaxCarveDepth;
            float stalT  = Cfg.StalactiteThreshold;
            float dFreq  = Cfg.DetailFrequency;
            int   floorY = Math.Max(yLo, Cfg.CaveFloorY);
            bool  doWarp = Cfg.WarpAmplitude > 0f;
            float wf = Cfg.WarpFrequency, wa = Cfg.WarpAmplitude;
            bool  doStal = stalT < 1f && dFreq > 0f;

            int nx = cols  / STEP + 2;
            int ny = (yHi - floorY) / STEP + 2;
            int nz = rows  / STEP + 2;
            var G1 = new float[nx * ny * nz];
            var G2 = doStal ? new float[nx * ny * nz] : null;
            var SH = new int[nx * nz];

            for (int xi = 0; xi < nx; xi++)
            for (int zi = 0; zi < nz; zi++)
            {
                int gx_s = Mathf.Clamp(ox + gx0 + xi * STEP, ox + gx0, ox + gx0 + cols - 1);
                int gz_s = Mathf.Clamp(oz + gz0 + zi * STEP, oz + gz0, oz + gz0 + rows - 1);
                int sy_g = floorY;
                for (int gy = yHi; gy > floorY; gy--)
                    if (_getVoxel!(field, gx_s, gy, gz_s) < 0) { sy_g = gy; break; }
                SH[xi * nz + zi] = sy_g;

                float wx = (ox + gx0 + xi * STEP) * s;
                float wz = (oz + gz0 + zi * STEP) * s;
                float fw_x = wx * freq, fw_z = wz * freq;
                if (doWarp)
                {
                    float dx = Fbm(wx * wf,        0f, wz * wf,        2, 0.5f) * wa;
                    float dz = Fbm(wx * wf + 5.2f, 0f, wz * wf + 1.3f, 2, 0.5f) * wa;
                    fw_x = (wx + dx) * freq;
                    fw_z = (wz + dz) * freq;
                }

                int col = (xi * nz + zi) * ny;
                for (int yi = 0; yi < ny; yi++)
                {
                    float fy = (floorY + yi * STEP) * freq * vs;
                    G1[col + yi] = Fbm(fw_x, fy, fw_z, 2, 0.5f);
                    if (doStal)
                        G2![col + yi] = Fbm(fw_x * dFreq / freq, fy * dFreq / freq, fw_z * dFreq / freq, 2, 0.5f);
                }
            }

            for (int ci = 0; ci < cols; ci++)
            for (int ri = 0; ri < rows; ri++)
            {
                float cMask = colMask[ci, ri];
                if (cMask <= 0f) continue;

                float gfx = (float)ci / STEP, gfz = (float)ri / STEP;
                int xi0 = Mathf.Min((int)gfx, nx - 2), zi0 = Mathf.Min((int)gfz, nz - 2);
                float tx = gfx - xi0, tz = gfz - zi0;

                float sh00 = SH[xi0*nz+zi0],      sh10 = SH[(xi0+1)*nz+zi0];
                float sh01 = SH[xi0*nz+(zi0+1)],  sh11 = SH[(xi0+1)*nz+(zi0+1)];
                int sY = Mathf.Clamp(Mathf.RoundToInt(Mathf.Lerp(
                    Mathf.Lerp(sh00, sh10, tx), Mathf.Lerp(sh01, sh11, tx), tz)), floorY, yHi);

                int caveBot = Math.Max(floorY, sY - Mathf.RoundToInt(maxD / s));
                if (caveBot >= sY) continue;

                int gx = ox + gx0 + ci, gz = oz + gz0 + ri;
                int c00 = (xi0       * nz + zi0      ) * ny;
                int c10 = ((xi0 + 1) * nz + zi0      ) * ny;
                int c01 = (xi0       * nz + (zi0 + 1)) * ny;
                int c11 = ((xi0 + 1) * nz + (zi0 + 1)) * ny;

                for (int gy = caveBot; gy <= sY; gy++)
                {
                    float depth = (sY - gy) * s;
                    float deepT = depth / maxD;
                    if (deepT >= 1f) continue;
                    float featureScale = (1f - deepT) * (1f - deepT) * cMask;

                    float gyF = (float)(gy - floorY) / STEP;
                    int   yi0 = Mathf.Min((int)gyF, ny - 2);
                    float ty  = gyF - yi0;

                    float b0 = Mathf.Lerp(Mathf.Lerp(G1[c00+yi0], G1[c10+yi0], tx), Mathf.Lerp(G1[c01+yi0], G1[c11+yi0], tx), tz);
                    float b1 = Mathf.Lerp(Mathf.Lerp(G1[c00+yi0+1], G1[c10+yi0+1], tx), Mathf.Lerp(G1[c01+yi0+1], G1[c11+yi0+1], tx), tz);
                    float noise = Mathf.Lerp(b0, b1, ty);

                    float density = depth + noise * amp * featureScale;
                    if (density >= 0f) continue;

                    if (doStal)
                    {
                        float d0 = Mathf.Lerp(Mathf.Lerp(G2![c00+yi0], G2[c10+yi0], tx), Mathf.Lerp(G2[c01+yi0], G2[c11+yi0], tx), tz);
                        float d1 = Mathf.Lerp(Mathf.Lerp(G2![c00+yi0+1], G2[c10+yi0+1], tx), Mathf.Lerp(G2[c01+yi0+1], G2[c11+yi0+1], tx), tz);
                        if (Mathf.Lerp(d0, d1, ty) > stalT) continue;
                    }

                    _setVoxel!(field, gx, gy, gz, (sbyte)Mathf.Clamp(-density / amp * 127f, 1f, 127f));
                    modCount++;
                }
            }

            // Populate surface cache from the coarse SH grid (zero extra voxel reads).
            // GetWorldHeightPatch reads this so Valheim's height system reflects our carving.
            if (modCount > 0)
            {
                float origX = (ox + gx0) * s, origZ = (oz + gz0) * s;
                var coarseH = new float[nx * nz];
                for (int xi = 0; xi < nx; xi++)
                for (int zi = 0; zi < nz; zi++)
                    coarseH[xi * nz + zi] = (SH[xi * nz + zi] + 1) * s;
                var center = new Vector3(origX + cols * s * 0.5f, 0f, origZ + rows * s * 0.5f);
                _surfaceCache[ZoneSystem.GetZone(center)] = new ZoneSurface {
                    originX = origX, originZ = origZ, cellSize = STEP * s,
                    cols = nx, rows = nz, heights = coarseH
                };
            }

            return modCount;
        }

        // ── Mode B: DRG-style sphere rooms + capsule tunnels ──────────────────────
        //
        // Scatter N room centres underground. Carve noisy spheres at each.
        // Connect rooms in a chain with noisy capsule tunnels.
        // Add one entrance shaft from the shallowest room to the surface — unfaded,
        // so it always punches through regardless of MinDepthVoxels.

        struct Room { public Vector3 Center; public float Radius; }

        static int[,] ScanSurface(object field, int ox, int oz, int gx0, int gz0, int cols, int rows, int yLo, int yHi)
        {
            var s = new int[cols, rows];
            for (int ci = 0; ci < cols; ci++)
            for (int ri = 0; ri < rows; ri++)
            {
                int gx = ox + gx0 + ci, gz = oz + gz0 + ri, sy = yLo;
                for (int gy = yHi; gy > yLo; gy--)
                {
                    if (_getVoxel!(field, gx, gy, gz) < 0) { sy = gy; break; }
                }
                s[ci, ri] = sy;
            }
            return s;
        }

        static int CarveDRG(object field, int ox, int oz, int gx0, int gz0, int cols, int rows, int yLo, int yHi, float[,] colMask)
        {
            var surfaceYs = ScanSurface(field, ox, oz, gx0, gz0, cols, rows, yLo, yHi);
            var rooms = GenerateRooms(ox, oz, gx0, gz0, cols, rows, yLo, surfaceYs);
            if (rooms.Count == 0) return 0;

            // Entrance: shallowest room → surface directly above it.
            Room entrance = rooms[0];
            foreach (var r in rooms) if (r.Center.y > entrance.Center.y) entrance = r;
            int eci = (int)entrance.Center.x - ox - gx0;
            int eri = (int)entrance.Center.z - oz - gz0;
            eci = Mathf.Clamp(eci, 0, cols - 1);
            eri = Mathf.Clamp(eri, 0, rows - 1);
            var entranceSurface = new Vector3(entrance.Center.x, surfaceYs[eci, eri], entrance.Center.z);

            int modCount = 0;

            for (int ci = 0; ci < cols; ci++)
            for (int ri = 0; ri < rows; ri++)
            {
                float cMask = colMask[ci, ri];
                if (cMask <= 0f) continue;

                int gx = ox + gx0 + ci, gz = oz + gz0 + ri;
                int sy = surfaceYs[ci, ri];
                int caveTop = sy - Cfg.MinDepthVoxels;

                for (int gy = yLo; gy <= yHi; gy++)
                {
                    float floorFade = Mathf.SmoothStep(0f, 1f,
                        (gy - Cfg.CaveFloorY) / (float)Mathf.Max(1, Cfg.CaveFloorFade));
                    if (floorFade <= 0f) continue;

                    var p = new Vector3(gx, gy, gz);

                    float cave_f = float.MinValue;
                    foreach (var room in rooms)
                    {
                        float noise = Fbm(gx * Cfg.RoomNoiseFreq, gy * Cfg.RoomNoiseFreq, gz * Cfg.RoomNoiseFreq, 2, 0.5f) * Cfg.RoomNoiseAmp;
                        float sdf   = (room.Radius + noise - Vector3.Distance(p, room.Center)) / room.Radius;
                        cave_f = Mathf.Max(cave_f, sdf);
                    }

                    for (int i = 0; i < rooms.Count - 1; i++)
                    {
                        float sdf = CapsuleSdf(p, rooms[i].Center, rooms[i + 1].Center, Cfg.TunnelRadius) / Cfg.TunnelRadius;
                        cave_f = Mathf.Max(cave_f, sdf);
                    }

                    float fade      = Mathf.SmoothStep(0f, 1f, (float)(caveTop - gy) / Mathf.Max(1f, Cfg.FadeDepthVoxels));
                    float effective = cave_f * fade;

                    float entranceSdf = CapsuleSdf(p, entrance.Center, entranceSurface, Cfg.TunnelRadius) / Cfg.TunnelRadius;
                    effective = Mathf.Max(effective, entranceSdf);

                    // Apply floor fade and location protection.
                    effective *= floorFade * cMask;
                    if (effective <= 0f) continue;

                    if (_getVoxel!(field, gx, gy, gz) >= 0) continue;

                    sbyte sdf2 = (sbyte)Mathf.Clamp(effective * 127f, 1f, 127f);
                    _setVoxel!(field, gx, gy, gz, sdf2);
                    modCount++;
                }
            }
            return modCount;
        }

        static List<Room> GenerateRooms(int ox, int oz, int gx0, int gz0, int cols, int rows, int yLo, int[,] surfaceYs)
        {
            var rooms = new List<Room>();
            for (int i = 0; i < Cfg.RoomCount; i++)
            {
                int ci   = RoomHash(i, ox, oz, 1)  % cols;
                int ri   = RoomHash(i, ox, oz, 2)  % rows;
                int sy   = surfaceYs[ci, ri];
                float r  = Cfg.RoomRadius * (0.75f + (RoomHash(i, ox, oz, 4) % 100) / 200f);
                int minY = yLo + (int)r + 1;
                int maxY = sy  - (int)r - Cfg.MinDepthVoxels - 1;
                if (minY >= maxY) continue;
                int gy = minY + RoomHash(i, ox, oz, 3) % (maxY - minY);
                rooms.Add(new Room { Center = new Vector3(ox + gx0 + ci, gy, oz + gz0 + ri), Radius = r });
            }
            return rooms;
        }

        // Deterministic per-room hash, separate from world noise hash.
        static int RoomHash(int i, int ox, int oz, int slot)
        {
            uint h = (uint)(i * 73856093 ^ ox * 19349663 ^ oz * 83492791 ^ slot * 1013904223 ^ (uint)_seedOffset);
            h ^= h >> 16; h *= 0x45d9f3b; h ^= h >> 16;
            return (int)(h & 0x7FFFFFFF);
        }

        // SDF of a capsule (line segment with radius). Positive inside.
        static float CapsuleSdf(Vector3 p, Vector3 a, Vector3 b, float radius)
        {
            Vector3 ab = b - a, ap = p - a;
            float denom = Vector3.Dot(ab, ab);
            if (denom < 1e-6f) return radius - Vector3.Distance(p, a);
            float t = Mathf.Clamp01(Vector3.Dot(ap, ab) / denom);
            return radius - Vector3.Distance(p, a + t * ab);
        }

        // ── Shared noise utilities ─────────────────────────────────────────────────

        static float Fbm(float x, float y, float z, int octaves, float persistence)
        {
            float val = 0f, amp = 1f, freq = 1f, total = 0f;
            for (int i = 0; i < octaves; i++)
            {
                val   += ValueNoise3D(x * freq, y * freq, z * freq) * amp;
                total += amp; amp *= persistence; freq *= 2f;
            }
            return val / total;
        }

        static float ValueNoise3D(float x, float y, float z)
        {
            int ix = Mathf.FloorToInt(x), iy = Mathf.FloorToInt(y), iz = Mathf.FloorToInt(z);
            float fx = x-ix, fy = y-iy, fz = z-iz;
            float ux = S(fx), uy = S(fy), uz = S(fz);
            return Mathf.Lerp(
                Mathf.Lerp(Mathf.Lerp(H(ix,iy,iz),   H(ix+1,iy,iz),   ux), Mathf.Lerp(H(ix,iy+1,iz),   H(ix+1,iy+1,iz),   ux), uy),
                Mathf.Lerp(Mathf.Lerp(H(ix,iy,iz+1), H(ix+1,iy,iz+1), ux), Mathf.Lerp(H(ix,iy+1,iz+1), H(ix+1,iy+1,iz+1), ux), uy),
                uz) * 2f - 1f;
        }

        static float S(float t) => t * t * t * (t * (t * 6f - 15f) + 10f);

        static float H(int x, int y, int z)
        {
            uint h = (uint)(x * 1619 + y * 31337 + z * 6971 + _seedOffset * 1013904223);
            h ^= h >> 16; h *= 0x45d9f3b; h ^= h >> 16;
            return (h & 0xFFFF) / 65535f;
        }

    }

    // ── Harmony patches ────────────────────────────────────────────────────────────

    [HarmonyPatch]
    static class VoxelizeColumnsPatch
    {
        static MethodBase? TargetMethod()
        {
            var t = Plugin.FindType("Voxheim.Core.HeightFieldVoxelizer", "Voxheim.Core");
            var m = t?.GetMethod("VoxelizeColumns", BindingFlags.Public | BindingFlags.Static);
            if (m == null) Plugin.Log.LogError("[VoxCaves] VoxelizeColumns not found");
            return m;
        }

        static void Postfix(object field, int originX, int originZ, int gx0, int gx1, int gz0, int gz1, int yLo, int yHi)
        {
            string key = $"{originX},{originZ}";
            bool fullInit = gx0 == 0 && gz0 == 0;
            if (fullInit) Plugin._carved.Add(key);
            else if (!Plugin._carved.Contains(key)) return;
            try { Plugin.ProcessZone(field, originX, originZ, gx0, gx1, gz0, gz1, yLo, yHi); }
            catch (Exception e) { Plugin.Log.LogError($"[VoxCaves] VoxelizeColumns patch: {e}"); }
        }
    }

    // After a zone finishes completing (becomes _isActive=true and is registered in
    // s_zones), poke its neighbors so they re-mesh boundary bricks that were meshed
    // before we carved this zone. Without this, Zone A's stale mesh from before Zone B
    // loaded shows solid rock at the boundary → visible seam.
    [HarmonyPatch]
    static class CompletePatch
    {
        static MethodBase? TargetMethod()
        {
            var t = Plugin.FindType("Voxheim.Unity.VoxelZone", "Voxheim");
            var m = t?.GetMethod("Complete", BindingFlags.NonPublic | BindingFlags.Instance);
            if (m == null) Plugin.Log.LogError("[VoxCaves] Complete not found — seam fix disabled");
            return m;
        }

        static void Postfix(object __instance)
        {
            if (Plugin._pokeNeighbors == null || Plugin._boundsInclude == null || Plugin._boundsType == null)
                return;
            try
            {
                int ox = (int)Plugin._zoneOriginX!.GetValue(__instance)!;
                int oz = (int)Plugin._zoneOriginZ!.GetValue(__instance)!;
                // Only do expensive work for zones where we actually carved voxels.
                if (!Plugin._actuallyCarved.Contains($"{ox},{oz}")) return;

                int w   = (int)Plugin._zoneWidth!.GetValue(__instance)!;
                int yLo = (int)Plugin._zoneYScanLo!.GetValue(__instance)!;
                int yHi = (int)Plugin._zoneYScanHi!.GetValue(__instance)!;

                // Build VoxelBounds covering this zone's full carved extent.
                // PokeNeighbors will find all active neighbors whose OwnedColumns overlap
                // these bounds and trigger EnqueueDeferredRemesh on each, so their
                // boundary bricks are re-meshed with our carved data in the next frame.
                object bounds   = Activator.CreateInstance(Plugin._boundsType)!;
                var    minCoord = Plugin._int3Ctor!.Invoke(new object[] { ox,         yLo, oz });
                var    maxCoord = Plugin._int3Ctor!.Invoke(new object[] { ox + w - 1, yHi, oz + w - 1 });
                Plugin._boundsInclude.Invoke(bounds, new[] { minCoord });
                Plugin._boundsInclude.Invoke(bounds, new[] { maxCoord });
                Plugin._pokeNeighbors.Invoke(__instance, new object[] { bounds, true });
            }
            catch (Exception e) { Plugin.Log.LogError($"[VoxCaves] CompletePatch: {e}"); }
        }
    }

    [HarmonyPatch]
    static class GetWorldHeightPatch
    {
        static MethodBase? TargetMethod()
        {
            var m = typeof(Heightmap).GetMethod("GetWorldHeight",
                BindingFlags.NonPublic | BindingFlags.Instance, null,
                new[] { typeof(Vector3), typeof(float).MakeByRefType() }, null);
            if (m == null) Plugin.Log?.LogWarning("[VoxCaves] Heightmap.GetWorldHeight not found — terrain height redirect disabled");
            return m;
        }

        static void Postfix(Vector3 worldPos, ref float height, ref bool __result)
        {
            try
            {
                Vector2i key = ZoneSystem.GetZone(worldPos);
                if (!Plugin._surfaceCache.TryGetValue(key, out var surf)) return;
                float gfx = (worldPos.x - surf.originX) / surf.cellSize;
                float gfz = (worldPos.z - surf.originZ) / surf.cellSize;
                int xi0 = Mathf.Clamp(Mathf.FloorToInt(gfx), 0, surf.cols - 2);
                int zi0 = Mathf.Clamp(Mathf.FloorToInt(gfz), 0, surf.rows - 2);
                float tx = gfx - xi0, tz = gfz - zi0;
                float h00 = surf.heights[xi0       * surf.rows + zi0];
                float h10 = surf.heights[(xi0 + 1) * surf.rows + zi0];
                float h01 = surf.heights[xi0       * surf.rows + (zi0 + 1)];
                float h11 = surf.heights[(xi0 + 1) * surf.rows + (zi0 + 1)];
                height = Mathf.Lerp(Mathf.Lerp(h00, h10, tx), Mathf.Lerp(h01, h11, tx), tz);
                __result = true;
            }
            catch { }
        }
    }

    [HarmonyPatch(typeof(ZNet), nameof(ZNet.SetServer))]
    static class WorldLoadPatch { static void Postfix() => Plugin.OnWorldLoad(); }

    // ── Config ─────────────────────────────────────────────────────────────────────

    public class VoxCavesConfig
    {
        public bool   Enabled         { get; set; } = true;
        // "NoiseTube" — two-field intersection spaghetti caves (Minecraft-style)
        // "DRG"       — sphere rooms + capsule tunnels with guaranteed surface entrance
        public string Mode            { get; set; } = "NoiseTube";

        // ── NoiseTube settings ───────────────────────────────────────────────
        // 3D noise frequency. Lower = bigger features. Typical 0.02–0.06.
        public float  Frequency       { get; set; } = 0.03f;
        // Tube half-width in noise space [0..1]. Larger = wider tunnels. Typical 0.3–0.5.
        public float  TunnelWidth     { get; set; } = 0.40f;
        // Y-axis noise scale relative to XZ. <1 = more horizontal passages. Typical 0.4–1.0.
        public float  VerticalScale   { get; set; } = 0.6f;
        // Domain warp: XZ sample coords are offset by a secondary noise field before the
        // primary tube evaluation. Turns smooth interpolation blobs into twisted organic
        // passages. WarpAmplitude is in world units; 0 = disabled.
        public float  WarpAmplitude   { get; set; } = 3.0f;
        // Frequency of the warp field (world-space). Lower = broader distortions.
        public float  WarpFrequency   { get; set; } = 0.05f;

        // ── DRG settings ─────────────────────────────────────────────────────
        // Rooms per zone.
        public int    RoomCount       { get; set; } = 4;
        // Base sphere radius in voxels.
        public float  RoomRadius      { get; set; } = 10f;
        // Noise frequency for organic sphere deformation.
        public float  RoomNoiseFreq   { get; set; } = 0.15f;
        // Noise amplitude for sphere deformation (voxels).
        public float  RoomNoiseAmp    { get; set; } = 4f;
        // Radius of capsule tunnels connecting rooms and the surface entrance.
        public float  TunnelRadius    { get; set; } = 4f;

        // ── NoiseTube density framework ─────────────────────────────────────
        // Max world-unit depth noise can carve (cave condition: |noise|*amp > depth).
        public float  NoiseAmplitude      { get; set; } = 10f;
        // World units where quadratic deep-fade envelope hits zero.
        public float  MaxCarveDepth       { get; set; } = 30f;
        // Secondary noise frequency for protrusions inside the cave void.
        // Should be 2-6× Frequency. Evaluated on same STEP=8 grid — no extra voxel reads.
        public float  DetailFrequency     { get; set; } = 0.05f;
        // Detail noise above this threshold → leave solid (rocky protrusion/outcropping).
        // Range [-1, 1]. 0.6 → ~20% of cave volume stays solid. 0.9 → ~5% (sparse spikes).
        public float  StalactiteThreshold { get; set; } = 0.6f;

        // ── Shared settings ──────────────────────────────────────────────────
        // Voxels below terrain surface before caves start (0 = can breach surface on slopes).
        public int    MinDepthVoxels  { get; set; } = 0;
        // Voxels over which cave strength ramps from 0 to full below MinDepthVoxels.
        public int    FadeDepthVoxels { get; set; } = 10;
        // Absolute world-space Y all zones start carving from. Every zone uses the same
        // value regardless of its local terrain floor (yLo), which is what eliminates
        // seams. Valheim ocean bottom is around Y=16-20, so 0 is safely below all terrain.
        public int    GlobalFloor     { get; set; } = 0;
        // Voxel Y below which cave carving fades to zero. Prevents caves from punching
        // through to the LOD mesh under the ocean. 5 keeps a solid floor above the LOD.
        public int    CaveFloorY      { get; set; } = 5;
        // Voxels over which cave strength ramps from 0 at CaveFloorY to full above it.
        public int    CaveFloorFade   { get; set; } = 8;
        // World-unit buffer beyond each location's m_exteriorRadius where carving fades
        // from 0 to full. Keeps terrain around dungeons, villages, etc. intact.
        public float  LocationProtectBuffer { get; set; } = 8f;

        public static VoxCavesConfig Default() => new VoxCavesConfig();
    }
}
