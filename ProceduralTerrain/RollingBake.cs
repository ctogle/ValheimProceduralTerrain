using System;
using System.Collections.Generic;
using System.Threading;
using UnityEngine;

namespace ProceduralTerrain
{
    // Rolling bake: as you move, carve the disc around you on a background thread and APPEND it into the
    // cfgHash-keyed bake file, so the world progressively, permanently bakes itself to exactly where you go —
    // removing the need for an upfront full-world pt_bake for most players. Degrades gracefully: zones it hasn't
    // reached (fast travel, outrunning it at fly speed) just live-carve as today. The file stays a valid, loadable
    // bake after every flush (Plugin.RollingBakeFlush), interchangeable with a pt_bake'd one.
    //
    // Reuses the proven thread-safe pure Carve (same call the 27-thread bake + ZonePrefetcher use); the persistence
    // layer (append + index rewrite) lives in BakeManager (RollingBake* helpers). One background worker keeps live
    // play responsive; the file write is a short locked flush that can't interleave with serve reads.
    internal static class RollingBake
    {
        internal static bool  Enabled      = false;
        internal static bool  ShowHud      = false;  // [Debug] rollinghud: on-screen status overlay to watch rolling keep up
        internal static int   ThreadCount  = 1;      // background carve workers (BelowNormal). More = faster roll, more CPU contention with live play.
        internal static float RadiusMeters = 500f;   // disc carved around the player
        internal static float MoveStep     = 64f;    // re-scan after moving this far (one zone)
        internal static int   FlushZones   = 40;     // flush to disk after this many carved zones…
        internal static float FlushSeconds = 5f;     // …or this long since the last flush
        internal static int   Workers => _workers?.Length ?? 0;

        // Dedicated servers run their own bake path and must not drive a player-follow rolling bake (no player).
        // Off while a server-bake download is in flight: the download targets the SAME seed+cfgHash file, so a
        // concurrent append would race it. Rolling re-arms (with the merged file) once the download completes.
        internal static bool Active => Enabled
            && !(ZNet.instance != null && ZNet.instance.IsDedicated())
            && !Plugin._bakeDownloadActive;

        static volatile bool _running;
        internal static bool IsRunning => _running;
        internal static long SessionPersisted => _flushedTotal;
        internal static long Carved => System.Threading.Interlocked.Read(ref _carved);
        internal static int  QueueDepth { get { lock (_lock) return _pending.Count; } }
        static System.Diagnostics.Stopwatch _sessionSw;
        internal static double ElapsedSec => _sessionSw?.Elapsed.TotalSeconds ?? 0;
        // Per-worker in-flight zone (threadId → zone + start ms) for the stall watchdog + HUD "carving N".
        static readonly System.Collections.Concurrent.ConcurrentDictionary<int, (int ox, int oz, long startMs)> _active = new();
        static readonly System.Collections.Concurrent.ConcurrentDictionary<(int, int), bool> _warnedHang = new();
        internal static int ActiveCount => _active.Count;
        internal static double OldestActiveSec
        {
            get
            {
                long now = _sessionSw?.ElapsedMilliseconds ?? 0; long oldest = now;
                foreach (var kv in _active) if (kv.Value.startMs < oldest) oldest = kv.Value.startMs;
                return _active.IsEmpty ? 0 : (now - oldest) / 1000.0;
            }
        }
        // Called from the main thread (Tick): log any zone that's been carving suspiciously long — that's the
        // zone to reproduce. Dedup so each hung zone logs once.
        internal static void Watchdog()
        {
            if (!_running || _sessionSw == null) return;
            long now = _sessionSw.ElapsedMilliseconds;
            foreach (var kv in _active)
            {
                if (now - kv.Value.startMs > 20000 && _warnedHang.TryAdd((kv.Value.ox, kv.Value.oz), true))
                    Plugin.Log.LogWarning($"[PT-rolling] zone ({kv.Value.ox},{kv.Value.oz}) has been carving {(now - kv.Value.startMs) / 1000}s — likely the hang. Reproduce with: pt_carvezone {kv.Value.ox} {kv.Value.oz}");
            }
        }
        static Thread[] _workers;
        // Shared write buffer: workers carve in parallel and drop finished blocks here; flushed as a batch
        // (RollingBakeFlush is itself locked, so appends serialize). Guarded by _writeLock.
        static readonly object _writeLock = new();
        static List<(int ox, int oz, byte[] blk)> _carvedBlocks = new();
        static System.Diagnostics.Stopwatch _flushSw;
        static long _lastFlushMs;
        static readonly object _lock = new();
        // Pending zones carved NEAREST-FIRST (to the last player position) — closest zones have the most value
        // (you stand on / reactivate them first). Dequeue scans for the nearest; cheap (queue is small unless
        // you're outrunning the bake, and that's exactly when proximity ordering matters most).
        static readonly HashSet<(int ox, int oz)> _pending = new();
        static readonly HashSet<(int, int)> _persisted = new();   // in the file OR already carved-and-skipped (unmodified)
        static float _lastX = float.NaN, _lastZ;
        static long _carved, _flushedTotal;

