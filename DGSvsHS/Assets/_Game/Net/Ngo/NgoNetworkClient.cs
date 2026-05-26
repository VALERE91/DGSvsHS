#if WITH_DGS
using System;
using System.IO;
using DGSvsHS.Gameplay;
using Unity.Collections;
using Unity.Netcode;
using Unity.Netcode.Transports.UTP;
using UnityEngine;

namespace DGSvsHS.Net.Ngo
{
    public sealed class NgoNetworkClient : INetworkClient
    {
        private const string MsgWelcome = "dgsvshs.welcome";
        private const string MsgSnapshot = "dgsvshs.snap";
        private const string MsgInput = "dgsvshs.input";
        private const string MsgDisconnect = "dgsvshs.bye";

        private readonly Snapshot _snapshotScratch = new Snapshot();
        private readonly SnapshotDecoder _snapDecoder = new SnapshotDecoder();
        private readonly byte[] _inputScratch = new byte[256];
        private readonly InputCmd[] _inputRingForRedundancy = new InputCmd[4];
        private int _redundancyFill;

        private float _smoothedRttMs = 60f;
        private float _lastSnapshotRecvTime;

        public ConnectionState State { get; private set; } = ConnectionState.Disconnected;
        public byte LocalPlayerId { get; private set; }
        public float OneWayLatencyMs => _smoothedRttMs * 0.5f;

        public event Action Connected;
        public event Action<DisconnectReason> Disconnected;
        public event Action<Snapshot> SnapshotReceived;

        public void Connect(string host, ushort port)
        {
            var nm = NetworkManager.Singleton;
            if (nm == null) throw new InvalidOperationException("NetworkManager.Singleton is null. Add a NetworkManager to the scene.");

            var utp = nm.GetComponent<UnityTransport>();
            utp.SetConnectionData(host, port);
            // Match the server: 1024-packet send/receive queue (~4 MB) so we can absorb bursts
            // of snapshots after a render hitch without dropping packets at the UTP layer.
            utp.MaxPacketQueueSize = 1024;

            nm.LogLevel = Unity.Netcode.LogLevel.Developer;

            nm.OnClientConnectedCallback += OnClientConnectedNgo;
            nm.OnClientDisconnectCallback += OnClientDisconnectedNgo;

            UnityEngine.Debug.Log($"[ClientNet] StartClient: host={host} port={port} approval={nm.NetworkConfig.ConnectionApproval} tickRate={nm.NetworkConfig.TickRate} sceneMgmt={nm.NetworkConfig.EnableSceneManagement}");

            State = ConnectionState.Connecting;
            nm.StartClient();

            // CustomMessagingManager is created during StartClient(); register after.
            nm.CustomMessagingManager.RegisterNamedMessageHandler(MsgWelcome, OnWelcome);
            nm.CustomMessagingManager.RegisterNamedMessageHandler(MsgSnapshot, OnSnapshot);
            nm.CustomMessagingManager.RegisterNamedMessageHandler(MsgDisconnect, OnDisconnectMsg);
        }

        public void SendInput(in InputCmd cmd)
        {
            if (State != ConnectionState.Connected) return;

            // Slide the redundancy window: newest at [0], shift others down.
            for (int i = _inputRingForRedundancy.Length - 1; i > 0; i--)
                _inputRingForRedundancy[i] = _inputRingForRedundancy[i - 1];
            _inputRingForRedundancy[0] = cmd;
            if (_redundancyFill < _inputRingForRedundancy.Length) _redundancyFill++;

            using var ms = new MemoryStream(_inputScratch);
            using var w = new BinaryWriter(ms);
            WireCodec.WriteInputBatch(w, _inputRingForRedundancy, _redundancyFill);

            int len = (int)ms.Position;
            var payload = new FastBufferWriter(len, Allocator.Temp);
            try
            {
                payload.WriteBytesSafe(_inputScratch, len);
                NetworkManager.Singleton.CustomMessagingManager.SendNamedMessage(
                    MsgInput,
                    NetworkManager.ServerClientId,
                    payload,
                    NetworkDelivery.Unreliable);
            }
            finally
            {
                payload.Dispose();
            }
        }

