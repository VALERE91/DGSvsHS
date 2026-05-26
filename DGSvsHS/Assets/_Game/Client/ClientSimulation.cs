using System.Collections.Generic;
using DGSvsHS.Gameplay;
using UnityEngine;

namespace DGSvsHS.Client
{
    public sealed class ClientSimulation
    {
        // ---------- Local player prediction ----------

        private byte _localPlayerId;
        private bool _haveLocalPlayer;
        private PlayerState _predictedLocalPlayer;
        
        private readonly LinkedList<InputCmd> _pendingInputs = new LinkedList<InputCmd>();

        // ---------- Snapshot history for interpolation ----------

        private readonly Snapshot _snapA = new Snapshot();
        private readonly Snapshot _snapB = new Snapshot();
        private bool _haveA;
        private bool _haveB;
        private float _snapARecvTime;
        private float _snapBRecvTime;

        /// <summary>Wall-clock time the latest snapshot was received (for interpolation render-time calc).</summary>
        public float LatestSnapshotRecvTime => _snapBRecvTime;
        public uint LatestServerTick => _haveB ? _snapB.Tick : 0; 
        public uint LastAckedServerTick => _haveB ? _snapB.Tick : 0;
        
        public readonly List<FireEvent> NewFireEvents = new List<FireEvent>(16);
        public readonly List<FireEvent> NewPredictedFires = new List<FireEvent>(16);

        // ---------- Setup ----------

        public void SetLocalPlayerId(byte id)
        {
            _localPlayerId = id;
            _haveLocalPlayer = false;
            _pendingInputs.Clear();
        }

        public byte LocalPlayerId => _localPlayerId;

        // ---------- Prediction (called every sim tick on the client) ----------
        
        public void PushPredictedInput(InputCmd cmd)
        {
            _pendingInputs.AddLast(cmd);
            
            while (_pendingInputs.Count > Constants.InputHistoryTicks)
                _pendingInputs.RemoveFirst();
            
            if (!_haveLocalPlayer) return;
            
            ApplyInputToPredictedLocalPlayer(cmd, emitPredictedFire: true);
        }
        
        public PlayerState GetPredictedLocalPlayer() => _predictedLocalPlayer;

        public bool HasLocalPlayer => _haveLocalPlayer;

        // ---------- Snapshot intake + reconciliation ----------

        public void OnSnapshotReceived(Snapshot s, float wallTime)
        {
            if (_haveB)
            {
                CopySnapshot(_snapB, _snapA);
                _snapARecvTime = _snapBRecvTime;
                _haveA = true;
            }
            CopySnapshot(s, _snapB);
            _snapBRecvTime = wallTime;
            _haveB = true;
            
            for (int i = 0; i < _snapB.RecentFireEvents.Count; i++)
            {
                var ev = _snapB.RecentFireEvents[i];
                if (ev.ShooterId == _localPlayerId) continue;
                NewFireEvents.Add(ev);
            }

            // Reconcile local player.
            ReconcileLocalPlayer(_snapB);
        }

        private void ReconcileLocalPlayer(Snapshot s)
        {
            // Find our authoritative state in the snapshot.
            PlayerSnap? mine = null;
            for (int i = 0; i < s.Players.Count; i++)
            {
                if (s.Players[i].Id == _localPlayerId) { mine = s.Players[i]; break; }
            }
            if (mine == null) return;

            // Snap predicted state to server state at LastProcessedInputTick.
            _predictedLocalPlayer = new PlayerState
            {
                Id = _localPlayerId,
                Position = mine.Value.Position,
                Aim = mine.Value.Aim,
                FireCooldown = _predictedLocalPlayer.FireCooldown, // server doesn't tell us cooldown; keep predicted
                DisableTimer = mine.Value.DisableTimer,            // server-authoritative; reflects touch + invuln window
                Alive = mine.Value.Alive,
            };
            _haveLocalPlayer = true;

            // Discard pending inputs the server has now acked.
            while (_pendingInputs.Count > 0 && _pendingInputs.First.Value.Tick <= s.LastProcessedInputTick)
                _pendingInputs.RemoveFirst();

            // Replay remaining inputs on top of the authoritative state.
            // Reconciliation path: do NOT re-emit predicted fires (already drawn on first apply).
            for (var node = _pendingInputs.First; node != null; node = node.Next)
                ApplyInputToPredictedLocalPlayer(node.Value, emitPredictedFire: false);
        }