        internal static void Arm()
        {
            if (_running) return;
            // Wait for the world to be fully loaded before capturing the seed: early in load WorldGenerator.m_world
            // exists but m_seed is still 0, which would open a junk seed-0 bake file. The player spawning is a
            // robust "world ready, seed valid" signal (and Tick needs the player anyway).
            if (Player.m_localPlayer == null) return;
            if (!Plugin.RollingBakeOpen()) return;   // world not ready yet; retry next tick
            lock (_lock)
            {
                _pending.Clear(); _persisted.Clear();
                foreach (var k in Plugin.RollingPersistedKeys()) _persisted.Add(k);
            }
            _lastX = float.NaN; _carved = 0; _flushedTotal = 0;
            lock (_writeLock) _carvedBlocks.Clear();
            _sessionSw = System.Diagnostics.Stopwatch.StartNew();
            _flushSw = System.Diagnostics.Stopwatch.StartNew(); _lastFlushMs = 0;
            _running = true;
            int n = Mathf.Clamp(ThreadCount, 1, 8);
            _workers = new Thread[n];
            for (int i = 0; i < n; i++)
            {
                _workers[i] = new Thread(WorkerLoop) { IsBackground = true, Name = $"PTRollingBake{i}", Priority = System.Threading.ThreadPriority.BelowNormal };
                _workers[i].Start();
            }
            if (ShowHud) BakeProgressUI.Show();   // overlay renders rolling status while running
            Plugin.Log.LogInfo($"[PT-rolling] armed: radius={RadiusMeters}m, {n} worker(s), flush@{FlushZones}z/{FlushSeconds}s, {_persisted.Count} zones already baked.");
        }

        // Main-thread: enqueue the ring around the player when they've moved enough.
        internal static void Tick()
        {
            if (!_running) return;
            var pl = Player.m_localPlayer;
            if (pl == null) return;
            var p = pl.transform.position;
            if (!float.IsNaN(_lastX)) { float dx = p.x - _lastX, dz = p.z - _lastZ; if (dx * dx + dz * dz < MoveStep * MoveStep) return; }
            _lastX = p.x; _lastZ = p.z;
            EnqueueRing(p.x, p.z);
        }

        static void EnqueueRing(float cx, float cz)
        {
            var wg = WorldGenerator.instance;
            if (wg == null) return;
            float s = Plugin.ActiveScale();
            float zoneWorld = 64f * s;
            int rz = Mathf.CeilToInt(RadiusMeters / zoneWorld);
            var (cox, coz) = Plugin.ZoneOriginFromWorld(cx, cz, s);   // centre zone origin (voxel, grid-aligned)
            float r2 = RadiusMeters * RadiusMeters;
            lock (_lock)
            {
                for (int dz = -rz; dz <= rz; dz++)
                for (int dx = -rz; dx <= rz; dx++)
                {
                    int ox = cox + dx * 64, oz = coz + dz * 64;
                    float wx = ox * s, wz = oz * s, ddx = wx - cx, ddz = wz - cz;
                    if (ddx * ddx + ddz * ddz > r2) continue;
                    if (ZonePrefetcher.IsFarZone(ox, oz)) continue;
                    var key = (ox, oz);
                    if (_persisted.Contains(key)) continue;
                    _pending.Add(key);
                }
                Monitor.PulseAll(_lock);   // wake all idle workers
            }
        }

