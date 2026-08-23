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
        // ── SDF bake helpers ─────────────────────────────────────────────────────

        internal static uint ComputeCfgHash()
        {
            if (_serverCfgHash.HasValue) return _serverCfgHash.Value;
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
            int seed = (int)(ZNet.instance?.GetWorldUID() ?? 0L);
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
            if (_scale == null || _scale.Value <= 0f)
            {
                float resolved = _scaleField != null ? (float)_scaleField.GetValue(null)! : 0f;
                _scale = resolved > 0f ? resolved : 1f;
                Log.LogInfo($"[ProceduralTerrain] bake: scale resolved to {_scale} (raw={resolved})");
            }
            float s = _scale.Value;
            int seed = (int)(ZNet.instance?.GetWorldUID() ?? 0L);
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
                        Carve(null, zox, zoz, zoneW, yLo, yHi);
                        int n = System.Threading.Interlocked.Increment(ref doneCount);
                        if (n % 50 == 0 || n == toCarve.Count)
                        {
                            float elapsed = bakeSw.ElapsedMilliseconds / 1000f;
                            float eta = elapsed > 0f ? (toCarve.Count - n) / (n / elapsed) : 0f;
                            Log.LogInfo($"[ProceduralTerrain] bake: {n}/{toCarve.Count} zones — {eta:F0}s remaining");
                            var prog = new ZPackage();
                            prog.Write(n); prog.Write(toCarve.Count);
                            ZRoutedRpc.instance.InvokeRoutedRPC(ZRoutedRpc.Everybody, "PTBakeProgress", prog);
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
            // Re-voxelization of active zones on config hot-reload requires Voxheim to
            // expose a way to re-run the generator pipeline on a live zone. Until that
            // API exists, we clear caches and rebuild meshes only — zones reload fully
            // on next unload/reload cycle.
            bool canRemesh = _enqueueMeshMethod != null && _finishSkinNowMethod != null && _int3Ctor != null;
            if (!canRemesh)
                Log.LogWarning("[ProceduralTerrain] EnqueueMesh/FinishSkinNow unavailable — mesh rebuild skipped.");

            var snapshot = new List<(string key, (int ox, int oz, int w, int yLo, int yHi) zp)>(_zoneParams.Count);
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

            // ── Extend _yScanHi for zones where Mode C raises terrain ────────────
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
    }

// ── Bake download progress UI ─────────────────────────────────────────────────
public class BakeProgressUI : MonoBehaviour
{
    static BakeProgressUI? _instance;
    static GUIStyle? _boxStyle;
    static GUIStyle? _textStyle;
    static GUIStyle? _captionStyle;
    static bool _stylesInit;
    static Texture2D? _bgTex;
    static Texture2D? _barBgTex;
    static Texture2D? _barFillTex;

    public static void Show()
    {
        if (_instance != null) return;
        var go = new GameObject("PTBakeProgressUI");
        DontDestroyOnLoad(go);
        _instance = go.AddComponent<BakeProgressUI>();
    }

    public static void Hide()
    {
        if (_instance == null || Plugin._bakeInProgress) return;
        Destroy(_instance.gameObject);
        _instance = null;
    }

    public static void ShowBake()
    {
        if (_instance != null) return;
        var go = new GameObject("PTBakeProgressUI");
        DontDestroyOnLoad(go);
        _instance = go.AddComponent<BakeProgressUI>();
    }

    public static void HideBake()
    {
        if (_instance == null || Plugin._bakeDownloadActive) return;
        Destroy(_instance.gameObject);
        _instance = null;
    }

    void OnGUI()
    {
        if (!Plugin._bakeDownloadActive && !Plugin._bakeInProgress) return;
        if (!_stylesInit)
        {
            _stylesInit = true;
            _bgTex = new Texture2D(1, 1);
            _bgTex.SetPixel(0, 0, new Color(0.08f, 0.07f, 0.05f, 0.92f));
            _bgTex.Apply();
            _barBgTex = new Texture2D(1, 1);
            _barBgTex.SetPixel(0, 0, new Color(0.2f, 0.18f, 0.12f, 1f));
            _barBgTex.Apply();
            _barFillTex = new Texture2D(1, 1);
            _barFillTex.SetPixel(0, 0, new Color(0.85f, 0.65f, 0.2f, 1f));
            _barFillTex.Apply();
            _boxStyle = new GUIStyle { normal = { background = _bgTex } };
            _textStyle = new GUIStyle
            {
                normal    = { textColor = new Color(0.95f, 0.80f, 0.45f) },
                fontStyle = FontStyle.Bold,
                fontSize  = 16,
                alignment = TextAnchor.MiddleCenter
            };
            _captionStyle = new GUIStyle
            {
                normal    = { textColor = new Color(0.6f, 0.55f, 0.4f) },
                fontStyle = FontStyle.Italic,
                fontSize  = 11,
                alignment = TextAnchor.MiddleCenter
            };
        }

        float pct; string label, sublabel;
        if (Plugin._bakeDownloadActive)
        {
            pct      = Plugin._bakeChunksExpected > 0 ? (float)Plugin._bakeChunksReceived / Plugin._bakeChunksExpected : 0f;
            long elS = (Plugin._bakeDownloadTimer?.ElapsedMilliseconds ?? 0) / 1000;
            float eta = pct > 0.01f ? elS / pct * (1f - pct) : 0f;
            label    = $"Downloading world terrain…  {pct * 100f:F0}%";
            sublabel = eta > 0 ? $"ETA {eta:F0}s" : "Connecting…";
        }
        else
        {
            pct      = Plugin._bakeProgressTotal > 0 ? (float)Plugin._bakeProgressDone / Plugin._bakeProgressTotal : 0f;
            long elS = (Plugin._bakeProgressTimer?.ElapsedMilliseconds ?? 0) / 1000;
            float eta = pct > 0.01f ? elS / pct * (1f - pct) : 0f;
            label    = $"Server baking terrain…  {pct * 100f:F0}%";
            sublabel = eta > 0 ? $"ETA {eta:F0}s" : "Starting…";
        }

        float w = 420f, h = 90f;
        float x = (Screen.width - w) * 0.5f;
        float y = 20f;

        GUI.Box(new Rect(x, y, w, h), GUIContent.none, _boxStyle);
        GUI.Label(new Rect(x, y + 6f,  w, 18f), "ProceduralTerrain", _captionStyle);
        GUI.Label(new Rect(x, y + 24f, w, 26f), label,    _textStyle);
        GUI.Label(new Rect(x, y + 50f, w, 22f), sublabel, _textStyle);

        float barX = x + 16f, barW = w - 32f, barY = y + 76f, barH = 8f;
        GUI.DrawTexture(new Rect(barX, barY, barW, barH), _barBgTex);
        if (pct > 0f)
            GUI.DrawTexture(new Rect(barX, barY, barW * pct, barH), _barFillTex);
    }
}

} // namespace ProceduralTerrain