        private void ApplyInputToPredictedLocalPlayer(InputCmd cmd, bool emitPredictedFire)
        {
            if (!_predictedLocalPlayer.Alive)
            {
                _predictedLocalPlayer.FireCooldown =
                    Mathf.Max(0f, _predictedLocalPlayer.FireCooldown - Constants.SimDt);
                _predictedLocalPlayer.DisableTimer =
                    Mathf.Max(0f, _predictedLocalPlayer.DisableTimer - Constants.SimDt);
                return;
            }

            Vector2 move = cmd.Move;
            float mag = move.magnitude;
            if (mag > 1f) move /= mag;
            _predictedLocalPlayer.Position += move * Constants.PlayerSpeed * Constants.SimDt;

            float r = _predictedLocalPlayer.Position.magnitude;
            float maxR = Constants.ArenaRadius - Constants.PlayerRadius;
            if (r > maxR) _predictedLocalPlayer.Position *= (maxR / r);

            if (cmd.Aim.sqrMagnitude > 0.0001f)
                _predictedLocalPlayer.Aim = cmd.Aim.normalized;
            
            if (cmd.Fire
                && _predictedLocalPlayer.FireCooldown <= 0f
                && _predictedLocalPlayer.DisableTimer <= 0f)
            {
                _predictedLocalPlayer.FireCooldown = Constants.PlayerFireCooldownSec;
                if (emitPredictedFire)
                {
                    NewPredictedFires.Add(new FireEvent
                    {
                        Tick = cmd.Tick,
                        ShooterId = _localPlayerId,
                        Origin = _predictedLocalPlayer.Position,
                        Direction = _predictedLocalPlayer.Aim,
                        Distance = Constants.BulletMaxRange,
                        KillCount = 0,
                    });
                }
            }

            _predictedLocalPlayer.FireCooldown =
                Mathf.Max(0f, _predictedLocalPlayer.FireCooldown - Constants.SimDt);
            _predictedLocalPlayer.DisableTimer =
                Mathf.Max(0f, _predictedLocalPlayer.DisableTimer - Constants.SimDt);
        }

        // ---------- Interpolation queries (for renderer) ----------
        
        public bool GetInterpolation(float wallNow, out Snapshot from, out Snapshot to, out float alpha)
        {
            from = _snapA;
            to = _snapB;
            alpha = 1f;
            if (!_haveA || !_haveB) return false;

            float renderTime = wallNow - (Constants.InterpolationBufferMs / 1000f);
            if (renderTime <= _snapARecvTime) { alpha = 0f; return true; }
            if (renderTime >= _snapBRecvTime) { alpha = 1f; return true; }
            float span = _snapBRecvTime - _snapARecvTime;
            alpha = span > 0f ? (renderTime - _snapARecvTime) / span : 1f;
            return true;
        }
        
        public bool TryGetInterpolatedPlayer(byte playerId, float wallNow, out Vector2 pos, out Vector2 aim, out bool alive, out bool disabled)
        {
            pos = default; aim = Vector2.right; alive = false; disabled = false;
            if (!GetInterpolation(wallNow, out var from, out var to, out float a)) return false;

            PlayerSnap? f = null, t = null;
            for (int i = 0; i < from.Players.Count; i++) if (from.Players[i].Id == playerId) { f = from.Players[i]; break; }
            for (int i = 0; i < to.Players.Count; i++)   if (to.Players[i].Id   == playerId) { t = to.Players[i];   break; }
            if (f == null || t == null) return false;

            pos = Vector2.Lerp(f.Value.Position, t.Value.Position, a);
            aim = Vector2.Lerp(f.Value.Aim, t.Value.Aim, a).normalized;
            alive = t.Value.Alive;
            disabled = t.Value.DisableTimer > 0f;
            return true;
        }
        
        public bool TryGetInterpolatedEnemy(ushort enemyId, float wallNow, out Vector2 pos)
        {
            pos = default;
            if (!GetInterpolation(wallNow, out var from, out var to, out float a)) return false;

            EnemySnap? f = null, t = null;
            for (int i = 0; i < from.Enemies.Count; i++) if (from.Enemies[i].Id == enemyId) { f = from.Enemies[i]; break; }
            for (int i = 0; i < to.Enemies.Count; i++)   if (to.Enemies[i].Id   == enemyId) { t = to.Enemies[i];   break; }
            if (f == null || t == null) return false;

            pos = Vector2.Lerp(f.Value.Position, t.Value.Position, a);
            return true;
        }
        
        public Snapshot LatestSnapshot => _snapB;
        public bool HasLatestSnapshot => _haveB;

        // ---------- Helpers ----------

        private static void CopySnapshot(Snapshot src, Snapshot dst) => dst.CopyFrom(src);
    }
}
