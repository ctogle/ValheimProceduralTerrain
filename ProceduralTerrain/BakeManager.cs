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

        // Keys excluded from the bake hash: live/render-only, location-overlay (applied live, never baked), and
        // the seed (captured in the bake filename's seed field). Exact-match so param names can't collide.
        static readonly HashSet<string> _hashExcludedKeys = new(StringComparer.OrdinalIgnoreCase)
        {
            "coarsexz", "coarsey", "bakeradius", "debugviz", "debugvizocclude", "hidelodmeshes",
            "disablevegetation", "applygroundwork", "protectlocations", "flatplanelocations",
            "vegetationconform", "vegmaxtilt", "seed",
            "prefetch", "prefetchradius", "prefetchthreads", "cavecontent", "lodunderground",
        };

        internal static uint ComputeCfgHash()
        {
            if (_serverCfgHash.HasValue) return _serverCfgHash.Value;
            string path = ConfigPath;
            if (!File.Exists(path)) return 0u;
            // Hash only content that affects BAKED bytes. Live-only knobs (coarseXZ/coarseY now drive the live
            // carve, not the bake; debug/render toggles; bakeRadius selects which zones, not their content) are
            // excluded so tuning them doesn't invalidate an existing bake.
            uint hash = 2166136261u;
            foreach (var rawLine in File.ReadAllLines(path))
            {
                // Exclude live/render-only + location-overlay + seed knobs by EXACT key (not line-prefix, which
                // collided: seed vs seedFrac/seedR/seeds, coarseXZ vs bakeCoarseXZ). These don't affect baked bytes
                // (the bake is purely procedural; groundwork/protection/vegetation/seed are applied live or keyed
                // into the filename separately), so tuning them must not invalidate a bake.
                int eq = rawLine.IndexOf('=');
                if (eq > 0)
                {
                    string key = rawLine.Substring(0, eq).Trim().ToLowerInvariant();
                    if (_hashExcludedKeys.Contains(key)) continue;
                }
                foreach (char c in rawLine) hash = (hash ^ (byte)c) * 16777619u;
                hash = (hash ^ (byte)'\n') * 16777619u;
            }
            return hash;
        }

        static string BakeFilePath(int seed, uint cfgHash) =>
            Path.Combine(PTConfigDir, $"procedural_terrain_bake_{seed:X8}_{cfgHash:X8}.bin");

        static readonly object _bakeStreamLock = new();
        static byte[] _bakeBlkScratch;   // reused compressed-block buffer (guarded by _bakeStreamLock)

        // Streaming-write context: when a bake is in progress these are non-null, and StageBakeEntry
        // appends each carved zone straight to the output file (bounded RAM) instead of accumulating in
        // _bakePending. Guarded by _bakeStreamLock. Set up / torn down in WriteBakeFile.
        static Stream _bakeWriteStream;
        static List<(int ox, int oz, long off, int len)> _bakeWriteIndex;

        // Serialize + gzip one zone into a self-contained block (same layout as the inline v4 writer).
        static byte[] SerializeZoneBlock(int ox, int oz, BakeZone z)
        {
            using var ms = new MemoryStream();
            using (var gz = new GZipStream(ms, System.IO.Compression.CompressionLevel.Optimal, leaveOpen: true))
            using (var zbw = new BinaryWriter(gz))
                WriteZoneBody(zbw, ox, oz, z);
            return ms.ToArray();
        }

        static void LoadBakeFile()
        {
            var wg = WorldGenerator.instance;
            CloseBakeStream();
            _bakeData = null;
            if (wg == null) return;
            int seed = Plugin.WorldSeed();
            uint cfgHash = ComputeCfgHash();
            string path = BakeFilePath(seed, cfgHash);
            if (!File.Exists(path)) return;
            try
            {
                var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                // Detect whole-file gzip (v3 compressed) by magic bytes without consuming the stream.
                bool isGzip = fs.ReadByte() == 0x1f && fs.ReadByte() == 0x8b;
                fs.Seek(0, SeekOrigin.Begin);

                // v4 STREAMED: raw (not whole-file gzip), per-zone gzip blocks + an offset index at the end.
                // Load only the index and keep the file open — zones are read on demand (no 6GB into RAM).
                if (!isGzip)
                {
                    var pr = new BinaryReader(fs, System.Text.Encoding.UTF8, leaveOpen: true);
                    if (pr.ReadUInt32() != 0x564F5846u) { Log.LogWarning("[ProceduralTerrain] bake: invalid magic."); fs.Dispose(); return; }
                    ushort pver = pr.ReadUInt16();
                    if (pver == 5 || pver == 6)
                    {
                        Plugin._bakeFileV6 = (pver == 6);
                        if (pr.ReadUInt64() != (ulong)(uint)seed || pr.ReadUInt32() != cfgHash)
                            { Log.LogWarning("[ProceduralTerrain] bake: seed/cfg mismatch."); fs.Dispose(); return; }
                        int cnt = (int)pr.ReadUInt32();
                        fs.Seek(-8, SeekOrigin.End);
                        long indexOff = pr.ReadInt64();
                        // An interrupted bake leaves cnt=0 (placeholder never patched) and no index/footer, so
                        // indexOff is garbage. Reject cleanly → live carve, instead of seeking to a bad offset.
                        if (cnt <= 0 || indexOff < 18 || indexOff >= fs.Length)
                        { Log.LogWarning($"[ProceduralTerrain] bake: incomplete/corrupt (cnt={cnt}, indexOff={indexOff}) — likely an interrupted bake. Ignoring; rebake."); fs.Dispose(); return; }
                        fs.Seek(indexOff, SeekOrigin.Begin);
                        var idx = new Dictionary<(int, int), (long, int)>(cnt);
                        for (int i = 0; i < cnt; i++)
                        {
                            int ox = pr.ReadInt32(), oz = pr.ReadInt32();
                            long off = pr.ReadInt64(); int len = pr.ReadInt32();
                            idx[(ox, oz)] = (off, len);
                        }
                        _bakeIndex = idx;
                        _bakeStream = fs;             // KEEP OPEN for per-zone seeks
                        _bakeFileStep = _bakeStep;    // v4 treated as current
                        Log.LogInfo($"[ProceduralTerrain] Bake streamed: {cnt} zones (v5), index-only, from {path}");
                        return;                       // deliberately do NOT dispose fs
                    }
                    fs.Seek(0, SeekOrigin.Begin);     // not v4 → fall through to v3 raw load-all
                }

                // v3: whole file into RAM (gzip-wrapped or raw).
                using (var inStream = isGzip ? (Stream)new GZipStream(fs, CompressionMode.Decompress) : fs)
                using (var br = new BinaryReader(inStream))
                {
                    if (br.ReadUInt32() != 0x564F5846u) { Log.LogWarning("[ProceduralTerrain] bake: invalid magic."); return; }
                    ushort ver = br.ReadUInt16();
                    if (ver != 3) { Log.LogWarning($"[ProceduralTerrain] bake: unsupported version {ver} (need v3/v4 — rebake)."); return; }
                    if (br.ReadUInt64() != (ulong)(uint)seed || br.ReadUInt32() != cfgHash)
                        { Log.LogWarning("[ProceduralTerrain] bake: seed/cfg mismatch."); return; }
                    int count = (int)br.ReadUInt32();
                    var data = new Dictionary<(int, int), BakeZone>(count);
                    int maxStep = 1;
                    for (int i = 0; i < count; i++)
                    {
                        var z = ReadZoneBody(br, out int eox, out int eoz);
                        data[(eox, eoz)] = z;
                        if (z.step > maxStep) maxStep = z.step;
                    }
                    _bakeData = data;
                    _bakeFileStep = maxStep;
                    Log.LogInfo($"[ProceduralTerrain] Bake loaded: {count} zones (v3 sparse, RAM) from {path}");
                }
            }
            catch (Exception ex) { Log.LogWarning($"[ProceduralTerrain] bake: load failed: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}"); _bakeData = null; CloseBakeStream(); }
        }

        // Shared per-zone (de)serialization — used by v3 load-all and v4 streamed blocks.
        static BakeZone ReadZoneBody(BinaryReader br, out int eox, out int eoz)
        {
            eox = br.ReadInt32(); eoz = br.ReadInt32();
            int extLo = br.ReadInt32();
            int bh = br.ReadUInt16(), ebw = br.ReadUInt16(), step = br.ReadUInt16();
            int nsx = br.ReadInt32(), nsy = br.ReadInt32(), nsz = br.ReadInt32();
            int cOrigin = br.ReadInt32();
            int ncol = nsx * nsz;
            if (Plugin._bakeFileV6)   // crossings layout: slope + per-column (count+sign, crossing ys)
            {
                float slope = br.ReadSingle();
                var nCross = new byte[ncol];
                var crossList = new List<float>(ncol * 2);
                for (int c = 0; c < ncol; c++)
                {
                    byte nb = br.ReadByte();
                    nCross[c] = nb;
                    int nc = nb & 0x7F;
                    for (int j = 0; j < nc; j++)
                        crossList.Add(br.ReadUInt16() / 256f);
                }
                return new BakeZone {
                    extLo = extLo, bh = bh, bw = ebw, step = step, nsx = nsx, nsy = nsy, nsz = nsz, cOrigin = cOrigin,
                    colCross = crossList.ToArray(), colNCross = nCross, crossSlope = slope
                };
            }
            var colYLo = new int[ncol]; var colYLen = new int[ncol];
            var colBelow = new sbyte[ncol]; var colAbove = new sbyte[ncol]; var colOff = new int[ncol];
            int off = 0;
            for (int c = 0; c < ncol; c++)
            {
                colYLo[c]  = br.ReadUInt16();
                colYLen[c] = br.ReadUInt16();
                colBelow[c] = br.ReadSByte();
                colAbove[c] = br.ReadSByte();
                colOff[c] = off; off += colYLen[c];
            }
            int slen = (int)br.ReadUInt32();
            var samples = new sbyte[slen];
            var bytes = br.ReadBytes(slen);
            Buffer.BlockCopy(bytes, 0, samples, 0, slen);
            return new BakeZone {
                extLo = extLo, bh = bh, bw = ebw, step = step, nsx = nsx, nsy = nsy, nsz = nsz, cOrigin = cOrigin,
                colYLo = colYLo, colYLen = colYLen, colOff = colOff,
                colBelow = colBelow, colAbove = colAbove, samples = samples
            };
        }

        static void WriteZoneBody(BinaryWriter bw2, int eox, int eoz, BakeZone z)
        {
            bw2.Write(eox); bw2.Write(eoz); bw2.Write(z.extLo);
            bw2.Write((ushort)z.bh); bw2.Write((ushort)z.bw); bw2.Write((ushort)z.step);
            bw2.Write(z.nsx); bw2.Write(z.nsy); bw2.Write(z.nsz);
            bw2.Write(z.cOrigin);
            int ncol = z.nsx * z.nsz;
            if (z.colCross != null)   // v6 crossings layout
            {
                bw2.Write(z.crossSlope);
                int ci = 0;
                for (int c = 0; c < ncol; c++)
                {
                    byte nb = z.colNCross[c];
                    bw2.Write(nb);
                    int nc = nb & 0x7F;
                    for (int j = 0; j < nc; j++)
                        bw2.Write((ushort)Math.Max(0, Math.Min(65535, (int)Math.Round(z.colCross[ci++] * 256f))));
                }
                return;
            }
            for (int c = 0; c < ncol; c++)
            {
                bw2.Write((ushort)z.colYLo[c]);
                bw2.Write((ushort)z.colYLen[c]);
                bw2.Write(z.colBelow[c]);
                bw2.Write(z.colAbove[c]);
            }
            bw2.Write((uint)z.samples.Length);
            var bytes = new byte[z.samples.Length];
            Buffer.BlockCopy(z.samples, 0, bytes, 0, z.samples.Length);
            bw2.Write(bytes);
        }

        // v4 streamed read: seek to the zone's block, gunzip it, deserialize. Decoded zones are cached (FIFO)
        // so the double-call (Carve's bw check + PopulateSparseFromBake) and nearby re-gens don't re-read.
        internal static bool TryGetBakeZone(int ox, int oz, out BakeZone z)
        {
            if (_bakeData != null) return _bakeData.TryGetValue((ox, oz), out z);
            z = null;
            if (_bakeIndex == null || _bakeStream == null) return false;
            lock (_bakeStreamLock)
            {
                if (_bakeZoneCache.TryGetValue((ox, oz), out z)) return true;
                if (!_bakeIndex.TryGetValue((ox, oz), out var loc)) return false;
                try
                {
                    // Reuse a scratch buffer for the transient compressed block so it doesn't churn the LOH
                    // every zone (the retained samples array still allocates, but the block no longer does).
                    if (_bakeBlkScratch == null || _bakeBlkScratch.Length < loc.len) _bakeBlkScratch = new byte[loc.len];
                    var blk = _bakeBlkScratch;
                    _bakeStream.Seek(loc.off, SeekOrigin.Begin);
                    int read = 0;
                    while (read < loc.len) { int r = _bakeStream.Read(blk, read, loc.len - read); if (r <= 0) break; read += r; }
                    using var ms = new MemoryStream(blk, 0, loc.len);
                    using var gz = new GZipStream(ms, CompressionMode.Decompress);
                    using var zbr = new BinaryReader(gz);
                    z = ReadZoneBody(zbr, out _, out _);
                }
                catch (Exception ex) { Log.LogWarning($"[ProceduralTerrain] bake stream read ({ox},{oz}) failed: {ex.Message}"); z = null; return false; }
                _bakeZoneCache[(ox, oz)] = z;
                _bakeCacheOrder.Enqueue((ox, oz));
                while (_bakeCacheOrder.Count > _bakeCacheCap)
                {
                    var old = _bakeCacheOrder.Dequeue();
                    _bakeZoneCache.Remove(old);
                }
                return true;
            }
        }

        internal static void CloseBakeStream()
        {
            lock (_bakeStreamLock)
            {
                try { _bakeStream?.Dispose(); } catch { }
                _bakeStream = null; _bakeIndex = null;
                _bakeZoneCache.Clear(); _bakeCacheOrder.Clear();
            }
        }

        // Load the bake file once (idempotent) — lets the live generator test IsZoneBaked before carving.
        internal static void EnsureBakeLoaded()
        {
            if (_bakeLoadAttempted) return;
            _bakeLoadAttempted = true;
            LoadBakeFile();
        }

        // True when this zone is present in the loaded bake file (its baseline is fully baked).
        internal static bool IsZoneBaked(int ox, int oz) =>
            (_bakeData != null && _bakeData.ContainsKey((ox, oz))) ||
            (_bakeIndex != null && _bakeIndex.ContainsKey((ox, oz)));

        static float CatmullRom(float p0, float p1, float p2, float p3, float t)
        {
            float t2 = t * t, t3 = t2 * t;
            return 0.5f * ((2*p1) + (-p0+p2)*t + (2*p0-5*p1+4*p2-p3)*t2 + (-p0+3*p1-3*p2+p3)*t3);
        }

        // A/B interp for step>1 rehydration: Catmull-Rom (C1, 4-tap, smooth) vs trilinear (C0, 2-tap, cheap).
        // Same bake file serves both ways — flip _bakeTrilinear to compare quality/serve-cost on identical data.
        static float InterpTap(float p0, float p1, float p2, float p3, float t)
            => _bakeTrilinear ? p1 + (p2 - p1) * t : CatmullRom(p0, p1, p2, p3, t);

        static double MsBetween(long a, long b) => (b - a) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;

        // Piece 2 / v3: fused per-column reconstruction straight into the SPARSE cache — no dense buf.
        // Reorders to X → Z (into small reused temps), then reconstructs each column's Y into a tiny reused
        // float column (cache-friendly, unlike a 1.5MB volume write), band-limited (tricubic only near a
        // transition, nearest in constant runs). From that column it emits surfH + the deviating band run.
        // Returns false (→ caller falls back to the dense path) on any bake mismatch.
        static bool PopulateSparseFromBake(int ox, int oz, int bw, int bh, int extLo, float s, int voxelFloorY, int overlap)
        {
            long _tp0 = System.Diagnostics.Stopwatch.GetTimestamp();
            if (!TryGetBakeZone(ox, oz, out var e)) return false;
            long _tpDec = System.Diagnostics.Stopwatch.GetTimestamp();
            if (e.bw != bw) return false;
            int owned = bw - 2 * overlap;
            int fY    = voxelFloorY;

            // ── step==1 FAST path: band samples are already full-res → map straight into the sparse
            // cache, no rehydrate and no tricubic. The bake stores ONLY the owned WxW grid (no skirt), so we
            // loop owned columns directly — ~43% fewer columns than the old full-grid loop. ──
            if (e.step == 1)
            {
                if (e.nsx != owned || e.nsz != owned) return false;   // stale full-grid bake → fall through to live carve (rebake)
                // surfH stays full-grid-indexed (FindCarvedSurfaceY indexes with the overlap offset); only the
                // owned sub-block is filled, the skirt ring stays NaN (never read for an owned-point lookup).
                var surfH1 = new float[bw * bw];
                for (int i = 0; i < surfH1.Length; i++) surfH1[i] = float.NaN;
                if (_colVScratch   == null || _colVScratch.Length   < bh) _colVScratch = new sbyte[bh];
                if (_sparseScratch == null || _sparseScratch.Length < owned * bh * owned) _sparseScratch = new sbyte[owned * bh * owned];
                var colV1 = _colVScratch; var scratch1 = _sparseScratch;
                var cStart = new int[owned * owned]; var cLen = new int[owned * owned]; var cOff = new int[owned * owned];
                // v6 crossings: per-column offset into the flattened crossing array (prefix sum of counts).
                bool xmode = e.colCross != null;
                int[] xoffs = null;
                if (xmode)
                {
                    xoffs = new int[e.nsx * e.nsz + 1];
                    for (int c = 0; c < e.nsx * e.nsz; c++) xoffs[c + 1] = xoffs[c] + (e.colNCross[c] & 0x7F);
                }
                int tot = 0;
                for (int ix = 0; ix < owned; ix++)
                for (int iz = 0; iz < owned; iz++)
                {
                    int col = ix * e.nsz + iz;
                    float sh = float.NaN;
                    if (xmode)
                    {
                        // Hydrate the column from its sign-crossings: signed distance to nearest crossing × slope,
                        // sign flipping at each crossing (below sign in bit 7). Surface lands exactly at the crossing.
                        int nc = e.colNCross[col] & 0x7F, xo = xoffs[col];
                        float slope = e.crossSlope;
                        bool belowSolid = (e.colNCross[col] & 0x80) != 0;
                        // Single pass: crossings are sorted ascending and bakCy increases 1/voxel, so a pointer
                        // `seg` (= #crossings strictly below bakCy) advances monotonically. Nearest crossing is
                        // whichever of the straddling pair (seg-1, seg) is closer. O(bh), no per-voxel search.
                        int seg = 0;
                        for (int cy = 0; cy < bh; cy++)
                        {
                            int bakCy = (extLo + cy) - e.extLo;
                            sbyte v;
                            if (bakCy < 0) v = -127;
                            else if (bakCy >= e.bh) v = 127;
                            else if (nc == 0) v = belowSolid ? (sbyte)-127 : (sbyte)127;
                            else
                            {
                                while (seg < nc && e.colCross[xo + seg] <= bakCy) seg++;
                                float dmin = 1e9f;
                                if (seg < nc) { float d = e.colCross[xo + seg] - bakCy; if (d < dmin) dmin = d; }
                                if (seg > 0)  { float d = bakCy - e.colCross[xo + seg - 1]; if (d < dmin) dmin = d; }
                                bool solid = belowSolid ^ ((seg & 1) != 0);
                                int mag = (int)(dmin * slope + 0.5f); if (mag > 127) mag = 127;
                                v = (sbyte)(solid ? -mag : mag);
                            }
                            colV1[cy] = v;
                            if (v < 0) sh = (extLo + cy) * s;
                        }
                    }
                    else
                    {
                    int yLo = e.colYLo[col], yLen = e.colYLen[col], off = e.colOff[col];
                    sbyte below = e.colBelow[col], above = e.colAbove[col];
                    for (int cy = 0; cy < bh; cy++)
                    {
                        int bakCy = (extLo + cy) - e.extLo;
                        sbyte v;
                        if (bakCy < 0) v = -127;
                        else if (bakCy >= e.bh) v = 127;
                        else { int ys = bakCy; v = (ys < yLo) ? below : (ys < yLo + yLen) ? e.samples[off + (ys - yLo)] : above; }
                        colV1[cy] = v;
                        if (v < 0) sh = (extLo + cy) * s;
                    }
                    }
                    surfH1[(ix + overlap) * bw + (iz + overlap)] = sh;
                    int oc = ix * owned + iz;
                    int first = -1, last = -1;
                    for (int cy = 0; cy < bh; cy++)
                    {
                        int wy = extLo + cy; sbyte v = colV1[cy];
                        bool isDefault = (wy < fY) ? (v == -127) : (v == 127);
                        if (!isDefault) { if (first < 0) first = cy; last = cy; }
                    }
                    cOff[oc] = tot;
                    if (first < 0) { cStart[oc] = -1; cLen[oc] = 0; }
                    else { cStart[oc] = first; int len = last - first + 1; cLen[oc] = len; Array.Copy(colV1, first, scratch1, tot, len); tot += len; }
                }
                var band1 = new sbyte[tot]; Array.Copy(scratch1, 0, band1, 0, tot);
                _sdfCache[$"{ox},{oz}"] = new ZoneField { extLo = extLo, bh = bh, width = owned, colStart = cStart, colLen = cLen, colOff = cOff, band = band1 };
                lock (_surfHCache) _surfHCache[(ox, oz)] = (bw, extLo, s, surfH1);
                return true;
            }

            if (e.colCross != null) { Log.LogWarning($"[ProceduralTerrain] v6 crossings bake at step>1 not supported yet ({ox},{oz}) — rebake at bakeStep=1."); return false; }
            int step = e.step;
            int nsx = e.nsx, nsy = e.nsy, nsz = e.nsz;
            float scale = 32f * s / 127f;

            // Rehydrate the sparse per-column bands into a dense coarse grid (reused buffer) that the
            // reconstruction below reads. For step==1 this grid is already full-res, so the tricubic
            // passes are an identity — correct, though not yet specialized to skip the work.
            int rawLen = nsx * nsy * nsz;
            if (_rehydrateScratch == null || _rehydrateScratch.Length < rawLen) _rehydrateScratch = new sbyte[rawLen];
            var raw = _rehydrateScratch;
            for (int xs = 0; xs < nsx; xs++)
            for (int zs = 0; zs < nsz; zs++)
            {
                int col = xs * nsz + zs;
                int yLo = e.colYLo[col], yLen = e.colYLen[col], off = e.colOff[col];
                sbyte below = e.colBelow[col], above = e.colAbove[col];
                for (int ys = 0; ys < nsy; ys++)
                    raw[(xs * nsy + ys) * nsz + zs] = (ys < yLo) ? below : (ys < yLo + yLen) ? e.samples[off + (ys - yLo)] : above;
            }
            long _tpRaw = System.Diagnostics.Stopwatch.GetTimestamp();

            int t1Len = bw * nsy * nsz, t2Len = bw * nsy * bw;
            if (_crTemp1.Length < t1Len) _crTemp1 = new float[t1Len];
            if (_crTemp2.Length < t2Len) _crTemp2 = new float[t2Len];
            var temp1 = _crTemp1;  // [cx, ys, zs]
            var temp2 = _crTemp2;  // [cx, ys, cz]  (X- and Z-expanded, coarse Y)

            // Only reconstruct the owned footprint + the interp halo the upsampler actually reaches past an owned
            // edge (2 coarse cells for Catmull-Rom, 1 for trilinear) — NOT the full overlap skirt. The skirt columns
            // beyond that halo are never consumed (owned-only SDF cache; FindCarvedSurfaceY indexes owned), so
            // reconstructing them is pure waste. Cuts ~⅓ off all three interp passes. (lattice unchanged: xs = cx/step)
            int haloV = (_bakeTrilinear ? 1 : 2) * step;
            int reconLo = Math.Max(0, overlap - haloV);
            int reconHi = Math.Min(bw, bw - overlap + haloV);

            // Pass 1: expand X
            for (int ys = 0; ys < nsy; ys++)
            for (int zs = 0; zs < nsz; zs++)
            for (int cx = reconLo; cx < reconHi; cx++)
            {
                float xf = (float)cx / step; int xsg = (int)xf; float tx = xf - xsg;
                int xs = xsg - e.cOrigin; if (xs < 0) xs = 0; else if (xs > nsx - 2) xs = nsx - 2;
                int xA = Math.Max(xs-1,0), xB=xs, xC=Math.Min(xs+1,nsx-1), xD=Math.Min(xs+2,nsx-1);
                temp1[(cx*nsy+ys)*nsz+zs] = InterpTap(raw[(xA*nsy+ys)*nsz+zs], raw[(xB*nsy+ys)*nsz+zs], raw[(xC*nsy+ys)*nsz+zs], raw[(xD*nsy+ys)*nsz+zs], tx);
            }
            long _tpX = System.Diagnostics.Stopwatch.GetTimestamp();
            // Pass 2: expand Z (coarse Y retained)
            for (int cx = reconLo; cx < reconHi; cx++)
            for (int ys = 0; ys < nsy; ys++)
            for (int cz = reconLo; cz < reconHi; cz++)
            {
                float zf = (float)cz / step; int zsg = (int)zf; float tz = zf - zsg;
                int zs = zsg - e.cOrigin; if (zs < 0) zs = 0; else if (zs > nsz - 2) zs = nsz - 2;
                int zA=Math.Max(zs-1,0), zB=zs, zC=Math.Min(zs+1,nsz-1), zD=Math.Min(zs+2,nsz-1);
                temp2[(cx*nsy+ys)*bw+cz] = InterpTap(temp1[(cx*nsy+ys)*nsz+zA], temp1[(cx*nsy+ys)*nsz+zB], temp1[(cx*nsy+ys)*nsz+zC], temp1[(cx*nsy+ys)*nsz+zD], tz);
            }
            long _tpZ = System.Diagnostics.Stopwatch.GetTimestamp();

            int width = bw - 2 * overlap;
            var surfH = new float[bw * bw];
            if (_colFloatScratch == null || _colFloatScratch.Length < bh) _colFloatScratch = new float[bh];
            if (_sparseScratch   == null || _sparseScratch.Length   < width * bh * width) _sparseScratch = new sbyte[width * bh * width];
            if (_colVScratch     == null || _colVScratch.Length     < bh) _colVScratch = new sbyte[bh];
            var colF = _colFloatScratch; var scratch = _sparseScratch; var colV = _colVScratch;
            var transBytes = new sbyte[bh]; var transHasOvr = new bool[bh];
            var colStart = new int[width * width]; var colLen = new int[width * width]; var colOff = new int[width * width];
            int total = 0, floorY = voxelFloorY;
            const float SAT = 120f;
            long yInterpTicks = 0, encodeTicks = 0;

            for (int cx = reconLo; cx < reconHi; cx++)
            for (int cz = reconLo; cz < reconHi; cz++)
            {
                long _ya = System.Diagnostics.Stopwatch.GetTimestamp();
                int cb = cx * nsy;   // temp2 base: (cb+ys)*bw + cz
                // Active coarse-Y band (near a transition); constant runs outside get nearest-neighbor.
                int bandLo = int.MaxValue, bandHi = int.MinValue;
                float prev = temp2[(cb + 0) * bw + cz];
                for (int ys = 0; ys < nsy; ys++)
                {
                    float v = temp2[(cb + ys) * bw + cz];
                    if (Math.Abs(v) < SAT || (ys > 0 && ((v < 0f) != (prev < 0f)))) { if (ys < bandLo) bandLo = ys; if (ys > bandHi) bandHi = ys; }
                    prev = v;
                }
                if (bandLo != int.MaxValue) { bandLo -= 2; bandHi += 2; }

                float sh = float.NaN;
                for (int cy = 0; cy < bh; cy++)
                {
                    int bakCy = (extLo + cy) - e.extLo;
                    float outv;
                    if (bakCy < 0) outv = -127f;
                    else if (bakCy >= e.bh) outv = 127f;
                    else
                    {
                        float yf = (float)bakCy / step; int ys = Math.Min((int)yf, nsy - 2); if (ys < 0) ys = 0;
                        if (ys >= bandLo && ys <= bandHi)
                        {
                            float ty = yf - ys; int yA=Math.Max(ys-1,0), yB=ys, yC=Math.Min(ys+1,nsy-1), yD=Math.Min(ys+2,nsy-1);
                            outv = InterpTap(temp2[(cb+yA)*bw+cz], temp2[(cb+yB)*bw+cz], temp2[(cb+yC)*bw+cz], temp2[(cb+yD)*bw+cz], ty);
                        }
                        else outv = temp2[(cb + Math.Min((int)(yf + 0.5f), nsy - 1)) * bw + cz];
                    }
                    float fv = outv * scale;
                    colF[cy] = fv;
                    if (fv < 0f) sh = (extLo + cy) * s;   // ascending → ends at topmost solid
                }
                surfH[cx * bw + cz] = sh;
                yInterpTicks += System.Diagnostics.Stopwatch.GetTimestamp() - _ya;

                // Owned columns → emit the sparse deviating run (same transition logic as the live fill).
                if (cx >= overlap && cx < overlap + width && cz >= overlap && cz < overlap + width)
                {
                    long _ea = System.Diagnostics.Stopwatch.GetTimestamp();
                    int col = (cx - overlap) * width + (cz - overlap);
                    Array.Clear(transHasOvr, 0, bh);
                    float av = colF[bh - 1];
                    for (int cy = bh - 2; cy >= 0; cy--)
                    {
                        float bv = colF[cy];
                        bool topSurf = av > 0f && bv <= 0f, botSurf = av <= 0f && bv > 0f;
                        if (topSurf || botSurf)
                        {
                            float d = av - bv; float t = Mathf.Clamp01(d != 0f ? (-bv) / d : 0.5f); sbyte lo, hi;
                            if (topSurf) { lo = (sbyte)(int)Math.Round(-t * 127f); hi = (sbyte)(int)Math.Round((1f - t) * 127f); }
                            else         { lo = (sbyte)(int)Math.Round(t * 127f);  hi = (sbyte)(int)Math.Round(-(1f - t) * 127f); }
                            transBytes[cy] = lo; transHasOvr[cy] = true; transBytes[cy + 1] = hi; transHasOvr[cy + 1] = true;
                        }
                        av = bv;
                    }
                    int first = -1, last = -1;
                    for (int cy = 0; cy < bh; cy++)
                    {
                        sbyte v = transHasOvr[cy] ? transBytes[cy] : ToSDFByte(colF[cy], s);
                        colV[cy] = v; int wy = extLo + cy;
                        bool isDefault = (wy < floorY) ? (v == -127) : (v == 127);
                        if (!isDefault) { if (first < 0) first = cy; last = cy; }
                    }
                    colOff[col] = total;
                    if (first < 0) { colStart[col] = -1; colLen[col] = 0; }
                    else { colStart[col] = first; int len = last - first + 1; colLen[col] = len; Array.Copy(colV, first, scratch, total, len); total += len; }
                    encodeTicks += System.Diagnostics.Stopwatch.GetTimestamp() - _ea;
                }
            }

            long _tpY = System.Diagnostics.Stopwatch.GetTimestamp();

            var band = new sbyte[total]; Array.Copy(scratch, 0, band, 0, total);
            string zoneKey = $"{ox},{oz}";
            _sdfCache[zoneKey] = new ZoneField { extLo = extLo, bh = bh, width = width, colStart = colStart, colLen = colLen, colOff = colOff, band = band };
            lock (_surfHCache) _surfHCache[(ox, oz)] = (bw, extLo, s, surfH);
            long _tpEnd = System.Diagnostics.Stopwatch.GetTimestamp();
            double _yi = yInterpTicks * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            double _enc = encodeTicks * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            Log.LogDebug($"[PT-serve] ({ox},{oz}) decode={MsBetween(_tp0,_tpDec):F1} raw={MsBetween(_tpDec,_tpRaw):F1} passX={MsBetween(_tpRaw,_tpX):F1} passZ={MsBetween(_tpX,_tpZ):F1} yrecon={MsBetween(_tpZ,_tpY):F1}[yinterp={_yi:F1} encode={_enc:F1}] finalize={MsBetween(_tpY,_tpEnd):F1} total={MsBetween(_tp0,_tpEnd):F1}ms");
            return true;
        }

        const float BakeSAT = 120f;   // |sample| >= this ⇒ saturated (constant) coarse sample

        static void StageBakeEntry(int ox, int oz, int bw, int bh, int extLo, float s, float[] buf)
        {
            int step = _bakeStep;
            int overlap = _overlap;
            // The overlap skirt is a live-carve device (blob fidelity across seams), consumed in-RAM during the
            // carve and NEVER served from a bake — every world point is owned by one zone holding its full SDF.
            // So we store only the owned footprint, plus (step>1 only) a 2-coarse-cell interp halo, which is all the
            // upsampler reaches past an owned edge. The full skirt is never stored at any step.
            bool dropSkirt = step == 1;
            int width = bw - 2 * overlap;
            int gOrig, cOrigin, nsx, nsz;
            if (dropSkirt)
            {
                gOrig = overlap; cOrigin = 0; nsx = nsz = width;        // step==1: owned only, no interp halo
            }
            else
            {
                // owned + 2-coarse-cell halo, aligned to the voxel-0 lattice. cOrigin = global coarse index of
                // stored column 0, so the load maps a voxel's coarse cell via xs = cx/step - cOrigin.
                int nsFull = (bw + step - 1) / step;
                const int haloC = 2;
                int ownedLoC = overlap / step, ownedHiC = (overlap + width - 1) / step;
                int cLo = Math.Max(0, ownedLoC - haloC), cHi = Math.Min(nsFull, ownedHiC + 1 + haloC);
                gOrig = 0; cOrigin = cLo; nsx = nsz = cHi - cLo;
            }
            int nsy  = (bh + step - 1) / step;
            int ncol = nsx * nsz;
            var colYLo   = new int[ncol];
            var colYLen  = new int[ncol];
            var colOff   = new int[ncol];
            var colBelow = new sbyte[ncol];
            var colAbove = new sbyte[ncol];
            var colV     = new sbyte[nsy];
            var packed   = new List<sbyte>(ncol * 4);

            // v6 crossings accumulators (allocated only when storing crossings)
            bool xmode = Plugin._bakeCrossings;
            var crossPacked = xmode ? new List<float>(ncol * 2) : null;
            var colNCross   = xmode ? new byte[ncol] : null;

            for (int xs = 0; xs < nsx; xs++)
            for (int zs = 0; zs < nsz; zs++)
            {
                int cx = dropSkirt ? (gOrig + xs) : Math.Min((cOrigin + xs) * step, bw - 1);
                int cz = dropSkirt ? (gOrig + zs) : Math.Min((cOrigin + zs) * step, bw - 1);
                for (int ys = 0; ys < nsy; ys++)
                {
                    int cy = Math.Min(ys * step, bh - 1);
                    colV[ys] = ToSDFByte(buf[(cx * bh + cy) * bw + cz], 32f * s);
                }
                int col = xs * nsz + zs;
                if (xmode)
                {
                    // Store the column as its sign-crossing y-positions (sub-voxel, linear-interp to zero).
                    // Count (low 7 bits) + sign-of-below (high bit: 1 = solid/negative below).
                    int nc = 0;
                    for (int ys = 1; ys < nsy && nc < 127; ys++)
                    {
                        int a = colV[ys - 1], b = colV[ys];
                        if ((a < 0) != (b < 0))
                        {
                            float t = (a != b) ? a / (float)(a - b) : 0.5f;
                            crossPacked!.Add((ys - 1) + t);
                            nc++;
                        }
                    }
                    colNCross![col] = (byte)(nc | (colV[0] < 0 ? 0x80 : 0));
                    continue;
                }
                // Active band: non-saturated or straddling a sign change; the rest is constant fill.
                int minA = int.MaxValue, maxA = int.MinValue;
                for (int ys = 0; ys < nsy; ys++)
                {
                    sbyte v = colV[ys];
                    bool active = Math.Abs((int)v) < BakeSAT || (ys > 0 && ((v < 0) != (colV[ys - 1] < 0)));
                    if (active) { if (ys < minA) minA = ys; if (ys > maxA) maxA = ys; }
                }
                colOff[col] = packed.Count;
                colBelow[col] = colV[0];
                colAbove[col] = colV[nsy - 1];
                if (minA == int.MaxValue) { colYLo[col] = 0; colYLen[col] = 0; }
                else
                {
                    colYLo[col]  = minA;
                    colYLen[col] = maxA - minA + 1;
                    for (int ys = minA; ys <= maxA; ys++) packed.Add(colV[ys]);
                }
            }

            // bw stays the FULL field width (serve-eligibility check + owned math on load); nsx/nsz record the
            // actually-stored grid (owned for step==1, full for step>1).
            var z = new BakeZone {
                extLo = extLo, bh = bh, bw = bw, step = step, nsx = nsx, nsy = nsy, nsz = nsz, cOrigin = cOrigin,
                colYLo = colYLo, colYLen = colYLen, colOff = colOff,
                colBelow = colBelow, colAbove = colAbove, samples = packed.ToArray(),
                colCross = xmode ? crossPacked!.ToArray() : null,
                colNCross = colNCross,
                crossSlope = 127f / 32f   // bytes per Y-sample for a vertical surface; hydrate ramp (surface is exact at any slope)
            };
            // Bake in progress: a BAKE WORKER (off the main thread — WriteBakeFile runs under Task.Run) compresses
            // (off-lock, parallel) and appends straight to disk → RAM stays flat regardless of radius. A MAIN-THREAD
            // call here is a LIVE carve (a connected player generating a zone while the server bakes in the
            // background); it must NOT write into the bake file — that races the workers and corrupts the stream —
            // so it falls to the in-RAM staging dict instead, same as a non-bake live carve.
            bool bakeWorker = _bakeWriteStream != null
                && System.Threading.Thread.CurrentThread.ManagedThreadId != _mainThreadId;
            if (bakeWorker)
            {
                var blk = SerializeZoneBlock(ox, oz, z);
                lock (_bakeStreamLock)
                {
                    long off = _bakeWriteStream.Position;
                    _bakeWriteStream.Write(blk, 0, blk.Length);
                    _bakeWriteIndex.Add((ox, oz, off, blk.Length));
                }
            }
            else _bakePending[(ox, oz)] = z;
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
            int seed = Plugin.WorldSeed();
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
                if (_bakeFileStep == _bakeStep && IsZoneBaked(ox, oz)) continue;
                toCarve.Add((ox, oz));
            }
            // The bake is PURELY PROCEDURAL — location groundwork is NOT frozen in. It's applied as a live
            // overlay at load (PTGenerator.Generate), so the bake needs no location data (cold-bakeable,
            // deterministic) and groundwork is always applied with modifiers present + cached for revisit.

            int threads = Math.Max(1, Environment.ProcessorCount - 1);
            Log.LogInfo($"[ProceduralTerrain] bake: {toCarve.Count} zones to carve (radius={radius:F0}m, threads={threads})...");
            var bakeSw = System.Diagnostics.Stopwatch.StartNew();
            int doneCount = 0;
            Plugin._bakeProgressTotal = toCarve.Count;
            Plugin._bakeProgressDone  = 0;
            // Pre-warm Script pool so bake threads don't all create Scripts simultaneously.
            foreach (var mode in Plugin._pipeline.OfType<LuaModeDef>())
                mode.WarmPool(threads);

            // Gather preserved existing zones (incremental bakes) as compressed blocks BEFORE truncating the
            // output file — un-recarved zones get block-copied into the new file so they survive. Zero for a
            // full / cfg-changed bake. Held compressed in RAM (small), not as the multi-GB decompressed set.
            var preserved = new List<(int ox, int oz, byte[] blk)>();
            {
                var carveSet = new HashSet<(int, int)>(toCarve);
                var existingKeys = new List<(int, int)>();
                if (_bakeData != null) existingKeys.AddRange(_bakeData.Keys);
                else if (_bakeIndex != null) existingKeys.AddRange(_bakeIndex.Keys);
                foreach (var key in existingKeys)
                    if (!carveSet.Contains(key) && TryGetBakeZone(key.Item1, key.Item2, out var zz))
                        preserved.Add((key.Item1, key.Item2, SerializeZoneBlock(key.Item1, key.Item2, zz)));
            }

            string path = BakeFilePath(seed, cfgHash);
            Directory.CreateDirectory(PTConfigDir);
            CloseBakeStream();   // release the old read handle before overwriting the file

            // v4 STREAMED format written INCREMENTALLY: header, preserved blocks, then each freshly-carved zone
            // appended straight from its bake worker (StageBakeEntry → _bakeWriteStream) so peak RAM stays flat
            // regardless of radius; finally the offset index + footer. The zone count isn't known up front
            // (streamed), so it's written as a placeholder and patched in after the carve.
            // Write to a temp file and promote on success, so a killed/interrupted bake can't clobber an existing
            // good bake (what bit us once: an aborted 10k overwrote a working 5k, leaving cnt=0 + no footer).
            string tmpPath = path + ".tmp";
            var index = new List<(int ox, int oz, long off, int len)>(toCarve.Count + preserved.Count);
            using (var fs = new FileStream(tmpPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 1 << 20))
            using (var bw2 = new BinaryWriter(fs))
            {
                bw2.Write(0x564F5846u);          // magic
                bw2.Write((ushort)(Plugin._bakeCrossings ? 6 : 5));  // v5 = dense distance band; v6 = sign-crossings (per-column)
                bw2.Write((ulong)(uint)seed);
                bw2.Write(cfgHash);
                long countPos = fs.Position;
                bw2.Write((uint)0);              // zone count placeholder — patched after the carve

                foreach (var (pox, poz, blk) in preserved)
                { long off = fs.Position; bw2.Write(blk, 0, blk.Length); index.Add((pox, poz, off, blk.Length)); }
                bw2.Flush();

                // Freshly-carved zones stream into fs via StageBakeEntry during the parallel carve below.
                _bakeWriteStream = fs;
                _bakeWriteIndex  = index;
                _parallelBakeActive = true;
                // Carve at BAKE res (full by default), decoupled from live coarseXZ/coarseY. Restored in finally.
                int liveCoarseXZ = Plugin._coarseXZ, liveCoarseY = Plugin._coarseY;
                Plugin._coarseXZ = Plugin._bakeCoarseXZ; Plugin._coarseY = Plugin._bakeCoarseY;
                Log.LogInfo($"[ProceduralTerrain] bake carve res: coarseXZ={Plugin._coarseXZ} coarseY={Plugin._coarseY} (live was {liveCoarseXZ}/{liveCoarseY})");
                try
                {
                    System.Threading.Tasks.Parallel.ForEach(toCarve,
                        new System.Threading.Tasks.ParallelOptions { MaxDegreeOfParallelism = threads },
                        zone =>
                        {
                            var (zox, zoz) = zone;
                            var (yLo, yHi) = ComputeYBounds(zox, zoz, zoneW, s, wg);
                            // Per-zone guard: a throw on a bake worker otherwise surfaces as an AggregateException
                            // to the bake caller and (if unhandled) silently kills the process — no [PT] Carve,
                            // since that catch only covers the live main-thread path. Log the zone + full stack so
                            // a managed failure (e.g. an indexing bug at fine coarseXZ) is diagnosable, not fatal.
                            try
                            {
                                // Purely procedural carve — groundwork is a live overlay applied at load, not baked.
                                Carve(null, zox, zoz, zoneW, yLo, yHi);
                            }
                            catch (Exception ce)
                            {
                                Log.LogError($"[ProceduralTerrain] bake Carve threw at zone ({zox},{zoz}) coarseXZ={Plugin._coarseXZ} coarseY={Plugin._coarseY}: {ce.GetType().Name}: {ce.Message}\n{ce.StackTrace}");
                            }
                            int n = System.Threading.Interlocked.Increment(ref doneCount);
                            Plugin._bakeProgressDone = n;
                            if (n % 50 == 0 || n == toCarve.Count)
                            {
                                // Progress is best-effort — a transient RPC/ZPackage hiccup must NOT abort the whole
                                // bake (that throws out of the ForEach and leaves a half-written file). Guard it.
                                try
                                {
                                    float elapsed = bakeSw.ElapsedMilliseconds / 1000f;
                                    float eta = elapsed > 0f ? (toCarve.Count - n) / (n / elapsed) : 0f;
                                    Log.LogInfo($"[ProceduralTerrain] bake: {n}/{toCarve.Count} zones — {eta:F0}s remaining");
                                    var prog = new ZPackage();
                                    prog.Write(n); prog.Write(toCarve.Count);
                                    ZRoutedRpc.instance?.InvokeRoutedRPC(ZRoutedRpc.Everybody, "PTBakeProgress", prog);
                                }
                                catch (Exception pe) { Log.LogWarning($"[ProceduralTerrain] bake progress report failed (continuing): {pe.Message}"); }
                            }
                        });
                }
                catch (Exception bex)
                {
                    // An abort out of the ForEach (not a per-zone throw — those are caught above) must still fall
                    // through to the index/footer write below, so we finalize a VALID bake of the zones completed
                    // so far instead of leaving a headers-only file with no footer (unloadable).
                    Log.LogError($"[ProceduralTerrain] bake aborted mid-run after {index.Count} zones; finalizing partial bake: {bex.GetType().Name}: {bex.Message}\n{bex.StackTrace}");
                }
                finally { _parallelBakeActive = false; _bakeWriteStream = null; _bakeWriteIndex = null;
                          Plugin._coarseXZ = liveCoarseXZ; Plugin._coarseY = liveCoarseY; }   // restore live carve res

                bw2.Flush();
                long indexOff = fs.Position;
                foreach (var e in index) { bw2.Write(e.ox); bw2.Write(e.oz); bw2.Write(e.off); bw2.Write(e.len); }
                bw2.Write(indexOff);             // footer: where the index begins
                bw2.Flush();
                fs.Seek(countPos, SeekOrigin.Begin);
                bw2.Write((uint)index.Count);    // patch the real zone count
                bw2.Flush();
            }

            // Footer written → the temp file is a complete, valid bake. Promote it over any prior bake atomically
            // (reaching here means we didn't die mid-write; a killed process leaves the .tmp and the old file intact).
            CloseBakeStream();   // release any read handle on the target so the delete/rename isn't blocked by our own lock
            if (File.Exists(path)) File.Delete(path);
            File.Move(tmpPath, path);

            // Switch straight to streaming from the file we just wrote (it was written incrementally, never
            // held whole in RAM) — reopen read-only and index by offset.
            _bakeData = null;
            var idx2 = new Dictionary<(int, int), (long, int)>(index.Count);
            foreach (var e in index) idx2[(e.ox, e.oz)] = (e.off, e.len);
            _bakeIndex  = idx2;
            _bakeStream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            _bakeFileStep = _bakeStep;
            Plugin._bakeFileV6 = _bakeCrossings;   // just-written format — reads must match (loader sets this; the post-bake rewire bypasses it)
            _bakeZoneCache.Clear(); _bakeCacheOrder.Clear();   // drop any zones decoded against the old file/format
            _bakePending.Clear();
            long bakeSecs = bakeSw.ElapsedMilliseconds / 1000;
            float zps = bakeSecs > 0 ? index.Count / (float)bakeSecs : 0f;
            Log.LogInfo($"[ProceduralTerrain] Bake written (v{(_bakeCrossings ? 6 : 5)} streamed): {index.Count} zones in {bakeSecs / 60}:{bakeSecs % 60:00} ({zps:F1} zones/s), radius={radius:F0}m → {path}");
        }

        // ── Hot-reload: re-run the full voxel pipeline on every loaded zone ───────
        internal static void ResetWorldState()
        {
            LoadBiomeConfig();
            _bakePending.Clear();
            _bakeData = null;
            CloseBakeStream();
            _bakeLoadAttempted = false;
            _sdfCache.Clear();
            _groundworkedZones.Clear();   // keyed by zone index — must not leak across worlds
            _groundworkCache.Clear();     // ditto: a prior world's groundworked heights would be served for the same index
            _heightCache.Clear();
            VanillaHeight.Clear();
            lock (_surfHCache) _surfHCache.Clear();
            _zoneFaceCache.Clear();
            _locationPositions.Clear();
            _locationsCached = false;
            _noisePool       = null;
            _warpX           = null;
            _warpY           = null;
            _warpZ           = null;
            _warpX2          = null;
            _warpY2          = null;
            _warpZ2          = null;
            _hmWarpX         = null;
            _hmWarpZ         = null;
        }

        static void RecarveAll()
        {
            _sdfCache.Clear();
            _heightCache.Clear();
            VanillaHeight.Clear();
            ClearComposedSurfCache();
            lock (_surfHCache) _surfHCache.Clear();
            _zoneFaceCache.Clear();

            if (_skinMethod == null || _unskinMethod == null || _hmapField == null)
            {
                Log.LogWarning("[ProceduralTerrain] Hot-reload reflection handles missing — skipping.");
                return;
            }

            var voxelZoneType = FindType("Voxheim.Unity.VoxelZone", "Voxheim");
            if (voxelZoneType == null) return;
            var zones = UnityEngine.Resources.FindObjectsOfTypeAll(voxelZoneType);
            Log.LogInfo($"[ProceduralTerrain] Hot-reload: re-activating {zones.Length} zone(s).");

            foreach (var zoneObj in zones)
            {
                if (zoneObj is not UnityEngine.Object uObj || !uObj) continue;
                var hmap = _hmapField.GetValue(zoneObj);
                if (hmap == null) { Log.LogWarning("[ProceduralTerrain] Hot-reload: zone has no hmap, skipping."); continue; }
                string key = "";
                if (_zoneOriginX != null && _zoneOriginZ != null)
                {
                    int ox = (int)_zoneOriginX.GetValue(zoneObj)!;
                    int oz = (int)_zoneOriginZ.GetValue(zoneObj)!;
                    key = $"{ox},{oz}";
                }
                try
                {
                    _unskinMethod.Invoke(zoneObj, null);       // destroys the old VoxelZone, restores vanilla hmap
                    _skinMethod.Invoke(null, new[] { hmap });  // static Skin: fresh VoxelZone + Activate + s_pendingZones.Add
                    // 0.1.7: Skin enqueues the zone; Voxheim's worker pump completes it over the next frames
                    // (CompleteWorkerSkin). No synchronous finish — re-mesh lands async, which is fine.
                    Log.LogDebug($"[ProceduralTerrain] Hot-reload: re-skinned zone {key}.");
                }
                catch (Exception e) { var inner = e.InnerException ?? e; Log.LogWarning($"[ProceduralTerrain] Hot-reload zone {key}: {inner.GetType().Name}: {inner.Message}\n{inner.StackTrace}"); }
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
            sublabel = eta > 0 ? $"ETA {eta:F0}s  ·  {elS / 60}:{elS % 60:00} elapsed" : "Connecting…";
        }
        else
        {
            pct      = Plugin._bakeProgressTotal > 0 ? (float)Plugin._bakeProgressDone / Plugin._bakeProgressTotal : 0f;
            long elS = (Plugin._bakeProgressTimer?.ElapsedMilliseconds ?? 0) / 1000;
            float eta = pct > 0.01f ? elS / pct * (1f - pct) : 0f;
            label    = $"Baking terrain…  {pct * 100f:F0}%";
            sublabel = eta > 0 ? $"ETA {eta:F0}s  ·  {elS / 60}:{elS % 60:00} elapsed  ({Plugin._bakeProgressDone}/{Plugin._bakeProgressTotal} zones)" : "Starting…";
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
