#if WITH_DGS
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using DGSvsHS.Gameplay;

namespace DGSvsHS.Server.Dots
{
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    public partial struct RewindRecordSystem : ISystem
    {
        private EntityQuery _enemiesQuery;

        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<RewindRingMeta>();
            _enemiesQuery = state.GetEntityQuery(
                ComponentType.ReadOnly<EnemyTag>(),
                ComponentType.ReadOnly<EnemyId>(),
                ComponentType.ReadOnly<Position2D>());
        }

        public void OnUpdate(ref SystemState state)
        {
            var globals = SystemAPI.GetSingletonEntity<RewindRingMeta>();
            var clock = SystemAPI.GetSingleton<WorldClock>();
            var meta = SystemAPI.GetSingletonRW<RewindRingMeta>();
            var headers = state.EntityManager.GetBuffer<RewindFrameHeader>(globals);
            var ids = state.EntityManager.GetBuffer<RewindId>(globals);
            var positions = state.EntityManager.GetBuffer<RewindPos>(globals);
            var offsets = state.EntityManager.GetBuffer<RewindCellOffset>(globals);

            int slot = meta.ValueRO.Head;
            int stride = meta.ValueRO.Stride;
            int slotStart = slot * stride;
            int cellCount = meta.ValueRO.CellCount;
            int cellsPerSide = meta.ValueRO.CellsPerSide;
            int offsetStart = slot * (cellCount + 1);

            // Pass 1: extract live enemies into temp arrays. Capped at stride —
            // overflow drops the tail, same semantics as the old flat-write path.
            int enemyCount = _enemiesQuery.CalculateEntityCount();
            int cap = math.min(enemyCount, stride);
            var tempIds = new NativeArray<ushort>(cap, Allocator.TempJob);
            var tempPos = new NativeArray<float2>(cap, Allocator.TempJob);
            int count = 0;
            foreach (var (id, pos) in SystemAPI.Query<RefRO<EnemyId>, RefRO<Position2D>>().WithAll<EnemyTag>())
            {
                if (count >= stride) break;
                tempIds[count] = id.ValueRO.Value;
                tempPos[count] = pos.ValueRO.Value;
                count++;
            }

            new CountSortJob
            {
                InIds = tempIds,
                InPos = tempPos,
                EnemyCount = count,
                OutIds = ids.AsNativeArray(),
                OutPos = positions.AsNativeArray(),
                OutOffsets = offsets.AsNativeArray(),
                SlotStart = slotStart,
                OffsetStart = offsetStart,
                CellsPerSide = cellsPerSide,
                CellCount = cellCount,
                HalfCells = Constants.GridHalfCells,
                CellSize = Constants.GridCellSize,
            }.Run();

            tempIds.Dispose();
            tempPos.Dispose();

            // `Count` is the number of enemies actually bucketed (out-of-grid
            // enemies are dropped by the count-sort, matching Bevy GridGeom).
            int placed = offsets[offsetStart + cellCount].Value;
            headers[slot] = new RewindFrameHeader { Tick = clock.Tick, Count = placed };

            meta.ValueRW.Head = (slot + 1) % headers.Length;
            if (meta.ValueRO.Count < headers.Length) meta.ValueRW.Count = meta.ValueRO.Count + 1;
        }

        [BurstCompile]
        private struct CountSortJob : IJob
        {
            [ReadOnly] public NativeArray<ushort> InIds;
            [ReadOnly] public NativeArray<float2> InPos;
            public int EnemyCount;

            [NativeDisableContainerSafetyRestriction] public NativeArray<RewindId> OutIds;
            [NativeDisableContainerSafetyRestriction] public NativeArray<RewindPos> OutPos;
            [NativeDisableContainerSafetyRestriction] public NativeArray<RewindCellOffset> OutOffsets;

            public int SlotStart;
            public int OffsetStart;
            public int CellsPerSide;
            public int CellCount;
            public int HalfCells;
            public float CellSize;

            public void Execute()
            {
                var cellOf = new NativeArray<int>(EnemyCount, Allocator.Temp);
                for (int i = 0; i < EnemyCount; i++)
                {
                    cellOf[i] = CellIdx(InPos[i].x, InPos[i].y);
                }

                // Clear the slot's [0..CellCount] offset window.
                for (int c = 0; c <= CellCount; c++)
                {
                    OutOffsets[OffsetStart + c] = new RewindCellOffset { Value = 0 };
                }
                // Count per cell (skip out-of-grid).
                for (int i = 0; i < EnemyCount; i++)
                {
                    int c = cellOf[i];
                    if (c >= 0)
                    {
                        var o = OutOffsets[OffsetStart + c];
                        o.Value++;
                        OutOffsets[OffsetStart + c] = o;
                    }
                }
                // Exclusive prefix sum into the same window; Offset[CellCount]
                // ends up as the total placed count (used as Header.Count).
                int running = 0;
                for (int c = 0; c <= CellCount; c++)
                {
                    int x = OutOffsets[OffsetStart + c].Value;
                    OutOffsets[OffsetStart + c] = new RewindCellOffset { Value = running };
                    running += x;
                }

                // Per-cell rolling write cursor — restored to the canonical
                // offsets at the end so the resolver reads valid CSR ranges.
                var writeCursor = new NativeArray<int>(CellCount, Allocator.Temp);
                for (int c = 0; c < CellCount; c++)
                {
                    writeCursor[c] = OutOffsets[OffsetStart + c].Value;
                }
                for (int i = 0; i < EnemyCount; i++)
                {
                    int c = cellOf[i];
                    if (c < 0) continue;
                    int dst = writeCursor[c]++;
                    OutIds[SlotStart + dst] = new RewindId { Value = InIds[i] };
                    OutPos[SlotStart + dst] = new RewindPos { Value = InPos[i] };
                }

                cellOf.Dispose();
                writeCursor.Dispose();
            }

            private int CellIdx(float x, float y)
            {
                int cx = (int)math.floor(x / CellSize) + HalfCells;
                int cy = (int)math.floor(y / CellSize) + HalfCells;
                if (cx < 0 || cy < 0 || cx >= CellsPerSide || cy >= CellsPerSide) return -1;
                return cy * CellsPerSide + cx;
            }
        }
    }
}
#endif
