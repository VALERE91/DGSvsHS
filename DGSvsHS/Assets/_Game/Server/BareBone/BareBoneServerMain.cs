#if WITH_BAREBONE
using System;
using System.Collections.Generic;
using System.Diagnostics;
using Unity.Netcode;
using Unity.Netcode.Transports.UTP;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace DGSvsHS.Server.BareBone
{
    /// <summary>
    /// Bare-bone Unity dedicated-server build. Used as the apples-to-apples baseline against
    /// the equivalent Rust/Bevy and C#/Arch idle servers — measures "what Unity itself costs"
    /// vs. "what gameplay adds".
    ///
    /// <para>No gameplay simulation, no DOTS World, no snapshot pipeline, no rewind, no inputs.
    /// Just: start NGO server, accept connections, track a slot table, emit a heartbeat
    /// formatted identically to the Bevy/Arch reference outputs so log scrapers see the
    /// same fields across all three builds.</para>
    ///
    /// <para><b>Build profile.</b> Define <c>WITH_BAREBONE</c> in Player Settings → Scripting
    /// Define Symbols for the dedicated-server platform. Do NOT also define <c>WITH_DGS</c> in
    /// the same build profile — they're mutually exclusive (different server flavors, different
    /// scenes, different binaries).</para>
    ///
    /// <para><b>Scene setup.</b> A scene with one GameObject holding a NetworkManager (+
    /// UnityTransport) component and one GameObject holding this MonoBehaviour. That's it.
    /// No camera, no UI, no other systems — dedicated-server platform strips most of that
    /// automatically, but having a minimal scene keeps the baseline clean.</para>
    /// </summary>
    public sealed class BareBoneServerMain : MonoBehaviour
    {
        [Header("Network")]
        public ushort Port = 7777;

        [Header("Slots")]
        [Tooltip("How many client slots the bare-bone listener accepts. Above this, new connections are refused.")]
        [Range(1, 256)] public int MaxSlots = 64;

        [Header("Logging")]
        [Tooltip("Seconds between heartbeat log lines. 0 disables.")]
        public float HeartbeatIntervalSec = 1.0f;

        // ---------- State machine ----------

        public enum ServerLifecycle : byte
        {
            Booting = 0,
            Listening = 1,
            ShuttingDown = 2,
        }

        private ServerLifecycle _state;

        // ---------- Slot table ----------

        private readonly Dictionary<ulong, byte> _clientToSlot = new Dictionary<ulong, byte>();
        private bool[] _slotUsed;

        // ---------- Telemetry ----------

        private float _heartbeatLastWallTime;
        private float _startupWallTime;
        private Process _selfProcess;

        // ---------- Lifecycle ----------

        private void Awake()
        {
            Application.runInBackground = true;
            Application.targetFrameRate = -1;
            QualitySettings.vSyncCount = 0;

            _state = ServerLifecycle.Booting;
            _slotUsed = new bool[MaxSlots];
            _selfProcess = Process.GetCurrentProcess();
            _startupWallTime = Time.unscaledTime;
            _heartbeatLastWallTime = Time.unscaledTime;
        }

        private void Start()
        {
            var nm = NetworkManager.Singleton;
            if (nm == null)
            {
                Debug.LogError("[BareBone] NetworkManager.Singleton is null — add a NetworkManager to the scene.");
                return;
            }

            var utp = nm.GetComponent<UnityTransport>();
            if (utp != null)
            {
                utp.SetConnectionData("0.0.0.0", Port, "0.0.0.0");
                utp.MaxPacketQueueSize = 1024;
            }

            // Strip NGO features we don't need. Approval/scene-management negotiations would
            // add memory and cycles that aren't part of the baseline we're measuring.
            nm.NetworkConfig.ConnectionApproval = false;
            nm.NetworkConfig.EnableSceneManagement = false;
            nm.LogLevel = LogLevel.Normal;

            nm.OnClientConnectedCallback += OnClientConnected;
            nm.OnClientDisconnectCallback += OnClientDisconnected;

            nm.StartServer();
            TransitionTo(ServerLifecycle.Listening, $"NGO StartServer port={Port}");
        }

        private void OnDestroy()
        {
            TransitionTo(ServerLifecycle.ShuttingDown, "OnDestroy");
            var nm = NetworkManager.Singleton;
            if (nm != null)
            {
                nm.OnClientConnectedCallback -= OnClientConnected;
                nm.OnClientDisconnectCallback -= OnClientDisconnected;
                if (nm.IsServer) nm.Shutdown();
            }
        }

        private void TransitionTo(ServerLifecycle to, string reason)
        {
            var from = _state;
            _state = to;
            Debug.Log($"[BareBone] state: {from} → {to} ({reason})");
        }

        // ---------- NGO callbacks ----------

        private void OnClientConnected(ulong clientId)
        {
            int slot = -1;
            for (int i = 0; i < _slotUsed.Length; i++)
            {
                if (!_slotUsed[i]) { slot = i; break; }
            }
            if (slot < 0)
            {
                Debug.LogWarning($"[BareBone] slot table full ({MaxSlots}); disconnecting client {clientId}");
                NetworkManager.Singleton.DisconnectClient(clientId, "server full");
                return;
            }
            _slotUsed[slot] = true;
            _clientToSlot[clientId] = (byte)slot;
            Debug.Log($"[BareBone] client {clientId} → slot {slot} (total {_clientToSlot.Count})");
        }

        private void OnClientDisconnected(ulong clientId)
        {
            if (_clientToSlot.TryGetValue(clientId, out byte slot))
            {
                _slotUsed[slot] = false;
                _clientToSlot.Remove(clientId);
            }
            Debug.Log($"[BareBone] client {clientId} disconnected (total {_clientToSlot.Count})");
        }

        // ---------- Main loop ----------

        private void Update()
        {
            HeartbeatIfDue();
        }

        private void HeartbeatIfDue()
        {
            if (HeartbeatIntervalSec <= 0f) return;
            float wallNow = Time.unscaledTime;
            float wallElapsed = wallNow - _heartbeatLastWallTime;
            if (wallElapsed < HeartbeatIntervalSec) return;
            _heartbeatLastWallTime = wallNow;

            double rssMb = 0, vmMb = 0;
            try
            {
                _selfProcess.Refresh();
                rssMb = _selfProcess.WorkingSet64 / (1024.0 * 1024.0);
                vmMb = _selfProcess.VirtualMemorySize64 / (1024.0 * 1024.0);
            }
            catch { /* WorkingSet/VirtualMemorySize can throw on some Linux configs; skip */ }

            float uptime = wallNow - _startupWallTime;

            // Format matches the Rust/Bevy and C#/Arch reference outputs so a log scraper sees
            // identical fields across all three idle baselines:
            //   SERVER STATS | RAM (RSS): X.XX MB | RAM (VM) : Y.YY MB | Uptime: Z.Zs | clients=N state=...
            Debug.Log(
                $"SERVER STATS | RAM (RSS): {rssMb:F2} MB | RAM (VM) : {vmMb:F2} MB | " +
                $"Uptime: {uptime:F1}s | clients={_clientToSlot.Count} state={_state}");
        }
    }
}
#endif
