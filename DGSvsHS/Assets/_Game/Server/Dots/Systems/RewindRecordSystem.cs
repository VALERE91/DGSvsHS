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
    public partial struct RewindRecordSystem : ISystem
    {
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<RewindRingMeta>();
        }

        public void OnUpdate(ref SystemState state)
        {
            var globals = SystemAPI.GetSingletonEntity<RewindRingMeta>();
            var clock = SystemAPI.GetSingleton<WorldClock>();
            var meta = SystemAPI.GetSingletonRW<RewindRingMeta>();
            var headers = state.EntityManager.GetBuffer<RewindFrameHeader>(globals);
            var ids = state.EntityManager.GetBuffer<RewindId>(globals);
            var positions = state.EntityManager.GetBuffer<RewindPos>(globals);

            int slot = meta.ValueRO.Head;
            int slotStart = slot * meta.ValueRO.Stride;
            int stride = meta.ValueRO.Stride;

            int count = 0;
            foreach (var (id, pos) in SystemAPI.Query<RefRO<EnemyId>, RefRO<Position2D>>().WithAll<EnemyTag>())
            {
                if (count >= stride) break;
                ids[slotStart + count] = new RewindId { Value = id.ValueRO.Value };
                positions[slotStart + count] = new RewindPos { Value = pos.ValueRO.Value };
                count++;
            }

            headers[slot] = new RewindFrameHeader { Tick = clock.Tick, Count = count };
            
            meta.ValueRW.Head = (slot + 1) % headers.Length;
            if (meta.ValueRO.Count < headers.Length) meta.ValueRW.Count = meta.ValueRO.Count + 1;
        }
    }
}
#endif
