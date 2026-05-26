using System.Collections.Generic;
using UnityEngine;

namespace DGSvsHS.Gameplay
{
    /// Pure scoring + selection algorithm for per-recipient delta snapshot composition.

    /// For each enemy currently in the world, computes a priority score relative
    /// to a recipient's anchor position. Lower score = higher priority. The algorithm
    /// then packs entries into the byte budget in ascending score order, distinguishing
    /// changed (in baseline) vs. new (not in baseline).

    /// Score: distance_to_recipient - StalenessWeight × ticks_since_last_sent_to_this_recipient

    public static class SnapshotPriority
    {
        public static void SelectForDelta(
            Snapshot current,
            Snapshot baseline,
            Vector2 recipientPos,
            HashSet<ushort> confirmedIds,
            IReadOnlyDictionary<ushort, ushort> ticksSinceLastSent,
            int enemyByteBudget,
            List<EnemyDeltaEntry> outChanged,
            List<ushort> outRemoved,
            List<EnemySnap> outAdded,
            HashSet<ushort> includedIds,
            List<ScoredEnemy> scratchScored)
        {
            outChanged.Clear();
            outRemoved.Clear();
            outAdded.Clear();
            includedIds.Clear();
            scratchScored.Clear();
            
            var currentIds = new HashSet<ushort>();
            for (int i = 0; i < current.Enemies.Count; i++) currentIds.Add(current.Enemies[i].Id);
            if (confirmedIds != null)
            {
                foreach (ushort cid in confirmedIds)
                {
                    if (currentIds.Contains(cid)) continue;
                    outRemoved.Add(cid);
                }
            }
            else
            {
                for (int i = 0; i < baseline.Enemies.Count; i++)
                {
                    ushort bid = baseline.Enemies[i].Id;
                    if (currentIds.Contains(bid)) continue;
                    outRemoved.Add(bid);
                }
            }
            
            var baselineIndexById = new Dictionary<ushort, int>(baseline.Enemies.Count);
            for (int i = 0; i < baseline.Enemies.Count; i++) baselineIndexById[baseline.Enemies[i].Id] = i;

            // ---- Removed entries
            int removedBytes = outRemoved.Count * 2;
            if (removedBytes > enemyByteBudget)
            {
                int keepable = enemyByteBudget / 2;
                if (keepable < outRemoved.Count)
                    outRemoved.RemoveRange(keepable, outRemoved.Count - keepable);
                removedBytes = outRemoved.Count * 2;
            }
            int remaining = enemyByteBudget - removedBytes;
            if (remaining < 0) remaining = 0;

            // ---- Phase A: pending-spawn lane. ----
            scratchScored.Clear();
            bool haveConfirmed = confirmedIds != null;
            for (int i = 0; i < current.Enemies.Count; i++)
            {
                var e = current.Enemies[i];
                bool isPendingSpawn = !haveConfirmed || !confirmedIds.Contains(e.Id);
                if (!isPendingSpawn) continue;
                float dx = e.Position.x - recipientPos.x;
                float dy = e.Position.y - recipientPos.y;
                scratchScored.Add(new ScoredEnemy { Index = i, Score = dx * dx + dy * dy });
            }
            scratchScored.Sort((a, b) => a.Score.CompareTo(b.Score));
            int spawnsThisSnapshot = 0;
            for (int s = 0; s < scratchScored.Count; s++)
            {
                if (spawnsThisSnapshot >= Constants.MaxSpawnsPerSnapshot) break;
                if (remaining < NewEntrySize()) break;
                int idx = scratchScored[s].Index;
                var e = current.Enemies[idx];
                outAdded.Add(e);
                includedIds.Add(e.Id);
                remaining -= NewEntrySize();
                spawnsThisSnapshot++;
            }

            // ---- Phase B: animation updates for confirmed-and-in-baseline enemies. ----
            scratchScored.Clear();
            for (int i = 0; i < current.Enemies.Count; i++)
            {
                var e = current.Enemies[i];
                if (!haveConfirmed || !confirmedIds.Contains(e.Id)) continue; // pending spawn, handled in A
                if (!baselineIndexById.ContainsKey(e.Id)) continue;            // no baseline value to diff against
                float dx = e.Position.x - recipientPos.x;
                float dy = e.Position.y - recipientPos.y;
                float dist = Mathf.Sqrt(dx * dx + dy * dy);
                int tsls = 0;
                if (ticksSinceLastSent != null && ticksSinceLastSent.TryGetValue(e.Id, out ushort t)) tsls = t;
                float score = dist - Constants.StalenessWeight * tsls;
                scratchScored.Add(new ScoredEnemy { Index = i, Score = score });
            }
            scratchScored.Sort((a, b) => a.Score.CompareTo(b.Score));
            for (int s = 0; s < scratchScored.Count; s++)
            {
                int idx = scratchScored[s].Index;
                var e = current.Enemies[idx];
                int baseIdx = baselineIndexById[e.Id];
                var b = baseline.Enemies[baseIdx];
                if (!PositionChanged(b, e))
                {
                    includedIds.Add(e.Id);
                    continue;
                }
                int sz = ChangedEntrySize();
                if (sz > remaining) break;
                remaining -= sz;
                outChanged.Add(new EnemyDeltaEntry
                {
                    Id = e.Id,
                    Position = e.Position,
                });
                includedIds.Add(e.Id);
            }
        }
        
        public static void SelectForFull(
            Snapshot current,
            Vector2 recipientPos,
            int enemyByteBudget,
            List<EnemySnap> outSelected,
            List<ScoredEnemy> scratchScored)
        {
            outSelected.Clear();
            scratchScored.Clear();

            for (int i = 0; i < current.Enemies.Count; i++)
            {
                var e = current.Enemies[i];
                float dx = e.Position.x - recipientPos.x;
                float dy = e.Position.y - recipientPos.y;
                scratchScored.Add(new ScoredEnemy { Index = i, Score = dx * dx + dy * dy });
            }
            scratchScored.Sort((a, b) => a.Score.CompareTo(b.Score));

            int remaining = enemyByteBudget;
            for (int s = 0; s < scratchScored.Count; s++)
            {
                int sz = NewEntrySize();
                if (sz > remaining) break;
                remaining -= sz;
                outSelected.Add(current.Enemies[scratchScored[s].Index]);
            }
        }

        public struct ScoredEnemy
        {
            public int Index;
            public float Score;
        }

        // Helpers re-stated locally so this module compiles inside the Gameplay assembly
        // (Gameplay doesn't reference Net). MUST match WireCodec equivalents byte-for-byte.

        private static bool PositionChanged(in EnemySnap baseline, in EnemySnap current)
        {
            short bpx = QuantPos(baseline.Position.x), bpy = QuantPos(baseline.Position.y);
            short cpx = QuantPos(current.Position.x),  cpy = QuantPos(current.Position.y);
            return bpx != cpx || bpy != cpy;
        }

        private static int ChangedEntrySize() => 6; // EnemyDeltaEntryBytes — id + pos_x + pos_y
        private static int NewEntrySize() => 6;     // EnemySnapFullBytes — id + pos_x + pos_y

        private static short QuantPos(float m)
        {
            int q = Mathf.RoundToInt(m * Constants.PositionScale);
            if (q > short.MaxValue) q = short.MaxValue;
            else if (q < short.MinValue) q = short.MinValue;
            return (short)q;
        }
    }
}
