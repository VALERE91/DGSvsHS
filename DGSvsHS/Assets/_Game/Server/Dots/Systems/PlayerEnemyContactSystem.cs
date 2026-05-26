#if WITH_DGS
using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using DGSvsHS.Gameplay;

namespace DGSvsHS.Server.Dots
{
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(EnemyIntegrateSystem))]
    [UpdateBefore(typeof(RewindRecordSystem))]
    public partial struct PlayerEnemyContactSystem : ISystem
    {
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<EnemyTag>();
            state.RequireForUpdate<PlayerTag>();
        }

        public void OnUpdate(ref SystemState state)
        {
            var god = SystemAPI.GetSingleton<GodModeFlag>();
            if (god.On) return;
            
            int enemyCap = state.EntityManager.CreateEntityQuery(ComponentType.ReadOnly<EnemyTag>()).CalculateEntityCount();
            var enemyPos = new NativeList<float2>(enemyCap, Allocator.TempJob);
            foreach (var pos in SystemAPI.Query<RefRO<Position2D>>().WithAll<EnemyTag>())
            {
                enemyPos.Add(pos.ValueRO.Value);
            }

            new ContactJob
            {
                EnemyPos = enemyPos.AsArray(),
                KillRadiusSq = (Constants.PlayerKillRadius + Constants.EnemyRadius) *
                               (Constants.PlayerKillRadius + Constants.EnemyRadius),
                DisableSeconds = Constants.DisableDurationSec,
            }.ScheduleParallel();

            state.Dependency = enemyPos.Dispose(state.Dependency);
        }

        [BurstCompile]
        private partial struct ContactJob : IJobEntity
        {
            [ReadOnly] public NativeArray<float2> EnemyPos;
            public float KillRadiusSq;
            public float DisableSeconds;

            public void Execute(in Position2D pos, ref DisableTimer dt, in Alive alive)
            {
                if (alive.Value == 0) return;
                if (dt.Seconds > 0f) return;

                float2 p = pos.Value;
                for (int i = 0; i < EnemyPos.Length; i++)
                {
                    float2 d = EnemyPos[i] - p;
                    if (math.lengthsq(d) <= KillRadiusSq)
                    {
                        dt.Seconds = DisableSeconds;
                        return;
                    }
                }
            }
        }
    }
}
#endif
