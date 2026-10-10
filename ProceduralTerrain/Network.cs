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
        // ── Server cfg sync RPC ───────────────────────────────────────────────────
        internal static void SendCfgSyncToPeer(long peerID)
        {
            string path = ConfigPath;
            string cfgText = File.Exists(path) ? File.ReadAllText(path) : "";
            uint hash = ComputeCfgHash();
            var pkg = new ZPackage();
            pkg.Write(cfgText);
            pkg.Write(hash);
            ZRoutedRpc.instance.InvokeRoutedRPC(peerID, "PTCfgSync", pkg);
            Log.LogInfo($"[ProceduralTerrain] Sent cfg to peer {peerID} (hash={hash:X8}, {cfgText.Length} chars).");
        }

        internal static void RPC_ReceiveCfgSync(long sender, ZPackage pkg)
        {
            string cfgText = pkg.ReadString();
            uint hash      = pkg.ReadUInt();
            _serverCfgLines = cfgText.Split('\n');
            _serverCfgHash  = hash;
            LoadBiomeConfig();
            _hotReloadPending = true;
            Log.LogInfo($"[ProceduralTerrain] Applied server cfg (hash={hash:X8}) — scheduling recarve.");
        }

        // ── Dedicated server location RPC ─────────────────────────────────────────
        internal static void SendLocationListToPeer(long peerID)
        {
            var entries = ReadLocationInstances();
            var pkg = new ZPackage();
            pkg.Write(entries.Count);
            foreach (var (pos, extR) in entries) { pkg.Write(pos); pkg.Write(extR); }
            ZRoutedRpc.instance.InvokeRoutedRPC(peerID, "PTLocationList", pkg);
            Log.LogInfo($"[ProceduralTerrain] Sent {entries.Count} location positions to peer {peerID}.");
        }

        internal static void RPC_ReceiveLocationList(long sender, ZPackage pkg)
        {
            int count = pkg.ReadInt();
            _locationPositions.Clear();
            for (int i = 0; i < count; i++)
            {
                Vector3 pos = pkg.ReadVector3();
                float extR  = pkg.ReadSingle();
                _locationPositions.Add((new Vector2(pos.x, pos.z), extR));
            }
            if (_locationPositions.Count > 0)
            {
                _locationsCached  = true;
                _hotReloadPending = true;
                Log.LogInfo($"[ProceduralTerrain] Received {_locationPositions.Count} location positions from server — scheduling recarve.");
            }
            // Server connection confirmed — query for bake file now that peer is established.
            var query = new ZPackage();
            query.Write(ComputeCfgHash());
            ZRoutedRpc.instance.InvokeRoutedRPC(sender, "PTBakeQuery", query);
            Log.LogInfo($"[ProceduralTerrain] Sent PTBakeQuery to server (cfgHash={ComputeCfgHash():X8}).");
        }

        // ── Bake progress RPC (server→all clients) ───────────────────────────────
        internal static void RPC_BakeProgress(long sender, ZPackage pkg)
        {
            int done  = pkg.ReadInt();
            int total = pkg.ReadInt();
            _bakeProgressDone  = done;
            _bakeProgressTotal = total;
            if (!_bakeInProgress)
            {
                _bakeInProgress    = true;
                _bakeProgressTimer = System.Diagnostics.Stopwatch.StartNew();
                BakeProgressUI.ShowBake();
            }
            if (done >= total)
            {
                _bakeInProgress = false;
                BakeProgressUI.HideBake();
                // Bake just finished server-side. A client that joined mid-bake was told "not available" and is on
                // live — re-query now so it downloads the completed file. Guard to clients (the server/host already
                // has the file; self-querying would make it stream its own bake to itself).
                if (ZNet.instance != null && !ZNet.instance.IsServer())
                {
                    var q = new ZPackage(); q.Write(ComputeCfgHash());
                    ZRoutedRpc.instance.InvokeRoutedRPC(sender, "PTBakeQuery", q);
                    Log.LogInfo("[ProceduralTerrain] Bake complete — re-querying server to download it.");
                }
            }
        }

        // ── Clear bake RPC (client→server, admin only) ───────────────────────────
        internal static void RPC_ClearBake(long sender, ZPackage _pkg)
        {
            bool isAdmin = sender == 0L;
            if (!isAdmin)
            {
                var peer   = ZNet.instance?.GetPeer(sender);
                var userId = peer?.m_rpc?.GetSocket()?.GetHostName();
                isAdmin = userId != null && (ZNet.instance?.GetAdminList()?.Contains(userId) ?? false);
            }
            if (!isAdmin) { Log.LogWarning($"[ProceduralTerrain] ClearBake from non-admin peer {sender} rejected."); return; }
            int seed = Plugin.WorldSeed();
            string pattern = $"procedural_terrain_bake_{seed:X8}_*.bin";
            var matches = System.IO.Directory.GetFiles(PTConfigDir, pattern);
            string msg;
            if (matches.Length > 0)
            {
                CloseBakeStream();   // release the open read handle so the file isn't locked on delete
                foreach (var f in matches) File.Delete(f);
                _bakeData = null; _bakeLoadAttempted = false;
                msg = $"Deleted {matches.Length} server bake file(s).";
                Log.LogInfo($"[ProceduralTerrain] {msg}");
            }
            else
            {
                msg = "No server bake file found.";
                Log.LogInfo($"[ProceduralTerrain] {msg}");
            }
            if (sender != 0L)
            {
                var resp = new ZPackage(); resp.Write(msg);
                ZRoutedRpc.instance.InvokeRoutedRPC(sender, "PTClearBakeResult", resp);
            }
        }

        internal static void RPC_ClearBakeResult(long sender, ZPackage pkg)
        {
            string msg = pkg.ReadString();
            Log.LogInfo($"[ProceduralTerrain] Server: {msg}");
            Console.instance?.Print($"[ProceduralTerrain] Server: {msg}");
        }

        // ── List bakes RPC (client→server, admin-only) ───────────────────────────
        internal static void RPC_ListBakes(long sender, ZPackage _pkg)
        {
            bool isAdmin = sender == 0L;
            if (!isAdmin)
            {
                var peer   = ZNet.instance?.GetPeer(sender);
                var userId = peer?.m_rpc?.GetSocket()?.GetHostName();
                isAdmin = userId != null && (ZNet.instance?.GetAdminList()?.Contains(userId) ?? false);
            }
            if (!isAdmin) { Log.LogWarning($"[ProceduralTerrain] ListBakes from non-admin peer {sender} rejected."); return; }

            string dir   = PTConfigDir;
            var    files = System.IO.Directory.GetFiles(dir, "procedural_terrain_bake_*.bin");
            var    resp  = new ZPackage();
            resp.Write(files.Length);
            foreach (var f in files)
            {
                resp.Write(System.IO.Path.GetFileName(f));
                resp.Write(new System.IO.FileInfo(f).Length);
            }

            if (sender == 0L)
            {
                RPC_ListBakesResult(0L, resp);
            }
            else
            {
                ZRoutedRpc.instance.InvokeRoutedRPC(sender, "PTListBakesResult", resp);
            }
        }

        internal static void RPC_ListBakesResult(long sender, ZPackage pkg)
        {
            int count = pkg.ReadInt();
            if (count == 0)
            {
                Console.instance?.Print("[ProceduralTerrain] Server: No bake files found.");
                return;
            }
            Console.instance?.Print($"[ProceduralTerrain] Server: {count} bake file(s):");
            for (int i = 0; i < count; i++)
            {
                string name = pkg.ReadString();
                long   size = pkg.ReadLong();
                long   mb   = size / (1024 * 1024);
                Console.instance?.Print($"  {name}  ({mb} MB)");
            }
        }

        // ── Bake execute RPC (client→server) ─────────────────────────────────────
        internal static void RPC_BakeExecute(long sender, ZPackage pkg)
        {
            float radius = pkg.ReadSingle();
            // Admin check: allow server itself (sender==0) or whitelisted Steam IDs.
            bool isAdmin = sender == 0L;
            if (!isAdmin)
            {
                var peer = ZNet.instance?.GetPeer(sender);
                var userId = peer?.m_rpc?.GetSocket()?.GetHostName();
                isAdmin = userId != null && (ZNet.instance?.GetAdminList()?.Contains(userId) ?? false);
            }
            if (!isAdmin)
            {
                Log.LogWarning($"[ProceduralTerrain] Bake request from non-admin peer {sender} rejected.");
                return;
            }
            Log.LogInfo($"[ProceduralTerrain] Bake request accepted from peer {sender} — running server-side (background).");
            System.Threading.Tasks.Task.Run(() => WriteBakeFile(radius));
        }

        // ── Bake file RPC (server→client transfer) ────────────────────────────────
        // Server: respond to a client querying whether a bake exists.
        internal static void RPC_BakeQuery(long sender, ZPackage pkg)
        {
            uint clientCfgHash = pkg.ReadUInt();
            int  seed = Plugin.WorldSeed();
            uint myCfgHash = ComputeCfgHash();
            string path = BakeFilePath(seed, myCfgHash);
            var info = new ZPackage();
            if (_parallelBakeActive)
            {
                // A bake is running — the file exists but is INCOMPLETE (no index/footer). Report "not available"
                // so the client doesn't download a half-written file; it uses live + shows PTBakeProgress, and
                // re-queries when the bake finishes (RPC_BakeProgress at done>=total).
                info.Write(0L);
                Log.LogInfo($"[ProceduralTerrain] Bake query from {sender}: bake in progress, not yet available.");
            }
            else if (myCfgHash == clientCfgHash && File.Exists(path))
            {
                long size = new System.IO.FileInfo(path).Length;
                info.Write(size);
                Log.LogInfo($"[ProceduralTerrain] Bake query from {sender}: sending info (size={size}).");
            }
            else
            {
                info.Write(0L);
                Log.LogInfo($"[ProceduralTerrain] Bake query from {sender}: no compatible bake file.");
            }
            ZRoutedRpc.instance.InvokeRoutedRPC(sender, "PTBakeInfo", info);
        }

        // Server: stream bake file in chunks after client requests it.
        internal static void RPC_BakeRequest(long sender, ZPackage _pkg)
        {
            int  seed = Plugin.WorldSeed();
            uint cfgHash = ComputeCfgHash();
            string path = BakeFilePath(seed, cfgHash);
            if (_parallelBakeActive) { Log.LogInfo($"[ProceduralTerrain] Bake request from {sender} refused: bake still running."); return; }
            if (!File.Exists(path)) return;
            byte[] data = File.ReadAllBytes(path);
            int total = (int)Math.Ceiling((double)data.Length / BakeChunkSize);
            Log.LogInfo($"[ProceduralTerrain] Streaming bake to {sender}: {data.Length} bytes in {total} chunks.");
            // Yield every 4 chunks so the server main thread isn't starved.
            ZNetScene.instance.StartCoroutine(StreamBakeCoroutine(sender, data, total));
        }

        // Keep up to this many bytes queued to fill the pipe without overflowing it.
        const int BakeSendWindowBytes = 2 * 1024 * 1024; // 2 MB

        static System.Collections.IEnumerator StreamBakeCoroutine(long sender, byte[] data, int total)
        {
            var peer = ZNet.instance?.GetPeer(sender);
            for (int i = 0; i < total; i++)
            {
                // Wait until the send queue drains below our window before adding more.
                while (peer?.m_socket?.GetSendQueueSize() > BakeSendWindowBytes)
                    yield return null;

                if (peer == null || !peer.m_socket.IsConnected()) yield break;

                int offset = i * BakeChunkSize;
                int len    = Math.Min(BakeChunkSize, data.Length - offset);
                byte[] slice = new byte[len];
                System.Array.Copy(data, offset, slice, 0, len);
                var chunk  = new ZPackage();
                chunk.Write(i);
                chunk.Write(total);
                chunk.Write(slice);
                ZRoutedRpc.instance.InvokeRoutedRPC(sender, "PTBakeChunk", chunk);
                yield return null;
            }
            var end = new ZPackage();
            end.Write(data.Length);
            ZRoutedRpc.instance.InvokeRoutedRPC(sender, "PTBakeEnd", end);
        }

        // Client: server confirmed a bake is available — start download.
        internal static void RPC_BakeInfo(long sender, ZPackage pkg)
        {
            long size = pkg.ReadLong();
            if (size <= 0) { Log.LogInfo("[ProceduralTerrain] Server has no bake file — using live calc."); return; }
            // Already have an identical bake? The bake is deterministic (same seed+cfg → identical bytes → identical
            // size), so a size match means our local copy IS the server's — skip the (slow, RPC) download and use it.
            string localPath = BakeFilePath(Plugin.WorldSeed(), ComputeCfgHash());
            if (File.Exists(localPath) && new System.IO.FileInfo(localPath).Length == size)
            {
                Log.LogInfo($"[ProceduralTerrain] Local bake matches server ({size / 1024 / 1024} MB) — skipping download.");
                return;
            }
            Log.LogInfo($"[ProceduralTerrain] Server bake available ({size / 1024 / 1024} MB) — requesting download.");
            _bakeDownloadBuffer  = new System.IO.MemoryStream((int)size);
            _bakeChunksExpected  = (int)Math.Ceiling((double)size / BakeChunkSize);
            _bakeChunksReceived  = 0;
            _bakeDownloadActive  = true;
            _bakeDownloadTimer   = System.Diagnostics.Stopwatch.StartNew();
            BakeProgressUI.Show();
            ZRoutedRpc.instance.InvokeRoutedRPC(sender, "PTBakeRequest", new ZPackage());
        }

        // Client: receive a chunk.
        internal static void RPC_BakeChunk(long sender, ZPackage pkg)
        {
            if (!_bakeDownloadActive || _bakeDownloadBuffer == null) return;
            int index  = pkg.ReadInt();
            int total  = pkg.ReadInt();
            byte[] buf = pkg.ReadByteArray();
            _bakeDownloadBuffer.Write(buf, 0, buf.Length);
            _bakeChunksReceived = index + 1;
            _bakeChunksExpected = total;
        }

        // Client: all chunks received — write to disk and hot-reload.
        internal static void RPC_BakeEnd(long sender, ZPackage pkg)
        {
            if (!_bakeDownloadActive || _bakeDownloadBuffer == null) return;
            int expectedLen = pkg.ReadInt();
            _bakeDownloadActive = false;
            BakeProgressUI.Hide();
            byte[] data = _bakeDownloadBuffer.ToArray();
            _bakeDownloadBuffer.Dispose();
            _bakeDownloadBuffer = null;
            if (data.Length != expectedLen)
            {
                Log.LogError($"[ProceduralTerrain] Bake download corrupt: got {data.Length} bytes, expected {expectedLen}.");
                return;
            }
            int  seed    = Plugin.WorldSeed();
            uint cfgHash = ComputeCfgHash();
            string path  = BakeFilePath(seed, cfgHash);
            System.IO.Directory.CreateDirectory(PTConfigDir);
            CloseBakeStream();   // release any open read handle before overwriting the file
            File.WriteAllBytes(path, data);
            long ms = _bakeDownloadTimer?.ElapsedMilliseconds ?? 0;
            Log.LogInfo($"[ProceduralTerrain] Bake downloaded in {ms / 1000.0:F1}s — written to {path}. Reloading.");
            _bakeData = null;
            _bakeLoadAttempted = false;
            _hotReloadPending  = true;
        }
    }

    [HarmonyPatch(typeof(ZoneSystem), "Start")]
    static class ZoneSystemStartPatch
    {
        static void Postfix()
        {
            if (ZNet.instance == null) return;
            if (ZNet.instance.IsServer())
            {
                ZRoutedRpc.instance.Register<ZPackage>("PTBakeExecute", Plugin.RPC_BakeExecute);
                ZRoutedRpc.instance.Register<ZPackage>("PTBakeQuery",   Plugin.RPC_BakeQuery);
                ZRoutedRpc.instance.Register<ZPackage>("PTBakeRequest", Plugin.RPC_BakeRequest);
                ZRoutedRpc.instance.Register<ZPackage>("PTClearBake",   Plugin.RPC_ClearBake);
                ZRoutedRpc.instance.Register<ZPackage>("PTListBakes",   Plugin.RPC_ListBakes);
            }
            else
            {
                ZRoutedRpc.instance.Register<ZPackage>("PTCfgSync",       Plugin.RPC_ReceiveCfgSync);
                ZRoutedRpc.instance.Register<ZPackage>("PTLocationList",  Plugin.RPC_ReceiveLocationList);
                ZRoutedRpc.instance.Register<ZPackage>("PTBakeInfo",      Plugin.RPC_BakeInfo);
                ZRoutedRpc.instance.Register<ZPackage>("PTBakeChunk",     Plugin.RPC_BakeChunk);
                ZRoutedRpc.instance.Register<ZPackage>("PTBakeEnd",       Plugin.RPC_BakeEnd);
                ZRoutedRpc.instance.Register<ZPackage>("PTBakeProgress",    Plugin.RPC_BakeProgress);
                ZRoutedRpc.instance.Register<ZPackage>("PTClearBakeResult", Plugin.RPC_ClearBakeResult);
                ZRoutedRpc.instance.Register<ZPackage>("PTListBakesResult", Plugin.RPC_ListBakesResult);
                // PTBakeQuery is sent from RPC_ReceiveLocationList once the server connection is confirmed.
            }
        }
    }

    [HarmonyPatch]
    static class ZoneSystemOnNewPeerPatch
    {
        static MethodBase? TargetMethod() =>
            typeof(ZoneSystem).GetMethod("OnNewPeer", BindingFlags.NonPublic | BindingFlags.Instance);

        static void Postfix(long peerID)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
            Plugin.SendCfgSyncToPeer(peerID);
            Plugin.SendLocationListToPeer(peerID);
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

    // On dedicated servers, m_locationInstances is server-side only. We send the full
    // position list to each connecting client via a custom RPC so they can apply
    // location protection and correctly gate Carve().
    [HarmonyPatch(typeof(ZNet), "OnDestroy")]
    static class ZNetOnDestroyPatch
    {
        static void Postfix()
        {
            if (Plugin._bakeDownloadActive)
            {
                Plugin._bakeDownloadActive = false;
                Plugin._bakeDownloadBuffer?.Dispose();
                Plugin._bakeDownloadBuffer = null;
                Plugin.Log.LogInfo("[ProceduralTerrain] Bake download cancelled — disconnected.");
            }
            if (Plugin._bakeInProgress)
            {
                Plugin._bakeInProgress = false;
            }
            BakeProgressUI.HideBake();
            if (Plugin._serverCfgLines != null)
            {
                Plugin._serverCfgLines = null;
                Plugin._serverCfgHash  = null;
                Plugin.LoadBiomeConfig();
                Plugin.Log.LogInfo("[ProceduralTerrain] Server cfg cleared — reverted to local config.");
            }
        }
    }

} // namespace ProceduralTerrain
