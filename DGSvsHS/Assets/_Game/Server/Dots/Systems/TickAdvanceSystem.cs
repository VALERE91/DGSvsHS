#if WITH_DGS
using Unity.Entities;

namespace DGSvsHS.Server.Dots
{
    [UpdateInGroup(typeof(SimulationSystemGroup), OrderFirst = true)]
    [UpdateBefore(typeof(RoundDirectorSystem))]
    public partial struct TickAdvanceSystem : ISystem
    {
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<WorldClock>();
        }

        public void OnUpdate(ref SystemState state)
        {
            var clock = SystemAPI.GetSingletonRW<WorldClock>();
            clock.ValueRW.Tick++;

            // Drop last tick's fire events.
            var globals = SystemAPI.GetSingletonEntity<WorldClock>();
            state.EntityManager.GetBuffer<FireEventBuf>(globals).Clear();
        }
    }
}
#endif
