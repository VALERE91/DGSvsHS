#if WITH_DGS
using System;
using System.Diagnostics;
using Unity.Entities;
using Unity.Mathematics;
using DGSvsHS.Gameplay;
using DGSvsHS.Net;
using DGSvsHS.Net.Ngo;
using DGSvsHS.Server.Dots;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace DGSvsHS.Server
{
    public sealed class DedicatedServerMain : MonoBehaviour
    {
        [Header("Network")]
        public ushort Port = 7777;

        [Header("Match")]
        [Tooltip("Server RNG seed. Same seed → same enemy spawn sequence across runs and across server builds.")]
        public ulong Seed = 0xC0FFEE_F00DUL;
        [Tooltip("Auto-start round 1 as soon as the first client connects. If false, round stays at 0 until externally triggered (test harness).")]
        public bool AutoStartMatch = true;
        [Tooltip("God mode: enemies cannot disable the player on contact. Use for trials where you don't want disable/team-wipe events distorting the workload.")]
        public bool GodMode = false;

        [Header("Logging")]
        [Tooltip("Print a heartbeat line every N seconds with stress telemetry. 0 disables.")]
        public float HeartbeatIntervalSec = 0.5f;

        // ---------- Server lifecycle state machine ----------

        public enum ServerLifecycle : byte
        {
            Booting = 0,
            Idle = 1,
            Running = 2,
            Resetting = 3,
            ShuttingDown = 4,
        }

        private ServerLifecycle _state;
        private ServerLifecycle? _pendingTransition;

        // ---------- Runtime ----------

        private NgoNetworkServer _net;
        private World _simWorld;
        private SimulationSystemGroup _simGroup;
        private RewindResolveSystem _rewindResolveSystem;
        private SnapshotCaptureSystem _snapshotCaptureSystem;

        private WorldStateHistory _history;
        private readonly Snapshot _snapshotScratch = new Snapshot();

        private float _tickAccumulator;

        // Stress telemetry
        private readonly Stopwatch _tickStopwatch = new Stopwatch();
        private double _heartbeatTickMsSum;
        private int _heartbeatTickCount;
        private float _heartbeatLastWallTime;
        private Process _selfProcess;

        // Transition detection — round/phase changes.
        private RoundPhase _prevPhase = RoundPhase.PreGame;
        private int _prevRound = -1;

        // ---------- Lifecycle ----------

        private void Awake()
        {
            Application.runInBackground = true;
            Application.targetFrameRate = -1;
            QualitySettings.vSyncCount = 0;

            _state = ServerLifecycle.Booting;
            _selfProcess = Process.GetCurrentProcess();
            _heartbeatLastWallTime = Time.unscaledTime;

            // Create the DOTS World with our systems manually — avoids dragging Unity's default
            // bootstrap systems into a headless server build.
            _simWorld = new World("DGSvsHS.SimWorld");
            _simGroup = _simWorld.GetOrCreateSystemManaged<SimulationSystemGroup>();

            AddSystem<TickAdvanceSystem>();
            AddSystem<Dots.RoundDirectorSystem>();
            AddSystem<PlayerInputSystem>();
            _rewindResolveSystem = _simWorld.GetOrCreateSystemManaged<RewindResolveSystem>();
            _simGroup.AddSystemToUpdateList(_rewindResolveSystem);
            AddSystem<EnemySeekSystem>();
            AddSystem<EnemyIntegrateSystem>();
            AddSystem<PlayerEnemyContactSystem>();
            AddSystem<RewindRecordSystem>();
            _snapshotCaptureSystem = _simWorld.GetOrCreateSystemManaged<SnapshotCaptureSystem>();
            _simGroup.AddSystemToUpdateList(_snapshotCaptureSystem);
            _simGroup.SortSystems();

            // Initial world state: globals + RNG seed.
            SimBootstrap.CreateOrResetGlobals(_simWorld.EntityManager, Seed);
            SetGodMode(GodMode);

            _history = new WorldStateHistory(Constants.SnapshotHistoryTicks);

            _net = new NgoNetworkServer();
            _net.ClientConnected += OnClientConnected;
            _net.ClientDisconnected += OnClientDisconnected;
            _net.InputReceived += OnInputReceived;
        }

        private void AddSystem<T>() where T : unmanaged, ISystem
        {
            var handle = _simWorld.CreateSystem<T>();
            _simGroup.AddSystemToUpdateList(handle);
        }

        private void Start()
        {
            _net.Start(Port);
            TransitionTo(ServerLifecycle.Idle, "Start() complete");
            Debug.Log($"[DedicatedServerMain] Listening on port {Port}, seed {Seed:X}, godMode={GodMode}");
        }

        private void OnDestroy()
        {
            TransitionTo(ServerLifecycle.ShuttingDown, "OnDestroy");
            if (_net != null)
            {
                _net.ClientConnected -= OnClientConnected;
                _net.ClientDisconnected -= OnClientDisconnected;
                _net.InputReceived -= OnInputReceived;
                _net.Dispose();
            }
            if (_simWorld != null && _simWorld.IsCreated)
            {
                _simWorld.Dispose();
                _simWorld = null;
            }
        }

        // ---------- State machine ----------

        private void TransitionTo(ServerLifecycle to, string reason)
        {
            _pendingTransition = to;
            _pendingTransitionReason = reason;
        }
        private string _pendingTransitionReason;

        private void ApplyPendingTransition()
        {
            if (!_pendingTransition.HasValue) return;
            var from = _state;
            var to = _pendingTransition.Value;
            _pendingTransition = null;
            if (from == to) return;
            _state = to;
            Debug.Log($"[Server] state: {from} → {to} tick={GetCurrentTick()} ({_pendingTransitionReason})");
            _pendingTransitionReason = null;
        }

        private uint GetCurrentTick()
        {
            if (_simWorld == null || !_simWorld.IsCreated) return 0;
            var q = _simWorld.EntityManager.CreateEntityQuery(ComponentType.ReadOnly<WorldClock>());
            if (q.CalculateEntityCount() == 0) return 0;
            return q.GetSingleton<WorldClock>().Tick;
        }

        // ---------- Main loop ----------

        private void Update()
        {
            if (_state != ServerLifecycle.ShuttingDown) _net.Poll();
            ApplyPendingTransition();

            switch (_state)
            {
                case ServerLifecycle.Booting:
                case ServerLifecycle.ShuttingDown:
                    break;

                case ServerLifecycle.Idle:
                    HeartbeatIfDue();
                    break;

                case ServerLifecycle.Running:
                    DriveSim();
                    HeartbeatIfDue();
                    break;

                case ServerLifecycle.Resetting:
                    DoReset();
                    TransitionTo(ServerLifecycle.Idle, "reset complete");
                    break;
            }

            ApplyPendingTransition();
        }

        private void DriveSim()
        {
            _net.SetServerTick(GetCurrentTick());
            _tickAccumulator += Time.unscaledDeltaTime;
            int safetyMaxTicks = 5;
            while (_tickAccumulator >= Constants.SimDt && safetyMaxTicks-- > 0)
            {
                _tickAccumulator -= Constants.SimDt;
                PushPerPlayerRtt();
                _snapshotCaptureSystem.Target = _snapshotScratch;

                _tickStopwatch.Restart();
                _simGroup.Update();
                _tickStopwatch.Stop();

                _heartbeatTickMsSum += _tickStopwatch.Elapsed.TotalMilliseconds;
                _heartbeatTickCount++;

                // Broadcast snapshot
                if (_snapshotScratch.Tick > 0)
                {
                    _history.Record(_snapshotScratch);
                    _net.Broadcast(_snapshotScratch, _history);
                }
            }
        }

        private void PushPerPlayerRtt()
        {
            if (_rewindResolveSystem == null) return;
            for (byte pid = 0; pid < Constants.MaxPlayers; pid++)
            {
                _rewindResolveSystem.PlayerRttMs[pid] = _net.GetRttMs(pid);
            }
        }

        private void DoReset()
        {
            // Wipe enemies, reset round state to PreGame, reseed RNG.
            SimBootstrap.CreateOrResetGlobals(_simWorld.EntityManager, Seed);
            SimBootstrap.DestroyAllEnemies(_simWorld.EntityManager);
            SetGodMode(GodMode);
            _history.Clear();
            _tickAccumulator = 0f;
            _prevPhase = RoundPhase.PreGame;
            _prevRound = -1;
            Debug.Log($"[Server] reset complete — RNG reseeded to {Seed:X}, world wiped");
        }

        private void SetGodMode(bool on)
        {
            var em = _simWorld.EntityManager;
            var q = em.CreateEntityQuery(ComponentType.ReadWrite<GodModeFlag>());
            if (q.CalculateEntityCount() == 0) return;
            q.SetSingleton(new GodModeFlag { Value = (byte)(on ? 1 : 0) });
        }

        // ---------- NGO callbacks ----------

        private void OnClientConnected(byte playerId)
        {
            // Spawn a Player entity on a circle inside the arena.
            float angle = (playerId / (float)Constants.MaxPlayers) * math.PI * 2f;
            var pos = new float2(math.cos(angle), math.sin(angle)) * (Constants.ArenaRadius * 0.3f);

            var em = _simWorld.EntityManager;
            var e = em.CreateEntity(
                typeof(PlayerTag), typeof(PlayerSlot), typeof(Position2D), typeof(Aim2D),
                typeof(FireCooldown), typeof(DisableTimer), typeof(Alive));
            em.SetComponentData(e, new PlayerSlot { Value = playerId });
            em.SetComponentData(e, new Position2D { Value = pos });
            em.SetComponentData(e, new Aim2D { Value = new float2(1f, 0f) });
            em.SetComponentData(e, new FireCooldown { Seconds = 0f });
            em.SetComponentData(e, new DisableTimer { Seconds = 0f });
            em.SetComponentData(e, Alive.From(true));
            em.SetName(e, $"Player {playerId}");

            Debug.Log($"[Server] Player {playerId} connected, spawned at ({pos.x:F2}, {pos.y:F2})");

            // Was Idle → first client triggers Running, and kicks off round 1.
            if (_state == ServerLifecycle.Idle)
            {
                if (AutoStartMatch) KickoffMatch();
                TransitionTo(ServerLifecycle.Running, $"first client (player {playerId}) connected");
            }
        }

        private void OnClientDisconnected(byte playerId, DisconnectReason reason)
        {
            var em = _simWorld.EntityManager;
            var q = em.CreateEntityQuery(ComponentType.ReadOnly<PlayerSlot>(), ComponentType.ReadOnly<PlayerTag>());
            var ents = q.ToEntityArray(Unity.Collections.Allocator.Temp);
            try
            {
                for (int i = 0; i < ents.Length; i++)
                {
                    var slot = em.GetComponentData<PlayerSlot>(ents[i]);
                    if (slot.Value == playerId)
                    {
                        em.DestroyEntity(ents[i]);
                        break;
                    }
                }
            }
            finally { ents.Dispose(); }

            Debug.Log($"[Server] Player {playerId} disconnected: {reason}");

            // Last client out → Resetting (next-frame wipe + back to Idle).
            int remaining = em.CreateEntityQuery(ComponentType.ReadOnly<PlayerTag>()).CalculateEntityCount();
            if (remaining == 0 && _state == ServerLifecycle.Running)
            {
                TransitionTo(ServerLifecycle.Resetting, "last client disconnected");
            }
        }

        private void OnInputReceived(byte playerId, InputCmd cmd)
        {
            var em = _simWorld.EntityManager;
            var clockQ = em.CreateEntityQuery(ComponentType.ReadOnly<WorldClock>());
            if (clockQ.CalculateEntityCount() == 0) return;
            var globals = clockQ.GetSingletonEntity();
            var buf = em.GetBuffer<TickInput>(globals);
            buf.Add(new TickInput
            {
                PlayerId = playerId,
                Tick = cmd.Tick,
                LastAckedServerTick = cmd.LastAckedServerTick,
                Move = new float2(cmd.Move.x, cmd.Move.y),
                Aim = new float2(cmd.Aim.x, cmd.Aim.y),
                Flags = cmd.Flags,
            });
        }

        private void KickoffMatch()
        {
            var em = _simWorld.EntityManager;
            var q = em.CreateEntityQuery(ComponentType.ReadWrite<RoundState>());
            if (q.CalculateEntityCount() == 0) return;
            var rs = q.GetSingleton<RoundState>();
            rs.Phase = RoundPhase.InterRound;
            rs.Round = 0;
            rs.InterRoundTimer = Constants.InterRoundDelaySec;
            rs.RoundTimer = 0f;
            q.SetSingleton(rs);
        }

        // ---------- Heartbeat + telemetry ----------

        private void HeartbeatIfDue()
        {
            var em = _simWorld.EntityManager;
            var rsQ = em.CreateEntityQuery(ComponentType.ReadOnly<RoundState>());
            if (rsQ.CalculateEntityCount() == 0) return;
            var rs = rsQ.GetSingleton<RoundState>();
            if (rs.Phase != _prevPhase || rs.Round != _prevRound)
            {
                int alive = em.CreateEntityQuery(ComponentType.ReadOnly<EnemyTag>()).CalculateEntityCount();
                Debug.Log($"[Server] state: phase {_prevPhase}→{rs.Phase} round {_prevRound}→{rs.Round} " +
                          $"tick={GetCurrentTick()} enemies={alive} players={em.CreateEntityQuery(ComponentType.ReadOnly<PlayerTag>()).CalculateEntityCount()}");
                _prevPhase = rs.Phase;
                _prevRound = rs.Round;
            }

            if (HeartbeatIntervalSec <= 0f) return;
            float wallNow = Time.unscaledTime;
            float wallElapsed = wallNow - _heartbeatLastWallTime;
            if (wallElapsed < HeartbeatIntervalSec) return;
            _heartbeatLastWallTime = wallNow;

            int aliveEnemies = em.CreateEntityQuery(ComponentType.ReadOnly<EnemyTag>()).CalculateEntityCount();
            int activePlayers = em.CreateEntityQuery(ComponentType.ReadOnly<PlayerTag>()).CalculateEntityCount();

            double avgMsPerTick = _heartbeatTickCount > 0 ? _heartbeatTickMsSum / _heartbeatTickCount : 0;
            float expectedTicks = wallElapsed * Constants.TicksPerSecond;
            long managedMb = GC.GetTotalMemory(false) / (1024 * 1024);
            long rssMb = 0;
            try { _selfProcess.Refresh(); rssMb = _selfProcess.WorkingSet64 / (1024 * 1024); }
            catch { }
            string stress = $"ms/tick={avgMsPerTick:F1} ticks={_heartbeatTickCount}/{expectedTicks:F0} mem={managedMb}MB rss={rssMb}MB";
            _heartbeatTickMsSum = 0;
            _heartbeatTickCount = 0;

            if (_state == ServerLifecycle.Running && rs.Phase == RoundPhase.InRound)
            {
                Debug.Log($"[Server] tick={GetCurrentTick()} round={rs.Round}/{Constants.TotalRounds} " +
                          $"elapsed={rs.RoundTimer:F1}s alive={aliveEnemies} " +
                          $"toSpawn={rs.SpawnsRemaining}/{rs.SpawnTarget} " +
                          $"players={activePlayers} {stress}");
            }
            else if (_state == ServerLifecycle.Running && rs.Phase == RoundPhase.InterRound)
            {
                Debug.Log($"[Server] tick={GetCurrentTick()} phase=InterRound nextRound={rs.Round + 1}/{Constants.TotalRounds} " +
                          $"countdown={rs.InterRoundTimer:F1}s players={activePlayers} {stress}");
            }
            else
            {
                Debug.Log($"[Server] tick={GetCurrentTick()} server={_state} phase={rs.Phase} round={rs.Round} players={activePlayers} {stress}");
            }
        }
    }
}
#endif
