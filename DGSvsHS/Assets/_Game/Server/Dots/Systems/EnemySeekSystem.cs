#if WITH_DGS
using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using DGSvsHS.Gameplay;

namespace DGSvsHS.Server.Dots
{
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(PlayerInputSystem))]
    public partial struct EnemySeekSystem : ISystem
    {
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<EnemyTag>();
        }

        public void OnUpdate(ref SystemState state)
        {
            var playerTargets = new NativeList<float2>(Constants.MaxPlayers, Allocator.TempJob);
            foreach (var (pos, alive, dt) in
                     SystemAPI.Query<RefRO<Position2D>, RefRO<Alive>, RefRO<DisableTimer>>()
                              .WithAll<PlayerTag>())
            {
                if (!alive.ValueRO.Bool) continue;
                if (dt.ValueRO.Seconds > 0f) continue;
                playerTargets.Add(pos.ValueRO.Value);
            }

            new SeekJob
            {
                PlayerTargets = playerTargets.AsArray(),
                EnemySpeed = Constants.EnemySpeed,
            }.ScheduleParallel();

            state.Dependency = playerTargets.Dispose(state.Dependency);
        }

        [BurstCompile]
        [WithAll(typeof(EnemyTag))]
        private partial struct SeekJob : IJobEntity
        {
            [ReadOnly] public NativeArray<float2> PlayerTargets;
            public float EnemySpeed;

            public void Execute(in Position2D pos, ref Velocity2D vel)
            {
                if (PlayerTargets.Length == 0) { vel.Value = float2.zero; return; }
                
                float bestSq = float.MaxValue;
                float2 best = PlayerTargets[0];
                for (int i = 0; i < PlayerTargets.Length; i++)
                {
                    float2 d = PlayerTargets[i] - pos.Value;
                    float sq = math.lengthsq(d);
                    if (sq < bestSq) { bestSq = sq; best = PlayerTargets[i]; }
                }

                float2 dir = best - pos.Value;
                float len = math.sqrt(bestSq);
                if (len > 0.0001f) dir /= len;
                else dir = float2.zero;

                vel.Value = dir * EnemySpeed;
            }
        }
    }
}
#endif
