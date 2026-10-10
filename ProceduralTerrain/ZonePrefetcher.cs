using System;
using System.Collections.Generic;
using System.Threading;
using UnityEngine;

namespace ProceduralTerrain
{
    // Phase 1: carve zones AHEAD of Voxheim's on-demand voxelize, on background threads, into the shared
    // _sdfCache/_surfHCache. When Voxheim later voxelizes a prefetched zone, PTGenerator.Generate finds the
    // cache hit and skips the (expensive) carve — a warm hit. No SpawnZone/vegetation gate yet; this phase just
    // proves zones get carved ahead and the live-carve hitch moves off the main thread.
    //
    // Carve is concurrency-safe (all per-zone scratch is [ThreadStatic]; the bake runs it on 27 threads) and is
    // called the same way the bake does: Carve(null, …) — purely procedural, no Voxheim heightGrid.
    //
    // NOT yet included (later phases): cache eviction (so this will grow _sdfCache unbounded — keep test runs
    // short / radius modest), the vegetation gate, and the in-flight/main-thread takeover (a main-thread carve
    // of a zone mid-prefetch just does redundant work into the same cache — safe via thread-local buffers).
    internal static class ZonePrefetcher
    {
        const int ZoneW = 64;

        internal static bool Enabled   = false;  // cfg [Debug] prefetch
        internal static int  RadiusZones = 4;    // prefetch ring radius in zones around the reference position
        internal static int  ThreadCount = 0;    // 0 = auto (cores/2, kept well under the bake's cores-1)

        static readonly object _lock = new();
        static readonly Queue<(int ox, int oz)> _queue = new();
        static readonly HashSet<(int, int)> _queued   = new();
        static readonly HashSet<(int, int)> _inFlight = new();
        static Thread[]? _workers;
        static volatile bool _running;

        // diagnostics
        static long _carved, _failed;
        static int  _lastLogCarved;

        internal static void Start()
        {
            if (_running) return;
            _running = true;
            // Keep this SMALL: live play needs the main thread (render + Voxheim skin/mesh) responsive. Too many
            // workers starve it → the carves that DO miss the prefetch balloon, and Voxheim's regen stutters.
            int n = ThreadCount > 0 ? ThreadCount : Math.Max(1, Math.Min(3, Environment.ProcessorCount - 2));
            _workers = new Thread[n];
            for (int i = 0; i < n; i++)
            {
                var th = new Thread(WorkerLoop) { IsBackground = true, Name = $"PTPrefetch{i}", Priority = System.Threading.ThreadPriority.BelowNormal };
                _workers[i] = th;
                th.Start();
            }
            Plugin.Log.LogInfo($"[PT-prefetch] started {n} worker(s), radius={RadiusZones} zones.");
        }

        internal static void Stop()
        {
            _running = false;
            lock (_lock) Monitor.PulseAll(_lock);
            lock (_lock) { _queue.Clear(); _queued.Clear(); }
        }

        internal static bool IsReady(int ox, int oz) => Plugin._sdfCache.ContainsKey($"{ox},{oz}");

        internal static void Request(int ox, int oz)
        {
            if (!Enabled) return;
            if (Plugin._sdfCache.ContainsKey($"{ox},{oz}")) return;
            var key = (ox, oz);
            lock (_lock)
            {
                if (_queued.Contains(key) || _inFlight.Contains(key)) return;
                _queued.Add(key);
                _queue.Enqueue(key);
                Monitor.Pulse(_lock);
            }
        }

        static void WorkerLoop()
        {
            while (_running)
            {
                (int ox, int oz) z;
                lock (_lock)
                {
                    while (_running && _queue.Count == 0) Monitor.Wait(_lock);
                    if (!_running) return;
                    z = _queue.Dequeue();
                    _queued.Remove(z);
                    if (_inFlight.Contains(z) || Plugin._sdfCache.ContainsKey($"{z.ox},{z.oz}")) continue;
                    _inFlight.Add(z);
                }
                try
                {
                    var wg = WorldGenerator.instance;
                    if (wg != null)
                    {
                        float s = Plugin.ActiveScale();
                        var (yLo, yHi) = Plugin.ComputeYBoundsPublic(z.ox, z.oz, ZoneW, s, wg);
                        Plugin.Carve(null, z.ox, z.oz, ZoneW, yLo, yHi);   // purely procedural, same call the bake uses
                        Interlocked.Increment(ref _carved);
                    }
                }
                catch (Exception e)
                {
                    Interlocked.Increment(ref _failed);
                    Plugin.Log.LogWarning($"[PT-prefetch] carve ({z.ox},{z.oz}) failed: {e.GetType().Name}: {e.Message}");
                }
                finally { lock (_lock) _inFlight.Remove(z); }

                long carved = Interlocked.Read(ref _carved);
                if (carved - _lastLogCarved >= 25)
                {
                    _lastLogCarved = (int)carved;
                    int qd, ifl; lock (_lock) { qd = _queue.Count; ifl = _inFlight.Count; }
                    Plugin.Log.LogInfo($"[PT-prefetch] carved={carved} failed={Interlocked.Read(ref _failed)} queue={qd} inflight={ifl} cache={Plugin._sdfCache.Count}");
                }
            }
        }

        // Zone-grid origin (voxel coords) for a ZoneSystem zone id — matches how _sdfCache/_surfHCache are keyed
        // (the same gridOffset mapping FindCarvedSurfaceY uses, which is proven to align with Voxheim's OriginX/Z).
        internal static (int ox, int oz) ZoneOrigin(int zx, int zz)
            => Plugin.ZoneOriginFromWorld(zx * 64f, zz * 64f, Plugin.ActiveScale());   // zone center world (m_zoneSize=64)
    }
}
