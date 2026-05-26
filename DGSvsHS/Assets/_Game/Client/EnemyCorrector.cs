using System.Collections.Generic;
using DGSvsHS.Gameplay;
using UnityEngine;

namespace DGSvsHS.Client
{
    public sealed class EnemyCorrector
    {
        private struct State
        {
            public Vector2 RenderPos;
            public Vector2 RenderVel;
        }

        private struct BufferedSnap
        {
            public float RecvWall;
            public Dictionary<ushort, Vector2> Positions;
        }

        private readonly Dictionary<ushort, State> _states = new Dictionary<ushort, State>(2048);
        private readonly List<BufferedSnap> _buffer = new List<BufferedSnap>(8);
        private readonly Stack<Dictionary<ushort, Vector2>> _dictPool = new Stack<Dictionary<ushort, Vector2>>();
        private readonly List<ushort> _removeScratch = new List<ushort>(128);
        private readonly List<ushort> _stepKeysScratch = new List<ushort>(2048);

        // Live-tunable parameters.
        public float K = Constants.EnemyCorrectionK;
        public float C = Constants.EnemyCorrectionC;
        public float SnapDistance = Constants.EnemyCorrectionSnapDistance;
        public float KMaxMultiplier = Constants.EnemyCorrectionKMaxMultiplier;
        public float BufferLatencyMs = Constants.EnemyCorrectionBufferLatencyMs;

        private Dictionary<ushort, Vector2> RentDict()
            => _dictPool.Count > 0 ? _dictPool.Pop() : new Dictionary<ushort, Vector2>(1024);

        private void ReturnDict(Dictionary<ushort, Vector2> d)
        {
            d.Clear();
            _dictPool.Push(d);
        }
        
        public void IngestSnapshot(Snapshot s, float recvWall)
        {
            var positions = RentDict();
            for (int i = 0; i < s.Enemies.Count; i++)
            {
                var e = s.Enemies[i];
                positions[e.Id] = e.Position;
                if (!_states.ContainsKey(e.Id))
                {
                    _states[e.Id] = new State { RenderPos = e.Position, RenderVel = Vector2.zero };
                }
            }
            _buffer.Add(new BufferedSnap { RecvWall = recvWall, Positions = positions });

            // Trim oldest entries beyond capacity.
            while (_buffer.Count > Constants.EnemyCorrectionBufferCapacity)
            {
                ReturnDict(_buffer[0].Positions);
                _buffer.RemoveAt(0);
            }

            // Prune _states for ids that no longer appear in the latest snapshot (enemy was
            // killed / culled by the server). Their render state isn't needed anymore.
            _removeScratch.Clear();
            foreach (var kv in _states)
                if (!positions.ContainsKey(kv.Key)) _removeScratch.Add(kv.Key);
            for (int i = 0; i < _removeScratch.Count; i++) _states.Remove(_removeScratch[i]);
        }
        
        private bool TryGetInterpolatedTarget(ushort id, float targetWall, out Vector2 target)
        {
            target = default;
            if (_buffer.Count == 0) return false;
            
            int aIdx = -1;
            for (int i = _buffer.Count - 1; i >= 0; i--)
            {
                if (_buffer[i].RecvWall <= targetWall) { aIdx = i; break; }
            }

            if (aIdx < 0)
            {
                if (_buffer[0].Positions.TryGetValue(id, out target)) return true;
                return false;
            }
            
            var a = _buffer[aIdx];
            if (aIdx + 1 < _buffer.Count)
            {
                var b = _buffer[aIdx + 1];
                bool inA = a.Positions.TryGetValue(id, out Vector2 posA);
                bool inB = b.Positions.TryGetValue(id, out Vector2 posB);
                if (inA && inB)
                {
                    float span = b.RecvWall - a.RecvWall;
                    float alpha = span > 0f ? Mathf.Clamp01((targetWall - a.RecvWall) / span) : 1f;
                    target = Vector2.Lerp(posA, posB, alpha);
                    return true;
                }
                if (inB) { target = posB; return true; }
                if (inA) { target = posA; return true; }
                return false;
            }
            
            if (a.Positions.TryGetValue(id, out target)) return true;
            return false;
        }
        
        public void Step(float wallNow, float frameDt)
        {
            if (frameDt <= 0f) return;

            float targetWall = wallNow - BufferLatencyMs * 0.001f;
            float kBase = K;
            float cBase = C;
            float snapDist = SnapDistance;
            float snapSq = snapDist * snapDist;
            float multMinusOne = Mathf.Max(0f, KMaxMultiplier - 1f);

            _stepKeysScratch.Clear();
            foreach (var key in _states.Keys) _stepKeysScratch.Add(key);
            for (int ki = 0; ki < _stepKeysScratch.Count; ki++)
            {
                ushort id = _stepKeysScratch[ki];
                if (!TryGetInterpolatedTarget(id, targetWall, out Vector2 target)) continue;

                var st = _states[id];
                Vector2 posError = target - st.RenderPos;
                float errorSq = posError.sqrMagnitude;

                if (errorSq > snapSq)
                {
                    // Hard fallback for extreme divergence — should be rare since K ramps quadratically.
                    st.RenderPos = target;
                    st.RenderVel = Vector2.zero;
                }
                else
                {
                    // Distance-scaled stiffness: K_eff = K · (1 + (mult − 1) · t²) where
                    // t = |error| / snapDist clamped to [0..1]. C scales by sqrt(K_eff/K)
                    // so the damping ratio the user set with (K, C) stays put as K ramps up.
                    float t = snapDist > 0f ? Mathf.Sqrt(errorSq) / snapDist : 0f;
                    if (t > 1f) t = 1f;
                    float kScale = 1f + multMinusOne * t * t;
                    float kEff = kBase * kScale;
                    float cEff = cBase * Mathf.Sqrt(kScale);

                    // Semi-implicit Euler. Damping on absolute renderVel (no truth velocity
                    // reference because the wire dropped enemy velocity in v3) — at equilibrium
                    // (posError ≈ 0) the velocity drains to zero and renderPos sits on the
                    // interpolated target, which itself keeps moving via the buffer's lerp.
                    Vector2 acc = kEff * posError - cEff * st.RenderVel;
                    st.RenderVel += acc * frameDt;
                    st.RenderPos += st.RenderVel * frameDt;
                }

                _states[id] = st;
            }
        }

        public bool TryGet(ushort id, out Vector2 pos)
        {
            if (_states.TryGetValue(id, out var st))
            {
                pos = st.RenderPos;
                return true;
            }
            pos = default;
            return false;
        }

        public void Clear()
        {
            _states.Clear();
            for (int i = 0; i < _buffer.Count; i++) ReturnDict(_buffer[i].Positions);
            _buffer.Clear();
        }
    }
}
