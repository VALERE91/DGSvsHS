using System.Collections.Generic;
using DGSvsHS.Client;
using DGSvsHS.Gameplay;
using UnityEngine;

namespace DGSvsHS.Harness
{
    /// <summary>
    /// Deterministic bot driver for benchmark trials.
    ///
    /// <para>The bot orbits the arena at a fixed radius and phase (derived from
    /// bot id + seed), aims at the nearest visible enemy from the latest snapshot,
    /// and fires continuously. Behavior is a pure function of (tick, bot id, seed,
    /// snapshot contents) — no wall-clock dependency, no Unity randomness — so the
    /// workload is identical across trials run against either server build.</para>
    ///
    /// <para><b>Why orbit + fire-at-nearest:</b> generates a workload close to what a
    /// real human player produces (continuous movement, continuous aim updates,
    /// continuous firing), keeps the server busy on hitscan + AI nearest-player
    /// queries, but is fully reproducible.</para>
    /// </summary>
    public sealed class AutoPilot : IAutoPilot
    {
        private readonly byte _botId;
        private readonly ulong _seed;
        private readonly float _orbitRadius;
        private readonly float _orbitAngularSpeed;
        private readonly float _phaseOffset;

        public AutoPilot(byte botId, ulong seed)
        {
            _botId = botId;
            _seed = seed;

            // Per-bot orbit parameters from a per-bot RNG. Different orbits per bot
            // so they don't all run the same trajectory, but reproducible per seed.
            var rng = DeterministicRng.FromSeed(seed ^ (0xA5A5A5A5UL + botId));
            _orbitRadius = rng.NextRange(Constants.ArenaRadius * 0.4f, Constants.ArenaRadius * 0.75f);
            _orbitAngularSpeed = rng.NextRange(0.5f, 1.2f);   // rad/sec
            _phaseOffset = rng.NextRange(0f, Mathf.PI * 2f);
        }

        public InputCmd Sample(uint clientTick, Vector2 localPlayerPredictedPos, ClientSimulation sim)
        {
            // Orbit target: a position on a circle whose angle advances with clientTick.
            float t = clientTick * Constants.SimDt;
            float angle = _phaseOffset + _orbitAngularSpeed * t;
            Vector2 target = new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * _orbitRadius;

            // Move toward target (unit-clamped direction).
            Vector2 toTarget = target - localPlayerPredictedPos;
            Vector2 move = toTarget.sqrMagnitude > 0.04f ? toTarget.normalized : Vector2.zero;

            // Aim at the nearest alive enemy from the latest snapshot.
            Vector2 aim = Vector2.right;
            if (sim.HasLatestSnapshot)
            {
                var snap = sim.LatestSnapshot;
                float best = float.MaxValue;
                bool found = false;
                Vector2 nearest = default;
                for (int i = 0; i < snap.Enemies.Count; i++)
                {
                    var e = snap.Enemies[i];
                    float d = (e.Position - localPlayerPredictedPos).sqrMagnitude;
                    if (d < best) { best = d; nearest = e.Position; found = true; }
                }
                if (found)
                {
                    Vector2 toE = nearest - localPlayerPredictedPos;
                    if (toE.sqrMagnitude > 0.0001f) aim = toE.normalized;
                }
            }

            // Always fire — server-side cooldown rate-limits us naturally.
            var flags = InputFlags.Fire;

            return new InputCmd { Tick = clientTick, Move = move, Aim = aim, Flags = flags };
        }
    }
}
