#if WITH_DGS
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using DGSvsHS.Gameplay;
using UnityEngine;

namespace DGSvsHS.Net.Quic
{
    /// <summary>
    /// DGS server transport over QUIC — the server counterpart to
    /// <see cref="QuicNetworkClient"/>, P/Invoking the same <c>dgsvshs_socket</c>
    /// cdylib (server side, <c>server.rs</c>). Replaces the old NGO/UDP transport so
    /// the Unity DOTS leg runs the same QUIC + TLS 1.3 crypto as every other leg.
    ///
    /// <para>All the per-recipient snapshot machinery (delta vs. full, priority
    /// selection, ack tracking) is identical to the former NgoNetworkServer — only
    /// the wire send/receive calls differ (native FFI instead of NGO named
    /// messages). The Rust side assigns each connection a slot id 0..MaxPlayers-1,
    /// which is used directly as the player id here.</para>
    /// </summary>
    public sealed class QuicNetworkServer : INetworkServer
    {
        // ------------------------------------------------------------------
        // Native ABI (Rust cdylib: dgsvshs_socket — server side)
        // ------------------------------------------------------------------
        private const string DLL = "dgsvshs_socket";

        [DllImport(DLL)] private static extern IntPtr dgs_server_create();
        [DllImport(DLL)] private static extern void   dgs_server_destroy(IntPtr h);
        [DllImport(DLL)] private static extern int    dgs_server_start(IntPtr h, ushort port);
        [DllImport(DLL)] private static extern int    dgs_server_send(IntPtr h, byte client_id, byte msg_type, byte[] data, int len);
        [DllImport(DLL)] private static extern int    dgs_server_poll(IntPtr h, out byte out_event, out byte out_client_id, out byte out_msg_type, byte[] buf, int buf_len);

        // Event kinds (server.rs event_kind).
        private const byte EvNone = 0, EvConnected = 1, EvDisconnected = 2, EvMessage = 3;

        // ------------------------------------------------------------------

        private IntPtr _h;

        private readonly bool[] _slotUsed = new bool[Constants.MaxPlayers];
        private readonly Queue<InputCmd>[] _inputQueues = new Queue<InputCmd>[Constants.MaxPlayers];
        private readonly uint[] _highestInputTick = new uint[Constants.MaxPlayers];
        private readonly float[] _rttMs = new float[Constants.MaxPlayers];

        private readonly RecipientSnapshotState[] _recipientState = new RecipientSnapshotState[Constants.MaxPlayers];

        private byte[] _scratch = new byte[65536];
        private readonly byte[] _recvScratch = new byte[65536];
        private readonly InputCmd[] _inputDecodeBuf = new InputCmd[4];

        private readonly List<EnemyDeltaEntry> _changedScratch = new List<EnemyDeltaEntry>(2048);
        private readonly List<ushort> _removedScratch = new List<ushort>(256);
        private readonly List<EnemySnap> _addedScratch = new List<EnemySnap>(512);
        private readonly List<EnemySnap> _fullSelectedScratch = new List<EnemySnap>(2048);
        private readonly HashSet<ushort> _includedScratch = new HashSet<ushort>();
        private readonly List<SnapshotPriority.ScoredEnemy> _scoredScratch = new List<SnapshotPriority.ScoredEnemy>(2048);
        private readonly HashSet<ushort> _currentIdsScratch = new HashSet<ushort>();
        private readonly Dictionary<ushort, int> _baselineIndexByIdScratch = new Dictionary<ushort, int>(2048);

        private uint _serverTick;

        public bool IsRunning { get; private set; }

        public event Action<byte> ClientConnected;
        public event Action<byte, DisconnectReason> ClientDisconnected;
        public event Action<byte, InputCmd> InputReceived;

        public QuicNetworkServer()
        {
            for (int i = 0; i < Constants.MaxPlayers; i++)
            {
                _inputQueues[i] = new Queue<InputCmd>(8);
                _rttMs[i] = 60f;
                _recipientState[i] = new RecipientSnapshotState();
            }
            _h = dgs_server_create();
            if (_h == IntPtr.Zero) throw new InvalidOperationException("dgs_server_create returned null");
        }

        public void Start(ushort port)
        {
            int rc = dgs_server_start(_h, port);
            if (rc < 0) throw new InvalidOperationException($"dgs_server_start failed (rc={rc})");
            IsRunning = true;
            Debug.Log($"[ServerNet] QUIC server listening on 0.0.0.0:{port}");
        }

        public void SetServerTick(uint tick) => _serverTick = tick;

        // Per-connection RTT isn't surfaced by the FFI yet; default like the Arch leg.
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

            Vector2 anchor = Vector2.zero;
            for (int i = 0; i < current.Players.Count; i++)
                if (current.Players[i].Id == pid) { anchor = current.Players[i].Position; break; }

