#if WITH_DGS
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using DGSvsHS.Gameplay;

namespace DGSvsHS.Server.Dots
{
    public static class SimBootstrap
    {
        public struct SimGlobalsTag : IComponentData { }

        public static Entity CreateOrResetGlobals(EntityManager em, ulong seed)
        {
            int cellsPerSide = Constants.GridHalfCells * 2;
            int cellCount = cellsPerSide * cellsPerSide;

            Entity globals;
            var existing = em.CreateEntityQuery(ComponentType.ReadOnly<SimGlobalsTag>());
            if (existing.CalculateEntityCount() > 0)
            {
                globals = existing.GetSingletonEntity();
                em.GetBuffer<TickInput>(globals).Clear();
                em.GetBuffer<PendingFire>(globals).Clear();
                em.GetBuffer<FireEventBuf>(globals).Clear();
                var meta = em.GetComponentData<RewindRingMeta>(globals);
                meta.Head = 0;
                meta.Count = 0;
                em.SetComponentData(globals, meta);
                var headers = em.GetBuffer<RewindFrameHeader>(globals);
                for (int i = 0; i < headers.Length; i++) headers[i] = new RewindFrameHeader { Tick = 0, Count = 0 };
                var resetOffsets = em.GetBuffer<RewindCellOffset>(globals);
                for (int i = 0; i < resetOffsets.Length; i++) resetOffsets[i] = new RewindCellOffset { Value = 0 };
            }
            else
            {
                var arch = em.CreateArchetype(
                    typeof(SimGlobalsTag),
                    typeof(WorldClock),
                    typeof(RoundState),
                    typeof(SimRng),
                    typeof(NextEnemyId),
                    typeof(TickInput),
                    typeof(PendingFire),
                    typeof(FireEventBuf),
                    typeof(RewindRingMeta),
                    typeof(RewindFrameHeader),
                    typeof(RewindId),
                    typeof(RewindPos),
                    typeof(RewindCellOffset),
                    typeof(GodModeFlag));
                globals = em.CreateEntity(arch);
                em.SetName(globals, "SimGlobals");

                int stride = Constants.MaxEnemies;
                int slots = Constants.SnapshotHistoryTicks;
                em.SetComponentData(globals, new RewindRingMeta
                {
                    Head = 0,
                    Count = 0,
                    Stride = stride,
                    CellsPerSide = cellsPerSide,
                    CellCount = cellCount,
                });
                var headers = em.GetBuffer<RewindFrameHeader>(globals);
                headers.ResizeUninitialized(slots);
                for (int i = 0; i < slots; i++) headers[i] = new RewindFrameHeader { Tick = 0, Count = 0 };
                em.GetBuffer<RewindId>(globals).ResizeUninitialized(slots * stride);
                em.GetBuffer<RewindPos>(globals).ResizeUninitialized(slots * stride);
                // (CellCount + 1) so a sentinel slot exists per cell, letting the
                // resolver read the cell's [start, end) without a branch.
                var offsets = em.GetBuffer<RewindCellOffset>(globals);
                offsets.ResizeUninitialized(slots * (cellCount + 1));
                for (int i = 0; i < offsets.Length; i++) offsets[i] = new RewindCellOffset { Value = 0 };
            }

            em.SetComponentData(globals, new WorldClock { Tick = 0 });
            em.SetComponentData(globals, new RoundState
            {
                Round = 0,
                Phase = RoundPhase.PreGame,
                RoundTimer = 0f,
                InterRoundTimer = 0f,
                SpawnTarget = 0,
                SpawnsRemaining = 0,
                SpawnInterval = 0f,
                SpawnAccumulator = 0f,
            });
            em.SetComponentData(globals, new SimRng { Rng = DeterministicRng.FromSeed(seed) });
            em.SetComponentData(globals, new NextEnemyId { Value = 0 });

            return globals;
        }
        
        public static void DestroyAllEnemies(EntityManager em)
        {
            var q = em.CreateEntityQuery(ComponentType.ReadOnly<EnemyTag>());
            em.DestroyEntity(q);
        }
        
        public static int CountEnemies(EntityManager em)
        {
            var q = em.CreateEntityQuery(ComponentType.ReadOnly<EnemyTag>());
            return q.CalculateEntityCount();
        }
    }
}
#endif
