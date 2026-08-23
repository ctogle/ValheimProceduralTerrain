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
        // ── Carve pass ────────────────────────────────────────────────────────────
        internal static void Carve(object? grid, int ox, int oz, int width, int yLo, int yHi, float gridScale = 0f)
        {
            float s;
            if (gridScale > 0f)
            {
                s = gridScale;
                if (_scale == null || _scale.Value <= 0f) _scale = s;
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
            if (!_parallelBakeActive) _zoneParams[zoneKey] = (ox, oz, width, yLo, yHi);

            const int overlap = 4;
            int bwEarly = width + 2 * overlap;
            int cw4Early = (bwEarly + 3) / 4;

            if (wg != null) BakeBlendedProfiles(cw4Early, ox, oz, overlap, s, wg);

            CaveProfile ProfAt(int cx4, int cz4) =>
                (_blendedProfs != null && cx4 * cw4Early + cz4 < _blendedProfs.Length)
                    ? _blendedProfs[cx4 * cw4Early + cz4] : CaveProfile.Default;
            int lastC = cw4Early - 1;
            var centerProf = ProfAt(cw4Early / 2, cw4Early / 2);

            var centerBiome = wg?.GetBiome(zoneX, zoneZ) ?? Heightmap.Biome.None;
            Log.LogDebug($"[ProceduralTerrain] zone ({ox},{oz}) biome={centerBiome} mB_r={centerProf.modeB_radius:F1} mA_w={centerProf.modeA_width:F2} mC_amp={centerProf.modeC_noiseAmp:F1} mC_flare={centerProf.modeC_flare:F3}");

            float searchR = width * s * 0.5f + 200f + LocFadeR;
            var nearbyLocs = new List<(Vector2 pos, float extR)>();
            foreach (var loc in _locationPositions)
            {
                float dx = loc.pos.x - zoneX, dz = loc.pos.y - zoneZ;
                float locSearchR = Mathf.Max(loc.extR, LocProtectR) + LocFadeR;
                if (dx * dx + dz * dz < (searchR + locSearchR) * (searchR + locSearchR))
                    nearbyLocs.Add(loc);
            }

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
            int bw      = width + 2 * overlap;
            int bh      = extHi - extLo + 1;
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

            int   cw4     = cw4Early;
            BakeSdfSetup(ox, oz, overlap, s, extLo, bh, bw, cw4, wg, nearbyLocs,
                out bakeC, out bakeA, out anyMbActive);

            _t0 = _sw.ElapsedMilliseconds;

            FillPhase1(buf, bw, bh, extLo, s, ox, oz, overlap, bakeC, bakeA, anyMbActive, wg, cw4, nearbyLocs);
            _t1 = _sw.ElapsedMilliseconds;

            _t1b = _sw.ElapsedMilliseconds;

            CaveProfile blurProf = centerProf;
            if (_blendedProfs != null)
            {
                int cCx = cw4 / 2, cCz = cw4 / 2;
                blurProf = _blendedProfs[cCx * cw4 + cCz];
            }
            int blurPasses = (bakeC || bakeA || anyMbActive)
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

            _t2 = _sw.ElapsedMilliseconds;

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

                for (int cx = 0; cx < bw; cx++)
                for (int cz = 0; cz < bw; cz++)
                {
                    int idx = cx * stride_x + cz;
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

                bool needsFullPass = ungNX || ungPX || ungNZ || ungPZ;
                if (needsFullPass)
                {
                    long _fixStart = _sw.ElapsedMilliseconds;
                    EnsureFixpoint(ox, oz, width, ungNX, ungPX, ungNZ, ungPZ);
                    _tFix = _sw.ElapsedMilliseconds - _fixStart;

                    Array.Clear(_visitedBuf, 0, islandBufSize);
                    qHead = 0; qTail = 0;

                    for (int cx = 0; cx < bw; cx++)
                    for (int cz = 0; cz < bw; cz++)
                    {
                        int idx = cx * stride_x + cz;
                        if (buf[idx] < 0f && !_visitedBuf[idx])
                        { _visitedBuf[idx] = true; _islandQueue[qTail++] = idx; }
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

            if (_pocketRemoval) {
                Array.Clear(_visitedBuf!, 0, bw * bh * bw);
                int stride_x = bh * bw;
                int qHead = 0, qTail = 0;

                void TryEnqueueAir(int i)
                {
                    if (!_visitedBuf![i] && buf[i] >= 0f) { _visitedBuf[i] = true; _islandQueue![qTail++] = i; }
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

            int maxYC  = yHi;
            int maxYAny = yHi;
            {
            var transBytes  = new sbyte[bh];
            var transHasOvr = new bool[bh];
            var cacheData   = new sbyte[width * bh * width];

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

            if (!_parallelBakeActive) _sdfCache[zoneKey] = (extLo, bh, width, cacheData);

            if (!_parallelBakeActive)
            {
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
            }
            }

            long _t3 = _sw.ElapsedMilliseconds;

            if (_dumpZonesRemaining > 0)
            {
                _dumpZonesRemaining--;
                DumpZoneData(ox, oz, overlap, s, wg, cw4Early, bw, bh, extLo, buf);
            }

            var _centerBiome = WorldGenerator.instance?.GetBiome((ox + width / 2) * (float)(_scale ?? 1f), (oz + width / 2) * (float)(_scale ?? 1f)) ?? Heightmap.Biome.None;
            Log.LogDebug($"[ProceduralTerrain] zone ({ox},{oz}) biome={_centerBiome} bakeC={bakeC} bakeA={bakeA} voxels={bw}x{bh}x{bw} fixHops={_lastFixpointHops} Ph0={_t0}ms Ph1={_t1-_t0}ms blur={_t1b-_t1}ms Ph2={_t2-_t1b}ms fix={_tFix}ms Ph3={_t3-_t2}ms total={_t3}ms");
        }

        // ── Direct brick write ────────────────────────────────────────────────────
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

                sbyte fill = (vy0 + 15 < extLo) ? (sbyte)-127 : (sbyte)127;
                var brick  = Activator.CreateInstance(_brickType, fill)!;
                var data   = new sbyte[4096];
                if (fill != 0) { for (int i = 0; i < 4096; i++) data[i] = fill; }

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

        // ── Zone data dump ───────────────────────────────────────────────────────
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

        static (int yLo, int yHi) ComputeYBounds(int ox, int oz, int w, float s, WorldGenerator wg)
        {
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

        static int _lastFixpointHops = 0;

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
            int cw4 = (bwZ + 3) / 4;

            if (wg != null) BakeBlendedProfiles(cw4, ox, oz, overlap, s, wg);

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

            float zoneX   = (ox + w * 0.5f) * s;
            float zoneZ   = (oz + w * 0.5f) * s;
            float searchR = w * s * 0.5f + 200f + LocFadeR;
            var   nearby  = new List<(Vector2 pos, float extR)>();
            foreach (var loc in _locationPositions)
            {
                float dx = loc.pos.x - zoneX, dz = loc.pos.y - zoneZ;
                float locSearchR = Mathf.Max(loc.extR, LocProtectR) + LocFadeR;
                if (dx * dx + dz * dz < (searchR + locSearchR) * (searchR + locSearchR)) nearby.Add(loc);
            }

            BakeSdfSetup(ox, oz, overlap, s, extLo, bhZ, bwZ, cw4, wg, nearby,
                out bool bakeC, out bool bakeA, out bool anyMbActive);
            FillPhase1(buf, bwZ, bhZ, extLo, s, ox, oz, overlap, bakeC, bakeA, anyMbActive, wg, cw4, nearby);

            int stX   = bhZ * bwZ;
            int bufSz2 = bwZ * bhZ * bwZ;
            if (_visitedBuf  == null || _visitedBuf.Length  < bufSz2) _visitedBuf  = new bool[bufSz2];
            if (_islandQueue == null || _islandQueue.Length < bufSz2) _islandQueue = new int[bufSz2];
            Array.Clear(_visitedBuf, 0, bufSz2);
            int qHead = 0, qTail = 0;

            for (int cx = 0; cx < bwZ; cx++)
            for (int cz = 0; cz < bwZ; cz++)
            {
                int idx = cx * stX + cz;
                if (buf[idx] < 0f && !_visitedBuf[idx])
                { _visitedBuf[idx] = true; _islandQueue[qTail++] = idx; }
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

            Array.Clear(_visitedBuf, 0, bufSz2);
            qHead = 0; qTail = 0;

            void TryEnqueueAirSeed(int idx) {
                if (!_visitedBuf[idx] && buf[idx] >= 0f)
                { _visitedBuf[idx] = true; _islandQueue![qTail++] = idx; }
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

            var percol = new bool[4, 4];
            if (_percolVisited == null || _percolVisited.Length < bufSz2) _percolVisited = new bool[bufSz2];

            for (int fromFace = 0; fromFace < 4; fromFace++)
            {
                Array.Clear(_percolVisited, 0, bufSz2);
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
                        if (buf[idx] < 0f && !_visitedBuf[idx] && !_percolVisited[idx])
                        { _percolVisited[idx] = true; _islandQueue![qTail++] = idx; }
                    }
                }
                else
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
                    float dist      = Mathf.Sqrt(na * na + nb * nb) * 0.7071f;
                    float tunnelSDF = (localWidth - dist) / localWidth * 4f * s;
                    float sdf = Mathf.Min(tunnelSDF, worldY - floorY);
                    combined = SmoothMax(combined, sdf, 2f * s);
                }

                float _fade = floorFade > 0f ? Mathf.Clamp01((worldY - floorY) / floorFade) : 1f;
                buf[(cx * bh + cy) * bw + cz] = baseSDF + (combined - baseSDF) * locFac * _fade;
            }

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
                    float dist      = Mathf.Sqrt(na * na + nb * nb) * 0.7071f;
                    float tunnelSDF = (localWidth - dist) / localWidth * 4f * s;
                    float sdf = Mathf.Min(tunnelSDF, worldY - floorY);
                    combined = SmoothMax(combined, sdf, 2f * s);
                }

                float _fade = floorFade > 0f ? Mathf.Clamp01((worldY - floorY) / floorFade) : 1f;
                buf[(cx * bh + cy) * bw + cz] = baseSDF + (combined - baseSDF) * locFac * _fade;
            }

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
        internal static bool TryComputePointSDF(int vx, int vy, int vz, out sbyte result)
        {
            result = sbyte.MaxValue;
            int ox = vx - ((vx % 64 + 64) % 64);
            int oz = vz - ((vz % 64 + 64) % 64);
            if (!_sdfCache.TryGetValue($"{ox},{oz}", out var c)) return false;
            int ci = vx - ox, ri = vz - oz, cy = vy - c.extLo;
            if (ci < 0 || ci >= c.width || ri < 0 || ri >= c.width || cy < 0 || cy >= c.bh)
                return false;
            result = c.data[(ci * c.bh + cy) * c.width + ri];
            return true;
        }

        // ── Placement height ──────────────────────────────────────────────────────
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

        static void FillPhase1(float[] buf, int bw, int bh, int extLo, float s,
            int ox, int oz, int overlap, bool bakeC, bool bakeA, bool anyMbActive, WorldGenerator? wg, int cw4,
            List<(Vector2 pos, float extR)> nearbyLocs)
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
                float locFac     = 1f;
                float effectiveH = h;
                if (_protectLocations && nearbyLocs.Count > 0)
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
                    locFac     = minFac;
                    effectiveH = Mathf.Lerp(flatH, h, locFac);
                }
                float floorY   = effectiveH - profFlD;

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
                    effectiveH, addH, profMcT, profMcFlare, localAmp, localAmp > 0f && bakeC,
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
