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
    // Per-thread scratch buffers for the Carve pipeline. Held in a single [ThreadStatic]
    // struct so parallel bake workers each get their own set without 26 separate statics.
    struct CarveBuffers
    {
        // Main SDF working buffers (buf = active, tmp/orig for Taubin blur swap)
        public float[]? bufA, bufB, bufC;
        // ComputeZoneFaceCache working buffer (kept separate to avoid clobbering Carve's buf)
        public float[]? precomputeBuf;
        // BakeSdfSetup 2D coarse param grids [cw4 × cw4]
        public float[]? bk2dTgX, bk2dTgZ, bk2dBlurH, bk2dGradMag;
        public float[]? bk2dFlD, bk2dFlFade, bk2dFlLevel;
        // Full-res Mode B warped heightmap [bw × bw]
        public float[]? bkWarpedH;
        // Island/pocket BFS (bool and int queues)
        public bool[]? visitedBuf, percolVisited;
        public int[]?  islandQueue;
        // Pipeline mode param grids [pipelineIdx][paramIdx][cellIdx]
        public float[][][]? bkModeParams;
        // Pipeline mode auxiliary data [pipelineIdx][auxIdx][dataIdx]
        public float[][][]? bkModeAux;
        // Surface-anchored modes: coarse addh per cell [pipelineIdx][cellIdx], for full-res baseline correction
        public float[][]? bkModeBaseline;
    }

    public partial class Plugin
    {
        [ThreadStatic] static CarveBuffers _cb;

        internal static float BL(float[] a, int i00, int i10, int i01, int i11, float fx, float fz) =>
            Mathf.Lerp(Mathf.Lerp(a[i00], a[i10], fx), Mathf.Lerp(a[i01], a[i11], fx), fz);

        // ── Carve pass ────────────────────────────────────────────────────────────
        internal static void Carve(object? grid, int ox, int oz, int width, int yLo, int yHi, float gridScale = 0f, Voxheim.Core.HeightGrid? heightGrid = null, int voxelFloorY = int.MinValue)
        {
            float s;
            if (gridScale > 0f)
            {
                s = gridScale;
                if (_scale == null || _scale.Value <= 0f) { _scale = s; Log.LogInfo($"[ProceduralTerrain] gridScale resolved to {s}"); }
            }
            else
            {
                if (_scale == null || _scale.Value <= 0f)
                {
                    float raw = _scaleField != null ? (float)_scaleField.GetValue(null)! : 0f;
                    if (raw <= 0f) return;
                    _scale = raw;
                    Log.LogInfo($"[ProceduralTerrain] s_scale resolved to {_scale}");
                }
                s = _scale.Value;
            }
            var   wg = WorldGenerator.instance;

            if (!_bakeLoadAttempted) { _bakeLoadAttempted = true; LoadBakeFile(); }

            BuildLocationCache();

            float zoneX = (ox + width * 0.5f) * s;
            float zoneZ = (oz + width * 0.5f) * s;

            string zoneKey = $"{ox},{oz}";

            int overlap = _overlap;
            int bwEarly = width + 2 * overlap;

            // A: baked zones need NONE of the live machinery (BakeBlendedProfiles, margins, BakeSdfSetup,
            // FillPhase1). Serve straight from the bake using its OWN baked extent, before any of that runs.
            // This is what makes a baked world cheap per zone (previously BakeBlendedProfiles ~60ms/zone ran
            // even for baked zones because it sat above the old early-return).
            if ((!_parallelBakeActive || System.Threading.Thread.CurrentThread.ManagedThreadId == Plugin._mainThreadId)
                && TryGetBakeZone(ox, oz, out var bakedE) && bakedE.bw == bwEarly)
            {
                long ft0 = System.Diagnostics.Stopwatch.GetTimestamp();
                if (PopulateSparseFromBake(ox, oz, bwEarly, bakedE.bh, bakedE.extLo, s, voxelFloorY, overlap))
                {
                    double fms = (System.Diagnostics.Stopwatch.GetTimestamp() - ft0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                    if (fms > 2.0) Log.LogDebug($"[PT-load] baked zone ({ox},{oz}) fused={fms:F2}ms (bh={bakedE.bh})");
                    return;
                }
            }

            // +1 guard cell on the far (+x/+z) side: interpolation needs a coarse neighbor past the
            // last owned voxel. Without it, far-edge owned voxels clamp to a flat cell when CellVox>overlap
            // (coarseMult>1), stepping vs the neighbor zone. No-op at coarseMult=1 (clamp never triggers).
            int cw4Early = (bwEarly + CellVox - 1) / CellVox + 1;

            // Cheap pre-check: if no modified biome is within blend-reach of this zone, it's pure vanilla —
            // skip the supersampled blend AND all mode machinery, carve straight from the heightmap (VanillaFill).
            // Sample center + 4 corners expanded by the blend radius (so border zones still take the full path).
            bool knownVanilla = false;
            if (wg != null && _modifiedBiomes.Count > 0)
            {
                float m = width * s * 0.5f + _biomeBlendRadius + _coastFade;
                knownVanilla = !_modifiedBiomes.Contains(wg.GetBiome(zoneX, zoneZ))
                    && !_modifiedBiomes.Contains(wg.GetBiome(zoneX - m, zoneZ - m))
                    && !_modifiedBiomes.Contains(wg.GetBiome(zoneX + m, zoneZ - m))
                    && !_modifiedBiomes.Contains(wg.GetBiome(zoneX - m, zoneZ + m))
                    && !_modifiedBiomes.Contains(wg.GetBiome(zoneX + m, zoneZ + m));
            }
            else if (_modifiedBiomes.Count == 0)
            {
                knownVanilla = true;   // no biome has active modes at all (e.g. floor-test config)
            }

            long _tbbp = System.Diagnostics.Stopwatch.GetTimestamp();
            if (wg != null && !knownVanilla) BakeBlendedProfiles(cw4Early, ox, oz, overlap, s, wg);
            double bbpMs = (System.Diagnostics.Stopwatch.GetTimestamp() - _tbbp) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            if (bbpMs > 3.0) Log.LogDebug($"[PT-load] BakeBlendedProfiles ({ox},{oz}) = {bbpMs:F2}ms cw4Early={cw4Early}");

            CaveProfile ProfAt(int cx4, int cz4) =>
                (_blendedProfs != null && cx4 * cw4Early + cz4 < _blendedProfs.Length)
                    ? _blendedProfs[cx4 * cw4Early + cz4] : CaveProfile.Default;
            int lastC = cw4Early - 1;
            var centerProf = ProfAt(cw4Early / 2, cw4Early / 2);

            var centerBiome = wg?.GetBiome(zoneX, zoneZ) ?? Heightmap.Biome.None;
            Log.LogDebug($"[ProceduralTerrain] zone ({ox},{oz}) biome={centerBiome} pipeline={_pipeline.Length} modes blur={centerProf.blur_passes:F1} floorDepth={centerProf.floorDepth:F1}");

            float searchR = width * s * 0.5f + 200f + LocFadeR;
            var nearbyLocs = new List<(Vector2 pos, float extR)>();
            foreach (var loc in _locationPositions)
            {
                float dx = loc.pos.x - zoneX, dz = loc.pos.y - zoneZ;
                float locSearchR = Mathf.Max(loc.extR, LocProtectR) + LocFadeR;
                if (dx * dx + dz * dz < (searchR + locSearchR) * (searchR + locSearchR))
                    nearbyLocs.Add((loc.pos, LocProtectRadius(loc.pos, loc.extR)));
            }

            int hiMargin = 24, loMargin = 24;
            if (!knownVanilla)   // vanilla zones: _blendedProfs is stale (blend skipped) → default margins
            {
                float maxExtM = 0f, maxFloor = 0f;
                // Floor + extendUp margins from blended profile-level params (per-zone, per-biome)
                for (int i = 0; i < cw4Early * cw4Early && _blendedProfs != null && i < _blendedProfs.Length; i++)
                {
                    if (_blendedProfs[i].floorDepth > maxFloor) maxFloor = _blendedProfs[i].floorDepth;
                    if (_blendedProfs[i].extendUp  > maxExtM)  maxExtM  = _blendedProfs[i].extendUp;
                }
                if (maxExtM  > 0f) hiMargin = Mathf.Max(24, (int)Mathf.Ceil(maxExtM  / s) + 8);
                if (maxFloor > 0f) loMargin = Mathf.Max(24, (int)Mathf.Ceil(maxFloor / s) + 4);
                if (_fieldDepthLo > 0f) loMargin = Mathf.Max(loMargin, (int)Mathf.Ceil(_fieldDepthLo / s));   // deepen field for deep tunnels
            }

            int extLo   = yLo - loMargin, extHi = yHi + hiMargin;
            int bw      = width + 2 * overlap;
            int bh      = extHi - extLo + 1;
            int bufSize = bw * bh * bw;

            if (_cb.bufA == null || _cb.bufA.Length < bufSize) _cb.bufA = new float[bufSize];
            if (_cb.bufB == null || _cb.bufB.Length < bufSize) _cb.bufB = new float[bufSize];
            if (_cb.bufC == null || _cb.bufC.Length < bufSize) _cb.bufC = new float[bufSize];
            float[] buf = _cb.bufA, tmp = _cb.bufB, orig = _cb.bufC;
            // (baked zones already returned above, before the live machinery ran)

            // Baked zones are fully handled by PopulateSparseFromBake above (early return). Reaching here
            // means the zone is unbaked → live carve.
            bool fromBake = false;

            // Full end-to-end phase timing (hoisted so the tail — stage/surfH/sparse — which runs outside the
            // !fromBake block is covered too; every segment timed directly, no subtraction).
            long _ph0 = System.Diagnostics.Stopwatch.GetTimestamp();
            long _phSetup = _ph0, _phFill = _ph0, _phBlur = _ph0, _phIsland = _ph0, _phPocket = _ph0,
                 _phStage = _ph0, _phSurf = _ph0, _phSparse = _ph0;
            bool anyPipelineActive = false;   // assigned after BakeSdfSetup populates _cb.bkModeParams
            if (!fromBake)
            {
            InitNoise();

            int   cw4     = cw4Early;
            BakeSdfSetup(ox, oz, overlap, s, extLo, bh, bw, cw4, wg, nearbyLocs, heightGrid);
            _phSetup = System.Diagnostics.Stopwatch.GetTimestamp();

            anyPipelineActive = !knownVanilla && _pipeline.Length > 0 && _cb.bkModeParams != null &&
                _pipeline.Any(m => _cb.bkModeParams![m.PipelineIndex][0].Any(v => v > 0f));
            if (anyPipelineActive)
                FillPhase1(buf, bw, bh, extLo, s, ox, oz, overlap, wg, cw4, nearbyLocs, heightGrid);
            else
                VanillaFill(buf, bw, bh, extLo, s, ox, oz, overlap, wg, heightGrid);   // no active modes → emit the base surface directly (skip compose/upsample)
            _phFill = System.Diagnostics.Stopwatch.GetTimestamp();

            CaveProfile blurProf = centerProf;
            if (_blendedProfs != null)
            {
                int cCx = cw4 / 2, cCz = cw4 / 2;
                blurProf = _blendedProfs[cCx * cw4 + cCz];
            }
            int blurPasses = anyPipelineActive
                ? Mathf.Max(Mathf.CeilToInt(blurProf.blur_passes), _transitionBlurPasses)
                : 0;

            const float taubinLambda = 0.5f;
            const float taubinMu    = -0.53f;
            int   pbSz = bw * bh * bw;

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
                for (int i = 0; i < pbSz; i++)
                    buf[i] = orig[i] + weight * (tmp[i] - orig[i]);
            }

            for (int pass = 0; pass < blurPasses; pass++)
            {
                Array.Copy(buf, orig, pbSz);
                TaubinStep(taubinLambda);
                Array.Copy(buf, orig, pbSz);
                TaubinStep(taubinMu);
            }
            _phBlur = System.Diagnostics.Stopwatch.GetTimestamp();

            if (_islandRemoval && anyPipelineActive) {   // vanilla heightfield has no floating solid → skip the flood
                int islandBufSize = bw * bh * bw;
                int stride_x      = bh * bw;
                int terNegX25 = overlap;
                int terPosX25 = overlap + width - 1;
                int terNegZ25 = overlap;
                int terPosZ25 = overlap + width - 1;
                if (_cb.visitedBuf  == null || _cb.visitedBuf.Length  < islandBufSize) _cb.visitedBuf  = new bool[islandBufSize];
                if (_cb.islandQueue == null || _cb.islandQueue.Length < islandBufSize) _cb.islandQueue = new int[islandBufSize];
                Array.Clear(_cb.visitedBuf, 0, islandBufSize);

                int qHead = 0, qTail = 0;

                for (int cx = 0; cx < bw; cx++)
                for (int cz = 0; cz < bw; cz++)
                {
                    int idx = cx * stride_x + cz;
                    if (buf[idx] < 0f && !_cb.visitedBuf[idx])
                    { _cb.visitedBuf[idx] = true; _cb.islandQueue[qTail++] = idx; }
                }
                while (qHead < qTail)
                {
                    int idx = _cb.islandQueue[qHead++];
                    int cx  = idx / stride_x;
                    int rem = idx % stride_x;
                    int cy  = rem / bw;
                    int cz  = rem % bw;
                    int ni;
                    if (cx > 0)    { ni = idx - stride_x; if (!_cb.visitedBuf[ni] && buf[ni] < 0f) { _cb.visitedBuf[ni] = true; _cb.islandQueue[qTail++] = ni; } }
                    if (cx < bw-1) { ni = idx + stride_x; if (!_cb.visitedBuf[ni] && buf[ni] < 0f) { _cb.visitedBuf[ni] = true; _cb.islandQueue[qTail++] = ni; } }
                    if (cy > 0)    { ni = idx - bw;        if (!_cb.visitedBuf[ni] && buf[ni] < 0f) { _cb.visitedBuf[ni] = true; _cb.islandQueue[qTail++] = ni; } }
                    if (cy < bh-1) { ni = idx + bw;        if (!_cb.visitedBuf[ni] && buf[ni] < 0f) { _cb.visitedBuf[ni] = true; _cb.islandQueue[qTail++] = ni; } }
                    if (cz > 0)    { ni = idx - 1;         if (!_cb.visitedBuf[ni] && buf[ni] < 0f) { _cb.visitedBuf[ni] = true; _cb.islandQueue[qTail++] = ni; } }
                    if (cz < bw-1) { ni = idx + 1;         if (!_cb.visitedBuf[ni] && buf[ni] < 0f) { _cb.visitedBuf[ni] = true; _cb.islandQueue[qTail++] = ni; } }
                }

                bool ungNX = false, ungPX = false, ungNZ = false, ungPZ = false;
                for (int cy = 0; cy < bh; cy++)
                for (int cz = 0; cz < bw; cz++)
                {
                    int iNX = terNegX25 * stride_x + cy * bw + cz;
                    int iPX = terPosX25 * stride_x + cy * bw + cz;
                    if (!ungNX && buf[iNX] < 0f && !_cb.visitedBuf[iNX]) ungNX = true;
                    if (!ungPX && buf[iPX] < 0f && !_cb.visitedBuf[iPX]) ungPX = true;
                }
                for (int cy = 0; cy < bh; cy++)
                for (int cx = 0; cx < bw; cx++)
                {
                    int iNZ = cx * stride_x + cy * bw + terNegZ25;
                    int iPZ = cx * stride_x + cy * bw + terPosZ25;
                    if (!ungNZ && buf[iNZ] < 0f && !_cb.visitedBuf[iNZ]) ungNZ = true;
                    if (!ungPZ && buf[iPZ] < 0f && !_cb.visitedBuf[iPZ]) ungPZ = true;
                }

                bool needsFullPass = ungNX || ungPX || ungNZ || ungPZ;
                if (needsFullPass)
                {
                    EnsureFixpoint(ox, oz, width, ungNX, ungPX, ungNZ, ungPZ);

                    Array.Clear(_cb.visitedBuf, 0, islandBufSize);
                    qHead = 0; qTail = 0;

                    for (int cx = 0; cx < bw; cx++)
                    for (int cz = 0; cz < bw; cz++)
                    {
                        int idx = cx * stride_x + cz;
                        if (buf[idx] < 0f && !_cb.visitedBuf[idx])
                        { _cb.visitedBuf[idx] = true; _cb.islandQueue[qTail++] = idx; }
                    }

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
                            if (buf[bufIdx] < 0f && !_cb.visitedBuf[bufIdx])
                            { _cb.visitedBuf[bufIdx] = true; _cb.islandQueue[qTail++] = bufIdx; }
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
                        int idx = _cb.islandQueue[qHead++];
                        int cx  = idx / stride_x;
                        int rem = idx % stride_x;
                        int cy  = rem / bw;
                        int cz  = rem % bw;
                        int ni;
                        if (cx > 0)    { ni = idx - stride_x; if (!_cb.visitedBuf[ni] && buf[ni] < 0f) { _cb.visitedBuf[ni] = true; _cb.islandQueue[qTail++] = ni; } }
                        if (cx < bw-1) { ni = idx + stride_x; if (!_cb.visitedBuf[ni] && buf[ni] < 0f) { _cb.visitedBuf[ni] = true; _cb.islandQueue[qTail++] = ni; } }
                        if (cy > 0)    { ni = idx - bw;        if (!_cb.visitedBuf[ni] && buf[ni] < 0f) { _cb.visitedBuf[ni] = true; _cb.islandQueue[qTail++] = ni; } }
                        if (cy < bh-1) { ni = idx + bw;        if (!_cb.visitedBuf[ni] && buf[ni] < 0f) { _cb.visitedBuf[ni] = true; _cb.islandQueue[qTail++] = ni; } }
                        if (cz > 0)    { ni = idx - 1;         if (!_cb.visitedBuf[ni] && buf[ni] < 0f) { _cb.visitedBuf[ni] = true; _cb.islandQueue[qTail++] = ni; } }
                        if (cz < bw-1) { ni = idx + 1;         if (!_cb.visitedBuf[ni] && buf[ni] < 0f) { _cb.visitedBuf[ni] = true; _cb.islandQueue[qTail++] = ni; } }
                    }
                }

                {
                    int faceSz25 = bh * bw;
                    var ac = new ZoneFaceCache {
                        extLo = extLo, bh = bh, bw = bw,
                        negX    = new bool[faceSz25], posX    = new bool[faceSz25],
                        negZ    = new bool[faceSz25], posZ    = new bool[faceSz25],
                        negXAir = new bool[faceSz25], posXAir = new bool[faceSz25],
                        negZAir = new bool[faceSz25], posZAir = new bool[faceSz25],
                        percol      = new bool[4, 4],
                        percolValid = false,
                    };
                    for (int cy = 0; cy < bh; cy++)
                    for (int cz = 0; cz < bw; cz++)
                    {
                        int fIdx = cy * bw + cz;
                        ac.negX[fIdx] = _cb.visitedBuf[terNegX25 * stride_x + cy * bw + cz];
                        ac.posX[fIdx] = _cb.visitedBuf[terPosX25 * stride_x + cy * bw + cz];
                    }
                    for (int cy = 0; cy < bh; cy++)
                    for (int cx = 0; cx < bw; cx++)
                    {
                        int fIdx = cy * bw + cx;
                        ac.negZ[fIdx] = _cb.visitedBuf[cx * stride_x + cy * bw + terNegZ25];
                        ac.posZ[fIdx] = _cb.visitedBuf[cx * stride_x + cy * bw + terPosZ25];
                    }
                    // Compute THIS zone's percol from the RAW composed field (buf is still pre-island/pre-pocket
                    // here) and cache it with percolValid=true — so a neighbour's EnsureFixpoint reuses it instead
                    // of recomposing the whole field (the big bake redundancy). exclude=null (vacuous, see helper).
                    int pvSz = bw * bh * bw;
                    if (_cb.percolVisited == null || _cb.percolVisited.Length < pvSz) _cb.percolVisited = new bool[pvSz];
                    ac.percol      = ComputePercolFaces(buf, null, _cb.percolVisited, _cb.islandQueue!, bw, bh, stride_x, terNegX25, terPosX25, terNegZ25, terPosZ25);
                    ac.percolValid = true;
                    PutZoneFace((ox, oz), ac);
                }

                int removed = 0;
                for (int i = 0; i < islandBufSize; i++)
                    if (!_cb.visitedBuf[i] && buf[i] < 0f) { buf[i] = s; removed++; }

                Log.LogDebug($"[ProceduralTerrain] island-fill zone ({ox},{oz}): pre={!needsFullPass} seeds={qTail} removed={removed}");
            }
            _phIsland = System.Diagnostics.Stopwatch.GetTimestamp();

            if (_pocketRemoval && anyPipelineActive) {   // vanilla heightfield has no sealed air pockets → skip the flood
                Array.Clear(_cb.visitedBuf!, 0, bw * bh * bw);
                int stride_x = bh * bw;
                int qHead = 0, qTail = 0;

                void TryEnqueueAir(int i)
                {
                    if (!_cb.visitedBuf![i] && buf[i] >= 0f) { _cb.visitedBuf[i] = true; _cb.islandQueue![qTail++] = i; }
                }

                for (int cx = 0; cx < bw; cx++)
                for (int cz = 0; cz < bw; cz++)
                    TryEnqueueAir(cx * stride_x + (bh - 1) * bw + cz);

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
                    int idx = _cb.islandQueue![qHead++];
                    int cx  = idx / stride_x;
                    int rem = idx % stride_x;
                    int cy  = rem / bw;
                    int cz  = rem % bw;
                    int ni;
                    if (cx > 0)    { ni = idx - stride_x; if (!_cb.visitedBuf![ni] && buf[ni] >= 0f) { _cb.visitedBuf[ni] = true; _cb.islandQueue[qTail++] = ni; } }
                    if (cx < bw-1) { ni = idx + stride_x; if (!_cb.visitedBuf![ni] && buf[ni] >= 0f) { _cb.visitedBuf[ni] = true; _cb.islandQueue[qTail++] = ni; } }
                    if (cy > 0)    { ni = idx - bw;        if (!_cb.visitedBuf![ni] && buf[ni] >= 0f) { _cb.visitedBuf[ni] = true; _cb.islandQueue[qTail++] = ni; } }
                    if (cy < bh-1) { ni = idx + bw;        if (!_cb.visitedBuf![ni] && buf[ni] >= 0f) { _cb.visitedBuf[ni] = true; _cb.islandQueue[qTail++] = ni; } }
                    if (cz > 0)    { ni = idx - 1;         if (!_cb.visitedBuf![ni] && buf[ni] >= 0f) { _cb.visitedBuf[ni] = true; _cb.islandQueue[qTail++] = ni; } }
                    if (cz < bw-1) { ni = idx + 1;         if (!_cb.visitedBuf![ni] && buf[ni] >= 0f) { _cb.visitedBuf[ni] = true; _cb.islandQueue[qTail++] = ni; } }
                }

                int filled = 0;
                int total  = bw * bh * bw;
                for (int i = 0; i < total; i++)
                    if (!_cb.visitedBuf![i] && buf[i] >= 0f) { buf[i] = -s; filled++; }

                Log.LogDebug($"[ProceduralTerrain] pocket-fill zone ({ox},{oz}): filled={filled}");
            }
            _phPocket = System.Diagnostics.Stopwatch.GetTimestamp();

            } // end if (!fromBake)

            if (!fromBake && _bakeWriteStream != null && (_bakeVanilla || anyPipelineActive))   // stage only during an actual bake; by default skip unmodified zones (carve live, keep them out of the bake file)
                StageBakeEntry(ox, oz, bw, bh, extLo, s, buf);
            _phStage = System.Diagnostics.Stopwatch.GetTimestamp();

            {
                var surfH = new float[bw * bw];
                for (int cx = 0; cx < bw; cx++)
                for (int cz = 0; cz < bw; cz++)
                {
                    float sh = float.NaN;
                    for (int cy = bh - 1; cy >= 0; cy--)
                    {
                        float below = buf[(cx * bh + cy) * bw + cz];
                        if (below < 0f)
                        {
                            // Sub-voxel surface: interpolate the SDF zero-crossing between this solid voxel (cy)
                            // and the air voxel above it (cy+1), instead of snapping to the integer row. The
                            // integer snap stair-steps surfH to s-metre plateaus → zero-gradient normals on
                            // smooth slopes (trees read "flat" and dangle on ridges).
                            float t = 0f;
                            if (cy + 1 < bh)
                            {
                                float above = buf[(cx * bh + (cy + 1)) * bw + cz];   // >= 0 (air)
                                float denom = below - above;
                                if (denom != 0f) t = Mathf.Clamp01(below / denom);   // fraction from cy up toward cy+1
                            }
                            sh = (extLo + cy + t) * s;
                            break;
                        }
                    }
                    surfH[cx * bw + cz] = sh;
                }
                if (!_parallelBakeActive)
                    lock (_surfHCache) _surfHCache[(ox, oz)] = (bw, extLo, s, surfH);
            }
            _phSurf = System.Diagnostics.Stopwatch.GetTimestamp();

            // Bake threads skip sdfCache (they stage the dense file via StageBakeEntry from buf). Only the
            // main-thread live/load path builds the sparse per-column cache PTGenerator consumes.
            if (!_parallelBakeActive || System.Threading.Thread.CurrentThread.ManagedThreadId == Plugin._mainThreadId)
            {
            var transBytes  = new sbyte[bh];
            var transHasOvr = new bool[bh];
            if (_sparseScratch == null || _sparseScratch.Length < width * bh * width) _sparseScratch = new sbyte[width * bh * width];
            if (_colVScratch   == null || _colVScratch.Length   < bh)                 _colVScratch   = new sbyte[bh];
            var scratch = _sparseScratch; var colV = _colVScratch;
            var colStart = new int[width * width];
            var colLen   = new int[width * width];
            var colOff   = new int[width * width];
            int total  = 0;
            int floorY = voxelFloorY;   // int.MinValue ⇒ default is air everywhere (skip air only)

            for (int ci = 0; ci < width; ci++)
            for (int ri = 0; ri < width; ri++)
            {
                int cx = ci + overlap, cz = ri + overlap;
                int col = ci * width + ri;
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
                // Compute the column and its deviating run [first..last] vs the floor-rule default.
                int first = -1, last = -1;
                for (int cy = 0; cy < bh; cy++)
                {
                    sbyte v = transHasOvr[cy] ? transBytes[cy] : ToSDFByte(buf[(cx * bh + cy) * bw + cz], s);
                    colV[cy] = v;
                    int wy = extLo + cy;
                    bool isDefault = (wy < floorY) ? (v == -127) : (v == 127);   // solid below floorY, air above
                    if (!isDefault) { if (first < 0) first = cy; last = cy; }
                }
                colOff[col] = total;
                if (first < 0) { colStart[col] = -1; colLen[col] = 0; continue; }
                colStart[col] = first;
                int len = last - first + 1;
                colLen[col] = len;
                Array.Copy(colV, first, scratch, total, len);
                total += len;
            }

            var band = new sbyte[total];
            Array.Copy(scratch, 0, band, 0, total);
            _sdfCache[zoneKey] = new Plugin.ZoneField {
                extLo = extLo, bh = bh, width = width,
                colStart = colStart, colLen = colLen, colOff = colOff, band = band
            };
            }
            _phSparse = System.Diagnostics.Stopwatch.GetTimestamp();
            double _pms(long a, long b) => (b - a) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            if (_pms(_ph0, _phSparse) > 5.0)
                Log.LogDebug($"[PT-phases] ({ox},{oz}) setup={_pms(_ph0,_phSetup):F1} fill={_pms(_phSetup,_phFill):F1} blur={_pms(_phFill,_phBlur):F1} island={_pms(_phBlur,_phIsland):F1} pocket={_pms(_phIsland,_phPocket):F1} stage={_pms(_phPocket,_phStage):F1} surf={_pms(_phStage,_phSurf):F1} sparse={_pms(_phSurf,_phSparse):F1} total={_pms(_ph0,_phSparse):F1}ms (bh={bh})");

            if (_dumpZonesRemaining > 0)
            {
                _dumpZonesRemaining--;
                DumpZoneData(ox, oz, overlap, s, wg, cw4Early, bw, bh, extLo, buf);
            }
        }

        // ── Zone data dump ───────────────────────────────────────────────────────
        static void DumpZoneData(int ox, int oz, int overlap, float s, WorldGenerator wg,
                                  int cw4, int bw, int bh, int extLo, float[] buf)
        {
            try
            {
                float coarseStep = CellVox * s;
                int pad = (int)(_biomeBlendRadius / coarseStep) + 2;
                int pcw = cw4 + 2 * pad;

                var sb = new System.Text.StringBuilder();
                sb.AppendLine("{");
                sb.AppendLine($"  \"zone\": {{ \"ox\": {ox}, \"oz\": {oz}, \"overlap\": {overlap}, \"s\": {s}, \"bw\": {bw}, \"bh\": {bh}, \"extLo\": {extLo}, \"cw4\": {cw4}, \"blendRadius\": {_biomeBlendRadius} }},");

                sb.Append("  \"biomeGrid\": { \"pcw\": "); sb.Append(pcw); sb.AppendLine(",");
                sb.Append("    \"data\": [");
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

                sb.AppendLine("  \"blendedProfs\": [");
                for (int cx4 = 0; cx4 < cw4; cx4++)
                for (int cz4 = 0; cz4 < cw4; cz4++)
                {
                    int idx = cx4 * cw4 + cz4;
                    bool last = (cx4 == cw4 - 1 && cz4 == cw4 - 1);
                    float wx = (ox - overlap + cx4 * CellVox) * s, wz = (oz - overlap + cz4 * CellVox) * s;
                    int pIdx2 = (cx4 + pad) * pcw + (cz4 + pad);
                    int biomeRaw = -1;
                    if (_biomeFrac != null) { float domF2=0f; for(int sl=0;sl<MaxBiomeSlots;sl++){float f=_biomeFrac[pIdx2*MaxBiomeSlots+sl];if(f>domF2){domF2=f;biomeRaw=sl;}} }
                    float totW  = (_biomeTotW    != null && idx < _biomeTotW.Length)    ? _biomeTotW[idx]    : -1f;
                    float blur  = (_blendedProfs != null && idx < _blendedProfs.Length) ? _blendedProfs[idx].blur_passes    : -1f;
                    float flD   = (_blendedProfs != null && idx < _blendedProfs.Length) ? _blendedProfs[idx].floorDepth : -1f;
                    sb.AppendLine($"    {{\"cx4\":{cx4},\"cz4\":{cz4},\"wx\":{wx:F1},\"wz\":{wz:F1},\"biome\":{biomeRaw},\"totW\":{totW:F3},\"blur\":{blur:F2},\"floorDepth\":{flD:F2}}}{(last ? "" : ",")}");
                }
                sb.AppendLine("  ],");

                int midZ = bw / 2;
                sb.AppendLine($"  \"sdfSlice\": {{ \"fixedBufZ\": {midZ}, \"worldZ\": {(oz * s + (midZ - overlap) * s):F1},");
                sb.Append("    \"data\": [");
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

        // ── Cross-zone face cache helpers ─────────────────────────────────────────

        internal static (int yLo, int yHi) ComputeYBoundsPublic(int ox, int oz, int w, float s, WorldGenerator wg)
            => ComputeYBounds(ox, oz, w, s, wg);

        static (int yLo, int yHi) ComputeYBounds(int ox, int oz, int w, float s, WorldGenerator wg)
        {
            int overlap = _overlap;
            float minH = float.MaxValue, maxH = float.MinValue;
            for (int gx = -overlap; gx <= w + overlap; gx++)
            for (int gz = -overlap; gz <= w + overlap; gz++)
            {
                float h = GroundHeight((ox + gx) * s, (oz + gz) * s, wg) / s;
                if (h < minH) minH = h;
                if (h > maxH) maxH = h;
            }
            return ((int)Math.Floor(minH) - 2, (int)Math.Ceiling(maxH) + 2);
        }

        static int _lastFixpointHops = 0;
        [ThreadStatic] static float _mdPeakX, _mdPeakZ;

        static readonly (int dx, int dz, int inFace)[] FaceHops = {
            (-1,  0, 1),
            ( 1,  0, 0),
            ( 0, -1, 3),
            ( 0,  1, 2),
        };

        static void EnsureFixpoint(int ox, int oz, int w,
                                   bool ungNX, bool ungPX, bool ungNZ, bool ungPZ)
        {
            float s  = _scale ?? 1f;
            var   wg = WorldGenerator.instance;
            if (wg == null) return;
            int FixpointMaxZones = _fixpointMaxZones;

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

        static void ComputeZoneFaceCache(int ox, int oz, int w, int yLo, int yHi)
        {
            int overlap = _overlap;
            float s  = _scale ?? 1f;
            var   wg = WorldGenerator.instance;

            int bwZ = w + 2 * overlap;
            int cw4 = (bwZ + CellVox - 1) / CellVox + 1;   // +1 far-side guard cell (see cw4Early note)

            if (wg != null) BakeBlendedProfiles(cw4, ox, oz, overlap, s, wg);

            int hiMargin = 24, loMargin = 24;
            {
                float maxExtM = 0f, maxFloor = 0f;
                int nb2 = cw4 * cw4;
                for (int i = 0; i < nb2 && _blendedProfs != null && i < _blendedProfs.Length; i++)
                {
                    if (_blendedProfs[i].floorDepth > maxFloor) maxFloor = _blendedProfs[i].floorDepth;
                    if (_blendedProfs[i].extendUp  > maxExtM)  maxExtM  = _blendedProfs[i].extendUp;
                }
                if (maxExtM  > 0f) hiMargin = Mathf.Max(24, (int)Mathf.Ceil(maxExtM  / s) + 8);
                if (maxFloor > 0f) loMargin = Mathf.Max(24, (int)Mathf.Ceil(maxFloor / s) + 4);
                if (_fieldDepthLo > 0f) loMargin = Mathf.Max(loMargin, (int)Mathf.Ceil(_fieldDepthLo / s));   // deepen field for deep tunnels
            }

            int extLo  = yLo - loMargin;
            int extHi  = yHi + hiMargin;
            int bhZ    = extHi - extLo + 1;
            int bufSz  = bwZ * bhZ * bwZ;

            if (_cb.precomputeBuf == null || _cb.precomputeBuf.Length < bufSz) _cb.precomputeBuf = new float[bufSz];
            float[] buf = _cb.precomputeBuf;

            InitNoise();

            float zoneX   = (ox + w * 0.5f) * s;
            float zoneZ   = (oz + w * 0.5f) * s;
            float searchR = w * s * 0.5f + 200f + LocFadeR;
            var   nearby  = new List<(Vector2 pos, float extR)>();
            foreach (var loc in _locationPositions)
            {
                float dx = loc.pos.x - zoneX, dz = loc.pos.y - zoneZ;
                float locSearchR = Mathf.Max(loc.extR, LocProtectR) + LocFadeR;
                if (dx * dx + dz * dz < (searchR + locSearchR) * (searchR + locSearchR)) nearby.Add((loc.pos, LocProtectRadius(loc.pos, loc.extR)));
            }

            BakeSdfSetup(ox, oz, overlap, s, extLo, bhZ, bwZ, cw4, wg, nearby);
            FillPhase1(buf, bwZ, bhZ, extLo, s, ox, oz, overlap, wg, cw4, nearby);

            int stX   = bhZ * bwZ;
            int bufSz2 = bwZ * bhZ * bwZ;
            if (_cb.visitedBuf  == null || _cb.visitedBuf.Length  < bufSz2) _cb.visitedBuf  = new bool[bufSz2];
            if (_cb.islandQueue == null || _cb.islandQueue.Length < bufSz2) _cb.islandQueue = new int[bufSz2];
            Array.Clear(_cb.visitedBuf, 0, bufSz2);
            int qHead = 0, qTail = 0;

            for (int cx = 0; cx < bwZ; cx++)
            for (int cz = 0; cz < bwZ; cz++)
            {
                int idx = cx * stX + cz;
                if (buf[idx] < 0f && !_cb.visitedBuf[idx])
                { _cb.visitedBuf[idx] = true; _cb.islandQueue[qTail++] = idx; }
            }

            void SeedFromNeighborFace(bool[]? nFace, int nExtLo, int nBh, int nBw,
                                      int ourCx, bool iterateCz)
            {
                if (nFace == null) return;
                int maxCperp = iterateCz ? bwZ : bwZ;
                for (int cperp = 0; cperp < maxCperp; cperp++)
                for (int cy = 0; cy < bhZ; cy++)
                {
                    int worldY = extLo + cy;
                    int ncy    = worldY - nExtLo;
                    if (ncy < 0 || ncy >= nBh) continue;
                    int nIdx = ncy * nBw + cperp;
                    if (nIdx >= nFace.Length || !nFace[nIdx]) continue;
                    int bufIdx = iterateCz
                        ? ourCx * stX + cy * bwZ + cperp
                        : cperp * stX + cy * bwZ + ourCx;
                    if (buf[bufIdx] < 0f && !_cb.visitedBuf[bufIdx])
                    { _cb.visitedBuf[bufIdx] = true; _cb.islandQueue[qTail++] = bufIdx; }
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

            while (qHead < qTail)
            {
                int idx = _cb.islandQueue[qHead++];
                int cx  = idx / stX;
                int rem = idx % stX;
                int cy  = rem / bwZ;
                int cz  = rem % bwZ;
                int ni;
                if (cx > 0)     { ni = idx - stX;  if (!_cb.visitedBuf[ni] && buf[ni] < 0f) { _cb.visitedBuf[ni] = true; _cb.islandQueue[qTail++] = ni; } }
                if (cx < bwZ-1) { ni = idx + stX;  if (!_cb.visitedBuf[ni] && buf[ni] < 0f) { _cb.visitedBuf[ni] = true; _cb.islandQueue[qTail++] = ni; } }
                if (cy > 0)     { ni = idx - bwZ;  if (!_cb.visitedBuf[ni] && buf[ni] < 0f) { _cb.visitedBuf[ni] = true; _cb.islandQueue[qTail++] = ni; } }
                if (cy < bhZ-1) { ni = idx + bwZ;  if (!_cb.visitedBuf[ni] && buf[ni] < 0f) { _cb.visitedBuf[ni] = true; _cb.islandQueue[qTail++] = ni; } }
                if (cz > 0)     { ni = idx - 1;    if (!_cb.visitedBuf[ni] && buf[ni] < 0f) { _cb.visitedBuf[ni] = true; _cb.islandQueue[qTail++] = ni; } }
                if (cz < bwZ-1) { ni = idx + 1;    if (!_cb.visitedBuf[ni] && buf[ni] < 0f) { _cb.visitedBuf[ni] = true; _cb.islandQueue[qTail++] = ni; } }
            }

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
                cache.negX[fIdx] = _cb.visitedBuf[terNegX * stX + cy * bwZ + cz];
                cache.posX[fIdx] = _cb.visitedBuf[terPosX * stX + cy * bwZ + cz];
            }
            for (int cy = 0; cy < bhZ; cy++)
            for (int cx = 0; cx < bwZ; cx++)
            {
                int fIdx = cy * bwZ + cx;
                cache.negZ[fIdx] = _cb.visitedBuf[cx * stX + cy * bwZ + terNegZ];
                cache.posZ[fIdx] = _cb.visitedBuf[cx * stX + cy * bwZ + terPosZ];
            }

            Array.Clear(_cb.visitedBuf, 0, bufSz2);
            qHead = 0; qTail = 0;

            void TryEnqueueAirSeed(int idx) {
                if (!_cb.visitedBuf[idx] && buf[idx] >= 0f)
                { _cb.visitedBuf[idx] = true; _cb.islandQueue![qTail++] = idx; }
            }

            for (int cx = 0; cx < bwZ; cx++)
            for (int cz = 0; cz < bwZ; cz++)
                TryEnqueueAirSeed(cx * stX + (bhZ - 1) * bwZ + cz);
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
                int idx = _cb.islandQueue[qHead++];
                int cx  = idx / stX;
                int rem = idx % stX;
                int cy  = rem / bwZ;
                int cz  = rem % bwZ;
                int ni;
                if (cx > 0)     { ni = idx - stX;  if (!_cb.visitedBuf[ni] && buf[ni] >= 0f) { _cb.visitedBuf[ni] = true; _cb.islandQueue[qTail++] = ni; } }
                if (cx < bwZ-1) { ni = idx + stX;  if (!_cb.visitedBuf[ni] && buf[ni] >= 0f) { _cb.visitedBuf[ni] = true; _cb.islandQueue[qTail++] = ni; } }
                if (cy > 0)     { ni = idx - bwZ;  if (!_cb.visitedBuf[ni] && buf[ni] >= 0f) { _cb.visitedBuf[ni] = true; _cb.islandQueue[qTail++] = ni; } }
                if (cy < bhZ-1) { ni = idx + bwZ;  if (!_cb.visitedBuf[ni] && buf[ni] >= 0f) { _cb.visitedBuf[ni] = true; _cb.islandQueue[qTail++] = ni; } }
                if (cz > 0)     { ni = idx - 1;    if (!_cb.visitedBuf[ni] && buf[ni] >= 0f) { _cb.visitedBuf[ni] = true; _cb.islandQueue[qTail++] = ni; } }
                if (cz < bwZ-1) { ni = idx + 1;    if (!_cb.visitedBuf[ni] && buf[ni] >= 0f) { _cb.visitedBuf[ni] = true; _cb.islandQueue[qTail++] = ni; } }
            }

            for (int cy = 0; cy < bhZ; cy++)
            for (int cz = 0; cz < bwZ; cz++)
            {
                int fIdx = cy * bwZ + cz;
                cache.negXAir[fIdx] = _cb.visitedBuf[terNegX * stX + cy * bwZ + cz];
                cache.posXAir[fIdx] = _cb.visitedBuf[terPosX * stX + cy * bwZ + cz];
            }
            for (int cy = 0; cy < bhZ; cy++)
            for (int cx = 0; cx < bwZ; cx++)
            {
                int fIdx = cy * bwZ + cx;
                cache.negZAir[fIdx] = _cb.visitedBuf[cx * stX + cy * bwZ + terNegZ];
                cache.posZAir[fIdx] = _cb.visitedBuf[cx * stX + cy * bwZ + terPosZ];
            }

            if (_cb.percolVisited == null || _cb.percolVisited.Length < bufSz2) _cb.percolVisited = new bool[bufSz2];
            // exclude = the air-connected set, preserving this method's original behaviour exactly (the exclusion
            // only ever matches air, so it's a no-op for the solid flood — see ComputePercolFaces).
            cache.percol      = ComputePercolFaces(buf, _cb.visitedBuf, _cb.percolVisited, _cb.islandQueue!, bwZ, bhZ, stX, terNegX, terPosX, terNegZ, terPosZ);
            cache.percolValid = true;

            PutZoneFace((ox, oz), cache);
        }

        // Solid-body face-connectivity (percolation): for each of the 4 side faces, flood the SOLID (buf<0) and
        // record which other faces it reaches. `exclude` (if non-null) blocks voxels — ComputeZoneFaceCache passes
        // its air-connected set to preserve its exact behaviour, the primary carve passes null. Either way the
        // exclusion only matches AIR (the flood only ever touches solid), so it's a no-op and the result is a pure
        // function of `buf`. Must be fed the RAW composed field (pre-island/pre-pocket removal) in both callers.
        static bool[,] ComputePercolFaces(float[] buf, bool[]? exclude, bool[] percolVisited, int[] queue,
            int bwD, int bhD, int stX, int terNegX, int terPosX, int terNegZ, int terPosZ)
        {
            var percol = new bool[4, 4];
            int n = bwD * bhD * bwD;
            for (int fromFace = 0; fromFace < 4; fromFace++)
            {
                Array.Clear(percolVisited, 0, n);
                int qHead = 0, qTail = 0;
                bool hitNX = false, hitPX = false, hitNZ = false, hitPZ = false;
                int facesLeft = 3;

                if (fromFace <= 1)
                {
                    int fCx = fromFace == 0 ? terNegX : terPosX;
                    for (int cy = 0; cy < bhD; cy++)
                    for (int cz = terNegZ; cz <= terPosZ; cz++)
                    {
                        int idx = fCx * stX + cy * bwD + cz;
                        if (buf[idx] < 0f && (exclude == null || !exclude[idx]) && !percolVisited[idx])
                        { percolVisited[idx] = true; queue[qTail++] = idx; }
                    }
                }
                else
                {
                    int fCz = fromFace == 2 ? terNegZ : terPosZ;
                    for (int cy = 0; cy < bhD; cy++)
                    for (int cx = terNegX; cx <= terPosX; cx++)
                    {
                        int idx = cx * stX + cy * bwD + fCz;
                        if (buf[idx] < 0f && (exclude == null || !exclude[idx]) && !percolVisited[idx])
                        { percolVisited[idx] = true; queue[qTail++] = idx; }
                    }
                }

                while (qHead < qTail && facesLeft > 0)
                {
                    int idx = queue[qHead++];
                    int pcx = idx / stX;
                    int rem = idx % stX;
                    int pcy = rem / bwD;
                    int pcz = rem % bwD;

                    if (pcx == terNegX && fromFace != 0 && !hitNX) { hitNX = true; facesLeft--; }
                    if (pcx == terPosX && fromFace != 1 && !hitPX) { hitPX = true; facesLeft--; }
                    if (pcz == terNegZ && fromFace != 2 && !hitNZ) { hitNZ = true; facesLeft--; }
                    if (pcz == terPosZ && fromFace != 3 && !hitPZ) { hitPZ = true; facesLeft--; }

                    int ni;
                    if (pcx > terNegX) { ni = idx - stX; if (!percolVisited[ni] && buf[ni] < 0f && (exclude == null || !exclude[ni])) { percolVisited[ni] = true; queue[qTail++] = ni; } }
                    if (pcx < terPosX) { ni = idx + stX; if (!percolVisited[ni] && buf[ni] < 0f && (exclude == null || !exclude[ni])) { percolVisited[ni] = true; queue[qTail++] = ni; } }
                    if (pcy > 0)       { ni = idx - bwD; if (!percolVisited[ni] && buf[ni] < 0f && (exclude == null || !exclude[ni])) { percolVisited[ni] = true; queue[qTail++] = ni; } }
                    if (pcy < bhD-1)   { ni = idx + bwD; if (!percolVisited[ni] && buf[ni] < 0f && (exclude == null || !exclude[ni])) { percolVisited[ni] = true; queue[qTail++] = ni; } }
                    if (pcz > terNegZ) { ni = idx - 1;   if (!percolVisited[ni] && buf[ni] < 0f && (exclude == null || !exclude[ni])) { percolVisited[ni] = true; queue[qTail++] = ni; } }
                    if (pcz < terPosZ) { ni = idx + 1;   if (!percolVisited[ni] && buf[ni] < 0f && (exclude == null || !exclude[ni])) { percolVisited[ni] = true; queue[qTail++] = ni; } }
                }

                percol[fromFace, 0] = hitNX;
                percol[fromFace, 1] = hitPX;
                percol[fromFace, 2] = hitNZ;
                percol[fromFace, 3] = hitPZ;
            }
            return percol;
        }

        // ── Placement height ──────────────────────────────────────────────────────────
        // Zone-grid origin (voxel coords) for a world point — MUST match how Carve/Voxheim key _sdfCache/_surfHCache
        // (origins are 64*k + gridOffset, gridOffset = ((-(int)(32/s)) mod 64); e.g. 64k+32 at s=1). The old
        // `gx - gx%64` form (offset 0) silently missed every lookup → location snapping + veg-conform no-op'd.
        internal static (int ox, int oz) ZoneOriginFromWorld(float worldX, float worldZ, float s)
        {
            const int width = 64;
            int gridOffset = ((-(int)(32f / s)) % width + width) % width;
            int ox = (int)Math.Floor((worldX / s - gridOffset) / width) * width + gridOffset;
            int oz = (int)Math.Floor((worldZ / s - gridOffset) / width) * width + gridOffset;
            return (ox, oz);
        }

        internal static float FindCarvedSurfaceY(float worldX, float originalY, float worldZ)
        {
            float s = ActiveScale();
            int overlap = _overlap;
            int gx = Mathf.RoundToInt(worldX / s);
            int gz = Mathf.RoundToInt(worldZ / s);
            var (ox, oz) = ZoneOriginFromWorld(worldX, worldZ, s);
            (int bw, int extLo, float zs, float[] surfH) entry;
            lock (_surfHCache)
                if (!_surfHCache.TryGetValue((ox, oz), out entry)) return originalY;
            int cx = Mathf.Clamp(gx - (ox - overlap), 0, entry.bw - 1);
            int cz = Mathf.Clamp(gz - (oz - overlap), 0, entry.bw - 1);
            float sh = entry.surfH[cx * entry.bw + cz];
            return float.IsNaN(sh) ? originalY : Mathf.Max(originalY, sh);
        }

        // Zone key (ox,oz) that TrySampleSurfaceY will look up for a world point — for diagnostics.
        internal static string SurfKeyFor(float worldX, float worldZ)
        {
            var (ox, oz) = ZoneOriginFromWorld(worldX, worldZ, ActiveScale());
            string present;
            lock (_surfHCache) { var ks = new List<string>(); foreach (var k in _surfHCache.Keys) { ks.Add($"({k.Item1},{k.Item2})"); if (ks.Count >= 4) break; } present = string.Join(",", ks); }
            return $"({ox},{oz}) present[{present}]";
        }

        // Raw voxel surface Y at a world point (highest solid), or false if that zone's surfH isn't cached yet.
        // Used by the vegetation-conform patch to snap height and build a finite-diff surface normal.
        internal static bool TrySampleSurfaceY(float worldX, float worldZ, out float sh)
        {
            sh = 0f;
            float s = ActiveScale();
            int overlap = _overlap;
            var (ox, oz) = ZoneOriginFromWorld(worldX, worldZ, s);
            (int bw, int extLo, float zs, float[] surfH) entry;
            lock (_surfHCache)
                if (!_surfHCache.TryGetValue((ox, oz), out entry)) return false;
            // Fractional column coordinate within the zone buffer, then bilinear-blend the 4 surrounding columns
            // (nearest-neighbor rounding snapped trees to a column and read a neighbour's Y on slopes).
            float fx = worldX / s - (ox - overlap);
            float fz = worldZ / s - (oz - overlap);
            int x0 = Mathf.Clamp(Mathf.FloorToInt(fx), 0, entry.bw - 1), x1 = Mathf.Clamp(x0 + 1, 0, entry.bw - 1);
            int z0 = Mathf.Clamp(Mathf.FloorToInt(fz), 0, entry.bw - 1), z1 = Mathf.Clamp(z0 + 1, 0, entry.bw - 1);
            float tx = Mathf.Clamp01(fx - x0), tz = Mathf.Clamp01(fz - z0);
            float v00 = entry.surfH[x0 * entry.bw + z0], v10 = entry.surfH[x1 * entry.bw + z0];
            float v01 = entry.surfH[x0 * entry.bw + z1], v11 = entry.surfH[x1 * entry.bw + z1];
            // Any NaN among the 4 (unset column) → can't interpolate cleanly; fall back to nearest valid corner.
            if (float.IsNaN(v00) || float.IsNaN(v10) || float.IsNaN(v01) || float.IsNaN(v11))
            {
                float nn = !float.IsNaN(v00) ? v00 : !float.IsNaN(v10) ? v10 : !float.IsNaN(v01) ? v01 : v11;
                if (float.IsNaN(nn)) return false;
                sh = nn;
                return true;
            }
            sh = Mathf.Lerp(Mathf.Lerp(v00, v10, tx), Mathf.Lerp(v01, v11, tx), tz);
            return true;
        }

        // Surface probe shared by the veg conform gate AND the debug viz, so they can never disagree.
        // Returns false if the centre column is unknown (surfH miss). Otherwise fills sh (bilinear surface Y),
        // normal (central-difference, for tree tilt orientation), and reject. Reject fires on EITHER:
        //   (a) averaged tilt too steep (normal.y < cos maxTilt), or
        //   (b) footprint support: the ground drops away faster than maxTilt in ANY of the 4 directions under
        //       the base — this catches cliff edges the central-difference normal averages into looking flat,
        //       the "trunk dangling over a void" case.
        // 8 unit directions (axes + diagonals) for the footprint support ring.
        static readonly (float dx, float dz)[] _ringDirs =
        {
            (1,0),(-1,0),(0,1),(0,-1),
            (0.7071f,0.7071f),(0.7071f,-0.7071f),(-0.7071f,0.7071f),(-0.7071f,-0.7071f),
        };

        internal static bool TrySurfaceConform(float x, float z, float maxTiltDeg, out float sh, out Vector3 normal, out bool reject)
        {
            normal = Vector3.up; reject = false;
            if (!TrySampleSurfaceY(x, z, out sh)) return false;
            // Axis samples for the tilt-orientation normal (central difference).
            const float d = 2f;
            bool gpx = TrySampleSurfaceY(x + d, z, out float hpx), gmx = TrySampleSurfaceY(x - d, z, out float hmx);
            bool gpz = TrySampleSurfaceY(x, z + d, out float hpz), gmz = TrySampleSurfaceY(x, z - d, out float hmz);
            bool axisOk = gpx && gmx && gpz && gmz;
            if (axisOk)
                normal = new Vector3(-(hpx - hmx) / (2f * d), 1f, -(hpz - hmz) / (2f * d)).normalized;

            float cosT = Mathf.Cos(maxTiltDeg * Mathf.Deg2Rad);
            float tanT = Mathf.Tan(maxTiltDeg * Mathf.Deg2Rad);

            // Footprint support: sample an 8-direction ring at the base radius. The ground must not fall away
            // faster than maxTilt in ANY direction — this catches thin/diagonal ridges the 4-axis normal misses.
            float r = _vegSupportRadius;
            int valid = 0; float maxDrop = 0f;
            foreach (var (ux, uz) in _ringDirs)
                if (TrySampleSurfaceY(x + ux * r, z + uz * r, out float hr))
                { valid++; maxDrop = Mathf.Max(maxDrop, sh - hr); }

            bool tiltBad = axisOk && normal.y < cosT;
            // Need a quorum of valid ring samples before trusting the support verdict (avoid over-rejecting at
            // uncarved zone borders where most samples miss).
            bool supportBad = valid >= 5 && maxDrop > r * tanT;
            reject = tiltBad || supportBad;
            return true;
        }

        // ── BakeSdfSetup ─────────────────────────────────────────────────────────
        static void BakeSdfSetup(
            int ox, int oz, int overlap, float s, int extLo, int bh, int bw, int cw4,
            WorldGenerator? wg, List<(Vector2 pos, float extR)> nearbyLocs,
            Voxheim.Core.HeightGrid? heightGrid = null)
        {
            var pipeline = _pipeline;
            int pLen     = pipeline.Length;
            int nb2Size  = cw4 * cw4;

            // Ensure bkModeParams sized [pipelineIdx][paramIdx][cellIdx]
            if (_cb.bkModeParams == null || _cb.bkModeParams.Length < pLen)
                _cb.bkModeParams = new float[pLen][][];
            for (int mi = 0; mi < pLen; mi++)
            {
                int nParams = pipeline[mi].ParamCount;
                if (_cb.bkModeParams[mi] == null || _cb.bkModeParams[mi].Length < nParams)
                    _cb.bkModeParams[mi] = new float[nParams][];
                for (int pi2 = 0; pi2 < nParams; pi2++)
                    if (_cb.bkModeParams[mi][pi2] == null || _cb.bkModeParams[mi][pi2].Length < nb2Size)
                        _cb.bkModeParams[mi][pi2] = new float[nb2Size];
            }

            // Ensure bkModeAux sized
            if (_cb.bkModeAux == null || _cb.bkModeAux.Length < pLen)
                _cb.bkModeAux = new float[pLen][][];
            for (int mi = 0; mi < pLen; mi++)
                if (_cb.bkModeAux[mi] == null)
                    _cb.bkModeAux[mi] = Array.Empty<float[]>();

            // Ensure per-mode coarse baseline arrays (surface-anchored modes only)
            if (_cb.bkModeBaseline == null || _cb.bkModeBaseline.Length < pLen)
                _cb.bkModeBaseline = new float[pLen][];
            for (int mi = 0; mi < pLen; mi++)
                if (pipeline[mi].SurfaceAnchored &&
                    (_cb.bkModeBaseline[mi] == null || _cb.bkModeBaseline[mi]!.Length < nb2Size))
                    _cb.bkModeBaseline[mi] = new float[nb2Size];

            // Gradient arrays sized based on cw4+1 grid
            int mdSize = (cw4 + 1) * (cw4 + 1);
            if (_cb.bk2dTgX     == null || _cb.bk2dTgX.Length     < mdSize) _cb.bk2dTgX     = new float[mdSize];
            if (_cb.bk2dTgZ     == null || _cb.bk2dTgZ.Length     < mdSize) _cb.bk2dTgZ     = new float[mdSize];
            if (_cb.bk2dBlurH   == null || _cb.bk2dBlurH.Length   < mdSize) _cb.bk2dBlurH   = new float[mdSize];
            if (_cb.bk2dGradMag == null || _cb.bk2dGradMag.Length < mdSize) _cb.bk2dGradMag = new float[mdSize];

            // Floor depth/fade grids
            if (_cb.bk2dFlD     == null || _cb.bk2dFlD.Length     < nb2Size) _cb.bk2dFlD     = new float[nb2Size];
            if (_cb.bk2dFlFade  == null || _cb.bk2dFlFade.Length  < nb2Size) _cb.bk2dFlFade  = new float[nb2Size];
            if (_cb.bk2dFlLevel == null || _cb.bk2dFlLevel.Length < nb2Size) _cb.bk2dFlLevel = new float[nb2Size];

            // Populate floor arrays from blended profiles
            for (int i = 0; i < nb2Size && _blendedProfs != null && i < _blendedProfs.Length; i++)
            {
                _cb.bk2dFlD!    [i] = _blendedProfs[i].floorDepth;
                _cb.bk2dFlFade! [i] = _blendedProfs[i].floorFade;
                _cb.bk2dFlLevel![i] = _blendedProfs[i].floorLevel;
            }

            // BakeBegin for each mode
            foreach (var mode in pipeline)
                mode.BakeBegin(ref _cb, nb2Size, bh, cw4);

            // Check if any mode requiring grad field is active
            bool anyMdActive = pipeline.Any(m => m.NeedsGradField &&
                _cb.bkModeParams != null && m.PipelineIndex >= 0 &&
                _cb.bkModeParams[m.PipelineIndex][0].Any(v => v > 0f));

            // BlurRadius: max across all grad-field modes
            float mdBlurRadius = pipeline.Where(m => m.NeedsGradField).Select(m => m.GradBlurRadius).DefaultIfEmpty(128f).Max();
            mdBlurRadius = Mathf.Clamp(mdBlurRadius, 16f, 2048f);

            if (anyMdActive && wg != null)
            {
                ComputeGradField(wg, (ox - overlap) * s, (oz - overlap) * s, cw4 + 1, mdBlurRadius, CellVox * s,
                                 _cb.bk2dTgX!, _cb.bk2dTgZ!, _cb.bk2dBlurH!, _cb.bk2dGradMag!);
                // estimate island center as peak of blurH in coarse grid
                float peakH = float.MinValue;
                _mdPeakX = 0f; _mdPeakZ = 0f;
                for (int cx4 = 0; cx4 < cw4; cx4++)
                for (int cz4 = 0; cz4 < cw4; cz4++)
                {
                    float h = _cb.bk2dBlurH![cx4 * cw4 + cz4];
                    if (h > peakH) { peakH = h; _mdPeakX = (ox - overlap + cx4 * CellVox) * s; _mdPeakZ = (oz - overlap + cz4 * CellVox) * s; }
                }
            }

            // Main coarse cell loop
            for (int cx4 = 0; cx4 < cw4; cx4++)
            for (int cz4 = 0; cz4 < cw4; cz4++)
            {
                int   cx4w  = Math.Min(cx4 * CellVox, bw - 1);
                int   cz4w  = Math.Min(cz4 * CellVox, bw - 1);
                int   gx4   = ox - overlap + cx4w, gz4 = oz - overlap + cz4w;
                float wx4   = gx4 * s, wz4 = gz4 * s;
                int   cellIdx = cx4 * cw4 + cz4;

                var args = new BakeCellArgs {
                    worldX = wx4, worldZ = wz4,
                    cx4 = cx4, cz4 = cz4, cellIdx = cellIdx, cw4 = cw4, bh = bh,
                    s = s, extLoS = extLo * s,
                    wg = wg!
                };

                if (_composeMode == 1) continue;   // Path B bakes per-mode inside the sequential compose below
                if (_blendMode == 1)
                    foreach (var mode in pipeline)
                        BakeColumnSdfBlend(mode, ref _cb, in args, cw4);
                else
                    foreach (var mode in pipeline)
                        mode.BakeColumn(ref _cb, in args);
            }

            // ── Path B (composeMode=1): TRUE sequential composition into a running iso-coarse SDF ──
            // For each mode in pipeline order: surface-anchored modes derive their {elevation, frame}
            // from the running field of PRIOR modes (so they conform to the built-up surface); the mode
            // bakes; its column composes into the running field; an isotropic eikonal redistances it so
            // the next mode reads a true distance field. FillPhase1 trilinearly upsamples the result.
            if (_composeMode == 1)
            {
                int cvXZ = CellVox, cvY = CellVoxY;
                int ch = (bh + cvY - 1) / cvY + 1;              // coarse-Y levels (Y cell = cvY voxels), +1 guard
                int cellCount = cw4 * cw4;
                if (_runSDF     == null || _runSDF.Length     < cellCount * ch) _runSDF     = new float[cellCount * ch];
                if (_composeInH == null || _composeInH.Length < cellCount)
                { _composeInH = new float[cellCount]; _composeInGX = new float[cellCount]; _composeInGZ = new float[cellCount]; _composeInGMag = new float[cellCount]; }
                if (_composeHCell == null || _composeHCell.Length < cellCount)
                { _composeHCell = new float[cellCount]; _composeFloorY = new float[cellCount]; }
                float scale2  = _scale ?? 1f;
                float extLoS2 = extLo * s;
                float xzStep  = cvXZ * s;                        // XZ world spacing
                float yStep   = cvY  * s;                        // Y world spacing (aniso-aware eikonal below)

                // (1) init the running field = groundworked base column per cell; cache hCell/floorY.
                for (int cx4 = 0; cx4 < cw4; cx4++)
                for (int cz4 = 0; cz4 < cw4; cz4++)
                {
                    int cell = cx4 * cw4 + cz4, cbaseC = cell * ch;
                    int cxw = Math.Min(cx4 * cvXZ, bw - 1), czw = Math.Min(cz4 * cvXZ, bw - 1);
                    float wx = (ox - overlap + cxw) * s, wz = (oz - overlap + czw) * s;
                    float hCell = heightGrid != null ? heightGrid[cxw - overlap, czw - overlap] * s
                                : (wg != null ? GroundHeight(wx, wz, wg) : 0f);
                    float floorD = (_blendedProfs != null && cell < _blendedProfs.Length) ? _blendedProfs[cell].floorDepth : 0f;
                    _composeHCell![cell]  = hCell;
                    _composeFloorY![cell] = hCell - floorD;
                    for (int cyc = 0; cyc < ch; cyc++) _runSDF![cbaseC + cyc] = (extLoS2 + cyc * yStep) - hCell;
                }

                // Eikonal redistance is only needed if a mode in THIS zone reads the running field as a distance
                // metric (surface_blobs). Non-blob zones (meadows/BF/swamp) skip it entirely — big per-zone save.
                bool needsEikonal = _eikonal;
                if (needsEikonal)
                {
                    needsEikonal = false;
                    foreach (var m in pipeline)
                        if (m.NeedsEikonal && m.PipelineIndex >= 0
                            && _cb.bkModeParams![m.PipelineIndex][0].Any(v => v > 0f)) { needsEikonal = true; break; }
                }

                // (2) per mode: [derive incoming] -> bake -> compose -> redistance.
                bool firstBlobViz = true;   // only the first surface_blobs mode clears the zone overlay; later ones append
                foreach (var mode in pipeline)
                {
                    int mi = mode.PipelineIndex;
                    if (mi < 0) continue;

                    // Skip modes with NO active cell in this zone (all params zero). Otherwise EVERY mode —
                    // including the slow Lua ones — runs its full per-cell bake on every zone even where no
                    // nearby biome uses it (the "gabor/contour logs far away" bug + a big share of live cost).
                    // Biome blending is preserved: cells near a biome border get non-zero params from the
                    // neighbour profile, so the mode still runs where it actually contributes.
                    {
                        var p0a = _cb.bkModeParams?[mi]?[0];
                        bool active = false;
                        if (p0a != null) for (int pi = 0; pi < p0a.Length; pi++) if (p0a[pi] > 0f) { active = true; break; }
                        if (!active) continue;
                    }

                    // SurfaceBlobs: SDF→SDF operator — union noise-modulated balls onto EVERY surface
                    // (floors + ceilings) via |D| − rEff, reading the running distance field in place.
                    // Needs eikonal=true for metric |D|. Skips the normal bake/compose path.
                    if (mode is SurfaceBlobs)
                    {
                        // Discrete spheres CENTERED ON the surface → clear hemispheres. Snap a jittered
                        // 3D lattice to the surface (c = p − D·∇D), then union min(runSDF, |x−c| − r).
                        int cc = (cw4 / 2) * cw4 + (cw4 / 2);
                        float maxR = _cb.bkModeParams![mi][0][cc];           // amplitude = MAX radius / gate
                        if (maxR <= 0f) { continue; }
                        float minR = _cb.bkModeParams![mi][2][cc];           // minradius (0 ⇒ fixed radius)
                        if (minR < 0f) minR = 0f; if (minR > maxR) minR = maxR;
                        // Stamp shape: oriented along the (composed) surface normal stored per blob.
                        float stretchN = _cb.bkModeParams![mi][3][cc]; if (stretchN <= 0f) stretchN = 1f;  // scale along N
                        float taper    = _cb.bkModeParams![mi][4][cc]; if (taper < 0f) taper = 0f; if (taper > 1f) taper = 1f;
                        float pnorm    = _cb.bkModeParams![mi][5][cc]; if (pnorm < 1f) pnorm = 2f;          // cross-section exponent
                        float stretchG = _cb.bkModeParams![mi][11][cc]; if (stretchG <= 0f) stretchG = 1f; // scale along G (downslope)
                        float stretchT = _cb.bkModeParams![mi][12][cc]; if (stretchT <= 0f) stretchT = 1f; // scale along T (cross-slope)
                        float taperPow = _cb.bkModeParams![mi][13][cc]; if (taperPow <= 0f) taperPow = 1f; // taper curve
                        // Distribution (Tier 2): acceptance bias on which lattice points become stamps.
                        float coverage    = _cb.bkModeParams![mi][6][cc];
                        int   bias        = Mathf.RoundToInt(_cb.bkModeParams![mi][7][cc]);
                        float biasSpacing = Mathf.Max(_cb.bkModeParams![mi][8][cc], 0.001f);
                        float biasWidth   = Mathf.Clamp(_cb.bkModeParams![mi][9][cc], 0f, 0.5f);
                        float biasPhase   = _cb.bkModeParams![mi][10][cc];
                        float slopeLo     = _cb.bkModeParams![mi][14][cc];
                        float slopeHi     = _cb.bkModeParams![mi][15][cc];
                        float altLo       = _cb.bkModeParams![mi][16][cc];
                        float altHi       = _cb.bkModeParams![mi][17][cc];
                        float clusterFreq = _cb.bkModeParams![mi][18][cc];
                        float clusterThr  = _cb.bkModeParams![mi][19][cc];
                        float aspectLo    = _cb.bkModeParams![mi][20][cc];
                        float aspectHi    = _cb.bkModeParams![mi][21][cc];
                        float curvLo      = _cb.bkModeParams![mi][22][cc];
                        float curvHi      = _cb.bkModeParams![mi][23][cc];
                        bool  carve       = _cb.bkModeParams![mi][24][cc] > 0.5f;
                        float fuse        = Mathf.Max(0f, _cb.bkModeParams![mi][25][cc]);
                        int   chainN      = Mathf.Max(0, Mathf.RoundToInt(_cb.bkModeParams![mi][26][cc]));
                        float chainStep   = _cb.bkModeParams![mi][27][cc];
                        float chainWander = _cb.bkModeParams![mi][28][cc];
                        float chainInward = Mathf.Clamp01(_cb.bkModeParams![mi][29][cc]);
                        float ridgeRatio  = _cb.bkModeParams![mi][30][cc];
                        float branchProb  = _cb.bkModeParams![mi][31][cc];
                        float branchAngle = _cb.bkModeParams![mi][32][cc];
                        int   branchDepth = Mathf.Max(0, Mathf.RoundToInt(_cb.bkModeParams![mi][33][cc]));
                        float flowSteer   = _cb.bkModeParams![mi][34][cc];
                        float flowFreq    = _cb.bkModeParams![mi][35][cc];
                        float hubSeek     = _cb.bkModeParams![mi][36][cc];
                        float hubSpacing  = _cb.bkModeParams![mi][37][cc];
                        float keepDepth   = _cb.bkModeParams![mi][38][cc];
                        float chainLevel  = Mathf.Clamp01(_cb.bkModeParams![mi][39][cc]);
                        bool  tile        = _cb.bkModeParams![mi][40][cc] > 0.5f && wg != null;
                        bool  chainHoriz  = _cb.bkModeParams![mi][45][cc] > 0.5f;   // launch chain horizontally (flat biomes)
                        float chainSlope  = _cb.bkModeParams![mi][41][cc];
                        float biomeMajority = _cb.bkModeParams![mi][42][cc];
                        float skyOpen     = _cb.bkModeParams![mi][43][cc];
                        float concaveRatio = _cb.bkModeParams![mi][44][cc];
                        // Peak (un-blended) radius across all biomes that use this mode → the CONSTANT radius to use when
                        // biomemajority gates in (so the tube doesn't shrink with the biome blend near the edge).
                        float peakMaxR = maxR, peakMinR = minR;
                        foreach (var kv in _biomeParams)
                        {
                            var bp = kv.Value;
                            if (bp != null && mi < bp.Length && bp[mi] != null && bp[mi].Length > 2)
                            { if (bp[mi][0] > peakMaxR) peakMaxR = bp[mi][0]; if (bp[mi][2] > peakMinR) peakMinR = bp[mi][2]; }
                        }
                        float freq = _cb.bkModeParams![mi][1][cc];
                        float L = freq > 1e-4f ? 1f / freq : 20f;              // lattice spacing (world units)
                        // Stamp reach grows with the largest axis stretch; sets the no-merge spacing.
                        float reach = maxR * Mathf.Max(1f, Mathf.Max(stretchN, Mathf.Max(stretchG, stretchT)));
                        // (3) Enforce L >= 2*reach only for HARD ADD, so discrete bumps don't merge into mush.
                        // Carve (tunnels) or fused add (continuous spines/coral) WANT merging → frequency sets
                        // density directly. Merge-intent decouples seed spacing from stamp size.
                        if (!carve && fuse <= 0f) L = Mathf.Max(L, 2f * reach);
                        // Total reach includes the chain walk, so seeds near a border whose chain enters this
                        // zone are still enumerated (lattice padding only; chains still truncate across zones).
                        float totalReach = reach + chainN * Mathf.Abs(chainStep);
                        float ox0 = (ox - overlap), oz0 = (oz - overlap);

                        // trilinear sample of the running field at a world position
                        float SampleD(float wx, float wy, float wz)
                        {
                            float gx = (wx / s - ox0) / cvXZ, gz = (wz / s - oz0) / cvXZ, gy = (wy - extLoS2) / yStep;
                            gx = Mathf.Clamp(gx, 0f, cw4 - 1.001f); gz = Mathf.Clamp(gz, 0f, cw4 - 1.001f); gy = Mathf.Clamp(gy, 0f, ch - 1.001f);
                            int x0 = (int)gx, z0 = (int)gz, y0 = (int)gy; float fx = gx - x0, fz = gz - z0, fy = gy - y0;
                            int x1 = x0 + 1, z1 = z0 + 1, y1 = y0 + 1;
                            float V(int x, int z, int y) => _runSDF![(x * cw4 + z) * ch + y];
                            float c00 = V(x0,z0,y0) + (V(x0,z0,y1)-V(x0,z0,y0))*fy, c01 = V(x0,z1,y0) + (V(x0,z1,y1)-V(x0,z1,y0))*fy;
                            float c10 = V(x1,z0,y0) + (V(x1,z0,y1)-V(x1,z0,y0))*fy, c11 = V(x1,z1,y0) + (V(x1,z1,y1)-V(x1,z1,y0))*fy;
                            float c0 = c00 + (c01-c00)*fz, c1 = c10 + (c11-c10)*fz;
                            return c0 + (c1-c0)*fx;
                        }
                        // deterministic hash jitter in [0,1)
                        float Hash(int a, int b, int c2) { float n = Mathf.Sin(a*127.1f + b*311.7f + c2*74.7f) * 43758.5453f; return n - Mathf.Floor(n); }
                        // GLOBAL composed surface (base ground + formation relief, from the shared noise) — a pure
                        // function of x,z, identical in every zone. Snapping the seed/walk to this (tile mode) makes
                        // the whole chain a pure global function → every zone regenerates it identically and carves
                        // its slice → full-length cross-zone tunnels, following the REAL surface (no vanilla bias).
                        float GH(float wx, float wz) => ComposedSurfaceHeight(wx, wz, wg);

                        // (1) In-field test: is a world point (plus the gradient stencil ±h) fully inside
                        // the input field this zone actually covers? SampleD clamps out-of-range reads,
                        // which would snap the SAME lattice point to DIFFERENT places in adjacent zones →
                        // seams. Instead we skip a point whose stencil isn't covered here; the neighbour
                        // zone that DOES cover it snaps it identically (shared overlap region agrees), so
                        // any sphere reaching this zone is placed with a matching center. Requires the
                        // blob reach (rad) to sit within the running field's overlap margin.
                        bool InField(float wx, float wy, float wz)
                        {
                            float gx = (wx / s - ox0) / cvXZ, gz = (wz / s - oz0) / cvXZ, gy = (wy - extLoS2) / yStep;
                            return gx >= 0f && gx <= cw4 - 1.001f && gz >= 0f && gz <= cw4 - 1.001f && gy >= 0f && gy <= ch - 1.001f;
                        }

                        // world extent of the running field
                        float wxLo = ox0 * s, wxHi = (ox0 + bw) * s, wzLo = oz0 * s, wzHi = (oz0 + bw) * s;
                        float wyLo = extLoS2, wyHi = extLoS2 + (ch - 1) * yStep;
                        // (2) Pad the lattice range by the blob reach so a sphere straddling this zone's
                        // boundary is enumerated (and drawn) by every zone it touches — its center may sit
                        // outside the owned region but within reach. Points beyond coverage get InField-skipped.
                        // latPad spans the FULL chain reach so every zone a chain passes through enumerates
                        // the (global, deterministic) seed and regenerates the identical walk → chains TILE
                        // seam-free across zones. Cost grows with chain length (more seeds enumerated per zone).
                        int latPad = Mathf.CeilToInt(totalReach / L) + 1;
                        int li0 = Mathf.FloorToInt(wxLo/L)-latPad, li1 = Mathf.FloorToInt(wxHi/L)+latPad;
                        int lj0 = Mathf.FloorToInt(wzLo/L)-latPad, lj1 = Mathf.FloorToInt(wzHi/L)+latPad;
                        int lk0 = Mathf.FloorToInt(wyLo/L)-latPad, lk1 = Mathf.FloorToInt(wyHi/L)+latPad;

                        // precompute surface-snapped centers (+ per-blob radius, rolled from the GLOBAL lattice
                        // index so a blob straddling a zone border gets the same radius in both zones — no seam).
                        var centers = new List<Vector3>(256);
                        var radii   = new List<float>(256);
                        var normals = new List<Vector3>(256);   // N: composed-surface normal (stamp axis)
                        var tangG   = new List<Vector3>(256);   // G: downslope tangent
                        var tangT   = new List<Vector3>(256);   // T = N×G: cross-slope tangent
                        float h = 1.5f;   // gradient sample step
                        float curvMin = 9999f, curvMax = -9999f, curvSum = 0f; int curvCnt = 0;   // curvature-range diag
                        var work = new List<(Vector3 pos, Vector3 dir, int left, int depth, int seg)>(64);   // chain-branch worklist (reused)
                        int branchEvents = 0;   // diag: how many times a chain forked
                        float maxChainReach = 0f;   // diag: farthest any stamp gets from its seed (proves chain length)
                        // Debug overlay (Tier viz). Only the live/main-thread path captures (not parallel bake), and
                        // only seeds OWNED by this zone draw — so a chain tiled into N zones is drawn once, by its
                        // owner. TODO: multi SurfaceBlobs modes/zone will clobber (BeginZone clears); fine for 1 mode.
                        bool viz = Plugin._debugViz && !_parallelBakeActive;   // carve capture: debugViz only, NOT vegViz (which shares the zone store)
                        if (viz) { if (firstBlobViz) DebugViz.BeginZone(ox, oz); else DebugViz.SetZone(ox, oz); firstBlobViz = false; }
                        int ownW = bw - 2 * overlap;
                        float ownXLo = ox * s, ownXHi = (ox + ownW) * s, ownZLo = oz * s, ownZHi = (oz + ownW) * s;
                        var colSeed = new Color(0.7f, 0.2f, 1f);   // purple = accepted seed (drawn along the surface normal)
                        var colPath = new Color(1f, 0.15f, 0.1f);  // red = tunnel chain
                        var colCand = new Color(0.45f, 0.45f, 0.45f); // grey = near-surface lattice candidate (rejected by a gate if no purple on it)
                        for (int li = li0; li <= li1; li++)
                        for (int lj = lj0; lj <= lj1; lj++)
                        for (int lk = lk0; lk <= lk1; lk++)
                        {
                            float px = (li + Hash(li, lj, tile ? 0 : lk)) * L, pz = (lj + Hash(lj+7, li-3, tile ? 0 : lk)) * L, py = (lk + Hash(lk+13, li+5, lj-2)) * L;
                            float nx, ny, nz; Vector3 ctr;
                            if (tile)
                            {
                                // GLOBAL surface snap → every zone computes the SAME seed (= cross-zone propagation).
                                // 2D lattice (one per li,lj, elevation-independent) snapped to the global composed surface;
                                // N from its gradient. Accuracy is VERIFIED via the occlusion overlay (purple must poke
                                // out of the terrain). ComposedSurfaceHeight returns the OUTER surface, so no openAbove needed.
                                if (lk != lk0) continue;
                                float ghc = ComposedSurfaceSample(px, pz, wg);
                                float ggx = (ComposedSurfaceSample(px+h,pz,wg) - ComposedSurfaceSample(px-h,pz,wg)) / (2f*h);
                                float ggz = (ComposedSurfaceSample(px,pz+h,wg) - ComposedSurfaceSample(px,pz-h,wg)) / (2f*h);
                                nx = -ggx; ny = 1f; nz = -ggz;
                                float nl = Mathf.Sqrt(nx*nx+ny*ny+nz*nz); if (nl < 1e-4f) continue; nx/=nl; ny/=nl; nz/=nl;
                                ctr = new Vector3(px, ghc, pz);
                            }
                            else
                            {
                                // Reliable per-zone field snap (reference): read the ACTUAL composed field, keep points
                                // near a surface, snap c = p − D·∇D. Accurate but zone-local (can't tile).
                                if (!InField(px-h, py-h, pz-h) || !InField(px+h, py+h, pz+h)) continue;
                                float Dp = SampleD(px, py, pz);
                                if (Mathf.Abs(Dp) > L) continue;                   // lattice point not near a surface
                                nx = SampleD(px+h,py,pz) - SampleD(px-h,py,pz);
                                ny = SampleD(px,py+h,pz) - SampleD(px,py-h,pz);
                                nz = SampleD(px,py,pz+h) - SampleD(px,py,pz-h);
                                float nl = Mathf.Sqrt(nx*nx+ny*ny+nz*nz); if (nl < 1e-4f) continue; nx/=nl; ny/=nl; nz/=nl;
                                ctr = new Vector3(px - Dp*nx, py - Dp*ny, pz - Dp*nz);     // snap to surface
                            }
                            // LATTICE VIZ: every near-surface candidate (passed |Dp|<L) gets a small grey tick, drawn
                            // BEFORE the gates. Accepted seeds later draw a purple normal on top → lone grey = a lattice
                            // point rejected by a gate (sky-open/slope/coverage/etc.); shows the seeding field + culling.
                            bool capCand = viz && ctr.x >= ownXLo && ctr.x < ownXHi && ctr.z >= ownZLo && ctr.z < ownZHi;
                            if (capCand) DebugViz.Line(new[] { ctr - new Vector3(0f, 1.5f, 0f), ctr + new Vector3(0f, 1.5f, 0f) }, colCand, 0.3f);
                            // OUTER-surface only: reject internal surfaces (overhang undersides, formation pockets,
                            // tube walls) whose normals point every which way. If going straight UP from the seed hits
                            // solid within 40m, it's enclosed → skip. Keeps seeds on the open mountainside (N outward,
                            // −N into the rock) so tunnels are predictable.
                            if (!tile && skyOpen > 0.5f)   // field-based sky-open test; skyopen=0 lets internal surfaces (cave ceilings) seed
                            {
                                bool openAbove = true;
                                float upTop = Mathf.Min(wyHi, ctr.y + 40f);
                                for (float uy = ctr.y + 2f; uy <= upTop; uy += 4f)
                                    if (SampleD(ctr.x, uy, ctr.z) < 0f) { openAbove = false; break; }
                                if (!openAbove) continue;
                            }
                            // Tile seeds are 2D (one per li,lj, processed at this zone's lk0 which VARIES per zone),
                            // so any hash keyed on lk would differ between zones → the same seed gets accepted/sized
                            // differently → chains fail to tile (flat cut at the boundary). Fix all tile hashes to lk=0.
                            int hk = tile ? 0 : lk;
                            // Per-stamp biome amplitude: read at THIS stamp's cell, not the single zone-center cc,
                            // so features are full-size where the biome is present and skipped where it isn't
                            // (fixes blended-down sub-meter stamps in border zones).
                            int scx = Mathf.Clamp((int)((ctr.x/s - ox0)/cvXZ + 0.5f), 0, cw4-1);
                            int scz = Mathf.Clamp((int)((ctr.z/s - oz0)/cvXZ + 0.5f), 0, cw4-1);
                            int scell = scx*cw4 + scz;
                            float smaxR = _cb.bkModeParams![mi][0][scell];
                            if (smaxR <= 0f) continue;
                            float sminR = _cb.bkModeParams![mi][2][scell]; if (sminR < 0f) sminR = 0f; if (sminR > smaxR) sminR = smaxR;
                            // Biome-MAJORITY gate (global, so it tiles): only keep the seed where THIS mode's biome is the
                            // dominant one at the point, and then use the CONSTANT peak radius (no blend fade → full-size
                            // features that cut off cleanly at the biome edge instead of trailing off into neighbours).
                            if (biomeMajority > 0f && wg != null)
                            {
                                var domB = wg.GetBiome(ctr.x, ctr.z);
                                bool usesMode = _biomeParams.TryGetValue(domB, out var bpd) && mi < bpd.Length && bpd[mi] != null && bpd[mi].Length > 0 && bpd[mi][0] > 0f;
                                if (!usesMode) continue;
                                smaxR = peakMaxR; sminR = peakMinR;
                            }
                            // Surface frame {N, G, T} up front (gates below use T for the ridge detector).
                            var N = new Vector3(nx, ny, nz);
                            var gh = new Vector3(nx, 0f, nz);
                            if (gh.sqrMagnitude < 1e-6f) gh = new Vector3(1f, 0f, 0f);
                            float gdn = gh.x*nx + gh.y*ny + gh.z*nz;
                            var G = new Vector3(gh.x - gdn*nx, gh.y - gdn*ny, gh.z - gdn*nz);
                            float gl = G.magnitude;
                            if (gl < 1e-5f) { var f2 = new Vector3(1f,0f,0f); float d2 = f2.x*nx+f2.y*ny+f2.z*nz; G = new Vector3(f2.x-d2*nx, f2.y-d2*ny, f2.z-d2*nz); gl = G.magnitude; }
                            G /= gl;
                            var T = Vector3.Cross(N, G);

                            // Principal-curvature RIDGE detector (orientation-agnostic). Build the 2×2 tangent
                            // Hessian in the (G,T) plane at a broad scale, eigendecompose → fast curvature k1
                            // (across) and slow k2 (along). A ridge = k1 strongly > |k2|. Keep only those, and
                            // realign the frame so G runs along the slow axis (the ridgeline) → stretchg elongates
                            // the stamp down the crest. Flat/dome/valley points fail the ratio and are skipped.
                            if (ridgeRatio > 0f)
                            {
                                const float hR = 8f;
                                float d0 = SampleD(ctr.x, ctr.y, ctr.z);
                                var gp = G * hR; var tp = T * hR;
                                float Dgg = SampleD(ctr.x+gp.x, ctr.y+gp.y, ctr.z+gp.z) + SampleD(ctr.x-gp.x, ctr.y-gp.y, ctr.z-gp.z) - 2f*d0;
                                float Dtt = SampleD(ctr.x+tp.x, ctr.y+tp.y, ctr.z+tp.z) + SampleD(ctr.x-tp.x, ctr.y-tp.y, ctr.z-tp.z) - 2f*d0;
                                float Dgt = (SampleD(ctr.x+gp.x+tp.x, ctr.y+gp.y+tp.y, ctr.z+gp.z+tp.z)
                                           + SampleD(ctr.x-gp.x-tp.x, ctr.y-gp.y-tp.y, ctr.z-gp.z-tp.z)
                                           - SampleD(ctr.x+gp.x-tp.x, ctr.y+gp.y-tp.y, ctr.z+gp.z-tp.z)
                                           - SampleD(ctr.x-gp.x+tp.x, ctr.y-gp.y+tp.y, ctr.z-gp.z+tp.z)) * 0.25f;
                                float tr = Dgg + Dtt;
                                float disc = Mathf.Sqrt(Mathf.Max(0f, (Dgg - Dtt)*(Dgg - Dtt)*0.25f + Dgt*Dgt));
                                float k1 = tr*0.5f + disc, k2 = tr*0.5f - disc;    // k1 = fast/across, k2 = slow/along
                                float ratio = k1 / Mathf.Max(Mathf.Abs(k2), 0.05f);
                                if (ratio < curvMin) curvMin = ratio; if (ratio > curvMax) curvMax = ratio; curvSum += ratio; curvCnt++;
                                if (!(k1 > 0.3f && k1 >= ridgeRatio * Mathf.Abs(k2))) continue;   // not an anisotropic convex ridge
                                // eigenvector of the MAX curvature (fast/across) in the (G,T) basis:
                                float theta = 0.5f * Mathf.Atan2(2f*Dgt, Dgg - Dtt);
                                float cs = Mathf.Cos(theta), sn = Mathf.Sin(theta);
                                var fastDir = cs*G + sn*T;      // across the ridge
                                var slowDir = -sn*G + cs*T;     // along the ridge (ridgeline)
                                G = slowDir.normalized; T = fastDir.normalized;   // stretchg now elongates along the crest
                            }
                            // CONCAVE gate (inverse of ridgeratio): keep only anisotropic concave surfaces — the fast
                            // (largest-|curvature|) axis bends AWAY (into air), like a tube ceiling or dome underside.
                            // For stalactites decorating cave/tunnel undersides. Realign G along the trough (low-curv axis).
                            if (concaveRatio > 0f)
                            {
                                const float hR = 8f;
                                float d0 = SampleD(ctr.x, ctr.y, ctr.z);
                                var gp = G * hR; var tp = T * hR;
                                float Dgg = SampleD(ctr.x+gp.x, ctr.y+gp.y, ctr.z+gp.z) + SampleD(ctr.x-gp.x, ctr.y-gp.y, ctr.z-gp.z) - 2f*d0;
                                float Dtt = SampleD(ctr.x+tp.x, ctr.y+tp.y, ctr.z+tp.z) + SampleD(ctr.x-tp.x, ctr.y-tp.y, ctr.z-tp.z) - 2f*d0;
                                float Dgt = (SampleD(ctr.x+gp.x+tp.x, ctr.y+gp.y+tp.y, ctr.z+gp.z+tp.z)
                                           + SampleD(ctr.x-gp.x-tp.x, ctr.y-gp.y-tp.y, ctr.z-gp.z-tp.z)
                                           - SampleD(ctr.x+gp.x-tp.x, ctr.y+gp.y-tp.y, ctr.z+gp.z-tp.z)
                                           - SampleD(ctr.x-gp.x+tp.x, ctr.y-gp.y+tp.y, ctr.z-gp.z+tp.z)) * 0.25f;
                                float tr = Dgg + Dtt;
                                float disc = Mathf.Sqrt(Mathf.Max(0f, (Dgg - Dtt)*(Dgg - Dtt)*0.25f + Dgt*Dgt));
                                float k1 = tr*0.5f + disc, k2 = tr*0.5f - disc;    // k2 = most concave (min curvature)
                                if (!(k2 < -0.3f && -k2 >= concaveRatio * Mathf.Abs(k1))) continue;   // not an anisotropic concave underside
                                // align G along the trough (the LOW-curvature slow axis = k1's eigenvector):
                                float theta = 0.5f * Mathf.Atan2(2f*Dgt, Dgg - Dtt);
                                float cs = Mathf.Cos(theta), sn = Mathf.Sin(theta);
                                var k1Dir = cs*G + sn*T;        // along the trough (low curvature)
                                var k2Dir = -sn*G + cs*T;       // across (concave)
                                G = k1Dir.normalized; T = k2Dir.normalized;
                            }
                            // Distribution gates (Tier 2): keep the stamp only if it passes every active gate.
                            if (coverage < 1f && Hash(li+401, lj+211, hk+307) > coverage) continue;  // thin
                            if (slopeLo > 0f || slopeHi < 180f)                                       // slope/orientation (0 floor → 90 wall → 180 ceiling)
                            {
                                float slopeDeg = Mathf.Acos(Mathf.Clamp(ny, -1f, 1f)) * Mathf.Rad2Deg;
                                if (slopeDeg < slopeLo || slopeDeg > slopeHi) continue;
                            }
                            if (ctr.y < altLo || ctr.y > altHi) continue;                            // altitude band
                            if (clusterFreq > 0f &&                                                   // global noise mask → patches
                                Plugin._noisePool![15].GetNoise(ctr.x * clusterFreq, ctr.z * clusterFreq, 0f) < clusterThr) continue;
                            if (aspectLo > 0f || aspectHi < 360f)                                     // aspect: compass direction the slope faces
                            {
                                float asp = Mathf.Atan2(nz, nx) * Mathf.Rad2Deg; if (asp < 0f) asp += 360f;
                                if (asp < aspectLo || asp > aspectHi) continue;
                            }
                            if (curvLo > -9000f || curvHi < 9000f)                                    // simple cross-slope curvature gate (superseded by ridgeratio when set)
                            {
                                const float hR = 8f;
                                float d0 = SampleD(ctr.x, ctr.y, ctr.z);
                                float cT = SampleD(ctr.x + hR*T.x, ctr.y + hR*T.y, ctr.z + hR*T.z)
                                         + SampleD(ctr.x - hR*T.x, ctr.y - hR*T.y, ctr.z - hR*T.z) - 2f*d0;
                                if (cT < curvLo || cT > curvHi) continue;
                            }
                            if (bias == 1)                                                            // contour: periodic elevation → rings
                            {
                                float rr = ctr.y / biasSpacing + biasPhase; rr -= Mathf.Floor(rr);
                                if (Mathf.Min(rr, 1f - rr) > biasWidth) continue;
                            }
                            else if (bias == 2)                                                       // ridge: periodic aspect → radial ribs down-slope
                            {
                                float asp = Mathf.Atan2(nz, nx) * Mathf.Rad2Deg; if (asp < 0f) asp += 360f;
                                float rr = asp / biasSpacing + biasPhase; rr -= Mathf.Floor(rr);
                                if (Mathf.Min(rr, 1f - rr) > biasWidth) continue;
                            }
                            centers.Add(ctr);
                            normals.Add(N); tangG.Add(G); tangT.Add(T);
                            // uniform radius roll in [minR, maxR] from the lattice index (decorrelated hash seeds)
                            float seedR = sminR > 0f ? sminR + (smaxR - sminR) * Hash(li*2+1, lj*2+1, hk*2+1) : smaxR;
                            radii.Add(seedR);

                            bool capSeed = viz && ctr.x >= ownXLo && ctr.x < ownXHi && ctr.z >= ownZLo && ctr.z < ownZHi;
                            // Seed marker drawn ALONG the surface normal N (from under the surface to above it) so we can
                            // confirm the snap landed ON the composed surface and see its orientation. Generalizes to any
                            // tier: spines will use the same call with their own color at their accept site.
                            if (capSeed) DebugViz.Line(new[] { ctr - N * 2f, ctr + N * 6f }, colSeed, 0.7f);

                            // Tier 4 — chain propagation: walk child stamps from this seed. The walk is derived
                            // ONLY from the global lattice index (li,lj,lk), so every zone within totalReach of
                            // the seed regenerates the IDENTICAL chain → tunnels tile seam-free across zones.
                            if (chainN > 0 && chainStep != 0f)
                            {
                                Vector3 dir0;
                                if (tile)
                                {
                                    dir0 = (-N).normalized;   // straight tube inward along the surface normal (into the mountainside)
                                }
                                else
                                {
                                    dir0 = new Vector3(Hash(li+71, lj+131, lk+191) * 2f - 1f,
                                                      (Hash(li+211, lj+17, lk+99) * 2f - 1f) * 0.35f,   // horizontal-biased
                                                       Hash(li+37, lj+223, lk+53) * 2f - 1f);
                                    if (dir0.sqrMagnitude < 1e-6f) dir0 = new Vector3(1f, 0f, 0f);
                                    dir0 = dir0.normalized;
                                    if (chainInward > 0f)                                             // aim into the mountainside (horizontal −N)
                                    {
                                        var inwardH = new Vector3(-N.x, 0f, -N.z);
                                        // On flat ground −N points straight down → its horizontal projection is ~0. For flat
                                        // biomes use a hashed horizontal azimuth instead of diving along −N.
                                        Vector3 aim;
                                        if (inwardH.sqrMagnitude > 1e-4f) aim = inwardH.normalized;
                                        else if (chainHoriz) { float a = Hash(li + 53, lj + 91, hk + 17) * 6.2831853f; aim = new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a)); }
                                        else aim = -N;
                                        dir0 = Vector3.Lerp(dir0, aim, chainInward);
                                        if (dir0.sqrMagnitude < 1e-6f) dir0 = aim;
                                        dir0 = dir0.normalized;
                                    }
                                }
                                // Force a horizontal launch regardless of surface normal (flat biomes like Plains, where
                                // −N dives straight down). chainslope/chainlevel then govern any gentle descent/leveling.
                                if (chainHoriz)
                                {
                                    dir0.y = 0f;
                                    if (dir0.sqrMagnitude < 1e-6f) { float a = Hash(li + 59, lj + 83, hk + 23) * 6.2831853f; dir0 = new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a)); }   // hk (not lk) so tile seeds hash identically across zones
                                    dir0 = dir0.normalized;
                                }
                                // Per-tunnel variety (hashed from the seed → constant for this chain, consistent across
                                // zones): width class (some narrow tunnels) and slope class (some shallow, some descending).
                                float tScale = 0.5f + 0.5f * Hash(li*7+3, lj*7+9, hk*7+1);               // overall width multiplier
                                float tSlope = chainSlope * (0.1f + 1.8f * Hash(li*13+2, lj*13+8, hk*13+4)); // this tunnel's descent
                                // Branching walk via a worklist (pos,dir,stepsLeft,depth,segId). Every decision is
                                // hashed from (li,lj,lk, seg, step) → the whole tree is a pure function of the seed,
                                // so it tiles across zones. Capped at maxEmit stamps / branchDepth to bound cost.
                                work.Clear();
                                work.Add((ctr, dir0, chainN, 0, 0));
                                int segCounter = 0, emitted = 0, maxEmit = chainN * 4;
                                while (work.Count > 0 && emitted < maxEmit)
                                {
                                    var sgd = work[work.Count - 1]; work.RemoveAt(work.Count - 1);
                                    var pos = sgd.pos; var dir = sgd.dir; int left = sgd.left, dep = sgd.depth, seg = sgd.seg;
                                    List<Vector3>? runPts = capSeed ? new List<Vector3> { pos } : null;
                                    for (int cs = 1; cs <= left && emitted < maxEmit; cs++)
                                    {
                                        // wander applies to BOTH (hk-keyed, so it's identical across zones → still tiles);
                                        // flow/hub/keepdepth/level are the non-tile free-walk path only.
                                        if (chainWander > 0f)
                                            dir += chainWander * new Vector3(Hash(li+seg*701 + cs*13 + 1, lj+seg*307 + cs*7 + 3,  hk+seg*509 + cs*5 + 9) * 2f - 1f,
                                                                            (Hash(li+seg*701 + cs*17 + 5, lj+seg*307 + cs*11 + 2, hk+seg*509 + cs*3 + 8) * 2f - 1f) * 0.35f,
                                                                             Hash(li+seg*701 + cs*19 + 7, lj+seg*307 + cs*23 + 4, hk+seg*509 + cs*29 + 6) * 2f - 1f);
                                        if (!tile)
                                        {
                                            if (flowSteer > 0f)                                           // steer toward a coherent flow field
                                            {
                                                float fx = pos.x*flowFreq, fy = pos.y*flowFreq, fz = pos.z*flowFreq;
                                                var flow = new Vector3(Plugin._noisePool![8].GetNoise(fx, fy, fz),
                                                                       Plugin._noisePool![9].GetNoise(fx+13.1f, fy+7.7f, fz+3.3f),
                                                                       Plugin._noisePool![10].GetNoise(fx-5.5f, fy-9.9f, fz+11.1f));
                                                if (flow.sqrMagnitude > 1e-6f) dir += flowSteer * flow.normalized;
                                            }
                                            if (hubSeek > 0f && hubSpacing > 1f)                          // steer toward the nearest deterministic hub
                                            {
                                                int hx = Mathf.FloorToInt(pos.x / hubSpacing), hz = Mathf.FloorToInt(pos.z / hubSpacing);
                                                float jx = (Hash(hx*131+5, hz*197+9, 7) - 0.5f) * hubSpacing * 0.4f;
                                                float jz = (Hash(hx*233+3, hz*97+11, 13) - 0.5f) * hubSpacing * 0.4f;
                                                float hubX = (hx + 0.5f) * hubSpacing + jx, hubZ = (hz + 0.5f) * hubSpacing + jz;
                                                var toHub = new Vector3(hubX - pos.x, 0f, hubZ - pos.z);
                                                if (toHub.sqrMagnitude > 1f) dir = Vector3.Lerp(dir, toHub.normalized, hubSeek);
                                            }
                                            if (keepDepth > 0f)   // bend back into rock if it would surface (needs the per-zone field)
                                            {
                                                var nextp = pos + dir * chainStep;
                                                float dsurf = SampleD(nextp.x, nextp.y, nextp.z);
                                                if (dsurf > -keepDepth)
                                                {
                                                    var grad = new Vector3(SampleD(pos.x+h,pos.y,pos.z) - SampleD(pos.x-h,pos.y,pos.z),
                                                                           SampleD(pos.x,pos.y+h,pos.z) - SampleD(pos.x,pos.y-h,pos.z),
                                                                           SampleD(pos.x,pos.y,pos.z+h) - SampleD(pos.x,pos.y,pos.z-h));
                                                    if (grad.sqrMagnitude > 1e-6f) { dir = Vector3.Lerp(dir, -grad.normalized, 0.8f); dir = dir.normalized; }
                                                }
                                            }
                                        }
                                        // level-out: relax dir.y toward the target slope each step so the inward −N
                                        // launch flattens as it extends. chainslope=0 → horizontal; >0 → gentle descent.
                                        // Pure per-step → tiles. (both paths)
                                        if (chainLevel > 0f) dir.y = Mathf.Lerp(dir.y, -tSlope, chainLevel);
                                        if (dir.sqrMagnitude < 1e-6f) dir = new Vector3(1f, 0f, 0f);
                                        dir = dir.normalized;
                                        pos += dir * chainStep;
                                        runPts?.Add(pos);
                                        float dr = (pos - ctr).magnitude; if (dr > maxChainReach) maxChainReach = dr;   // reach from seed
                                        // Organic width: smooth pinch/swell ALONG the tube (low-freq noise at pos) scaled by
                                        // the per-tunnel width class → coherent swells, not per-stamp noise; some tunnels narrow.
                                        float swell = 0.5f + 0.5f * Plugin._noisePool![11].GetNoise(pos.x*0.07f, pos.y*0.07f, pos.z*0.07f);
                                        float cr = (sminR + (smaxR - sminR) * Mathf.Clamp01(swell)) * tScale;
                                        // Orient the stamp to the PATH: G = travel direction (so stretchg elongates ALONG
                                        // the tube → smooth capsule, not beads), N = vertical-ish perp, T = cross. stretchn/
                                        // stretcht set the cross-section dims; pnorm its shape (round→square).
                                        var pG = dir;
                                        var pN = Vector3.up - Vector3.Dot(Vector3.up, pG) * pG;
                                        if (pN.sqrMagnitude < 1e-5f) pN = Vector3.Cross(pG, Vector3.right);
                                        pN = pN.normalized;
                                        var pT = Vector3.Cross(pN, pG).normalized;
                                        centers.Add(pos); normals.Add(pN); tangG.Add(pG); tangT.Add(pT); radii.Add(cr); emitted++;
                                        if (branchProb > 0f && dep < branchDepth && left - cs > 1
                                            && Hash(li+seg*131 + cs*53 + 11, lj+seg*197 + cs*29 + 7, lk+seg*233 + cs*61 + 3) < branchProb)
                                        {
                                            var rnd = new Vector3(Hash(li+seg*51+cs, lj+seg*91+cs, lk+seg*71+cs) * 2f - 1f,
                                                                  Hash(li+seg*53+cs, lj+seg*97+cs, lk+seg*73+cs) * 2f - 1f,
                                                                  Hash(li+seg*59+cs, lj+seg*101+cs, lk+seg*79+cs) * 2f - 1f);
                                            var axis = Vector3.Cross(dir, rnd);
                                            if (axis.sqrMagnitude < 1e-6f) axis = Vector3.Cross(dir, Vector3.up);
                                            axis = axis.normalized;
                                            float sgn = Hash(li+seg*7+cs, lj+seg*11+cs, lk+seg*13+cs) < 0.5f ? -1f : 1f;
                                            var bdir = (Quaternion.AngleAxis(sgn * branchAngle, axis) * dir).normalized;
                                            segCounter++; branchEvents++;
                                            work.Add((pos, bdir, left - cs, dep + 1, segCounter));
                                        }
                                    }
                                    if (runPts != null && runPts.Count >= 2) DebugViz.Line(runPts, colPath, 0.8f);
                                }
                            }
                        }

                        // DIAG: field Y window vs how deep/far the stamps go, + how many stamps fall OUTSIDE this
                        // zone's field (Y below floor / above top, or XZ outside) → can't be carved here.
                        float fieldYlo = extLoS2, fieldYhi = extLoS2 + (ch - 1) * yStep;
                        float minCY = 1e9f, maxCY = -1e9f; int belowField = 0, aboveField = 0, outsideXZ = 0;
                        for (int ci = 0; ci < centers.Count; ci++)
                        {
                            var c = centers[ci];
                            if (c.y < minCY) minCY = c.y; if (c.y > maxCY) maxCY = c.y;
                            if (c.y < fieldYlo) belowField++;
                            else if (c.y > fieldYhi) aboveField++;
                            if (c.x < wxLo || c.x > wxHi || c.z < wzLo || c.z > wzHi) outsideXZ++;
                        }
                        Log.LogDebug($"[PT-blobs-diag] ({ox},{oz}) fieldY=[{fieldYlo:F0},{fieldYhi:F0}] stampY=[{minCY:F0},{maxCY:F0}] centers={centers.Count} belowField={belowField} aboveField={aboveField} outsideXZ={outsideXZ}");

                        // Spatial buckets (cell = reach) so each voxel tests only stamps in its 27 neighbour
                        // buckets, not all N. Turns the union from O(voxels*centers) into O(voxels*localCenters).
                        float bsz = Mathf.Max(reach, 1f);
                        var buckets = new Dictionary<long, List<int>>(centers.Count + 1);
                        long BKey(int bx, int by, int bz) => ((long)(bx + 100000) * 200003L + (by + 100000)) * 200003L + (bz + 100000);
                        for (int ci = 0; ci < centers.Count; ci++)
                        {
                            var cc2 = centers[ci];
                            long key = BKey(Mathf.FloorToInt(cc2.x/bsz), Mathf.FloorToInt(cc2.y/bsz), Mathf.FloorToInt(cc2.z/bsz));
                            if (!buckets.TryGetValue(key, out var lst)) { lst = new List<int>(); buckets[key] = lst; }
                            lst.Add(ci);
                        }

                        // union spheres into the running field
                        int dbgHit = 0;
                        for (int cx4 = 0; cx4 < cw4; cx4++)
                        for (int cz4 = 0; cz4 < cw4; cz4++)
                        {
                            int cell = cx4 * cw4 + cz4, cbaseC = cell * ch;
                            if (_cb.bkModeParams![mi][0][cell] <= 0f) continue;
                            int cxw = Math.Min(cx4 * cvXZ, bw - 1), czw = Math.Min(cz4 * cvXZ, bw - 1);
                            float wx = (ox0 + cxw) * s, wz = (oz0 + czw) * s;
                            for (int cyc = 0; cyc < ch; cyc++)
                            {
                                float wy = extLoS2 + cyc * yStep;
                                float best = 1e9f;
                                int vbx = Mathf.FloorToInt(wx/bsz), vby = Mathf.FloorToInt(wy/bsz), vbz = Mathf.FloorToInt(wz/bsz);
                                for (int dbx = -1; dbx <= 1; dbx++)
                                for (int dby = -1; dby <= 1; dby++)
                                for (int dbz = -1; dbz <= 1; dbz++)
                                {
                                    if (!buckets.TryGetValue(BKey(vbx+dbx, vby+dby, vbz+dbz), out var blist)) continue;
                                    for (int bi = 0; bi < blist.Count; bi++)
                                    {
                                    int ci = blist[bi];
                                    var c = centers[ci]; var N = normals[ci]; var G = tangG[ci]; var T = tangT[ci];
                                    float rr = radii[ci];
                                    float dx = wx-c.x, dy = wy-c.y, dz = wz-c.z;
                                    // Project into the surface frame {N, G, T}, scale each axis independently,
                                    // taper the tangential radii toward the +N tip. Reduces EXACTLY to |d|-r at
                                    // defaults (all stretch=1, taper=0, p=2); eikonal re-normalizes the metric after.
                                    float dN = dx*N.x + dy*N.y + dz*N.z;
                                    float dG = dx*G.x + dy*G.y + dz*G.z;
                                    float dT = dx*T.x + dy*T.y + dz*T.z;
                                    float RN = rr*stretchN, RG = rr*stretchG, RT = rr*stretchT;
                                    float tn = dN > 0f ? dN / RN : 0f; if (tn > 1f) tn = 1f;    // 0 at base → 1 at tip
                                    float shrink = taper * (taperPow == 1f ? tn : Mathf.Pow(tn, taperPow));
                                    float tf = 1f - shrink; if (tf < 0.02f) tf = 0.02f;         // tangential taper factor
                                    float uN = dN / RN, uG = dG / (RG*tf), uT = dT / (RT*tf);
                                    float comb;
                                    if (pnorm == 2f) comb = Mathf.Sqrt(uN*uN + uG*uG + uT*uT);
                                    else comb = Mathf.Pow(Mathf.Pow(Mathf.Abs(uN),pnorm) + Mathf.Pow(Mathf.Abs(uG),pnorm) + Mathf.Pow(Mathf.Abs(uT),pnorm), 1f/pnorm);
                                    float dd = (comb - 1f) * rr;
                                    if (dd < best) best = dd;
                                    }
                                }
                                ref float r = ref _runSDF![cbaseC + cyc];
                                // Operator: add (union=min) or carve (subtract=max(r,-stamp)); fuse = smooth blend.
                                float nr = carve
                                    ? (fuse > 0f ? -Smin(-r, best, fuse) : Mathf.Max(r, -best))
                                    : (fuse > 0f ?  Smin(r, best, fuse)  : Mathf.Min(r, best));
                                if (nr != r) { r = nr; dbgHit++; }
                            }
                        }
                        Log.LogDebug($"[PT-blobs] centers={centers.Count} L={L:F0} rad=[{minR:F1},{maxR:F1}] stretchN={stretchN:F1} carve={carve} branches={branchEvents} branchprob={branchProb:F2} flow={flowSteer:F2} hub={hubSeek:F2} maxreach={maxChainReach:F0}m ridgeratio={ridgeRatio:F1} solidVoxels={dbgHit} curv=[{curvMin:F3},{curvMax:F3}]");
                        if (needsEikonal) RedistanceEikonal(_runSDF!, cw4, cw4, ch, xzStep, xzStep, yStep, 2);
                        continue;
                    }

                    bool anchored = mode is LuaModeDef lm && lm.SurfaceAnchoredFlag;

                    if (anchored)
                    {
                        // composed-surface elevation (zero-crossing of the running field) per cell...
                        for (int cell = 0; cell < cellCount; cell++)
                            _composeInH![cell] = SurfaceElevation(_runSDF!, cell * ch, ch, extLoS2, yStep);
                        // DIAGNOSTIC: how far the incoming (composed) surface departs from the bare base
                        // ground. >0 ⇒ this mode is conforming to PRIOR modes (composition working);
                        // ~0 ⇒ it's only seeing vanilla ground (wiring bug). Logged once per mode/zone.
                        {
                            float maxD = 0f;
                            for (int cell = 0; cell < cellCount; cell++)
                                maxD = Mathf.Max(maxD, Mathf.Abs(_composeInH![cell] - _composeHCell![cell]));
                            Log.LogDebug($"[PT-compose] {mode.Name}: incoming surface vs base ground maxΔ={maxD:F1}m (>0 ⇒ conforming to prior modes)");
                        }
                        // ...then the downslope frame from that elevation field (central difference).
                        for (int cx4 = 0; cx4 < cw4; cx4++)
                        for (int cz4 = 0; cz4 < cw4; cz4++)
                        {
                            int cell = cx4 * cw4 + cz4;
                            int xm = ((cx4 > 0 ? cx4 - 1 : cx4) * cw4 + cz4), xp = ((cx4 < cw4 - 1 ? cx4 + 1 : cx4) * cw4 + cz4);
                            int zm = (cx4 * cw4 + (cz4 > 0 ? cz4 - 1 : cz4)), zp = (cx4 * cw4 + (cz4 < cw4 - 1 ? cz4 + 1 : cz4));
                            float gx = (_composeInH![xp] - _composeInH![xm]) / (2f * xzStep);
                            float gz = (_composeInH![zp] - _composeInH![zm]) / (2f * xzStep);
                            float mag = Mathf.Sqrt(gx * gx + gz * gz), inv = 1f / (mag + 1e-4f);
                            _composeInGX![cell] = gx * inv; _composeInGZ![cell] = gz * inv; _composeInGMag![cell] = mag;
                        }
                        _composeIncoming = true;
                    }

                    // bake this mode (respecting blendMode) with incoming inputs active for anchored modes
                    for (int cx4 = 0; cx4 < cw4; cx4++)
                    for (int cz4 = 0; cz4 < cw4; cz4++)
                    {
                        int cxw = Math.Min(cx4 * cvXZ, bw - 1), czw = Math.Min(cz4 * cvXZ, bw - 1);
                        var bargs = new BakeCellArgs {
                            worldX = (ox - overlap + cxw) * s, worldZ = (oz - overlap + czw) * s,
                            cx4 = cx4, cz4 = cz4, cellIdx = cx4 * cw4 + cz4, cw4 = cw4, bh = bh,
                            s = s, extLoS = extLoS2, wg = wg!
                        };
                        if (_blendMode == 1) BakeColumnSdfBlend(mode, ref _cb, in bargs, cw4);
                        else                 mode.BakeColumn(ref _cb, in bargs);
                    }
                    _composeIncoming = false;

                    // compose this mode's coarse-sampled column into the running field
                    var aux = _cb.bkModeAux![mi][0];
                    string blend = mode.Blend;
                    float k = mode.BlendK * scale2;
                    for (int cell = 0; cell < cellCount; cell++)
                    {
                        if (_cb.bkModeParams![mi][0][cell] <= 0f) continue;   // mode inactive here
                        int cbaseF = cell * bh, cbaseC = cell * ch;
                        float hCell = _composeHCell![cell], floorY = _composeFloorY![cell];
                        for (int cyc = 0; cyc < ch; cyc++)
                        {
                            int cyf = Math.Min(cyc * cvY, bh - 1);
                            float worldY = extLoS2 + cyc * yStep;
                            float raw = aux[cbaseF + cyf] * scale2;
                            float sdf = Mathf.Min(raw, worldY - floorY);
                            ref float r = ref _runSDF![cbaseC + cyc];
                            switch (blend)
                            {
                                case "smoothmax": r = Plugin.SmoothMax(r, sdf, k); break;
                                case "max":       if (sdf > r) r = sdf; break;
                                case "add":       r += sdf - (worldY - hCell); break;
                                case "subtract":  r -= sdf - (worldY - hCell); break;
                                default:          if (sdf < r) r = sdf; break; // "min"
                            }
                        }
                    }

                    // redistance so the next mode reads a true distance field — only when enabled
                    // (current modes read zero-crossing elevation/normal, which don't need |grad|=1;
                    // this per-mode pass is what makes fine coarseXZ explode, so it's off by default).
                    if (needsEikonal) RedistanceEikonal(_runSDF!, cw4, cw4, ch, xzStep, xzStep, yStep, 2);
                }
            }
        }

        // Topmost surface elevation (world units) from a coarse SDF column [baseIdx .. baseIdx+ch).
        // Scans top→down for the first air(>0)-above / solid(<=0)-below transition and linearly
        // interpolates the zero-crossing. Falls back to window extremes if all-air or all-solid.
        static float SurfaceElevation(float[] f, int baseIdx, int ch, float extLoS, float yStep)
        {
            for (int cyc = ch - 1; cyc > 0; cyc--)
            {
                float hi = f[baseIdx + cyc], lo = f[baseIdx + cyc - 1];
                if (hi > 0f && lo <= 0f)
                {
                    float t = (hi == lo) ? 0f : (0f - lo) / (hi - lo);   // fraction lo→hi where sdf=0
                    return extLoS + (cyc - 1 + t) * yStep;
                }
            }
            return f[baseIdx] > 0f ? extLoS : extLoS + (ch - 1) * yStep;
        }

        // Bilinear over the 4 XZ corner values (v00=base, v10=+x, v01=+z, v11=+xz).
        static float Bilerp(float v00, float v10, float v01, float v11, float fx, float fz)
            => (v00 * (1f - fx) + v10 * fx) * (1f - fz) + (v01 * (1f - fx) + v11 * fx) * fz;

        // Polynomial smooth-min: blends two SDFs with a rounded seam of radius k (k=0 → hard min).
        // Smooth-max (for carve) is -Smin(-a,-b,k); we call Smin(-r, best, fuse) and negate.
        static float Smin(float a, float b, float k)
        {
            if (k <= 0f) return Mathf.Min(a, b);
            float h = Mathf.Clamp01(0.5f + 0.5f * (b - a) / k);
            return Mathf.Lerp(b, a, h) - k * h * (1f - h);
        }

        // Anisotropic 3-D eikonal redistance (fast sweeping) in place: rewrites |value| to true world
        // distance from the zero-crossing (|grad|=1) while preserving sign and the surface. Grid nx*nz
        // columns of height bh, layout [(cx*nz+cz)*bh + cy]; per-axis world spacings hx,hz (XZ), hy (Y)
        // — anisotropy handled in the Godunov solve. Fully deterministic (fixed sweep orders).
        static void RedistanceEikonal(float[] f, int nx, int nz, int bh, float hx, float hy, float hz, int rounds)
        {
            int n = nx * nz * bh;
            const float BIG = 1e9f;
            var d = _eikScratch;
            if (d == null || d.Length < n) { d = new float[n]; _eikScratch = d; }
            int SX = nz * bh, SZ = bh, SY = 1;
            for (int i = 0; i < n; i++) d[i] = BIG;
            // Freeze the interface band: nodes adjacent to a sign change get a subcell distance.
            for (int cx = 0; cx < nx; cx++)
            for (int cz = 0; cz < nz; cz++)
            for (int cy = 0; cy < bh; cy++)
            {
                int i = (cx * nz + cz) * bh + cy;
                float fi = f[i], best = BIG;
                void Chk(float fj, float h)
                {
                    if (fi == 0f || fi * fj < 0f)
                    {
                        float t  = fi == 0f ? 0f : Mathf.Abs(fi) / (Mathf.Abs(fi) + Mathf.Abs(fj));
                        float dd = t * h; if (dd < best) best = dd;
                    }
                }
                if (cx > 0)      Chk(f[i - SX], hx);
                if (cx < nx - 1) Chk(f[i + SX], hx);
                if (cz > 0)      Chk(f[i - SZ], hz);
                if (cz < nz - 1) Chk(f[i + SZ], hz);
                if (cy > 0)      Chk(f[i - SY], hy);
                if (cy < bh - 1) Chk(f[i + SY], hy);
                if (best < BIG) d[i] = best;
            }
            // Fast sweeping: 8 direction combos × rounds.
            for (int r = 0; r < rounds; r++)
            for (int sx = 0; sx < 2; sx++)
            for (int sz = 0; sz < 2; sz++)
            for (int sy = 0; sy < 2; sy++)
            {
                int x0 = sx == 0 ? 0 : nx - 1, x1 = sx == 0 ? nx : -1, dx = sx == 0 ? 1 : -1;
                int z0 = sz == 0 ? 0 : nz - 1, z1 = sz == 0 ? nz : -1, dz = sz == 0 ? 1 : -1;
                int y0 = sy == 0 ? 0 : bh - 1, y1 = sy == 0 ? bh : -1, dy = sy == 0 ? 1 : -1;
                for (int cx = x0; cx != x1; cx += dx)
                for (int cz = z0; cz != z1; cz += dz)
                for (int cy = y0; cy != y1; cy += dy)
                {
                    int i = (cx * nz + cz) * bh + cy;
                    float ax = Mathf.Min(cx > 0 ? d[i - SX] : BIG, cx < nx - 1 ? d[i + SX] : BIG);
                    float az = Mathf.Min(cz > 0 ? d[i - SZ] : BIG, cz < nz - 1 ? d[i + SZ] : BIG);
                    float ay = Mathf.Min(cy > 0 ? d[i - SY] : BIG, cy < bh - 1 ? d[i + SY] : BIG);
                    float u = SolveEikonal(ax, ay, az, hx, hy, hz);
                    if (u < d[i]) d[i] = u;
                }
            }
            for (int i = 0; i < n; i++) f[i] = f[i] < 0f ? -d[i] : d[i];
        }

        // Godunov solve of the anisotropic eikonal at one node: min upwind neighbor distance per axis
        // (ax,ay,az) with world spacings (hx,hy,hz). Adds axes lowest-first until consistent.
        static float SolveEikonal(float ax, float ay, float az, float hx, float hy, float hz)
        {
            Span<float> a = stackalloc float[3]; a[0] = ax; a[1] = ay; a[2] = az;
            Span<float> h = stackalloc float[3]; h[0] = hx; h[1] = hy; h[2] = hz;
            for (int p = 0; p < 3; p++)
            for (int q = p + 1; q < 3; q++)
                if (a[q] < a[p]) { float ta = a[p]; a[p] = a[q]; a[q] = ta; float th = h[p]; h[p] = h[q]; h[q] = th; }
            float u = a[0] + h[0];
            if (u <= a[1]) return u;
            float i0 = 1f / (h[0] * h[0]), i1 = 1f / (h[1] * h[1]);
            {
                float A = i0 + i1, B = -2f * (a[0] * i0 + a[1] * i1), C = a[0] * a[0] * i0 + a[1] * a[1] * i1 - 1f;
                float disc = B * B - 4f * A * C;
                if (disc >= 0f) { float uu = (-B + Mathf.Sqrt(disc)) / (2f * A); if (uu <= a[2]) return uu; u = uu; }
            }
            {
                float i2 = 1f / (h[2] * h[2]);
                float A = i0 + i1 + i2, B = -2f * (a[0] * i0 + a[1] * i1 + a[2] * i2);
                float C = a[0] * a[0] * i0 + a[1] * a[1] * i1 + a[2] * a[2] * i2 - 1f;
                float disc = B * B - 4f * A * C;
                if (disc >= 0f) return (-B + Mathf.Sqrt(disc)) / (2f * A);
            }
            return u;
        }

        // SDF-blend driver: instead of one BakeColumn on cross-biome-averaged params (which chirps
        // when frequencies differ), bake each contributing biome's column at ITS OWN params — with
        // amplitude scaled by coverageFrac so the mode fades toward its own correct off-state — then
        // weighted-average the output columns among present biomes. Full-coverage boundaries
        // (both biomes have the mode) reduce to a pure crisp crossfade. Interior cells (one present
        // biome, coverageFrac=1) reduce to a single bake, identical cost to the param path.
        // Leaves bkModeParams[mi][0] (the ComputeVoxel amplitude gate) as BakeBlendedProfiles set it.
        static void BakeColumnSdfBlend(ModeDefinition mode, ref CarveBuffers cb, in BakeCellArgs args, int cw4)
        {
            int mi = mode.PipelineIndex;
            if (mi < 0) return;
            int idx = args.cellIdx, bh = args.bh, baseIdx = idx * bh;
            int cellCount = cw4 * cw4;
            float[] outCol = cb.bkModeAux![mi][0];

            float totW     = (_biomeTotW  != null && idx < _biomeTotW.Length)  ? _biomeTotW[idx] : 0f;
            float presentW = (_sdfPresentW != null) ? _sdfPresentW[mi * cellCount + idx] : 0f;
            if (presentW <= 0f || totW <= 0f)
            {
                for (int cy = 0; cy < bh; cy++) outCol[baseIdx + cy] = 0f;
                // Mode fully absent here, but the baseline field must still be defined for neighbors'
                // bilinear correction. Canonical value = unwarped ground (matches param mode, where the
                // blended warp params → 0 at fully-absent cells). Prevents stale-baseline boundary spikes.
                if (mode.SurfaceAnchored && cb.bkModeBaseline != null
                    && mi < cb.bkModeBaseline.Length && cb.bkModeBaseline[mi] != null)
                    cb.bkModeBaseline[mi]![idx] = Plugin.GroundHeight(args.worldX, args.worldZ, args.wg ?? WorldGenerator.instance);
                return;
            }
            float coverageFrac = presentW / totW;

            var P  = cb.bkModeParams![mi];
            int nP = mode.ParamCount;
            float gate = P[0][idx];   // totW-normalized amplitude for the gate; clobbered below, restored after

            Span<float> accum = stackalloc float[bh];
            for (int cy = 0; cy < bh; cy++) accum[cy] = 0f;

            // Surface-anchored modes store per-biome coarse addh into bkModeBaseline during BakeColumn;
            // each biome overwrites it, so we crossfade the baseline with the SAME weights as the column
            // (else the full-res correction in PrepareColumn uses only the last biome's addh at multi-biome cells).
            bool surfAnch = mode.SurfaceAnchored && cb.bkModeBaseline != null
                            && mi < cb.bkModeBaseline.Length && cb.bkModeBaseline[mi] != null;
            float baseAccum = 0f;

            int n = _sdfBiomeN![idx];
            var biomeParams = _biomeParams;
            for (int k = 0; k < n; k++)
            {
                int   biomeInt = _sdfBiomeInt![idx * MaxBiomeSlots + k];
                float w        = _sdfBiomeW  ![idx * MaxBiomeSlots + k];
                if (w <= 0f) continue;
                if (!biomeParams.TryGetValue((Heightmap.Biome)biomeInt, out var bp)) continue;
                float[]? mp = (mi < bp.Length) ? bp[mi] : null;
                if (mp == null) continue;                       // biome lacks this mode (only diluted coverage)
                float ampB = mp.Length > 0 ? mp[0] : 0f;
                if (ampB <= 0f) continue;
                // Feed this biome's params to the mode; amplitude coverage-faded so it fades toward off.
                for (int pi = 0; pi < nP; pi++) P[pi][idx] = (pi < mp.Length) ? mp[pi] : 0f;
                P[0][idx] = ampB * coverageFrac;
                mode.BakeColumn(ref cb, in args);               // writes into outCol[baseIdx..] (and bkModeBaseline[mi][idx] if surface-anchored)
                for (int cy = 0; cy < bh; cy++) accum[cy] += w * outCol[baseIdx + cy];
                if (surfAnch) baseAccum += w * cb.bkModeBaseline![mi]![idx];
            }

            float rcp = 1f / presentW;
            for (int cy = 0; cy < bh; cy++) outCol[baseIdx + cy] = accum[cy] * rcp;
            if (surfAnch) cb.bkModeBaseline![mi]![idx] = baseAccum * rcp;   // crossfaded baseline matches crossfaded column
            P[0][idx] = gate;   // restore the ComputeVoxel amplitude gate
        }

        // Shared gradient field computation used by both BakeSdfSetup and pt_gradmap.
        // originX/Z: world position of coarse cell (0,0). cw: grid width (cw4). blurRadius: wu.
        // Output arrays are cw*cw, indexed [cx*cw+cz]. tgX/tgZ: normalized gradient (uphill dir).
        // blurH: smoothed height at each coarse point. gradMag: raw gradient magnitude (height/wu).
        // hStep: world-unit spacing between samples (pass 4*s from BakeSdfSetup; 4f when s≈1)
        internal static void ComputeGradField(WorldGenerator wg, float originX, float originZ,
            int cw, float blurRadius, float hStep, float[] tgX, float[] tgZ, float[] blurH, float[] gradMag)
        {
            int blurCells     = (int)(Mathf.Clamp(blurRadius, 16f, 2048f) / hStep);
            int padCells      = blurCells + 1;
            int gw2           = cw + 2 * padCells;
            float wxOrig      = originX - padCells * hStep;
            float wzOrig      = originZ - padCells * hStep;

            var hGrid    = new float[gw2 * gw2];
            var hTmp     = new float[gw2 * gw2];
            var blurGrid = new float[gw2 * gw2];

            for (int gi = 0; gi < gw2; gi++)
            for (int gj = 0; gj < gw2; gj++)
                hGrid[gi * gw2 + gj] = GroundHeight(wxOrig + gi * hStep, wzOrig + gj * hStep, wg);

            for (int gj = 0; gj < gw2; gj++)
            {
                float sum = 0f; int cnt = 0;
                for (int gi = 0; gi < blurCells && gi < gw2; gi++) { sum += hGrid[gi * gw2 + gj]; cnt++; }
                for (int gi = 0; gi < gw2; gi++)
                {
                    if (gi + blurCells < gw2) { sum += hGrid[(gi + blurCells) * gw2 + gj]; cnt++; }
                    if (gi - blurCells - 1 >= 0) { sum -= hGrid[(gi - blurCells - 1) * gw2 + gj]; cnt--; }
                    hTmp[gi * gw2 + gj] = sum / cnt;
                }
            }
            for (int gi = 0; gi < gw2; gi++)
            {
                float sum = 0f; int cnt = 0;
                for (int gj = 0; gj < blurCells && gj < gw2; gj++) { sum += hTmp[gi * gw2 + gj]; cnt++; }
                for (int gj = 0; gj < gw2; gj++)
                {
                    if (gj + blurCells < gw2) { sum += hTmp[gi * gw2 + gj + blurCells]; cnt++; }
                    if (gj - blurCells - 1 >= 0) { sum -= hTmp[gi * gw2 + gj - blurCells - 1]; cnt--; }
                    blurGrid[gi * gw2 + gj] = sum / cnt;
                }
            }

            for (int cx = 0; cx < cw; cx++)
            for (int cz = 0; cz < cw; cz++)
            {
                int gi = cx + padCells, gj = cz + padCells;
                float gdx = (blurGrid[(gi + 1) * gw2 + gj] - blurGrid[(gi - 1) * gw2 + gj]) * 0.5f;
                float gdz = (blurGrid[gi * gw2 + gj + 1]   - blurGrid[gi * gw2 + gj - 1])   * 0.5f;
                float gdm = Mathf.Sqrt(gdx * gdx + gdz * gdz);
                int cidx = cx * cw + cz;
                gradMag[cidx] = gdm;
                if (gdm > 0.001f) { gdx /= gdm; gdz /= gdm; } else { gdx = 1f; gdz = 0f; }
                tgX[cidx]   = gdx;
                tgZ[cidx]   = gdz;
                blurH[cidx] = blurGrid[gi * gw2 + gj];
            }
        }

        // Per-zone-center gradient cache for continuous cross-boundary grad field.
        // Keyed by zone coordinate (zone = floor(world / ZoneSize)). Cleared on config reload.
        internal static class ZoneGradCache
        {
            const float ZoneSize = 64f;
            static readonly Dictionary<(int, int), (float tgX, float tgZ, float mag)> _cache = new();
            static readonly object _lock = new();

            public static void Clear() { lock (_lock) _cache.Clear(); }

            static (float tgX, float tgZ, float mag) Compute(WorldGenerator wg, int zx, int zz, float blurRadius)
            {
                float cx = (zx + 0.5f) * ZoneSize;
                float cz = (zz + 0.5f) * ZoneSize;
                float r  = Mathf.Max(blurRadius, ZoneSize * 0.5f);
                // 8-direction ring: cardinal + diagonal. Diagonal weight 0.5 (projected onto axes).
                float hpx = Plugin.GroundHeight(cx + r, cz, wg),     hnx = Plugin.GroundHeight(cx - r, cz, wg);
                float hpz = Plugin.GroundHeight(cx, cz + r, wg),     hnz = Plugin.GroundHeight(cx, cz - r, wg);
                float hpp = Plugin.GroundHeight(cx + r, cz + r, wg), hnn = Plugin.GroundHeight(cx - r, cz - r, wg);
                float hpn = Plugin.GroundHeight(cx + r, cz - r, wg), hnp = Plugin.GroundHeight(cx - r, cz + r, wg);
                float gx = ((hpx - hnx) + 0.5f * ((hpp - hnp) + (hpn - hnn))) / (4f * r);
                float gz = ((hpz - hnz) + 0.5f * ((hpp - hpn) + (hnp - hnn))) / (4f * r);
                float mag = Mathf.Sqrt(gx * gx + gz * gz);
                float inv = 1f / (mag + 0.001f);
                return (gx * inv, gz * inv, mag);
            }

            static (float tgX, float tgZ, float mag) Get(WorldGenerator wg, int zx, int zz, float blurRadius)
            {
                lock (_lock)
                    if (_cache.TryGetValue((zx, zz), out var v)) return v;
                var r = Compute(wg, zx, zz, blurRadius);
                lock (_lock) { _cache[(zx, zz)] = r; return r; }
            }

            public static (float tgX, float tgZ, float mag) Sample(WorldGenerator wg, float wx, float wz, float blurRadius)
            {
                // Bicubic (smoothstep) interpolation between surrounding zone-center gradients — C1 continuous.
                float fx = wx / ZoneSize - 0.5f;
                float fz = wz / ZoneSize - 0.5f;
                int zx0 = Mathf.FloorToInt(fx), zz0 = Mathf.FloorToInt(fz);
                float bfx = Mathf.SmoothStep(0f, 1f, fx - zx0);
                float bfz = Mathf.SmoothStep(0f, 1f, fz - zz0);

                var (tx00, tz00, m00) = Get(wg, zx0,     zz0,     blurRadius);
                var (tx10, tz10, m10) = Get(wg, zx0 + 1, zz0,     blurRadius);
                var (tx01, tz01, m01) = Get(wg, zx0,     zz0 + 1, blurRadius);
                var (tx11, tz11, m11) = Get(wg, zx0 + 1, zz0 + 1, blurRadius);

                float w00 = (1-bfx)*(1-bfz), w10 = bfx*(1-bfz);
                float w01 = (1-bfx)*bfz,      w11 = bfx*bfz;

                float tgX = tx00*w00 + tx10*w10 + tx01*w01 + tx11*w11;
                float tgZ = tz00*w00 + tz10*w10 + tz01*w01 + tz11*w11;
                float mag  = m00*w00  + m10*w10  + m01*w01  + m11*w11;

                float len = Mathf.Sqrt(tgX*tgX + tgZ*tgZ);
                if (len > 0.001f) { tgX /= len; tgZ /= len; }
                return (tgX, tgZ, mag);
            }
        }

        // Vanilla-matching ground height. WorldGenerator.GetHeight is SINGLE-BIOME (steps ~tens of
        // meters at biome boundaries); vanilla's rendered terrain is a per-zone bilinear blend of the
        // 4 corner biomes' height functions, computed by HeightmapBuilder.Build. We reuse that exact
        // pure function (private, via reflection — vanilla runs it on its own worker thread, so it's
        // thread-safe) per 64m zone, cache the 65×65 grid, and bilinear-sample it. No replication.
        internal static class VanillaHeight
        {
            const int   Wv       = 64;      // heightmap width; verts = Wv+1. Near terrain = 64m @ scale 1.
            const int   Num      = Wv + 1;  // 65
            const float ZoneSize = 64f;
            const float ZoneHalf = 32f;

            static System.Reflection.MethodInfo? _build;
            static object? _builder;
            static bool _init, _ok;
            static readonly System.Collections.Concurrent.ConcurrentDictionary<(int, int), float[]?> _cache = new();

            public static void Clear() => _cache.Clear();

            static void Init()
            {
                if (_init) return;
                _init = true;
                try
                {
                    _builder = HeightmapBuilder.instance;
                    _build   = typeof(HeightmapBuilder).GetMethod("Build",
                        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                    _ok = _builder != null && _build != null;
                    if (!_ok) Log.LogWarning("[ProceduralTerrain] VanillaHeight: reflection unavailable, using GetHeight fallback");
                }
                catch (System.Exception e) { _ok = false; Log.LogWarning($"[ProceduralTerrain] VanillaHeight init failed: {e.Message}"); }
            }

            static float[]? BuildZone((int, int) z, WorldGenerator wg)
            {
                try
                {
                    var center = new Vector3(z.Item1 * ZoneSize, 0f, z.Item2 * ZoneSize);
                    var data   = new HeightmapBuilder.HMBuildData(center, Wv, 1f, false, wg);
                    _build!.Invoke(_builder, new object[] { data });
                    var list = data.m_baseHeights;
                    return (list != null && list.Count >= Num * Num) ? list.ToArray() : null;
                }
                catch (System.Exception e) { Log.LogWarning($"[ProceduralTerrain] VanillaHeight build failed: {e.Message}"); return null; }
            }

            public static float Sample(float wx, float wz, WorldGenerator wg)
            {
                Init();
                if (!_ok) return wg.GetHeight(wx, wz);
                int zx = Mathf.FloorToInt((wx + ZoneHalf) / ZoneSize);
                int zz = Mathf.FloorToInt((wz + ZoneHalf) / ZoneSize);
                var grid = _cache.GetOrAdd((zx, zz), key => BuildZone(key, wg));
                if (grid == null) return wg.GetHeight(wx, wz);
                float ox = zx * ZoneSize - ZoneHalf;   // world pos of vertex (l=0,k=0)
                float oz = zz * ZoneSize - ZoneHalf;
                float lx = Mathf.Clamp(wx - ox, 0f, Wv);
                float lz = Mathf.Clamp(wz - oz, 0f, Wv);
                int il = Mathf.Min((int)lx, Wv - 1), ik = Mathf.Min((int)lz, Wv - 1);
                float fx = lx - il, fz = lz - ik;
                // vanilla layout: m_baseHeights[k*Num + l], vertex world = (ox+l, oz+k)
                float h00 = grid[ik * Num + il],           h10 = grid[ik * Num + il + 1];
                float h01 = grid[(ik + 1) * Num + il],     h11 = grid[(ik + 1) * Num + il + 1];
                float a = h00 + (h10 - h00) * fx;
                float b = h01 + (h11 - h01) * fx;
                return a + (b - a) * fz;
            }
        }

        // Ground surface height: vanilla-blended (matches rendered terrain) when _vanillaHeight, else raw single-biome.
        internal static float GroundHeight(float wx, float wz, WorldGenerator? wg)
        {
            if (wg == null) return 0f;
            return _vanillaHeight ? VanillaHeight.Sample(wx, wz, wg) : wg.GetHeight(wx, wz);
        }

        // Acceleration for the cave-seed surface query: ComposedSurfaceHeight is a smooth 2D scalar but each exact
        // call root-finds the formation column (~30 noise evals). Seeds hammer it ~5×/candidate over a wide lattice,
        // so we memoize it on a FIXED WORLD-ALIGNED grid (same nodes for every zone → seeds stay identical across
        // zones → tiling preserved) and bilinear-sample. NOT a heightmap of the terrain — the field stays full 3D;
        // this is a throwaway cache of the surface-elevation function used only to anchor seeds. World-aligned grid
        // step is coarse because the mountain surface is low-frequency; the interp error is sub-metre.
        const float SurfGridStep = 8f;
        const int   SurfCacheCap = 262144;   // ~512², a few MB; FIFO-bounded so it can't grow unbounded over a bake
        static readonly System.Collections.Concurrent.ConcurrentDictionary<(int, int), float> _composedSurfCache = new();
        static readonly System.Collections.Concurrent.ConcurrentQueue<(int, int)> _composedSurfOrder = new();
        internal static void ClearComposedSurfCache() { _composedSurfCache.Clear(); while (_composedSurfOrder.TryDequeue(out _)) { } }

        static float SurfNode(int gx, int gz, WorldGenerator wg)
        {
            var key = (gx, gz);
            if (_composedSurfCache.TryGetValue(key, out var v)) return v;
            v = ComposedSurfaceHeight(gx * SurfGridStep, gz * SurfGridStep, wg);
            if (_composedSurfCache.TryAdd(key, v))
            {
                _composedSurfOrder.Enqueue(key);
                while (_composedSurfCache.Count > SurfCacheCap && _composedSurfOrder.TryDequeue(out var old))
                    if (!old.Equals(key)) _composedSurfCache.TryRemove(old, out _);
            }
            return v;
        }

        // Bilinear sample of the cached composed surface — the cheap replacement for a per-query ComposedSurfaceHeight.
        internal static float ComposedSurfaceSample(float wx, float wz, WorldGenerator? wg)
        {
            if (wg == null) return 0f;
            float fx = wx / SurfGridStep, fz = wz / SurfGridStep;
            int gx = Mathf.FloorToInt(fx), gz = Mathf.FloorToInt(fz);
            float tx = fx - gx, tz = fz - gz;
            float h00 = SurfNode(gx, gz, wg),     h10 = SurfNode(gx + 1, gz, wg);
            float h01 = SurfNode(gx, gz + 1, wg), h11 = SurfNode(gx + 1, gz + 1, wg);
            float a = h00 + (h10 - h00) * tx, b = h01 + (h11 - h01) * tx;
            return a + (b - a) * tz;
        }

        // GLOBAL composed ground surface = base ground + the formation mode's relief, evaluated straight from
        // the shared noise pool — a pure function of (x,z), identical in every zone. This is the surface the
        // per-zone running field produces at its zero-crossing, but computable ANYWHERE, so a neighbour zone can
        // snap a tunnel seed to the exact same point the owner did → chains tile across zones. Uses the DOMINANT
        // biome's formation params (GetBiome is a cheap global function every zone agrees on); biome param-blending
        // is skipped (the surface is ~continuous and the seed only needs a few-metre-accurate, CONSISTENT point).
        internal static float ComposedSurfaceHeight(float wx, float wz, WorldGenerator? wg)
        {
            if (wg == null) return 0f;
            float h = GroundHeight(wx, wz, wg);
            var pool = _noisePool;
            if (pool == null || pool.Length < 8) return h;
            if (!_biomeParams.TryGetValue(wg.GetBiome(wx, wz), out var bp)) return h;
            var pipe = _pipeline;
            for (int k = 0; k < pipe.Length; k++)
            {
                if (!(pipe[k] is IsotropicFormation)) continue;
                int mi = pipe[k].PipelineIndex;
                if (mi < 0 || mi >= bp.Length) continue;
                var P = bp[mi];
                if (P == null || P.Length < 16) continue;
                float amp = P[0]; if (amp <= 0f) continue;
                float freq = P[1], warpAmp = P[2], warpFreq = P[3], vscale = P[4], threshold = P[5],
                      twist = P[6], twistVary = P[7], twistFq = P[8], ampVary = P[9], ampFreq = P[10],
                      flare = P[11], yoffset = P[12], smoothR = P[13], yanchor = P[15];
                float warpx = pool[3].GetNoise(wx * warpFreq, wz * warpFreq, 0f) * warpAmp;
                float warpz = pool[4].GetNoise(wx * warpFreq, wz * warpFreq, 0f) * warpAmp;
                float addh;
                if (yanchor != 0f) addh = yanchor;
                else if (smoothR > 0f)
                {
                    float s9 = 0f;
                    for (int dx = -1; dx <= 1; dx++) for (int dz = -1; dz <= 1; dz++) s9 += GroundHeight(wx + dx * smoothR + warpx, wz + dz * smoothR + warpz, wg);
                    addh = s9 / 9f;
                }
                else addh = GroundHeight(wx + warpx, wz + warpz, wg);
                addh -= yoffset;
                float lamp = Mathf.Max(amp * (1f + ampVary * pool[5].GetNoise(wx * ampFreq, wz * ampFreq, 0f)), 0.001f);
                float ts   = 1f + twistVary * pool[6].GetNoise(wx * twistFq, wz * twistFq, 0f);
                // Column SDF at rely=d: d - (n(d) - efft(d))*lamp. Surface = root. Scan a ±1.5·lamp window for a
                // sign change (the formation displacement is bounded by ~lamp), then bisect. Mirrors BakeColumn.
                float F(float d)
                {
                    float wy = addh + d, sy = d * vscale, ang = wy * twist * ts;
                    float c = Mathf.Cos(ang), ss = Mathf.Sin(ang);
                    float n = pool[7].GetNoise((wx * c - wz * ss) * freq, sy * freq, (wx * ss + wz * c) * freq);
                    float ft = Mathf.Clamp01(d / lamp);
                    float efft = threshold - flare * (4f * ft * (1f - ft));
                    return d - (n - efft) * lamp;
                }
                float lo = -1.5f * lamp, hi = 1.5f * lamp, prev = F(lo), pd = lo, droot = 0f; bool found = false;
                for (int i = 1; i <= 12; i++)
                {
                    float d = lo + (hi - lo) * i / 12f, fd = F(d);
                    if ((prev <= 0f) != (fd <= 0f))
                    {
                        float a = pd, b = d;
                        for (int j = 0; j < 14; j++) { float m = (a + b) * 0.5f; if ((F(a) <= 0f) == (F(m) <= 0f)) a = m; else b = m; }
                        droot = (a + b) * 0.5f; found = true; break;
                    }
                    prev = fd; pd = d;
                }
                h = addh + (found ? droot : 0f);
            }
            return h;
        }

        // Fast path for zones with NO active modes (vanilla / Ocean / any unmodified biome): fill buf with the
        // base-ground SDF per column directly from Voxheim's heights (groundwork already baked into heightGrid) —
        // no coarse running field, no trilinear upsample, no mode compose. The surface is a heightfield, so there's
        // nothing to compose and (with island/pocket also skipped) no flood. ~56ms FillPhase1 → a few ms here.
        static void VanillaFill(float[] buf, int bw, int bh, int extLo, float s,
            int ox, int oz, int overlap, WorldGenerator? wg, Voxheim.Core.HeightGrid? heightGrid)
        {
            int owned = bw - 2 * overlap;
            for (int cx = 0; cx < bw; cx++)
            for (int cz = 0; cz < bw; cz++)
            {
                int ix = cx - overlap, iz = cz - overlap;
                float h;
                if (heightGrid != null && ix >= 0 && ix < owned && iz >= 0 && iz < owned)
                    h = heightGrid[ix, iz] * s;                                   // owned: Voxheim heights (groundwork baked in)
                else
                    h = wg != null ? GroundHeight((ox - overlap + cx) * s, (oz - overlap + cz) * s, wg) : 0f;  // skirt: vanilla
                for (int cy = 0; cy < bh; cy++)
                    buf[(cx * bh + cy) * bw + cz] = (extLo + cy) * s - h;         // SDF: >0 air above the surface, <0 solid below
            }
        }

        // River protection as a direct per-column crossfade toward vanilla (not a biome-weight injection, which
        // seamed at zone boundaries). Returns 1 away from rivers, →0 inside the corridor, so the column write
        // `baseSDF + (combined-baseSDF)*riverFac` collapses our deviation back to the vanilla river bed
        // (GetHeight already carries vanilla's own AddRivers bed). riverCore = knee plateau; riverProtect =
        // shoulder sharpness/width (saturates the suppression). Both hashed (change the baked SDF → rebake).
        const float RiverWaterLevel = 30f;   // Valheim sea level (matches coastFade's SeaLevel)

        // Per-COLUMN river protection: suppress strength in [0,1] AND a DOME TOP (absolute Y). The river field is
        // 2D (GetRiverWeight), so we fake a 3D vault: the protection shell rises highest over the channel centre
        // (weight≈1) and tapers to the waterline at the banks → an arch/dome cross-section instead of a flat slab
        // with shelves. The per-voxel band factor then fades protection out above that per-column dome top.
        static void RiverColumn(float worldX, float worldZ, WorldGenerator? wg, out float suppress, out float domeTop)
        {
            suppress = 0f; domeTop = RiverWaterLevel;
            if (Plugin._riverProtect <= 0f || wg == null) return;
            var rf = RiverWeightFn(wg);
            if (rf == null) return;
            rf(worldX, worldZ, out float rw, out float _);
            float edge = Plugin._riverEdge, core = Mathf.Max(Plugin._riverCore, edge + 0.001f);
            if (rw <= edge) return;                                           // outside the outer radius → untouched
            float shaped = Mathf.Clamp01((rw - edge) / (core - edge));       // remap [edge..core] → [0..1]
            suppress = Mathf.Clamp01(shaped * Plugin._riverProtect);         // gain sharpens the shoulder (1 = full protect)
            // Rounded dome profile: height ∝ sqrt(shaped·(2−shaped)) ≈ a semicircle → full domeHeight at centre,
            // bulging smoothly, tapering to 0 at the bank. domeHeight = 0 → flat at water; huge → ~full column.
            float domeShape = Mathf.Sqrt(Mathf.Clamp01(shaped * (2f - shaped)));
            domeTop = RiverWaterLevel + Plugin._riverDomeHeight * domeShape;
        }

        // Per-voxel dome band factor: 1 at/below the (per-column) dome top, fading to 0 over `fade` above it.
        // Above the dome → 0 → no protection → terrain/overhangs free to arch over. Shared by rivers + locations.
        static float DomeBandFactor(float worldY, float domeTop, float fade)
            => 1f - Mathf.Clamp01((worldY - domeTop) / Mathf.Max(fade, 0.001f));

        static void FillPhase1(float[] buf, int bw, int bh, int extLo, float s,
            int ox, int oz, int overlap, WorldGenerator? wg, int cw4,
            List<(Vector2 pos, float extR)> nearbyLocs, Voxheim.Core.HeightGrid? heightGrid = null)
        {
            var pipeline = _pipeline;
            int pLen2 = pipeline.Length;
            int cv = CellVox;               // voxels per coarse cell (coarseMult knob)
            float invCv = 1f / cv;
            if (Plugin._modeColCtx == null || Plugin._modeColCtx.Length < pLen2)
                Plugin._modeColCtx = new ModeColCtx[pLen2];
            for (int cx = 0; cx < bw; cx++)
            for (int cz = 0; cz < bw; cz++)
            {
                int   gx     = ox - overlap + cx, gz = oz - overlap + cz;
                float worldX = gx * s,            worldZ = gz * s;

                int cx4  = cx / cv, cz4  = cz / cv;
                int cx4b = Math.Min(cx4 + 1, cw4 - 1);
                int cz4b = Math.Min(cz4 + 1, cw4 - 1);
                float bfx = (cx % cv) * invCv;
                float bfz = (cz % cv) * invCv;
                int b200 = cx4  * cw4 + cz4,  b210 = cx4b * cw4 + cz4;
                int b201 = cx4  * cw4 + cz4b, b211 = cx4b * cw4 + cz4b;

                float profFlD    = BL(_cb.bk2dFlD!,    b200, b210, b201, b211, bfx, bfz);
                float profFlFade = BL(_cb.bk2dFlFade!, b200, b210, b201, b211, bfx, bfz);
                float profFlLvl  = BL(_cb.bk2dFlLevel!, b200, b210, b201, b211, bfx, bfz);
                float h;
                if (heightGrid != null)
                {
                    int hgx = cx - overlap, hgz = cz - overlap;
                    h = heightGrid[hgx, hgz] * s;
                }
                else if (wg == null) { h = 0f; }
                else
                {
                    long hKey = (long)gx << 32 | (uint)gz;
                    if (!_heightCache.TryGetValue(hKey, out h))
                    {
                        h = GroundHeight(worldX, worldZ, wg);
                        if (!_parallelBakeActive) _heightCache[hKey] = h;
                    }
                }
                float locFac     = 1f;
                float locSuppress = 0f, locDomeTop = 1e9f;   // dome over the location (1e9 = full column / off)
                RiverColumn(worldX, worldZ, wg, out float riverSuppress, out float riverDomeTop);   // per-column strength + dome top; band applied per-voxel below
                float effectiveH = h;
                if ((_protectLocations || _flatPlaneLocations) && nearbyLocs.Count > 0)
                {
                    float minFac = 1f, flatH = h;
                    for (int li = 0; li < nearbyLocs.Count; li++)
                    {
                        float lf = LocFactor(nearbyLocs[li], worldX, worldZ);
                        if (lf < minFac)
                        {
                            minFac = lf;
                            flatH  = wg != null ? wg.GetHeight(nearbyLocs[li].pos.x, nearbyLocs[li].pos.y) : h;
                        }
                    }
                    if (_protectLocations)
                    {
                        locFac = minFac;
                        // Dome recipe (same as rivers): protection strength (1−minFac) is tallest over the location
                        // centre, tapering to its edge → a hemispherical bubble above the pad (ground = flatH). The
                        // per-voxel band then frees terrain above the dome. _locDomeHeight=0 → full column (off).
                        locSuppress = 1f - minFac;
                        if (Plugin._locDomeHeight > 0f)
                        {
                            float domeShape = Mathf.Sqrt(Mathf.Clamp01(locSuppress * (2f - locSuppress)));
                            locDomeTop = flatH + Plugin._locDomeHeight * domeShape;
                        }
                    }
                    if (_flatPlaneLocations) effectiveH = Mathf.Lerp(flatH, h, minFac);
                }

                // mode D uses a (cw4+1)×(cw4+1) coarse grid
                int mdCw  = cw4 + 1;
                int mdCx4 = cx / cv, mdCz4 = cz / cv;
                int md200 = mdCx4 * mdCw + mdCz4,       md210 = (mdCx4 + 1) * mdCw + mdCz4;
                int md201 = mdCx4 * mdCw + (mdCz4 + 1), md211 = (mdCx4 + 1) * mdCw + (mdCz4 + 1);
                float nmX = BL(_cb.bk2dTgX!, md200, md210, md201, md211, bfx, bfz);
                float nmZ = BL(_cb.bk2dTgZ!, md200, md210, md201, md211, bfx, bfz);
                float nmLen = Mathf.Sqrt(nmX * nmX + nmZ * nmZ);
                if (nmLen > 0.001f) { nmX /= nmLen; nmZ /= nmLen; } else { nmX = 1f; nmZ = 0f; }
                float mdBlurH  = BL(_cb.bk2dBlurH!,   md200, md210, md201, md211, bfx, bfz);
                float mdGradMg = BL(_cb.bk2dGradMag!, md200, md210, md201, md211, bfx, bfz);

                var col = new ColumnCtx {
                    worldX=worldX, worldZ=worldZ, effectiveH=effectiveH, locFac=locFac,
                    blurH=mdBlurH, gradMag=mdGradMg, gradDirX=nmX, gradDirZ=nmZ,
                    bfx=bfx, bfz=bfz,
                    b200=b200, b210=b210, b201=b201, b211=b211,
                    b3_00=b200*bh, b3_10=b210*bh, b3_01=b201*bh, b3_11=b211*bh,
                    md200=md200, md210=md210, md201=md201, md211=md211,
                    cw4=cw4, bh=bh,
                    cx=cx, cz=cz, bw=bw
                };
                bool compose = _composeMode == 1;
                if (!compose) foreach (var mode in pipeline) mode.PrepareColumn(ref _cb, in col);

                // Voxel loop
                // floorY = where carving re-solidifies. Take the HIGHER of the surface-relative floor and the
                // ABSOLUTE floorLevel (e.g. sea level), so carving can't go below floorLevel. floorFade softens
                // the band above it. Per-voxel (the fade below), so it clamps only the bottom — not the whole column.
                float floorY = Mathf.Max(effectiveH - profFlD, profFlLvl);
                for (int cy = 0; cy < bh; cy++)
                {
                    float worldY  = (extLo + cy) * s;
                    float baseSDF = worldY - effectiveH;
                    float combined;
                    if (compose)
                    {
                        // Path B: trilinearly upsample the iso-coarse running field ([cell*ch + cyc],
                        // world units) to this voxel — XZ bilinear + linear in Y between two coarse levels.
                        int cvY = CellVoxY;
                        int ch = (bh + cvY - 1) / cvY + 1;
                        int cyc0 = cy / cvY; if (cyc0 > ch - 2) cyc0 = ch - 2;
                        float fyc = (cy - cyc0 * cvY) / (float)cvY;
                        int aa = col.b200 * ch, bb = col.b210 * ch, cc = col.b201 * ch, dd = col.b211 * ch;
                        float lo = Bilerp(_runSDF![aa + cyc0],     _runSDF![bb + cyc0],     _runSDF![cc + cyc0],     _runSDF![dd + cyc0],     col.bfx, col.bfz);
                        float hi = Bilerp(_runSDF![aa + cyc0 + 1], _runSDF![bb + cyc0 + 1], _runSDF![cc + cyc0 + 1], _runSDF![dd + cyc0 + 1], col.bfx, col.bfz);
                        combined = lo + (hi - lo) * fyc;
                    }
                    else
                    {
                        combined = baseSDF;
                        var vox = new VoxelCtx {
                            worldX=worldX, worldY=worldY, worldZ=worldZ, effectiveH=effectiveH,
                            cy=cy, locFac=locFac, floorY=floorY, floorFade=profFlFade
                        };
                        foreach (var mode in pipeline) mode.ComputeVoxel(ref combined, in vox);
                    }
                    float _fade = profFlFade > 0f ? Mathf.Clamp01((worldY - floorY) / profFlFade) : 1f;
                    float riverFac = 1f - riverSuppress * DomeBandFactor(worldY, riverDomeTop, Plugin._riverBandFade);   // per-voxel river dome vault
                    float locFacV  = Plugin._locDomeHeight > 0f                                   // per-voxel location dome (else full-column locFac)
                                   ? 1f - locSuppress * DomeBandFactor(worldY, locDomeTop, Plugin._locBandFade)
                                   : locFac;
                    buf[(cx * bh + cy) * bw + cz] = baseSDF + (combined - baseSDF) * locFacV * riverFac * _fade;
                }
            }
        }
    }

    // Optional cfg-forced world seed (testing aid). Hooks the NEW-WORLD ctor `World(string name, string seed)`
    // ONLY — the load path is `World(SaveWithBackups ...)`, which we never touch, so an existing world always
    // keeps its saved seed regardless of the current cfg. Forcing it here matches the UI's seed-string path
    // (m_seed = GetStableHashCode(name)); WorldGenerator + our WorldSeed() both read m_seed, so everything aligns.
    // Not in the bake hash and not hot-reloadable into a live world by design — the seed is baked into the world.
    [HarmonyPatch(typeof(World), MethodType.Constructor, new System.Type[] { typeof(string), typeof(string) })]
    static class ForceSeedPatch
    {
        static void Postfix(World __instance)
        {
            string? forced = Plugin._forcedSeed;
            // Skip empty-seed worlds: GetMenuWorld/GetEditorWorld use new World("menu"/"editor", "") — forcing a
            // seed there hijacks (and breaks) the menu-background preview. Real new worlds always arrive with a
            // non-empty seed (GetCreateWorld → GenerateSeed()), so this still forces every genuine new world.
            if (string.IsNullOrEmpty(forced) || __instance == null ||
                string.IsNullOrEmpty(__instance.m_seedName) || __instance.m_seedName == forced) return;
            int newSeed = StringExtensionMethods.GetStableHashCode(forced);
            Plugin.Log.LogWarning(
                $"════════ PT SEED OVERRIDE ════════ new world forced to seed \"{forced}\" (hash {newSeed:X8}) " +
                $"from pt cfg [Debug] seed= — typed seed \"{__instance.m_seedName}\" ({__instance.m_seed:X8}) ignored. " +
                $"Only affects NEW worlds; existing saves keep their seed. Remove the cfg line to restore normal. ═════════");
            __instance.m_seedName = forced;
            __instance.m_seed     = newSeed;
        }
    }

    // Demand-gate: make ZoneSystem.SpawnZone wait for our SDF the same way it already waits for the async
    // heightmap (IsTerrainReady → return false → retry next tick). First time a Full/Ghost zone is generated,
    // if our carve isn't cached we kick it off (background) and skip — ZoneSystem retries until it's ready, then
    // SpawnZone runs with surfH present so PlaceVegetation (and location snapping) conform to the voxel surface.
    // No racing ring, no worker-count balancing: worker count is pure throughput (ghost-ring fill speed).
    [HarmonyPatch]
    static class SpawnZonePrefetchGate
    {
        static MethodBase? TargetMethod() =>
            typeof(ZoneSystem).GetMethod("SpawnZone", BindingFlags.NonPublic | BindingFlags.Instance);
        static readonly MethodInfo? _isGen =
            typeof(ZoneSystem).GetMethod("IsZoneGenerated", BindingFlags.NonPublic | BindingFlags.Instance);
        static readonly Dictionary<(int, int), int> _misses = new();
        const int MaxMisses = 150;   // ZoneSystem ticks ~10/s → ~15s cap before falling through to vanilla

        static bool Prefix(ZoneSystem __instance, Vector2s zoneID, ZoneSystem.SpawnMode mode,
                           out GameObject root, ref bool __result)
        {
            root = null;
            if (!ZonePrefetcher.Enabled) return true;                       // feature off → vanilla
            if (mode != ZoneSystem.SpawnMode.Full && mode != ZoneSystem.SpawnMode.Ghost) return true;
            if (_isGen != null && (bool)_isGen.Invoke(__instance, new object[] { zoneID })!) return true;  // already generated → don't block revisits

            var (ox, oz) = ZonePrefetcher.ZoneOrigin(zoneID.x, zoneID.y);
            var key = (zoneID.x, zoneID.y);
            if (ZonePrefetcher.IsReady(ox, oz)) { _misses.Remove(key); return true; }   // carved → proceed, conform works

            _misses.TryGetValue(key, out int m);
            if (m >= MaxMisses)                                             // carve stuck/failed → don't wedge the zone
            {
                Plugin.Log.LogWarning($"[PT-prefetch] gate giving up on zone ({ox},{oz}) after {m} retries — vanilla placement.");
                _misses.Remove(key);
                return true;
            }
            _misses[key] = m + 1;
            ZonePrefetcher.Request(ox, oz);
            __result = false;                                               // not spawned → ZoneSystem retries next tick
            return false;
        }
    }

    [HarmonyPatch]
    static class PlaceVegetationPatch
    {
        static MethodBase? TargetMethod() =>
            typeof(ZoneSystem).GetMethod("PlaceVegetation",
                BindingFlags.NonPublic | BindingFlags.Instance);

        static int _vegLog;
        static bool Prefix(Vector3 zoneCenterPos)
        {
            if (_vegLog < 20) { _vegLog++; Plugin.Log.LogInfo($"[PT-veg] PlaceVegetation zone({zoneCenterPos.x:F0},{zoneCenterPos.z:F0}) disableVeg={Plugin._disableVegetation} conform={Plugin._vegetationConform} maxTilt={Plugin._vegMaxTilt:F0} surfHcache={Plugin._surfHCache.Count}zones"); }
            if (Plugin._disableVegetation) return false;   // skip entirely
            Plugin._inVegPlacement = true;                 // scope the GetGroundData postfix to this call
            return true;
        }
        // runs once per zone at generation, SDF ready (gate) → place additional cave content (rung 2 POC)
        static void Postfix(Vector3 zoneCenterPos, ZoneSystem.SpawnMode mode, List<GameObject> spawnedObjects)
        {
            Plugin._inVegPlacement = false;
            try { Plugin.PlaceCaveContent(zoneCenterPos, mode, spawnedObjects); }
            catch (Exception e) { Plugin.Log.LogWarning($"[PT-cave] PlaceCaveContent threw: {e.Message}"); }
            if (Plugin._vegViz && spawnedObjects != null)
                try { VegVizRealTrees(spawnedObjects); } catch { }
        }

        // Draw one line per ACTUALLY-placed object, coloured by how far its trunk sits from the SDF surface —
        // this IS the clip metric. green=on-surface, blue=floating, red=buried(overhang clip), grey=surfH miss.
        static void VegVizRealTrees(List<GameObject> objs)
        {
            float s = Plugin.ActiveScale();
            var cleared = new HashSet<(int, int)>();
            foreach (var go in objs)
            {
                if (go == null) continue;
                // Only trees — skip rocks/bushes/pickables so lines correspond to the big visible items.
                if (go.GetComponentInChildren<TreeBase>() == null) continue;
                Vector3 p = go.transform.position;
                var (ox, oz) = Plugin.ZoneOriginFromWorld(p.x, p.z, s);
                if (cleared.Add((ox, oz))) DebugViz.BeginZone(ox, oz);   // replace stale lines when a zone re-places veg
                // Same probe the real gate uses → viz and placement can never disagree.
                Color col; Vector3 nrm = Vector3.up;
                if (!Plugin.TrySurfaceConform(p.x, p.z, Plugin._vegMaxTilt, out float sh, out Vector3 n, out bool reject))
                    col = Color.grey;                                        // surfH miss (zone not carved)
                else
                {
                    nrm = n;
                    float dy = p.y - sh;
                    col = reject ? Color.red                                 // tilt/support fail → gate drops it
                        : (Mathf.Abs(dy) <= 0.5f ? Color.green               // on surface
                        : (dy > 0f ? Color.cyan : Color.yellow));            // cyan=floating, yellow=buried
                }
                DebugViz.AddToZone(ox, oz, new[] { p, p + nrm * 1.3f }, col, 0.15f);
            }
        }
    }

    // Vanilla vegetation placement samples the 2.5D heightmap via GetGroundData (it raycasts a collider with a
    // Heightmap component — our voxel mesh has none). So trees land at heightmap height/normal, blind to our 3D
    // terrain: under overhangs (trunk into earth above) and on voxel walls (gentle heightmap normal passes the
    // tilt gate). Scoped to PlaceVegetation, we rewrite the sampled point to the voxel surface + voxel normal so
    // vanilla's own altitude/tilt gates do the right thing, and hard-reject anything steeper than vegMaxTilt.
    [HarmonyPatch]
    static class GetGroundDataConformPatch
    {
        static MethodBase? TargetMethod() =>
            typeof(ZoneSystem).GetMethod("GetGroundData",
                BindingFlags.Public | BindingFlags.Instance);

        static int _diag;
        static void Postfix(ref Vector3 p, ref Vector3 normal)
        {
            if (!Plugin._inVegPlacement) return;                             // only instrument/act during veg placement
            bool diag = _diag < 60;
            if (!Plugin._vegetationConform)
            {
                if (diag) { _diag++; Plugin.Log.LogInfo($"[PT-veg] CONFORM-DISABLED (vegetationConform=false) at ({p.x:F0},{p.z:F0}) — vanilla placement"); }
                return;
            }
            if (!Plugin.TrySurfaceConform(p.x, p.z, Plugin._vegMaxTilt, out float sh, out Vector3 n, out bool reject))
            {
                if (diag) { _diag++; Plugin.Log.LogInfo($"[PT-veg] surfH MISS ({p.x:F0},{p.z:F0}) → vanilla (no conform)  [cache={Plugin._surfHCache.Count} zones, want key={Plugin.SurfKeyFor(p.x, p.z)}]"); }
                return;   // zone not carved yet → leave vanilla
            }
            float oldY = p.y;
            p.y = sh;                                                        // snap to the voxel surface (highest solid)
            // Reject = either averaged tilt too steep OR footprint drops away under the base (cliff-edge dangle).
            // Force a horizontal normal so vanilla's own tilt gate drops the tree; otherwise orient to the slope.
            normal = reject ? new Vector3(1f, 0f, 0f) : n;
            if (diag) { _diag++; Plugin.Log.LogInfo($"[PT-veg] CONFORM ({p.x:F0},{p.z:F0}) snapY {oldY:F1}→{sh:F1} n.y={n.y:F2} {(reject ? "REJECT" : "keep")}"); }
        }
    }

    [HarmonyPatch]
    static class SpawnLocationPatch
    {
        static MethodBase? TargetMethod() =>
            typeof(ZoneSystem).GetMethod("SpawnLocation",
                BindingFlags.NonPublic | BindingFlags.Instance);

        static void Prefix(ref Vector3 pos)
        {
            var wg = WorldGenerator.instance;
            if (wg == null) return;
            float vanillaY = wg.GetHeight(pos.x, pos.z);
            pos.y = Plugin.FindCarvedSurfaceY(pos.x, vanillaY, pos.z);
        }
    }

} // namespace ProceduralTerrain
