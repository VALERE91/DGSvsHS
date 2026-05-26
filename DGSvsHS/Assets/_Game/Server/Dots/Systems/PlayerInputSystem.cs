#if WITH_DGS
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using DGSvsHS.Gameplay;

namespace DGSvsHS.Server.Dots
{
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(RoundDirectorSystem))]
    public partial struct PlayerInputSystem : ISystem
    {
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<WorldClock>();
        }

        public void OnUpdate(ref SystemState state)
        {
            var globals = SystemAPI.GetSingletonEntity<WorldClock>();
            var tickInputs = state.EntityManager.GetBuffer<TickInput>(globals);
            var pendingFires = state.EntityManager.GetBuffer<PendingFire>(globals);
            
            int max = Constants.MaxPlayers;
            var latestIdx = new NativeArray<int>(max, Allocator.Temp);
            var latestTick = new NativeArray<uint>(max, Allocator.Temp);
            for (int i = 0; i < max; i++) { latestIdx[i] = -1; latestTick[i] = 0; }

            for (int i = 0; i < tickInputs.Length; i++)
            {
                var input = tickInputs[i];
                if (input.PlayerId >= max) continue;
                if (latestIdx[input.PlayerId] < 0 || input.Tick > latestTick[input.PlayerId])
                {
                    latestIdx[input.PlayerId] = i;
                    latestTick[input.PlayerId] = input.Tick;
                }
            }
            
            for (int i = 0; i < tickInputs.Length; i++)
            {
                var input = tickInputs[i];
                if (!input.Fire) continue;
                if (input.PlayerId >= max) continue;
                if (!TryGetPlayerSnapshot(ref state, input.PlayerId, out var pos, out var aim, out var alive)) continue;
                if (!alive) continue;

                float2 dir = math.lengthsq(input.Aim) > 0.0001f ? math.normalize(input.Aim) : aim;
                pendingFires.Add(new PendingFire
                {
                    PlayerId = input.PlayerId,
                    ClientInputTick = input.Tick,
                    Origin = pos,
                    Direction = dir,
                });
            }

            // Apply movement / aim / cooldown / disable timer per Player entity
            foreach (var (slot, pos, aim, cd, dt, aliveRef) in
                     SystemAPI.Query<RefRO<PlayerSlot>, RefRW<Position2D>, RefRW<Aim2D>,
                                     RefRW<FireCooldown>, RefRW<DisableTimer>, RefRO<Alive>>()
                              .WithAll<PlayerTag>())
            {
                byte pid = slot.ValueRO.Value;
                if (!aliveRef.ValueRO.Bool)
                {
                    cd.ValueRW.Seconds = math.max(0f, cd.ValueRO.Seconds - Constants.SimDt);
                    dt.ValueRW.Seconds = math.max(0f, dt.ValueRO.Seconds - Constants.SimDt);
                    continue;
                }

                int idx = latestIdx[pid];
                if (idx < 0)
                {
                    cd.ValueRW.Seconds = math.max(0f, cd.ValueRO.Seconds - Constants.SimDt);
                    dt.ValueRW.Seconds = math.max(0f, dt.ValueRO.Seconds - Constants.SimDt);
                    continue;
                }

                var input = tickInputs[idx];

                // Movement — clamp magnitude to ≤1 so diagonal input doesn't exceed PlayerSpeed.
                float2 move = input.Move;
                float mag = math.length(move);
                if (mag > 1f) move /= mag;
                float2 newPos = pos.ValueRO.Value + move * Constants.PlayerSpeed * Constants.SimDt;

                // Arena clamp — keep player inside the circle (radius = ArenaRadius − PlayerRadius).
                float r = math.length(newPos);
                float maxR = Constants.ArenaRadius - Constants.PlayerRadius;
                if (r > maxR) newPos *= maxR / r;
                pos.ValueRW.Value = newPos;

                if (math.lengthsq(input.Aim) > 0.0001f)
                    aim.ValueRW.Value = math.normalize(input.Aim);

                cd.ValueRW.Seconds = math.max(0f, cd.ValueRO.Seconds - Constants.SimDt);
                dt.ValueRW.Seconds = math.max(0f, dt.ValueRO.Seconds - Constants.SimDt);
            }
            
            tickInputs.Clear();
        }
        
        private bool TryGetPlayerSnapshot(ref SystemState state, byte slotId, out float2 pos, out float2 aim, out bool alive)
        {
            foreach (var (s, p, a, al) in
                     SystemAPI.Query<RefRO<PlayerSlot>, RefRO<Position2D>, RefRO<Aim2D>, RefRO<Alive>>()
                              .WithAll<PlayerTag>())
            {
                if (s.ValueRO.Value == slotId)
                {
                    pos = p.ValueRO.Value;
                    aim = a.ValueRO.Value;
                    alive = al.ValueRO.Bool;
                    return true;
                }
            }
            pos = default; aim = new float2(1f, 0f); alive = false;
            return false;
        }
    }
}
#endif
