#if WITH_DGS
using System;
using System.Diagnostics;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Physics;
using Unity.Physics.Systems;
using Unity.Transforms;
using DGSvsHS.Gameplay;
using DGSvsHS.Net;
using DGSvsHS.Net.Quic;
using DGSvsHS.Server.Dots;
using Unity.Collections;
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

        private INetworkServer _net;
        private World _simWorld;
        private SimulationSystemGroup _simGroup;
        private RewindResolveSystem _rewindResolveSystem;
        private SnapshotCaptureSystem _snapshotCaptureSystem;

        private WorldStateHistory _history;
        private readonly Snapshot _snapshotScratch = new Snapshot();

        private float _tickAccumulator;
        private double _simElapsedTime;

        private readonly Stopwatch _tickStopwatch = new Stopwatch();
        private double _heartbeatTickMsSum;
        private int _heartbeatTickCount;
        private float _heartbeatLastWallTime;
        private Process _selfProcess;

        private RoundPhase _prevPhase = RoundPhase.PreGame;
        private int _prevRound = -1;

        private const float StatsIntervalSec = 0.05f;
        private float _statsWindowStartTime;
        private int _statsOuterFrames;
        private int _statsInnerTicks;
        private bool _statsPrimed;
        
        private EntityQuery _worldClockQuery;
        private EntityQuery _roundStateQuery;
        private EntityQuery _godModeQuery;
        private EntityQuery _enemyTagQuery;
        private EntityQuery _playerTagQuery;
        private EntityQuery _playerSlotQuery;

        // ---------- Lifecycle ----------

        private void Awake()
        {
            Application.runInBackground = true;
            Application.targetFrameRate = 125;
            QualitySettings.vSyncCount = 0;

            _state = ServerLifecycle.Booting;
            _selfProcess = Process.GetCurrentProcess();
            _heartbeatLastWallTime = Time.unscaledTime;

            _simWorld = new World("DGSvsHS.SimWorld");
            _simGroup = _simWorld.GetOrCreateSystemManaged<SimulationSystemGroup>();

            AddSystem<TickAdvanceSystem>();
            AddSystem<Dots.RoundDirectorSystem>();
            AddSystem<PlayerInputSystem>();
            _rewindResolveSystem = _simWorld.GetOrCreateSystemManaged<RewindResolveSystem>();
            _simGroup.AddSystemToUpdateList(_rewindResolveSystem);
            AddSystem<EnemySeekSystem>();
            var fixedStep = _simWorld.GetOrCreateSystemManaged<Unity.Entities.FixedStepSimulationSystemGroup>();
            _simGroup.AddSystemToUpdateList(fixedStep);

            var depTypes = new System.Collections.Generic.List<System.Type>();
            foreach (var t in DefaultWorldInitialization.GetAllSystems(WorldSystemFilterFlags.Default))
            {
                if (t.Namespace != null &&
                    (t.Namespace.StartsWith("Unity.Physics") || t.Namespace.StartsWith("Unity.Transforms")))
                {
                    depTypes.Add(t);
                }
            }
            DefaultWorldInitialization.AddSystemsToRootLevelSystemGroups(_simWorld, depTypes);

            {
                var stepEntity = _simWorld.EntityManager.CreateEntity(typeof(Unity.Physics.PhysicsStep), typeof(Unity.Physics.PhysicsWorldIndex));
                _simWorld.EntityManager.SetName(stepEntity, "PhysicsStep");
                _simWorld.EntityManager.SetComponentData(stepEntity, new Unity.Physics.PhysicsStep
                {
                    SimulationType = Unity.Physics.SimulationType.UnityPhysics,
                    Gravity = float3.zero,
                    SolverIterationCount = Unity.Physics.PhysicsStep.Default.SolverIterationCount,
                    SolverStabilizationHeuristicSettings = Unity.Physics.PhysicsStep.Default.SolverStabilizationHeuristicSettings,
                    MultiThreaded = 1,
                    SynchronizeCollisionWorld = 1,
                });
            }

            {
                var fs = _simWorld.GetExistingSystemManaged<FixedStepSimulationSystemGroup>();
                if (fs != null)
                {
                    fs.Timestep = Constants.SimDt;
                    Debug.Log($"[Diag] FixedStep.Timestep = {fs.Timestep}");

                    var syncIn  = _simWorld.CreateSystem<SyncPos2DToLocalTransformSystem>();
                    var syncOut = _simWorld.CreateSystem<SyncPhysicsToPos2DSystem>();
                    fs.AddSystemToUpdateList(syncIn);
                    fs.AddSystemToUpdateList(syncOut);
                    fs.SortSystems();
                }
                else
                {
                    Debug.LogWarning("[Diag] FixedStepSimulationSystemGroup not found");
                }
            }

            AddSystem<PlayerEnemyContactSystem>();
            AddSystem<RewindRecordSystem>();
            _snapshotCaptureSystem = _simWorld.GetOrCreateSystemManaged<SnapshotCaptureSystem>();
            _simGroup.AddSystemToUpdateList(_snapshotCaptureSystem);
            _simGroup.SortSystems();

            ScriptBehaviourUpdateOrder.RemoveWorldFromCurrentPlayerLoop(_simWorld);
            Debug.Log("[Diag] called RemoveWorldFromCurrentPlayerLoop — DriveSim should be the sole driver");

            // ----- DIAGNOSTIC: confirm key groups + systems exist -----
            {
                var defaultHandle = default(SystemHandle);
                var pGroup = _simWorld.GetExistingSystemManaged<PhysicsSystemGroup>();
                var fGroup = _simWorld.GetExistingSystemManaged<FixedStepSimulationSystemGroup>();
                var tGroup = _simWorld.GetExistingSystemManaged<TransformSystemGroup>();
                var seek    = _simWorld.GetExistingSystem<EnemySeekSystem>();
                var syncIn  = _simWorld.GetExistingSystem<SyncPos2DToLocalTransformSystem>();
                var syncOut = _simWorld.GetExistingSystem<SyncPhysicsToPos2DSystem>();
                Debug.Log(
                    $"[Diag] PhysicsGroup={(pGroup != null ? "OK" : "NULL")} " +
                    $"FixedStepGroup={(fGroup != null ? "OK" : "NULL")} " +
                    $"TransformGroup={(tGroup != null ? "OK" : "NULL")} " +
                    $"EnemySeek={(seek.Equals(defaultHandle) ? "NULL" : "OK")} " +
                    $"SyncIn={(syncIn.Equals(defaultHandle) ? "NULL" : "OK")} " +
                    $"SyncOut={(syncOut.Equals(defaultHandle) ? "NULL" : "OK")}");
            }

            // Initial world state: globals + RNG seed.
            SimBootstrap.CreateOrResetGlobals(_simWorld.EntityManager, Seed);
            
            var em = _simWorld.EntityManager;
            _worldClockQuery = em.CreateEntityQuery(ComponentType.ReadOnly<WorldClock>());
            _roundStateQuery = em.CreateEntityQuery(ComponentType.ReadWrite<RoundState>());
            _godModeQuery    = em.CreateEntityQuery(ComponentType.ReadWrite<GodModeFlag>());
            _enemyTagQuery   = em.CreateEntityQuery(ComponentType.ReadOnly<EnemyTag>());
            _playerTagQuery  = em.CreateEntityQuery(ComponentType.ReadOnly<PlayerTag>());
            _playerSlotQuery = em.CreateEntityQuery(ComponentType.ReadOnly<PlayerSlot>(), ComponentType.ReadOnly<PlayerTag>());

            SetGodMode(GodMode);

            _history = new WorldStateHistory(Constants.SnapshotHistoryTicks);

            _net = new QuicNetworkServer();
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
            SetGodMode(GodMode);
            SimBootstrap.CreateOrResetGlobals(_simWorld.EntityManager, Seed);
            
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
            if (_worldClockQuery.CalculateEntityCount() == 0) return 0;
            return _worldClockQuery.GetSingleton<WorldClock>().Tick;
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

            WriteStatsFileIfDue();
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

                _simElapsedTime += Constants.SimDt;
                _simWorld.SetTime(new Unity.Core.TimeData(_simElapsedTime, Constants.SimDt));

                _tickStopwatch.Restart();
                _simGroup.Update();
                _tickStopwatch.Stop();

                _heartbeatTickMsSum += _tickStopwatch.Elapsed.TotalMilliseconds;
                _heartbeatTickCount++;
                _statsInnerTicks++;

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
            if (_godModeQuery.CalculateEntityCount() == 0) return;
            _godModeQuery.SetSingleton(new GodModeFlag { Value = (byte)(on ? 1 : 0) });
        }

        // ---------- NGO callbacks ----------

        private void OnClientConnected(byte playerId)
        {
            float angle = (playerId / (float)Constants.MaxPlayers) * math.PI * 2f;
            var pos = new float2(math.cos(angle), math.sin(angle)) * (Constants.ArenaRadius * 0.3f);

            var em = _simWorld.EntityManager;

            var collider = PhysicsColliders.Player();
            var pmass = PhysicsMass.CreateKinematic(MassProperties.UnitSphere);

            var e = em.CreateEntity(
                typeof(PlayerTag), typeof(PlayerSlot), typeof(Position2D), typeof(Aim2D),
                typeof(FireCooldown), typeof(DisableTimer), typeof(Alive),
                typeof(LocalTransform), typeof(LocalToWorld), typeof(PhysicsCollider), typeof(PhysicsVelocity),
                typeof(PhysicsMass), typeof(PhysicsDamping), typeof(PhysicsGravityFactor), typeof(PhysicsWorldIndex));
            em.SetComponentData(e, new PlayerSlot { Value = playerId });
            em.SetComponentData(e, new Position2D { Value = pos });
            em.SetComponentData(e, new Aim2D { Value = new float2(1f, 0f) });
            em.SetComponentData(e, new FireCooldown { Seconds = 0f });
            em.SetComponentData(e, new DisableTimer { Seconds = 0f });
            em.SetComponentData(e, Alive.From(true));
            em.SetComponentData(e, LocalTransform.FromPosition(new float3(pos.x, pos.y, 0f)));
            em.SetComponentData(e, new PhysicsCollider { Value = collider });
            em.SetComponentData(e, new PhysicsVelocity { Linear = float3.zero, Angular = float3.zero });
            em.SetComponentData(e, pmass);
            em.SetComponentData(e, new PhysicsDamping { Linear = Constants.PlayerLinearDamping, Angular = 0f });
            em.SetComponentData(e, new PhysicsGravityFactor { Value = 0f });
            em.SetName(e, $"Player {playerId}");

            Debug.Log($"[Server] Player {playerId} connected, spawned at ({pos.x:F2}, {pos.y:F2})");

            if (_state == ServerLifecycle.Idle)
            {
                if (AutoStartMatch) KickoffMatch();
                TransitionTo(ServerLifecycle.Running, $"first client (player {playerId}) connected");
            }
        }

        private void OnClientDisconnected(byte playerId, DisconnectReason reason)
        {
            var em = _simWorld.EntityManager;
            var ents = _playerSlotQuery.ToEntityArray(Unity.Collections.Allocator.Temp);
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

            if (_playerTagQuery.CalculateEntityCount() == 0 && _state == ServerLifecycle.Running)
            {
                TransitionTo(ServerLifecycle.Resetting, "last client disconnected");
            }
        }

        private void OnInputReceived(byte playerId, InputCmd cmd)
        {
            var em = _simWorld.EntityManager;
            if (_worldClockQuery.CalculateEntityCount() == 0) return;
            var globals = _worldClockQuery.GetSingletonEntity();
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
            if (_roundStateQuery.CalculateEntityCount() == 0) return;
            var rs = _roundStateQuery.GetSingleton<RoundState>();
            rs.Phase = RoundPhase.InterRound;
            rs.Round = 0;
            rs.InterRoundTimer = Constants.InterRoundDelaySec;
            rs.RoundTimer = 0f;
            _roundStateQuery.SetSingleton(rs);
        }

        // ---------- Heartbeat + telemetry ----------

        private void HeartbeatIfDue()
        {
            if (_roundStateQuery.CalculateEntityCount() == 0) return;
            var rs = _roundStateQuery.GetSingleton<RoundState>();
            if (rs.Phase != _prevPhase || rs.Round != _prevRound)
            {
                int alive = _enemyTagQuery.CalculateEntityCount();
                Debug.Log($"[Server] state: phase {_prevPhase}→{rs.Phase} round {_prevRound}→{rs.Round} " +
                          $"tick={GetCurrentTick()} enemies={alive} players={_playerTagQuery.CalculateEntityCount()}");
                _prevPhase = rs.Phase;
                _prevRound = rs.Round;
            }

            if (HeartbeatIntervalSec <= 0f) return;
            float wallNow = Time.unscaledTime;
            float wallElapsed = wallNow - _heartbeatLastWallTime;
            if (wallElapsed < HeartbeatIntervalSec) return;
            _heartbeatLastWallTime = wallNow;

            int aliveEnemies = _enemyTagQuery.CalculateEntityCount();
            int activePlayers = _playerTagQuery.CalculateEntityCount();

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

        // ---------- 20 Hz stats file ----------
        private void WriteStatsFileIfDue()
        {
            _statsOuterFrames++;
            float wallNow = Time.unscaledTime;
            float elapsed = wallNow - _statsWindowStartTime;
            if (elapsed < StatsIntervalSec) return;

            if (!_statsPrimed)
            {
                _statsPrimed = true;
                _statsWindowStartTime = wallNow;
                _statsOuterFrames = 0;
                _statsInnerTicks = 0;
                return;
            }

            int toSpawn = 0;
            int spawned = 0;
            if (_roundStateQuery.CalculateEntityCount() > 0)
            {
                var rs = _roundStateQuery.GetSingleton<RoundState>();
                toSpawn = rs.SpawnTarget;
                spawned = rs.SpawnTarget - rs.SpawnsRemaining;
            }
            int alive = _enemyTagQuery.CalculateEntityCount();
            float innerFps = _statsInnerTicks / elapsed;
            float outerFps = _statsOuterFrames / elapsed;
            double t = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;

            string json =
                $"{{\"t\":{t.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)}," +
                $"\"inner_fps\":{innerFps.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)}," +
                $"\"outer_fps\":{outerFps.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)}," +
                $"\"to_spawn\":{toSpawn}," +
                $"\"spawned\":{spawned}," +
                $"\"alive\":{alive}," +
                $"\"tick\":{GetCurrentTick()}," +
                $"\"state\":\"{_state}\"}}\n";

            try { System.IO.File.WriteAllText("/tmp/stats.log", json); }
            catch { /* /tmp/ may not exist (e.g. Windows dev) — silently skip */ }

            _statsWindowStartTime = wallNow;
            _statsOuterFrames = 0;
            _statsInnerTicks = 0;
        }
    }
}
#endif
