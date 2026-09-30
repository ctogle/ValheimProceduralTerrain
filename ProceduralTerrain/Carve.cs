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
        public float[]? bk2dFlD, bk2dFlFade;
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

            const int overlap = 4;
            int bwEarly = width + 2 * overlap;
            // +1 guard cell on the far (+x/+z) side: interpolation needs a coarse neighbor past the
            // last owned voxel. Without it, far-edge owned voxels clamp to a flat cell when CellVox>overlap
            // (coarseMult>1), stepping vs the neighbor zone. No-op at coarseMult=1 (clamp never triggers).
            int cw4Early = (bwEarly + CellVox - 1) / CellVox + 1;

            if (wg != null) BakeBlendedProfiles(cw4Early, ox, oz, overlap, s, wg);

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
            }

            int extLo   = yLo - loMargin, extHi = yHi + hiMargin;
            int bw      = width + 2 * overlap;
            int bh      = extHi - extLo + 1;
            int bufSize = bw * bh * bw;

            if (_cb.bufA == null || _cb.bufA.Length < bufSize) _cb.bufA = new float[bufSize];
            if (_cb.bufB == null || _cb.bufB.Length < bufSize) _cb.bufB = new float[bufSize];
            if (_cb.bufC == null || _cb.bufC.Length < bufSize) _cb.bufC = new float[bufSize];
            float[] buf = _cb.bufA, tmp = _cb.bufB, orig = _cb.bufC;

            // Piece 2: for baked zones on the main/load path, reconstruct per-column straight into the
            // sparse cache (surfH + band) with no dense buf. Falls through to the dense path on mismatch.
            if ((!_parallelBakeActive || System.Threading.Thread.CurrentThread.ManagedThreadId == Plugin._mainThreadId)
                && _bakeData != null && _bakeData.ContainsKey((ox, oz)))
            {
                long ft0 = System.Diagnostics.Stopwatch.GetTimestamp();
                if (PopulateSparseFromBake(ox, oz, bw, bh, extLo, s, voxelFloorY, overlap))
                {
                    double fms = (System.Diagnostics.Stopwatch.GetTimestamp() - ft0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                    if (fms > 2.0) Log.LogInfo($"[PT-load] baked zone ({ox},{oz}) fused reconstruct+fill={fms:F2}ms (bh={bh})");
                    return;
                }
            }

            // Baked zones are fully handled by PopulateSparseFromBake above (early return). Reaching here
            // means the zone is unbaked → live carve.
            bool fromBake = false;

            if (!fromBake)
            {
            InitNoise();

            int   cw4     = cw4Early;
            BakeSdfSetup(ox, oz, overlap, s, extLo, bh, bw, cw4, wg, nearbyLocs, heightGrid);

            bool anyPipelineActive = _pipeline.Length > 0 && _cb.bkModeParams != null &&
                _pipeline.Any(m => _cb.bkModeParams![m.PipelineIndex][0].Any(v => v > 0f));
            FillPhase1(buf, bw, bh, extLo, s, ox, oz, overlap, wg, cw4, nearbyLocs, heightGrid);

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

            if (_islandRemoval) {
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
                    _zoneFaceCache[(ox, oz)] = ac;
                }

                int removed = 0;
                for (int i = 0; i < islandBufSize; i++)
                    if (!_cb.visitedBuf[i] && buf[i] < 0f) { buf[i] = s; removed++; }

                Log.LogDebug($"[ProceduralTerrain] island-fill zone ({ox},{oz}): pre={!needsFullPass} seeds={qTail} removed={removed}");
            }

            if (_pocketRemoval) {
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

            } // end if (!fromBake)

            if (!fromBake)
                StageBakeEntry(ox, oz, bw, bh, extLo, s, buf);

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
            const int overlap = 4;
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
            const int overlap = 4;
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

            var percol = new bool[4, 4];
            if (_cb.percolVisited == null || _cb.percolVisited.Length < bufSz2) _cb.percolVisited = new bool[bufSz2];

            for (int fromFace = 0; fromFace < 4; fromFace++)
            {
                Array.Clear(_cb.percolVisited, 0, bufSz2);
                qHead = 0; qTail = 0;
                bool hitNX = false, hitPX = false, hitNZ = false, hitPZ = false;
                int facesLeft = 3;

                if (fromFace <= 1)
                {
                    int fCx = fromFace == 0 ? terNegX : terPosX;
                    for (int cy = 0; cy < bhZ; cy++)
                    for (int cz = terNegZ; cz <= terPosZ; cz++)
                    {
                        int idx = fCx * stX + cy * bwZ + cz;
                        if (buf[idx] < 0f && !_cb.visitedBuf[idx] && !_cb.percolVisited[idx])
                        { _cb.percolVisited[idx] = true; _cb.islandQueue![qTail++] = idx; }
                    }
                }
                else
                {
                    int fCz = fromFace == 2 ? terNegZ : terPosZ;
                    for (int cy = 0; cy < bhZ; cy++)
                    for (int cx = terNegX; cx <= terPosX; cx++)
                    {
                        int idx = cx * stX + cy * bwZ + fCz;
                        if (buf[idx] < 0f && !_cb.visitedBuf[idx] && !_cb.percolVisited[idx])
                        { _cb.percolVisited[idx] = true; _cb.islandQueue![qTail++] = idx; }
                    }
                }

                while (qHead < qTail && facesLeft > 0)
                {
                    int idx = _cb.islandQueue![qHead++];
                    int pcx = idx / stX;
                    int rem = idx % stX;
                    int pcy = rem / bwZ;
                    int pcz = rem % bwZ;

                    if (pcx == terNegX && fromFace != 0 && !hitNX) { hitNX = true; facesLeft--; }
                    if (pcx == terPosX && fromFace != 1 && !hitPX) { hitPX = true; facesLeft--; }
                    if (pcz == terNegZ && fromFace != 2 && !hitNZ) { hitNZ = true; facesLeft--; }
                    if (pcz == terPosZ && fromFace != 3 && !hitPZ) { hitPZ = true; facesLeft--; }

                    int ni;
                    if (pcx > terNegX) { ni = idx - stX; if (!_cb.percolVisited[ni] && buf[ni] < 0f && !_cb.visitedBuf[ni]) { _cb.percolVisited[ni] = true; _cb.islandQueue[qTail++] = ni; } }
                    if (pcx < terPosX) { ni = idx + stX; if (!_cb.percolVisited[ni] && buf[ni] < 0f && !_cb.visitedBuf[ni]) { _cb.percolVisited[ni] = true; _cb.islandQueue[qTail++] = ni; } }
                    if (pcy > 0)       { ni = idx - bwZ; if (!_cb.percolVisited[ni] && buf[ni] < 0f && !_cb.visitedBuf[ni]) { _cb.percolVisited[ni] = true; _cb.islandQueue[qTail++] = ni; } }
                    if (pcy < bhZ-1)   { ni = idx + bwZ; if (!_cb.percolVisited[ni] && buf[ni] < 0f && !_cb.visitedBuf[ni]) { _cb.percolVisited[ni] = true; _cb.islandQueue[qTail++] = ni; } }
                    if (pcz > terNegZ) { ni = idx - 1;   if (!_cb.percolVisited[ni] && buf[ni] < 0f && !_cb.visitedBuf[ni]) { _cb.percolVisited[ni] = true; _cb.islandQueue[qTail++] = ni; } }
                    if (pcz < terPosZ) { ni = idx + 1;   if (!_cb.percolVisited[ni] && buf[ni] < 0f && !_cb.visitedBuf[ni]) { _cb.percolVisited[ni] = true; _cb.islandQueue[qTail++] = ni; } }
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

        // ── Placement height ──────────────────────────────────────────────────────────
        internal static float FindCarvedSurfaceY(float worldX, float originalY, float worldZ)
        {
            float s = _scale is { } v && v > 0f ? v
                    : _scaleField != null && _scaleField.GetValue(null) is float sf && sf > 0f ? sf
                    : 1f;
            const int overlap = 4, width = 64;
            int gx = Mathf.RoundToInt(worldX / s);
            int gz = Mathf.RoundToInt(worldZ / s);
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

            // Populate floor arrays from blended profiles
            for (int i = 0; i < nb2Size && _blendedProfs != null && i < _blendedProfs.Length; i++)
            {
                _cb.bk2dFlD!    [i] = _blendedProfs[i].floorDepth;
                _cb.bk2dFlFade! [i] = _blendedProfs[i].floorFade;
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

                // (2) per mode: [derive incoming] -> bake -> compose -> redistance.
                foreach (var mode in pipeline)
                {
                    int mi = mode.PipelineIndex;
                    if (mi < 0) continue;

                    // SurfaceBlobs: SDF→SDF operator — union noise-modulated balls onto EVERY surface
                    // (floors + ceilings) via |D| − rEff, reading the running distance field in place.
                    // Needs eikonal=true for metric |D|. Skips the normal bake/compose path.
                    if (mode is SurfaceBlobs)
                    {
                        // Discrete spheres CENTERED ON the surface → clear hemispheres. Snap a jittered
                        // 3D lattice to the surface (c = p − D·∇D), then union min(runSDF, |x−c| − r).
                        int cc = (cw4 / 2) * cw4 + (cw4 / 2);
                        float rad = _cb.bkModeParams![mi][0][cc];
                        if (rad <= 0f) { continue; }
                        float freq = _cb.bkModeParams![mi][1][cc];
                        float L = freq > 1e-4f ? 1f / freq : 20f;              // lattice spacing (world units)
                        // (3) Enforce L >= 2*rad: spheres closer than a diameter apart merge into a
                        // blanket ("mush") instead of reading as discrete blobs on the surface.
                        L = Mathf.Max(L, 2f * rad);
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
                        int latPad = Mathf.CeilToInt(rad / L) + 1;
                        int li0 = Mathf.FloorToInt(wxLo/L)-latPad, li1 = Mathf.FloorToInt(wxHi/L)+latPad;
                        int lj0 = Mathf.FloorToInt(wzLo/L)-latPad, lj1 = Mathf.FloorToInt(wzHi/L)+latPad;
                        int lk0 = Mathf.FloorToInt(wyLo/L)-latPad, lk1 = Mathf.FloorToInt(wyHi/L)+latPad;

                        // precompute surface-snapped centers
                        var centers = new List<Vector3>(256);
                        float h = 1.5f;   // gradient sample step
                        for (int li = li0; li <= li1; li++)
                        for (int lj = lj0; lj <= lj1; lj++)
                        for (int lk = lk0; lk <= lk1; lk++)
                        {
                            float px = (li + Hash(li, lj, lk)) * L, pz = (lj + Hash(lj+7, li-3, lk)) * L, py = (lk + Hash(lk+13, li+5, lj-2)) * L;
                            // Skip points whose snap stencil isn't covered by this zone's input field —
                            // the owning zone snaps them identically (no clamp divergence → no seams).
                            if (!InField(px-h, py-h, pz-h) || !InField(px+h, py+h, pz+h)) continue;
                            float Dp = SampleD(px, py, pz);
                            if (Mathf.Abs(Dp) > L) continue;                   // lattice point not near a surface
                            float nx = SampleD(px+h,py,pz) - SampleD(px-h,py,pz);
                            float ny = SampleD(px,py+h,pz) - SampleD(px,py-h,pz);
                            float nz = SampleD(px,py,pz+h) - SampleD(px,py,pz-h);
                            float nl = Mathf.Sqrt(nx*nx+ny*ny+nz*nz); if (nl < 1e-4f) continue; nx/=nl; ny/=nl; nz/=nl;
                            centers.Add(new Vector3(px - Dp*nx, py - Dp*ny, pz - Dp*nz));  // snap to surface
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
                                for (int ci = 0; ci < centers.Count; ci++)
                                {
                                    var c = centers[ci];
                                    float dx = wx-c.x, dy = wy-c.y, dz = wz-c.z;
                                    float dd = Mathf.Sqrt(dx*dx+dy*dy+dz*dz) - rad;
                                    if (dd < best) best = dd;
                                }
                                ref float r = ref _runSDF![cbaseC + cyc];
                                if (best < r) { r = best; dbgHit++; }
                            }
                        }
                        Log.LogInfo($"[PT-blobs] centers={centers.Count} L={L:F0} rad={rad:F1} solidVoxels={dbgHit}");
                        if (_eikonal) RedistanceEikonal(_runSDF!, cw4, cw4, ch, xzStep, xzStep, yStep, 2);
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
                            Log.LogInfo($"[PT-compose] {mode.Name}: incoming surface vs base ground maxΔ={maxD:F1}m (>0 ⇒ conforming to prior modes)");
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
                    if (_eikonal) RedistanceEikonal(_runSDF!, cw4, cw4, ch, xzStep, xzStep, yStep, 2);
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
                    if (_protectLocations)   locFac     = minFac;
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
                float floorY = effectiveH - profFlD;
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
                    buf[(cx * bh + cy) * bw + cz] = baseSDF + (combined - baseSDF) * locFac * _fade;
                }
            }
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

        static void Prefix(ref Vector3 pos)
        {
            var wg = WorldGenerator.instance;
            if (wg == null) return;
            float vanillaY = wg.GetHeight(pos.x, pos.z);
            pos.y = Plugin.FindCarvedSurfaceY(pos.x, vanillaY, pos.z);
        }
    }

} // namespace ProceduralTerrain
