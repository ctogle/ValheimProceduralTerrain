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
        internal static bool  ShowHud      = true;   // [Debug] rollinghud: on-screen status overlay — default ON so rolling is obvious at login
        internal static int   ThreadCount  = 1;      // background carve workers (BelowNormal). More = faster roll, more CPU contention with live play.
        internal static float RadiusMeters = 500f;   // disc carved around the player
        internal static float MoveStep     = 64f;    // re-scan after moving this far (one zone)
        internal static int   FlushZones   = 40;     // flush to disk after this many carved zones…
        internal static float FlushSeconds = 5f;     // …or this long since the last flush
        internal static int   Workers => System.Threading.Volatile.Read(ref _activeWorkers);

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
        // Worker pool is resizable LIVE (rollingthreads hot-reload): _desiredWorkers is the target, _activeWorkers
        // the current live count. Grow = spawn more; shrink = excess workers self-retire (see WorkerLoop top).
        static readonly List<Thread> _workers = new();
        static readonly object _workersLock = new();
        static volatile int _desiredWorkers;
        static int _activeWorkers;   // live worker threads (Interlocked)
        // Shared write buffer: workers carve in parallel and drop finished blocks here; flushed as a batch
        // (RollingBakeFlush is itself locked, so appends serialize). Guarded by _writeLock.
        static readonly object _writeLock = new();
        static List<(int ox, int oz, byte[] blk, int len)> _carvedBlocks = new();   // blk is a pooled buffer (len ≤ blk.Length); flush returns it
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
        static uint _openHash;   // cfg hash the current bake file is keyed to; re-key when it changes

        internal static void Arm()
        {
            if (_running)
            {
                // Config hot-reloaded to terrain that changes the hash → the open bake file is keyed to the OLD
                // hash but we're now carving the NEW config. Finalize the old-hash file and re-open the new one so
                // the bake always matches the live config (no stale geometry served, no mislabeled file).
                if (Plugin.ComputeCfgHash() == _openHash) return;
                Plugin.Log.LogInfo($"[PT-rolling] cfg hash {_openHash:X8}→{Plugin.ComputeCfgHash():X8} — re-keying bake to the new config.");
                Stop();   // flush+close the old-hash file, clear persisted; falls through to re-open below
            }
            // Wait for the world to be fully loaded before capturing the seed: early in load WorldGenerator.m_world
            // exists but m_seed is still 0, which would open a junk seed-0 bake file. The player spawning is a
            // robust "world ready, seed valid" signal (and Tick needs the player anyway).
            if (Player.m_localPlayer == null) return;
            if (!Plugin.RollingBakeOpen()) return;   // world not ready yet; retry next tick
            _openHash = Plugin.ComputeCfgHash();     // remember what this bake file is keyed to (re-key on change)
            lock (_lock)
            {
                _pending.Clear(); _persisted.Clear();
                foreach (var k in Plugin.RollingPersistedKeys()) _persisted.Add(k);
            }
            _lastX = float.NaN; _carved = 0; _flushedTotal = 0;
            _fillAnchorOx = int.MinValue; _fillRing = 0;   // reset the idle-fill sweep for the new session
            lock (_writeLock) _carvedBlocks.Clear();
            _sessionSw = System.Diagnostics.Stopwatch.StartNew();
            _flushSw = System.Diagnostics.Stopwatch.StartNew(); _lastFlushMs = 0;
            _running = true;
            int n = Mathf.Clamp(ThreadCount, 1, 8);
            _desiredWorkers = n;
            Interlocked.Exchange(ref _activeWorkers, 0);
            lock (_workersLock) _workers.Clear();
            for (int i = 0; i < n; i++) SpawnWorker();
            if (ShowHud) BakeProgressUI.Show();   // overlay renders rolling status while running
            Plugin.Log.LogInfo($"[PT-rolling] armed: radius={RadiusMeters}m, {n} worker(s), flush@{FlushZones}z/{FlushSeconds}s, {_persisted.Count} zones already baked.");
        }

        // Spawn one background carve worker and track it. Increments _activeWorkers before the thread starts so the
        // live count is accurate the instant Arm/ApplyThreadCount returns.
        static void SpawnWorker()
        {
            Interlocked.Increment(ref _activeWorkers);
            var t = new Thread(WorkerLoop) { IsBackground = true, Name = "PTRollingBake", Priority = System.Threading.ThreadPriority.BelowNormal };
            lock (_workersLock) _workers.Add(t);
            t.Start();
        }

        // Hot-reload rollingthreads: resize the live pool without tearing down the queue/stream. Grow by spawning;
        // shrink by lowering the target so excess workers retire themselves on their next loop. No-op if not running
        // (Arm reads ThreadCount directly) or unchanged. Called on the main thread from the cfg parse.
        internal static void ApplyThreadCount()
        {
            if (!_running) return;
            int n = Mathf.Clamp(ThreadCount, 1, 8);
            if (n == _desiredWorkers) return;
            int old = _desiredWorkers;
            _desiredWorkers = n;
            while (Volatile.Read(ref _activeWorkers) < n) SpawnWorker();   // grow now
            lock (_lock) Monitor.PulseAll(_lock);                          // wake idle workers so excess can retire
            Plugin.Log.LogInfo($"[PT-rolling] workers {old} → {n} (live resize).");
        }

        // Force the next Tick to re-enqueue the ring even if the player hasn't moved MoveStep — so a live
        // rollingradius change applies immediately instead of waiting for movement.
        internal static void ForceRescan() { _lastX = float.NaN; }

        // Idle-fill (rollingfill): when the pool is under-utilised, keep feeding the nearest UN-baked zones so the
        // bake eventually fills the whole map in the background — one long session completes it for everyone to
        // distribute. Off by default (most players just want the disc around them).
        internal static bool  Fill = false;          // rollingfill
        internal static float FillRadius = 10000f;   // rollingfillradius (m): how far the sweep goes (≈ default world radius)
        static int _fillRing;                         // spiral cursor (Chebyshev ring, in zones) from the anchor
        static int _fillAnchorOx = int.MinValue, _fillAnchorOz;

        // Main-thread: enqueue the ring around the player when they've moved enough, then (if enabled) idle-fill.
        internal static void Tick()
        {
            if (!_running) return;
            var pl = Player.m_localPlayer;
            if (pl == null) return;
            var p = pl.transform.position;
            bool moved = float.IsNaN(_lastX) || (p.x - _lastX) * (p.x - _lastX) + (p.z - _lastZ) * (p.z - _lastZ) >= MoveStep * MoveStep;
            if (moved) { _lastX = p.x; _lastZ = p.z; EnqueueRing(p.x, p.z); }
            if (Fill) TickFill(p.x, p.z);
        }

        // When idle (queue drained AND ≤ half the workers in-flight), sweep the next ring(s) outward from the fill
        // anchor and enqueue any un-baked zones. The cursor (_fillRing) persists so we don't re-scan the baked
        // interior every tick; it re-anchors + resets only on a big jump (teleport). Bounded by FillRadius.
        static void TickFill(float cx, float cz)
        {
            lock (_lock) { if (_pending.Count > 0) return; }                 // still work queued → not idle
            int workers = Volatile.Read(ref _activeWorkers);
            if (ActiveCount * 2 > workers) return;                           // more than half busy → not idle

            float s = Plugin.ActiveScale();
            var (cox, coz) = Plugin.ZoneOriginFromWorld(cx, cz, s);
            if (_fillAnchorOx == int.MinValue || Math.Abs(cox - _fillAnchorOx) > 128 * 64 || Math.Abs(coz - _fillAnchorOz) > 128 * 64)
            { _fillAnchorOx = cox; _fillAnchorOz = coz; _fillRing = 0; }      // re-anchor on teleport-scale moves

            int maxRing = Mathf.CeilToInt(FillRadius / (64f * s));
            int enq = 0;
            while (_fillRing < maxRing && enq == 0)                          // advance until a ring yields un-baked zones
            {
                _fillRing++;
                int r = _fillRing;
                lock (_lock)
                {
                    for (int dx = -r; dx <= r; dx++) { enq += TryFill(_fillAnchorOx + dx * 64, _fillAnchorOz - r * 64); enq += TryFill(_fillAnchorOx + dx * 64, _fillAnchorOz + r * 64); }
                    for (int dz = -r + 1; dz <= r - 1; dz++) { enq += TryFill(_fillAnchorOx - r * 64, _fillAnchorOz + dz * 64); enq += TryFill(_fillAnchorOx + r * 64, _fillAnchorOz + dz * 64); }
                    if (enq > 0) Monitor.PulseAll(_lock);
                }
            }
            if (enq > 0) Plugin.Log.LogDebug($"[PT-rolling] fill: +{enq} zones at ring {_fillRing}");
        }

        // Caller holds _lock. Enqueue one zone if not already baked/queued/far. Returns 1 if added.
        static int TryFill(int ox, int oz)
        {
            var key = (ox, oz);
            if (_persisted.Contains(key) || _pending.Contains(key) || ZonePrefetcher.IsFarZone(ox, oz)) return 0;
            _pending.Add(key);
            return 1;
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
                // Pool downsized live (rollingthreads lowered) → shed excess workers. CAS so exactly the right
                // number retire even if several wake at once.
                if (Volatile.Read(ref _activeWorkers) > _desiredWorkers)
                {
                    int cur = Volatile.Read(ref _activeWorkers);
                    if (cur > _desiredWorkers && Interlocked.CompareExchange(ref _activeWorkers, cur - 1, cur) == cur)
                    {
                        lock (_workersLock) _workers.Remove(Thread.CurrentThread);
                        FlushCarved(force: true);
                        return;
                    }
                }
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
                    if (Plugin.RollingCarveBlock(z.ox, z.oz, out var blk, out var len))
                    {
                        lock (_writeLock) _carvedBlocks.Add((z.ox, z.oz, blk, len));
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
            List<(int ox, int oz, byte[] blk, int len)> batch = null;
            lock (_writeLock)
            {
                bool due = _carvedBlocks.Count > 0 &&
                           (force || _carvedBlocks.Count >= FlushZones || _flushSw.ElapsedMilliseconds - _lastFlushMs > FlushSeconds * 1000);
                if (due) { batch = _carvedBlocks; _carvedBlocks = new List<(int, int, byte[], int)>(); _lastFlushMs = _flushSw.ElapsedMilliseconds; }
            }
            if (batch == null) return;
            int n = Plugin.RollingBakeFlush(batch);
            if (n > 0)
            {
                long total = Interlocked.Add(ref _flushedTotal, n);
                Plugin.Log.LogInfo($"[PT-rolling] flushed {n} zones (carved={Interlocked.Read(ref _carved)}, persisted this session={total}).");
            }
        }

        // The active bake file was deleted out from under the rolling bake (pt_clearbake). The shared stream was
        // closed and _persisted still lists the now-gone zones, so carving would continue but save nothing and
        // never re-enqueue. Tear the stale state down and re-arm on a fresh file so carving resumes AND persists.
        // No-op when rolling wasn't running (live-carve mode — the caller still recarves).
        internal static void RestartForDeletedBake()
        {
            if (!_running) return;
            Stop();   // safe with a null stream: flush/close are guarded, _pending/_persisted cleared
            Arm();    // reopens a fresh file (FileMode.Create), rescans the ring, persisted starts empty
            Plugin.Log.LogInfo("[PT-rolling] re-armed after bake deletion — resuming fresh.");
        }

        internal static void Stop()
        {
            if (!_running) return;
            _running = false;
            lock (_lock) Monitor.PulseAll(_lock);
            List<Thread> snapshot; lock (_workersLock) { snapshot = new List<Thread>(_workers); _workers.Clear(); }
            foreach (var w in snapshot) { try { w?.Join(4000); } catch { } }
            Interlocked.Exchange(ref _activeWorkers, 0); _desiredWorkers = 0;
            FlushCarved(force: true);   // persist anything the workers finished but hadn't flushed
            Plugin.RollingBakeClose();
            lock (_lock) { _pending.Clear(); _persisted.Clear(); }
            Plugin.Log.LogInfo($"[PT-rolling] stopped (persisted {_flushedTotal} zones this session).");
        }
    }
}