            int playerOverhead = 1 + current.Players.Count * WireCodec.PlayerSnapFullBytes;
            int fireOverhead = 1 + Math.Min(16, current.RecentFireEvents.Count) * WireCodec.FireEventBytes;
            int fixedOverhead = WireCodec.SnapshotHeaderBytes + playerOverhead + fireOverhead;
            int enemySectionHeader = useDelta ? (2 + 2 + 2 + 4) : (2 + 4);
            int budgetForEnemies = Constants.SnapshotByteBudget - fixedOverhead - enemySectionHeader;
            if (budgetForEnemies < 0) budgetForEnemies = 0;

            using var ms = new MemoryStream(_scratch);
            using var w = new BinaryWriter(ms);

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
                    _includedScratch, _scoredScratch,
                    _currentIdsScratch, _baselineIndexByIdScratch);
                WireCodec.WriteDeltaSnapshotBody(
                    w, current.Players, _changedScratch, _removedScratch, _addedScratch,
                    current.EnemyTotalInWorld, current.RecentFireEvents);
            }
            else
            {
                SnapshotPriority.SelectForFull(current, anchor, budgetForEnemies, _fullSelectedScratch, _scoredScratch);
                WireCodec.WriteFullSnapshotBody(
                    w, current.Players, _fullSelectedScratch, current.EnemyTotalInWorld, current.RecentFireEvents);

                _includedScratch.Clear();
                for (int i = 0; i < _fullSelectedScratch.Count; i++) _includedScratch.Add(_fullSelectedScratch[i].Id);
                _removedScratch.Clear();
            }

            current.Kind = prevKind;
            current.BaselineTick = prevBaseline;

            int len = (int)ms.Position;
            int rc = dgs_server_send(_h, pid, WireCodec.MsgSnapshot, _scratch, len);
            if (rc >= 0)
                rstate.OnSnapshotSent(current.Tick, isFull: !useDelta, _includedScratch, _removedScratch);
            else
                Debug.LogWarning($"[Server] Snapshot send failed (size={len}B, pid={pid}, rc={rc}).");
        }

        public void Poll()
        {
            if (_h == IntPtr.Zero) return;

            // Drain all pending native events this frame.
            while (true)
            {
                int n = dgs_server_poll(_h, out byte ev, out byte clientId, out byte msgType, _recvScratch, _recvScratch.Length);
                if (ev == EvNone) break;
                switch (ev)
                {
                    case EvConnected:    OnConnected(clientId); break;
                    case EvDisconnected: OnDisconnected(clientId); break;
                    case EvMessage:      if (n >= 0) OnMessage(clientId, msgType, n); break;
                }
            }
        }

        private void OnConnected(byte pid)
        {
            if (pid >= Constants.MaxPlayers) return; // Rust caps at MaxPlayers, but be defensive.
            _slotUsed[pid] = true;
            _inputQueues[pid].Clear();
            _highestInputTick[pid] = 0;
            _recipientState[pid].Clear();

            Debug.Log($"[Server] Client -> player {pid} connected.");
            SendWelcome(pid);
            ClientConnected?.Invoke(pid);
        }

        private void OnDisconnected(byte pid)
        {
            if (pid >= Constants.MaxPlayers || !_slotUsed[pid]) return;
            _slotUsed[pid] = false;
            _inputQueues[pid].Clear();
            _recipientState[pid].Clear();
            ClientDisconnected?.Invoke(pid, DisconnectReason.Timeout);
        }

        private void OnMessage(byte pid, byte msgType, int len)
        {
            if (pid >= Constants.MaxPlayers || !_slotUsed[pid]) return;

            if (msgType == WireCodec.MsgInput)
            {
                HandleInput(pid, len);
            }
            // MsgClientHello (0x01) is ignored — Welcome is sent proactively on connect.
        }

        private void HandleInput(byte pid, int len)
        {
            if (len <= 0 || len > _recvScratch.Length) return;

            using var ms = new MemoryStream(_recvScratch, 0, len);
            using var r = new BinaryReader(ms);
            int count;
            try { count = WireCodec.ReadInputBatch(r, _inputDecodeBuf); }
            catch (Exception) { return; } // malformed input — drop

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

            if (ackAdvanced) _recipientState[pid].OnAckAdvanced();
        }

        private void SendWelcome(byte pid)
        {
            using var ms = new MemoryStream(_scratch);
            using var w = new BinaryWriter(ms);
            WireCodec.WriteServerWelcome(w, pid, _serverTick);
            int len = (int)ms.Position;
            dgs_server_send(_h, pid, WireCodec.MsgServerWelcome, _scratch, len);
        }

        public void Dispose()
        {
            if (_h != IntPtr.Zero)
            {
                dgs_server_destroy(_h);
                _h = IntPtr.Zero;
            }
            IsRunning = false;
        }
    }
}
#endif
