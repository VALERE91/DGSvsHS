#if WITH_DGS
using System.Collections.Generic;
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
    [UpdateAfter(typeof(PlayerInputSystem))]
    [UpdateBefore(typeof(EnemySeekSystem))]
    public partial class RewindResolveSystem : SystemBase
    {
        public float[] PlayerRttMs = new float[Constants.MaxPlayers];

        private EntityQuery _aliveEnemiesQuery;

        protected override void OnCreate()
        {
            RequireForUpdate<WorldClock>();
            _aliveEnemiesQuery = GetEntityQuery(ComponentType.ReadOnly<EnemyTag>());
            for (int i = 0; i < PlayerRttMs.Length; i++) PlayerRttMs[i] = 60f;
        }

        protected override void OnUpdate()
        {
            var globals = SystemAPI.GetSingletonEntity<WorldClock>();
            var clock = SystemAPI.GetSingleton<WorldClock>();
            var pendingFires = EntityManager.GetBuffer<PendingFire>(globals);
            if (pendingFires.Length == 0) return;

            var meta = EntityManager.GetComponentData<RewindRingMeta>(globals);
            var headers = EntityManager.GetBuffer<RewindFrameHeader>(globals);
            var ids = EntityManager.GetBuffer<RewindId>(globals);
            var positions = EntityManager.GetBuffer<RewindPos>(globals);
            var offsets = EntityManager.GetBuffer<RewindCellOffset>(globals);

            int aliveCount = _aliveEnemiesQuery.CalculateEntityCount();
            var aliveIds = new NativeArray<ushort>(aliveCount, Allocator.TempJob);
            var aliveEntities = new NativeArray<Entity>(aliveCount, Allocator.TempJob);
            int writeIdx = 0;
            foreach (var (idC, ent) in SystemAPI.Query<RefRO<EnemyId>>().WithAll<EnemyTag>().WithEntityAccess())
            {
                aliveIds[writeIdx] = idC.ValueRO.Value;
                aliveEntities[writeIdx] = ent;
                writeIdx++;
            }

            // Aggregate "claimed" kills across all fires this tick (first-claim-wins,
            // matches Bevy `kill_owner` semantics + DOTS shared KillFlags).
            var idToIndex = new NativeParallelHashMap<ushort, int>(aliveCount, Allocator.TempJob);
            for (int i = 0; i < writeIdx; i++) idToIndex.Add(aliveIds[i], i);

            var killFlags = new NativeArray<byte>(writeIdx, Allocator.TempJob);

            var rttCopy = new NativeArray<float>(Constants.MaxPlayers, Allocator.TempJob);
            for (int i = 0; i < Constants.MaxPlayers; i++) rttCopy[i] = PlayerRttMs[i];

            var newFires = new NativeList<FireEventBuf>(pendingFires.Length, Allocator.TempJob);
            var pendingCopy = new NativeArray<PendingFire>(pendingFires.Length, Allocator.TempJob);
            for (int i = 0; i < pendingFires.Length; i++) pendingCopy[i] = pendingFires[i];

            // by_id maps are reused across every fire — allocate them here so
            // Burst doesn't have to. Sized for the current live-enemy population
            // (worst case for a bracket slot once the ring is settled).
            var floorById = new NativeParallelHashMap<ushort, float2>(aliveCount, Allocator.TempJob);
            var ceilById = new NativeParallelHashMap<ushort, float2>(aliveCount, Allocator.TempJob);

            new ResolveJob
            {
                CurrentTick = clock.Tick,
                Pending = pendingCopy,
                Headers = headers.AsNativeArray(),
                Ids = ids.AsNativeArray(),
                Positions = positions.AsNativeArray(),
                Offsets = offsets.AsNativeArray(),
                RingHead = meta.Head,
                RingCount = meta.Count,
                RingStride = meta.Stride,
                CellCount = meta.CellCount,
                CellsPerSide = meta.CellsPerSide,
                HalfCells = Constants.GridHalfCells,
                CellSize = Constants.GridCellSize,
                IdToIndex = idToIndex,
                Rtt = rttCopy,
                KillFlags = killFlags,
                NewFires = newFires,
                FloorById = floorById,
                CeilById = ceilById,
            }.Run();

            var ecb = new EntityCommandBuffer(Allocator.Temp);
            for (int i = 0; i < writeIdx; i++)
            {
                if (killFlags[i] != 0)
                    ecb.DestroyEntity(aliveEntities[i]);
            }
            ecb.Playback(EntityManager);
            ecb.Dispose();

            var fireBuf = EntityManager.GetBuffer<FireEventBuf>(globals);
            for (int i = 0; i < newFires.Length; i++) fireBuf.Add(newFires[i]);

            pendingFires.Clear();

            aliveIds.Dispose();
            aliveEntities.Dispose();
            idToIndex.Dispose();
            killFlags.Dispose();
            rttCopy.Dispose();
            newFires.Dispose();
            pendingCopy.Dispose();
            floorById.Dispose();
            ceilById.Dispose();
        }

        [BurstCompile]
        private struct ResolveJob : IJob
        {
            public uint CurrentTick;
            [ReadOnly] public NativeArray<PendingFire> Pending;
            [ReadOnly] public NativeArray<RewindFrameHeader> Headers;
            [ReadOnly] public NativeArray<RewindId> Ids;
            [ReadOnly] public NativeArray<RewindPos> Positions;
            [ReadOnly] public NativeArray<RewindCellOffset> Offsets;

            public int RingHead;
            public int RingCount;
            public int RingStride;
            public int CellCount;
            public int CellsPerSide;
            public int HalfCells;
            public float CellSize;

            [ReadOnly] public NativeParallelHashMap<ushort, int> IdToIndex;
            [ReadOnly] public NativeArray<float> Rtt;
            [NativeDisableParallelForRestriction] public NativeArray<byte> KillFlags;
            public NativeList<FireEventBuf> NewFires;
            public NativeParallelHashMap<ushort, float2> FloorById;
            public NativeParallelHashMap<ushort, float2> CeilById;

            public void Execute()
            {
                float hitRadius = Constants.EnemyRadius + Constants.BeamRadius;
                float hitRadiusSq = hitRadius * hitRadius;
                float maxRange = Constants.BulletMaxRange;
                // Beam AABB is expanded by hit_r AND one cell — bracketing-lerp
                // can shift an enemy across a cell boundary, mirrors Bevy.
                float queryR = hitRadius + CellSize;

                // by_id cross-frame lookup, reused across fires that share the
                // same bracketing slots. Maps are owned by the caller (Burst
                // doesn't allow NativeParallelHashMap allocation inside jobs).
                int loadedFloorSlot = -1;
                int loadedCeilSlot = -1;

                for (int fi = 0; fi < Pending.Length; fi++)
                {
                    var f = Pending[fi];
                    float oneWayMs = (f.PlayerId < Rtt.Length ? Rtt[f.PlayerId] : 60f) * 0.5f;
                    float viewTickF = ComputeViewTickF(CurrentTick, oneWayMs);

                    if (!FindBracketingSlots(viewTickF, out int floorSlot, out int ceilSlot, out float alpha))
                        continue;

                    var floorHdr = Headers[floorSlot];
                    var ceilHdr  = Headers[ceilSlot];
                    int floorStart = floorSlot * RingStride;
                    int ceilStart  = ceilSlot  * RingStride;
                    int floorOffStart = floorSlot * (CellCount + 1);
                    int ceilOffStart  = ceilSlot  * (CellCount + 1);
                    bool sameSlot = floorSlot == ceilSlot;

                    // Repopulate by_id maps only when the slot identity changes.
                    if (floorSlot != loadedFloorSlot)
                    {
                        FloorById.Clear();
                        int n = floorHdr.Count;
                        for (int i = 0; i < n; i++)
                        {
                            FloorById.TryAdd(Ids[floorStart + i].Value, Positions[floorStart + i].Value);
                        }
                        loadedFloorSlot = floorSlot;
                    }
                    if (!sameSlot && ceilSlot != loadedCeilSlot)
                    {
                        CeilById.Clear();
                        int n = ceilHdr.Count;
                        for (int i = 0; i < n; i++)
                        {
                            CeilById.TryAdd(Ids[ceilStart + i].Value, Positions[ceilStart + i].Value);
                        }
                        loadedCeilSlot = ceilSlot;
                    }

                    // Cell range for the beam AABB ± queryR.
                    float2 origin = f.Origin;
                    float2 end = origin + f.Direction * maxRange;
                    if (!SegmentCellRange(origin.x, origin.y, end.x, end.y, queryR,
                                          out int cxmin, out int cymin, out int cxmax, out int cymax))
                    {
                        continue;
                    }

                    int kills = 0;

                    // Floor pass: lerp each candidate with its ceil match.
                    for (int cy = cymin; cy <= cymax; cy++)
                    {
                        for (int cx = cxmin; cx <= cxmax; cx++)
                        {
                            int cellIdx = cy * CellsPerSide + cx;
                            int rangeStart = Offsets[floorOffStart + cellIdx].Value;
                            int rangeEnd   = Offsets[floorOffStart + cellIdx + 1].Value;
                            for (int k = rangeStart; k < rangeEnd; k++)
                            {
                                ushort id = Ids[floorStart + k].Value;
                                float2 fPos = Positions[floorStart + k].Value;
                                float2 pos = fPos;
                                if (!sameSlot && CeilById.TryGetValue(id, out float2 cPos))
                                {
                                    pos = math.lerp(fPos, cPos, alpha);
                                }
                                if (SegmentHits(f.Origin, f.Direction, maxRange, pos, hitRadiusSq))
                                {
                                    if (IdToIndex.TryGetValue(id, out int aliveIdx))
                                    {
                                        if (KillFlags[aliveIdx] == 0)
                                        {
                                            KillFlags[aliveIdx] = 1;
                                            kills++;
                                        }
                                    }
                                }
                            }
                        }
                    }

                    // Ceil-only pass: include enemies spawned mid-bracket if alpha ≥ 0.5.
                    if (alpha >= 0.5f && !sameSlot)
                    {
                        for (int cy = cymin; cy <= cymax; cy++)
                        {
                            for (int cx = cxmin; cx <= cxmax; cx++)
                            {
                                int cellIdx = cy * CellsPerSide + cx;
                                int rangeStart = Offsets[ceilOffStart + cellIdx].Value;
                                int rangeEnd   = Offsets[ceilOffStart + cellIdx + 1].Value;
                                for (int k = rangeStart; k < rangeEnd; k++)
                                {
                                    ushort id = Ids[ceilStart + k].Value;
                                    if (FloorById.ContainsKey(id)) continue;
                                    float2 cPos = Positions[ceilStart + k].Value;
                                    if (SegmentHits(f.Origin, f.Direction, maxRange, cPos, hitRadiusSq))
                                    {
                                        if (IdToIndex.TryGetValue(id, out int aliveIdx))
                                        {
                                            if (KillFlags[aliveIdx] == 0)
                                            {
                                                KillFlags[aliveIdx] = 1;
                                                kills++;
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }

                    NewFires.Add(new FireEventBuf
                    {
                        Tick = CurrentTick,
                        ShooterId = f.PlayerId,
                        Origin = f.Origin,
                        Direction = f.Direction,
                        Distance = maxRange,
                        KillCount = (byte)math.min(255, kills),
                    });
                }

            }

            private bool FindBracketingSlots(float viewTickF, out int floorSlot, out int ceilSlot, out float alpha)
            {
                floorSlot = -1; ceilSlot = -1; alpha = 0f;
                if (RingCount == 0) return false;

                uint viewFloor = (uint)math.floor(viewTickF);
                uint viewCeil = viewFloor + 1;

                for (int i = 0; i < RingCount; i++)
                {
                    int slot = (RingHead - 1 - i + Headers.Length) % Headers.Length;
                    var hdr = Headers[slot];
                    if (hdr.Tick == viewFloor) floorSlot = slot;
                    if (hdr.Tick == viewCeil) ceilSlot = slot;
                }
                if (floorSlot < 0)
                {
                    int oldest = (RingHead - RingCount + Headers.Length) % Headers.Length;
                    floorSlot = oldest;
                    ceilSlot = oldest;
                    alpha = 0f;
                    return true;
                }
                if (ceilSlot < 0)
                {
                    ceilSlot = floorSlot;
                    alpha = 0f;
                    return true;
                }
                alpha = math.saturate(viewTickF - viewFloor);
                return true;
            }

            private bool SegmentCellRange(float ax, float ay, float bx, float by, float radius,
                                          out int cxmin, out int cymin, out int cxmax, out int cymax)
            {
                cxmin = cymin = cxmax = cymax = 0;
                float halfWorld = HalfCells * CellSize;
                float xmin = math.max(math.min(ax, bx) - radius, -halfWorld);
                float xmax = math.min(math.max(ax, bx) + radius, halfWorld - 0.001f);
                float ymin = math.max(math.min(ay, by) - radius, -halfWorld);
                float ymax = math.min(math.max(ay, by) + radius, halfWorld - 0.001f);
                if (xmin > xmax || ymin > ymax) return false;
                if (!CellCoords(xmin, ymin, out cxmin, out cymin)) return false;
                if (!CellCoords(xmax, ymax, out cxmax, out cymax)) return false;
                return true;
            }

            private bool CellCoords(float x, float y, out int cx, out int cy)
            {
                cx = (int)math.floor(x / CellSize) + HalfCells;
                cy = (int)math.floor(y / CellSize) + HalfCells;
                if (cx < 0 || cy < 0 || cx >= CellsPerSide || cy >= CellsPerSide) return false;
                return true;
            }

            private static float ComputeViewTickF(uint serverTick, float oneWayLatencyMs)
            {
                return (float)serverTick
                       - (oneWayLatencyMs / 1000f) * Constants.TicksPerSecond
                       - (Constants.InterpolationBufferMs / 1000f) * Constants.TicksPerSecond;
            }

            private static bool SegmentHits(float2 origin, float2 dir, float maxRange, float2 enemyPos, float hitRadiusSq)
            {
                float2 toEnemy = enemyPos - origin;
                float t = math.dot(toEnemy, dir);
                if (t < 0f || t > maxRange) return false;
                float2 closest = origin + dir * t;
                return math.lengthsq(enemyPos - closest) <= hitRadiusSq;
            }
        }
    }
}
#endif
