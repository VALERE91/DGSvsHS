#if WITH_DGS
using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using DGSvsHS.Gameplay;

namespace DGSvsHS.Server.Dots
{
    [UpdateInGroup(typeof(SimulationSystemGroup), OrderFirst = true)]
    public partial struct RoundDirectorSystem : ISystem
    {
        private EntityQuery _activePlayersQuery;
        private EntityQuery _allPlayersQuery;
        private EntityQuery _enemiesQuery;

        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<RoundState>();
            state.RequireForUpdate<SimRng>();
            state.RequireForUpdate<NextEnemyId>();

            _activePlayersQuery = state.GetEntityQuery(ComponentType.ReadOnly<PlayerTag>(), ComponentType.ReadOnly<Alive>());
            _allPlayersQuery = state.GetEntityQuery(ComponentType.ReadOnly<PlayerTag>());
            _enemiesQuery = state.GetEntityQuery(ComponentType.ReadOnly<EnemyTag>());
        }

        public void OnUpdate(ref SystemState state)
        {
            var em = state.EntityManager;
            var globals = SystemAPI.GetSingletonEntity<RoundState>();
            var round = SystemAPI.GetSingletonRW<RoundState>();
            var rng = SystemAPI.GetSingletonRW<SimRng>();
            var nextId = SystemAPI.GetSingletonRW<NextEnemyId>();

            int activePlayerCount = _allPlayersQuery.CalculateEntityCount();
            if (activePlayerCount == 0 && round.ValueRO.Phase != RoundPhase.PreGame)
            {
                // No clients — back to idle.
                round.ValueRW.Phase = RoundPhase.PreGame;
                round.ValueRW.Round = 0;
                round.ValueRW.RoundTimer = 0f;
                round.ValueRW.InterRoundTimer = 0f;
                round.ValueRW.SpawnsRemaining = 0;
                round.ValueRW.SpawnTarget = 0;
                return;
            }

            switch (round.ValueRO.Phase)
            {
                case RoundPhase.PreGame:
                    break;

                case RoundPhase.InterRound:
                    round.ValueRW.InterRoundTimer -= Constants.SimDt;
                    if (round.ValueRW.InterRoundTimer <= 0f)
                    {
                        round.ValueRW.Round++;
                        if (round.ValueRW.Round > Constants.TotalRounds)
                        {
                            round.ValueRW.Phase = RoundPhase.Victory;
                        }
                        else
                        {
                            round.ValueRW.Phase = RoundPhase.InRound;
                            round.ValueRW.RoundTimer = 0f;
                            StartWave(ref round.ValueRW, round.ValueRO.Round);
                        }
                    }
                    break;

                case RoundPhase.InRound:
                    round.ValueRW.RoundTimer += Constants.SimDt;
                    TickWave(em, ref round.ValueRW, ref rng.ValueRW.Rng, ref nextId.ValueRW.Value);

                    if (AllConnectedPlayersDisabled(ref state))
                    {
                        ResetToRoundOne(em, ref state, ref round.ValueRW);
                    }
                    else if (round.ValueRO.SpawnsRemaining == 0 && _enemiesQuery.CalculateEntityCount() == 0)
                    {
                        round.ValueRW.Phase = RoundPhase.InterRound;
                        round.ValueRW.InterRoundTimer = Constants.InterRoundDelaySec;
                    }
                    break;

                case RoundPhase.Victory:
                case RoundPhase.Defeat:
                    // Terminal states — held until a client disconnect resets to PreGame.
                    break;
            }
        }

        private static void StartWave(ref RoundState round, int forRound)
        {
            int target = TargetEnemiesForRound(forRound);
            round.SpawnTarget = target;
            round.SpawnsRemaining = target;
            round.SpawnInterval = Constants.RoundSpawnWindowSec / math.max(1, target);
            round.SpawnAccumulator = 0f;
        }

        public static int TargetEnemiesForRound(int forRound)
        {
            if (forRound < 1) return 0;
            float scaled = Constants.BaseEnemiesPerRound * math.pow(Constants.EnemyScalingPerRound, forRound - 1);
            int t = (int)math.round(scaled);
            return math.min(Constants.MaxEnemies, t);
        }

        private void TickWave(EntityManager em, ref RoundState round, ref DeterministicRng rng, ref ushort nextId)
        {
            if (round.SpawnsRemaining <= 0) return;
            round.SpawnAccumulator += Constants.SimDt;
            while (round.SpawnAccumulator >= round.SpawnInterval && round.SpawnsRemaining > 0)
            {
                round.SpawnAccumulator -= round.SpawnInterval;
                SpawnOneEnemy(em, ref rng, ref nextId);
                round.SpawnsRemaining--;
            }
        }

        private static void SpawnOneEnemy(EntityManager em, ref DeterministicRng rng, ref ushort nextId)
        {
            float angle = rng.NextRange(0f, math.PI * 2f);
            float r = Constants.ArenaRadius - Constants.EnemyRadius - 0.1f;
            float2 pos = new float2(math.cos(angle) * r, math.sin(angle) * r);
            
            var e = em.CreateEntity(typeof(EnemyTag), typeof(EnemyId), typeof(Position2D), typeof(Velocity2D));
            em.SetComponentData(e, new EnemyId { Value = nextId++ });
            em.SetComponentData(e, new Position2D { Value = pos });
            em.SetComponentData(e, new Velocity2D { Value = float2.zero });
        }

        private bool AllConnectedPlayersDisabled(ref SystemState state)
        {
            int total = 0, disabled = 0;
            foreach (var (alive, dt) in
                     SystemAPI.Query<RefRO<Alive>, RefRO<DisableTimer>>().WithAll<PlayerTag>())
            {
                if (!alive.ValueRO.Bool) continue;
                total++;
                if (dt.ValueRO.Seconds > 0f) disabled++;
            }
            return total > 0 && disabled == total;
        }

        ///Team wipe — destroy all enemies, clear spawn state, queue a fresh round 1 via InterRound.
        private void ResetToRoundOne(EntityManager em, ref SystemState state, ref RoundState round)
        {
            round.Round = 0;
            round.Phase = RoundPhase.InterRound;
            round.InterRoundTimer = Constants.InterRoundDelaySec;
            round.RoundTimer = 0f;
            round.SpawnTarget = 0;
            round.SpawnsRemaining = 0;
            round.SpawnInterval = 0f;
            round.SpawnAccumulator = 0f;
            SimBootstrap.DestroyAllEnemies(em);

            // Re-enable every player: clear disable + cooldown, set Alive.
            foreach (var (alive, dt, cd) in
                     SystemAPI.Query<RefRW<Alive>, RefRW<DisableTimer>, RefRW<FireCooldown>>().WithAll<PlayerTag>())
            {
                alive.ValueRW = Alive.From(true);
                dt.ValueRW.Seconds = 0f;
                cd.ValueRW.Seconds = 0f;
            }
        }
    }
}
#endif
