#if WITH_DGS
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;
using DGSvsHS.Gameplay;

namespace DGSvsHS.Server.Dots
{
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(RewindRecordSystem))]
    public partial class SnapshotCaptureSystem : SystemBase
    {
        public Snapshot Target;

        protected override void OnCreate()
        {
            RequireForUpdate<WorldClock>();
        }

        protected override void OnUpdate()
        {
            if (Target == null) return;
            var s = Target;
            var globals = SystemAPI.GetSingletonEntity<WorldClock>();
            var clock = SystemAPI.GetSingleton<WorldClock>();
            var round = SystemAPI.GetSingleton<RoundState>();

            s.Clear();
            s.Kind = SnapshotKind.Full;     // NgoNetworkServer composes per-recipient delta from this baseline
            s.Tick = clock.Tick;
            s.LastProcessedInputTick = 0;   // per-recipient; set by NgoNetworkServer.ComposeAndSend
            s.Round = round.Round;
            s.RoundTimer = round.RoundTimer;
            s.InterRoundTimer = round.InterRoundTimer;
            s.Phase = round.Phase;

            // Players.
            foreach (var (slot, pos, aim, cd, dt, alive) in
                     SystemAPI.Query<RefRO<PlayerSlot>, RefRO<Position2D>, RefRO<Aim2D>,
                                     RefRO<FireCooldown>, RefRO<DisableTimer>, RefRO<Alive>>()
                              .WithAll<PlayerTag>())
            {
                s.Players.Add(new PlayerSnap
                {
                    Id = slot.ValueRO.Value,
                    Position = new Vector2(pos.ValueRO.Value.x, pos.ValueRO.Value.y),
                    Aim = new Vector2(aim.ValueRO.Value.x, aim.ValueRO.Value.y),
                    Alive = alive.ValueRO.Bool,
                    DisableTimer = dt.ValueRO.Seconds,
                });
            }

            // Enemies — every existing entity.
            uint enemyCount = 0;
            foreach (var (id, pos) in
                     SystemAPI.Query<RefRO<EnemyId>, RefRO<Position2D>>().WithAll<EnemyTag>())
            {
                s.Enemies.Add(new EnemySnap
                {
                    Id = id.ValueRO.Value,
                    Position = new Vector2(pos.ValueRO.Value.x, pos.ValueRO.Value.y),
                });
                enemyCount++;
            }
            s.EnemyTotalInWorld = enemyCount;

            // Fire events — drain the per-tick buffer.
            var fires = EntityManager.GetBuffer<FireEventBuf>(globals);
            for (int i = 0; i < fires.Length; i++) s.RecentFireEvents.Add(fires[i].ToWire());
        }
    }
}
#endif
