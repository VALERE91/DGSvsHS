#if WITH_DGS
using System;
using System.Collections.Generic;
using System.IO;
using DGSvsHS.Gameplay;
using Unity.Collections;
using Unity.Netcode;
using Unity.Netcode.Transports.UTP;
using UnityEngine;

namespace DGSvsHS.Net.Ngo
{
    public sealed class NgoNetworkServer : INetworkServer
    {
        private const string MsgWelcome = "dgsvshs.welcome";
        private const string MsgSnapshot = "dgsvshs.snap";
        private const string MsgInput = "dgsvshs.input";
        private const string MsgDisconnect = "dgsvshs.bye";

        private readonly Dictionary<ulong, byte> _clientToPlayer = new Dictionary<ulong, byte>();
        private readonly ulong[] _playerToClient = new ulong[Constants.MaxPlayers];
        private readonly bool[] _slotUsed = new bool[Constants.MaxPlayers];

        private readonly Queue<InputCmd>[] _inputQueues = new Queue<InputCmd>[Constants.MaxPlayers];
        private readonly uint[] _highestInputTick = new uint[Constants.MaxPlayers];
        private readonly float[] _rttMs = new float[Constants.MaxPlayers];
        
        private readonly RecipientSnapshotState[] _recipientState = new RecipientSnapshotState[Constants.MaxPlayers];
        
        private byte[] _scratch = new byte[65536];
        private readonly InputCmd[] _inputDecodeBuf = new InputCmd[4];
        
        private readonly List<EnemyDeltaEntry> _changedScratch = new List<EnemyDeltaEntry>(2048);
        private readonly List<ushort> _removedScratch = new List<ushort>(256);
        private readonly List<EnemySnap> _addedScratch = new List<EnemySnap>(512);
        private readonly List<EnemySnap> _fullSelectedScratch = new List<EnemySnap>(2048);
        private readonly HashSet<ushort> _includedScratch = new HashSet<ushort>();
        private readonly List<SnapshotPriority.ScoredEnemy> _scoredScratch = new List<SnapshotPriority.ScoredEnemy>(2048);

        private uint _serverTick;

        public bool IsRunning { get; private set; }

        public event Action<byte> ClientConnected;
        public event Action<byte, DisconnectReason> ClientDisconnected;
        public event Action<byte, InputCmd> InputReceived;

        public NgoNetworkServer()
        {
            for (int i = 0; i < Constants.MaxPlayers; i++)
            {
                _inputQueues[i] = new Queue<InputCmd>(8);
                _rttMs[i] = 60f;
                _recipientState[i] = new RecipientSnapshotState();
            }
        }

        public void Start(ushort port)
        {
            var nm = NetworkManager.Singleton;
            if (nm == null) throw new InvalidOperationException("NetworkManager.Singleton is null.");
            var utp = nm.GetComponent<UnityTransport>();
            utp.SetConnectionData("0.0.0.0", port, "0.0.0.0");
            
            utp.MaxPacketQueueSize = 1024;
            nm.NetworkConfig.ConnectionApproval = false;
            nm.LogLevel = Unity.Netcode.LogLevel.Developer;

            // Subscribe lifecycle callbacks once, before StartServer.
            nm.OnClientConnectedCallback += OnClientConnectedNgo;
            nm.OnClientDisconnectCallback += OnClientDisconnectedNgo;

            UnityEngine.Debug.Log($"[ServerNet] StartServer: approval={nm.NetworkConfig.ConnectionApproval} tickRate={nm.NetworkConfig.TickRate} sceneMgmt={nm.NetworkConfig.EnableSceneManagement}");

            nm.StartServer();

            // CustomMessagingManager exists after StartServer.
            nm.CustomMessagingManager.RegisterNamedMessageHandler(MsgInput, OnInputMsg);

            IsRunning = true;
        }

        public void SetServerTick(uint tick) => _serverTick = tick;

        public float GetRttMs(byte playerId) =>
            playerId < Constants.MaxPlayers ? _rttMs[playerId] : 60f;

        public bool TryDequeueInput(byte playerId, out InputCmd cmd)
        {
            if (playerId >= Constants.MaxPlayers || !_slotUsed[playerId])
            {
                cmd = default;
                return false;
            }
            var q = _inputQueues[playerId];
            if (q.Count == 0) { cmd = default; return false; }
            cmd = q.Dequeue();
            return true;
        }

        public void Broadcast(Snapshot current, WorldStateHistory history)
        {
            for (byte pid = 0; pid < Constants.MaxPlayers; pid++)
            {
                if (!_slotUsed[pid]) continue;
                ComposeAndSend(pid, current, history);
            }
        }