        public void Poll()
        {
            // NGO pumps itself on Unity's main loop; nothing to do here.
            // Method kept for INetworkClient parity with the QUIC implementation,
            // which needs explicit polling from C#.
        }

        // ---------- NGO callbacks ----------

        private void OnClientConnectedNgo(ulong clientId)
        {
            // We are the client; connection complete at transport level. We still
            // need the ServerWelcome message to learn our assigned player id.
            // State stays Connecting until then.
        }

        private void OnClientDisconnectedNgo(ulong clientId)
        {
            State = ConnectionState.Disconnected;
            _snapDecoder.Reset();
            Disconnected?.Invoke(DisconnectReason.Timeout);
        }

        private void OnWelcome(ulong senderClientId, FastBufferReader reader)
        {
            int len = reader.Length - reader.Position;
            var bytes = new byte[len];
            reader.ReadBytesSafe(ref bytes, len);
            using var ms = new MemoryStream(bytes);
            using var r = new BinaryReader(ms);
            WireCodec.ReadServerWelcome(r, out uint version, out byte playerId, out uint serverTick,
                out ushort simTickMs, out ushort snapshotEveryNTicks);

            if (version != WireCodec.ProtocolVersion ||
                simTickMs != Constants.SimTickMs ||
                snapshotEveryNTicks != Constants.SnapshotEveryNTicks)
            {
                Disconnect(DisconnectReason.ProtocolError);
                return;
            }

            LocalPlayerId = playerId;
            State = ConnectionState.Connected;
            Connected?.Invoke();
        }

        private void OnSnapshot(ulong senderClientId, FastBufferReader reader)
        {
            int len = reader.Length - reader.Position;
            var bytes = new byte[len];
            reader.ReadBytesSafe(ref bytes, len);
            using var ms = new MemoryStream(bytes);
            using var r = new BinaryReader(ms);
            if (!_snapDecoder.Decode(r, _snapshotScratch))
            {
                // Delta arrived against a baseline we no longer have. Drop; the next input's
                // ack will pull the server back into sync (Full or matching delta).
                return;
            }

            // Coarse RTT estimate: smooth over recent intervals between snapshots.
            float now = Time.realtimeSinceStartup;
            if (_lastSnapshotRecvTime > 0f)
            {
                // Not a real RTT — better than nothing until we add explicit pings.
                // The server-side INetworkServer.GetRttMs is what server rewind actually
                // uses; OneWayLatencyMs here is only for client-side diagnostics.
                float interval = (now - _lastSnapshotRecvTime) * 1000f;
                _smoothedRttMs = Mathf.Lerp(_smoothedRttMs, interval, 0.1f);
            }
            _lastSnapshotRecvTime = now;

            SnapshotReceived?.Invoke(_snapshotScratch);
        }

        private void OnDisconnectMsg(ulong senderClientId, FastBufferReader reader)
        {
            byte reason = 0;
            if (reader.Length - reader.Position >= 1) reader.ReadByteSafe(out reason);
            Disconnect((DisconnectReason)reason);
        }

        // ---------- Teardown ----------

        public void Dispose()
        {
            var nm = NetworkManager.Singleton;
            if (nm == null) return;
            nm.OnClientConnectedCallback -= OnClientConnectedNgo;
            nm.OnClientDisconnectCallback -= OnClientDisconnectedNgo;
            if (nm.CustomMessagingManager != null)
            {
                nm.CustomMessagingManager.UnregisterNamedMessageHandler(MsgWelcome);
                nm.CustomMessagingManager.UnregisterNamedMessageHandler(MsgSnapshot);
                nm.CustomMessagingManager.UnregisterNamedMessageHandler(MsgDisconnect);
            }
            if (nm.IsClient) nm.Shutdown();
            State = ConnectionState.Disconnected;
        }

        private void Disconnect(DisconnectReason reason)
        {
            var prev = State;
            State = ConnectionState.Disconnected;
            if (prev != ConnectionState.Disconnected) Disconnected?.Invoke(reason);
            if (NetworkManager.Singleton != null && NetworkManager.Singleton.IsClient)
                NetworkManager.Singleton.Shutdown();
        }
    }
}
#endif