        static void WorkerLoop()
        {
            while (_running)
            {
                (int ox, int oz) z = default; bool have = false;
                lock (_lock)
                {
                    if (_pending.Count > 0)
                    {
                        // NEAREST-FIRST: pick the queued zone closest to the last known player position.
                        float s = Plugin.ActiveScale();
                        bool haveRef = !float.IsNaN(_lastX);
                        float bestD = float.MaxValue;
                        foreach (var k in _pending)
                        {
                            float d = haveRef ? (k.ox * s - _lastX) * (k.ox * s - _lastX) + (k.oz * s - _lastZ) * (k.oz * s - _lastZ) : 0f;
                            if (d < bestD) { bestD = d; z = k; have = true; if (!haveRef) break; }
                        }
                        if (have) _pending.Remove(z);
                    }
                }
                if (!have)
                {
                    FlushCarved(force: true);   // nothing to do → flush any finished blocks, then sleep
                    lock (_lock) { if (_running && _pending.Count == 0) Monitor.Wait(_lock, 1000); }
                    continue;
                }
                int tid = Thread.CurrentThread.ManagedThreadId;
                _active[tid] = (z.ox, z.oz, _sessionSw?.ElapsedMilliseconds ?? 0);
                try
                {
                    if (Plugin.RollingCarveBlock(z.ox, z.oz, out var blk))
                    {
                        lock (_writeLock) _carvedBlocks.Add((z.ox, z.oz, blk));
                        Interlocked.Increment(ref _carved);
                    }
                    lock (_lock) _persisted.Add(z);   // mark done even if unmodified → don't re-enqueue
                }
                catch (Exception e) { Plugin.Log.LogWarning($"[PT-rolling] carve ({z.ox},{z.oz}) failed: {e.GetType().Name}: {e.Message}"); }
                finally { _active.TryRemove(tid, out _); }
                FlushCarved(force: false);
            }
            FlushCarved(force: true);
        }

        // Drain the shared carved-block buffer to disk when a batch is ready (count or time). Safe for N workers:
        // swaps the buffer under _writeLock, then appends outside it (RollingBakeFlush serializes on _bakeStreamLock).
        static void FlushCarved(bool force)
        {
            List<(int ox, int oz, byte[] blk)> batch = null;
            lock (_writeLock)
            {
                bool due = _carvedBlocks.Count > 0 &&
                           (force || _carvedBlocks.Count >= FlushZones || _flushSw.ElapsedMilliseconds - _lastFlushMs > FlushSeconds * 1000);
                if (due) { batch = _carvedBlocks; _carvedBlocks = new List<(int, int, byte[])>(); _lastFlushMs = _flushSw.ElapsedMilliseconds; }
            }
            if (batch == null) return;
            int n = Plugin.RollingBakeFlush(batch);
            if (n > 0)
            {
                long total = Interlocked.Add(ref _flushedTotal, n);
                Plugin.Log.LogInfo($"[PT-rolling] flushed {n} zones (carved={Interlocked.Read(ref _carved)}, persisted this session={total}).");
            }
        }

        internal static void Stop()
        {
            if (!_running) return;
            _running = false;
            lock (_lock) Monitor.PulseAll(_lock);
            if (_workers != null) foreach (var w in _workers) { try { w?.Join(4000); } catch { } }
            FlushCarved(force: true);   // persist anything the workers finished but hadn't flushed
            Plugin.RollingBakeClose();
            lock (_lock) { _pending.Clear(); _persisted.Clear(); }
            _workers = null;
            Plugin.Log.LogInfo($"[PT-rolling] stopped (persisted {_flushedTotal} zones this session).");
        }
    }
}