        private void ComposeAndSend(byte pid, Snapshot current, WorldStateHistory history)
        {
            var rstate = _recipientState[pid];
            
            Snapshot baseline = null;
            bool useDelta = false;
            uint baselineTick = 0;
            uint ackedTick = rstate.LastAckedServerTick;
            if (ackedTick > 0 && current.Tick >= ackedTick && (current.Tick - ackedTick) <= (uint)Constants.MaxDeltaDepth)
            {
                if (history.TryGet(ackedTick, out baseline))
                {
                    useDelta = true;
                    baselineTick = ackedTick;
                }
            }

            // Per-recipient anchor for priority scoring = that player's position in current world.
            Vector2 anchor = Vector2.zero;
            for (int i = 0; i < current.Players.Count; i++)
                if (current.Players[i].Id == pid) { anchor = current.Players[i].Position; break; }

            // Budget left for enemies = total budget minus header/players/fires overhead.
            int playerOverhead = 1 + current.Players.Count * WireCodec.PlayerSnapFullBytes;
            int fireOverhead = 1 + Math.Min(16, current.RecentFireEvents.Count) * WireCodec.FireEventBytes;
            int fixedOverhead = WireCodec.SnapshotHeaderBytes + playerOverhead + fireOverhead;
            
            // Delta path also has enemy-count headers + enemy_total_in_world.
            int enemySectionHeader = useDelta ? (2 + 2 + 2 + 4) : (2 + 4);
            int budgetForEnemies = Constants.SnapshotByteBudget - fixedOverhead - enemySectionHeader;
            if (budgetForEnemies < 0) budgetForEnemies = 0;

            using var ms = new MemoryStream(_scratch);
            using var w = new BinaryWriter(ms);

            // Write header.
            var prevKind = current.Kind;
            var prevBaseline = current.BaselineTick;
            current.Kind = useDelta ? SnapshotKind.Delta : SnapshotKind.Full;
            current.BaselineTick = useDelta ? baselineTick : 0u;
            current.LastProcessedInputTick = _highestInputTick[pid];
            WireCodec.WriteSnapshotHeader(w, current);

            if (useDelta)
            {
                SnapshotPriority.SelectForDelta(
                    current, baseline, anchor,
                    rstate.ConfirmedIds,
                    rstate.TicksSinceLastSent,
                    budgetForEnemies,
                    _changedScratch, _removedScratch, _addedScratch,
                    _includedScratch, _scoredScratch);
                WireCodec.WriteDeltaSnapshotBody(
                    w,
                    current.Players,
                    _changedScratch,
                    _removedScratch,
                    _addedScratch,
                    current.EnemyTotalInWorld,
                    current.RecentFireEvents);
            }
            else
            {
                SnapshotPriority.SelectForFull(current, anchor, budgetForEnemies, _fullSelectedScratch, _scoredScratch);
                WireCodec.WriteFullSnapshotBody(
                    w,
                    current.Players,
                    _fullSelectedScratch,
                    current.EnemyTotalInWorld,
                    current.RecentFireEvents);
                
                _includedScratch.Clear();
                for (int i = 0; i < _fullSelectedScratch.Count; i++) _includedScratch.Add(_fullSelectedScratch[i].Id);
                _removedScratch.Clear();
            }

            // Restore caller's snapshot fields so it can be reused for the next recipient.
            current.Kind = prevKind;
            current.BaselineTick = prevBaseline;

            int len = (int)ms.Position;

            var payload = new FastBufferWriter(len, Allocator.Temp);
            bool sendOk = false;
            try
            {
                payload.WriteBytesSafe(_scratch, len);
                NetworkManager.Singleton.CustomMessagingManager.SendNamedMessage(
                    MsgSnapshot,
                    _playerToClient[pid],
                    payload,
                    NetworkDelivery.UnreliableSequenced);
                sendOk = true;
            }
            catch (System.Exception ex)
            {
                UnityEngine.Debug.LogWarning(
                    $"[Server] Snapshot send failed (size={len}B, pid={pid}). " +
                    $"Detail: {ex.Message}");
            }
            finally
            {
                payload.Dispose();
            }
            
            if (sendOk)
                rstate.OnSnapshotSent(current.Tick, isFull: !useDelta, _includedScratch, _removedScratch);
        }

