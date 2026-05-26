#if WITH_DGS
using Unity.Burst;
using Unity.Entities;
using Unity.Mathematics;
using DGSvsHS.Gameplay;

namespace DGSvsHS.Server.Dots
{
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(EnemySeekSystem))]
    public partial struct EnemyIntegrateSystem : ISystem
    {
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<EnemyTag>();
        }

        public void OnUpdate(ref SystemState state)
        {
            new IntegrateJob { Dt = Constants.SimDt }.ScheduleParallel();
        }

        [BurstCompile]
        [WithAll(typeof(EnemyTag))]
        private partial struct IntegrateJob : IJobEntity
        {
            public float Dt;
            public void Execute(ref Position2D pos, in Velocity2D vel)
            {
                pos.Value += vel.Value * Dt;
            }
        }
    }
}
#endif
