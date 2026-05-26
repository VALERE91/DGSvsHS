#if WITH_DGS
using System.Collections.Generic;
using Unity.Burst;
using Unity.Collections;
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

            // Map: enemyId → indexInAliveArrays. NativeParallelHashMap for fast lookup in the resolver.
            var idToIndex = new NativeParallelHashMap<ushort, int>(aliveCount, Allocator.TempJob);
            for (int i = 0; i < writeIdx; i++) idToIndex.Add(aliveIds[i], i);
            
            var killFlags = new NativeArray<byte>(writeIdx, Allocator.TempJob);

            // Pull RTT array into Native form for Burst.
            var rttCopy = new NativeArray<float>(Constants.MaxPlayers, Allocator.TempJob);
            for (int i = 0; i < Constants.MaxPlayers; i++) rttCopy[i] = PlayerRttMs[i];

            // Output buffer for new FireEvent entries
            var newFires = new NativeList<FireEventBuf>(pendingFires.Length, Allocator.TempJob);
            var pendingCopy = new NativeArray<PendingFire>(pendingFires.Length, Allocator.TempJob);
            for (int i = 0; i < pendingFires.Length; i++) pendingCopy[i] = pendingFires[i];

            new ResolveJob
            {
                CurrentTick = clock.Tick,
                Pending = pendingCopy,
                Headers = headers.AsNativeArray(),
                Ids = ids.AsNativeArray(),
                Positions = positions.AsNativeArray(),
                RingHead = meta.Head,
                RingCount = meta.Count,
                RingStride = meta.Stride,
                IdToIndex = idToIndex,
                Rtt = rttCopy,
                KillFlags = killFlags,
                NewFires = newFires,
            }.Run();
            
            var ecb = new EntityCommandBuffer(Allocator.Temp);
            for (int i = 0; i < writeIdx; i++)
            {
                if (killFlags[i] != 0)
                    ecb.DestroyEntity(aliveEntities[i]);
            }
            ecb.Playback(EntityManager);
            ecb.Dispose();

            // Append new fire events to the persistent buffer for snapshot capture.
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
        }

        [BurstCompile]
        private struct ResolveJob : IJob
        {
            public uint CurrentTick;
            [ReadOnly] public NativeArray<PendingFire> Pending;
            [ReadOnly] public NativeArray<RewindFrameHeader> Headers;
            [ReadOnly] public NativeArray<RewindId> Ids;
            [ReadOnly] public NativeArray<RewindPos> Positions;
            public int RingHead;
            public int RingCount;
            public int RingStride;
            [ReadOnly] public NativeParallelHashMap<ushort, int> IdToIndex;
            [ReadOnly] public NativeArray<float> Rtt;
            [NativeDisableParallelForRestriction] public NativeArray<byte> KillFlags;
            public NativeList<FireEventBuf> NewFires;

            public void Execute()
            {
                float hitRadius = Constants.EnemyRadius + Constants.BeamRadius;
                float hitRadiusSq = hitRadius * hitRadius;
                float maxRange = Constants.BulletMaxRange;

                for (int fi = 0; fi < Pending.Length; fi++)
                {
                    var f = Pending[fi];
                    float oneWayMs = (f.PlayerId < Rtt.Length ? Rtt[f.PlayerId] : 60f) * 0.5f;
                    float viewTickF = ComputeViewTickF(CurrentTick, oneWayMs);

                    // Find bracketing ring slots.
                    if (!FindBracketingSlots(viewTickF, out int floorSlot, out int ceilSlot, out float alpha))
                        continue;

                    var floorHdr = Headers[floorSlot];
                    var ceilHdr = Headers[ceilSlot];

                    // Iterate floor slot's enemies; lerp position with ceil entry if same id present.
                    // No spatial grid — brute-force seg-circle. Piercing: collect all hits.
                    int kills = 0;
                    int floorStart = floorSlot * RingStride;
                    int ceilStart = ceilSlot * RingStride;

                    for (int i = 0; i < floorHdr.Count; i++)
                    {
                        ushort id = Ids[floorStart + i].Value;
                        float2 fPos = Positions[floorStart + i].Value;
                        float2 pos = fPos;
                        // If id exists in ceil slot too, lerp.
                        for (int j = 0; j < ceilHdr.Count; j++)
                        {
                            if (Ids[ceilStart + j].Value != id) continue;
                            float2 cPos = Positions[ceilStart + j].Value;
                            pos = math.lerp(fPos, cPos, alpha);
                            break;
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

                    // Also include ceil-only enemies (spawned mid-bracket) if alpha ≥ 0.5.
                    if (alpha >= 0.5f)
                    {
                        for (int j = 0; j < ceilHdr.Count; j++)
                        {
                            ushort id = Ids[ceilStart + j].Value;
                            // Skip if it was in floor.
                            bool inFloor = false;
                            for (int i = 0; i < floorHdr.Count; i++)
                            {
                                if (Ids[floorStart + i].Value == id) { inFloor = true; break; }
                            }
                            if (inFloor) continue;
                            float2 pos = Positions[ceilStart + j].Value;
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

                // Linear scan over filled slots — RingCount ≤ SnapshotHistoryTicks.
                for (int i = 0; i < RingCount; i++)
                {
                    int slot = (RingHead - 1 - i + Headers.Length) % Headers.Length;
                    var hdr = Headers[slot];
                    if (hdr.Tick == viewFloor) floorSlot = slot;
                    if (hdr.Tick == viewCeil) ceilSlot = slot;
                }
                if (floorSlot < 0)
                {
                    // Clamp to oldest if view-time precedes our buffer.
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