        public void Poll()
        {
            // RTT refresh
            var nm = NetworkManager.Singleton;
            if (nm == null) return;
            var utp = nm.GetComponent<UnityTransport>();
            for (byte pid = 0; pid < Constants.MaxPlayers; pid++)
            {
                if (!_slotUsed[pid]) continue;
                ulong cid = _playerToClient[pid];
                ulong rtt = utp.GetCurrentRtt(cid);
                if (rtt > 0) _rttMs[pid] = Mathf.Lerp(_rttMs[pid], rtt, 0.2f);
            }
        }

        public void Dispose()
        {
            var nm = NetworkManager.Singleton;
            if (nm != null && nm.IsServer)
            {
                nm.OnClientConnectedCallback -= OnClientConnectedNgo;
                nm.OnClientDisconnectCallback -= OnClientDisconnectedNgo;
                if (nm.CustomMessagingManager != null)
                    nm.CustomMessagingManager.UnregisterNamedMessageHandler(MsgInput);
                nm.Shutdown();
            }
            IsRunning = false;
        }

        // ---------- NGO callbacks ----------

        private void OnClientConnectedNgo(ulong clientId)
        {
            // Find a free slot. If full, disconnect immediately — server is at capacity.
            int slot = FindFreeSlot();
            if (slot < 0)
            {
                UnityEngine.Debug.LogWarning($"[Server] Client {clientId} connected but server is full; disconnecting.");
                NetworkManager.Singleton.DisconnectClient(clientId, "server full");
                return;
            }

            byte pid = (byte)slot;
            _slotUsed[slot] = true;
            _playerToClient[slot] = clientId;
            _clientToPlayer[clientId] = pid;
            _inputQueues[slot].Clear();
            _highestInputTick[slot] = 0;
            _recipientState[slot].Clear();

            UnityEngine.Debug.Log($"[Server] Client {clientId} -> player {pid} connected.");
            SendWelcome(clientId, pid);
            ClientConnected?.Invoke(pid);
        }

        private void OnClientDisconnectedNgo(ulong clientId)
        {
            if (!_clientToPlayer.TryGetValue(clientId, out byte pid)) return;
            _slotUsed[pid] = false;
            _clientToPlayer.Remove(clientId);
            _inputQueues[pid].Clear();
            _recipientState[pid].Clear();
            ClientDisconnected?.Invoke(pid, DisconnectReason.Timeout);
        }

        private void SendWelcome(ulong clientId, byte pid)
        {
            using var ms = new MemoryStream(_scratch);
            using var w = new BinaryWriter(ms);
            WireCodec.WriteServerWelcome(w, pid, _serverTick);
            int len = (int)ms.Position;

            var payload = new FastBufferWriter(len, Allocator.Temp);
            try
            {
                payload.WriteBytesSafe(_scratch, len);
                NetworkManager.Singleton.CustomMessagingManager.SendNamedMessage(
                    MsgWelcome,
                    clientId,
                    payload,
                    NetworkDelivery.Reliable);
            }
            finally
            {
                payload.Dispose();
            }
        }

        private void OnInputMsg(ulong senderClientId, FastBufferReader reader)
        {
            if (!_clientToPlayer.TryGetValue(senderClientId, out byte pid)) return;

            int len = reader.Length - reader.Position;
            if (len <= 0 || len > _scratch.Length) return;
            reader.ReadBytesSafe(ref _scratch, len);

            using var ms = new MemoryStream(_scratch, 0, len);
            using var r = new BinaryReader(ms);
            int count;
            try { count = WireCodec.ReadInputBatch(r, _inputDecodeBuf); }
            catch (Exception) { return; } // malformed input — drop silently

            // Process oldest → newest (the batch is newest-first by spec).
            bool ackAdvanced = false;
            for (int i = count - 1; i >= 0; i--)
            {
                var cmd = _inputDecodeBuf[i];
                if (cmd.LastAckedServerTick > _recipientState[pid].LastAckedServerTick)
                {
                    _recipientState[pid].LastAckedServerTick = cmd.LastAckedServerTick;
                    ackAdvanced = true;
                }

                if (cmd.Tick <= _highestInputTick[pid]) continue;
                _highestInputTick[pid] = cmd.Tick;
                _inputQueues[pid].Enqueue(cmd);
                InputReceived?.Invoke(pid, cmd);
            }

            // Promote pending sends ≤ new ack tick into ConfirmedIds
            if (ackAdvanced) _recipientState[pid].OnAckAdvanced();
        }

        private int FindFreeSlot()
        {
            for (int i = 0; i < Constants.MaxPlayers; i++) if (!_slotUsed[i]) return i;
            return -1;
        }
    }
}
#endif
