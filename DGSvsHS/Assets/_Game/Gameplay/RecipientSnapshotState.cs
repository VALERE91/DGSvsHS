using System.Collections.Generic;

namespace DGSvsHS.Gameplay
{
    public sealed class RecipientSnapshotState
    {
        public uint LastAckedServerTick;

        public readonly HashSet<ushort> ConfirmedIds = new HashSet<ushort>();
        
        public readonly Dictionary<ushort, ushort> TicksSinceLastSent = new Dictionary<ushort, ushort>();

        private struct PendingEntry
        {
            public uint Tick;
            public bool IsFull;
            public HashSet<ushort> Included;
            public HashSet<ushort> Removed;
        }

        private readonly List<PendingEntry> _pending = new List<PendingEntry>();
        private readonly Stack<HashSet<ushort>> _setPool = new Stack<HashSet<ushort>>();

        private HashSet<ushort> RentSet() => _setPool.Count > 0 ? _setPool.Pop() : new HashSet<ushort>();
        private void ReturnSet(HashSet<ushort> s) { s.Clear(); _setPool.Push(s); }
        
        public void OnSnapshotSent(uint tick, bool isFull, HashSet<ushort> included, IReadOnlyList<ushort> removed)
        {
            var incCopy = RentSet();
            foreach (var id in included) incCopy.Add(id);
            var remCopy = RentSet();
            for (int i = 0; i < removed.Count; i++) remCopy.Add(removed[i]);
            _pending.Add(new PendingEntry { Tick = tick, IsFull = isFull, Included = incCopy, Removed = remCopy });

            // Staleness maintenance for already-confirmed ids
            var keys = new List<ushort>(TicksSinceLastSent.Keys);
            for (int i = 0; i < keys.Count; i++)
            {
                ushort id = keys[i];
                if (included.Contains(id)) TicksSinceLastSent[id] = 0;
                else
                {
                    ushort cur = TicksSinceLastSent[id];
                    TicksSinceLastSent[id] = cur < ushort.MaxValue ? (ushort)(cur + 1) : ushort.MaxValue;
                }
            }
        }
        
        public void OnAckAdvanced()
        {
            int writeIdx = 0;
            for (int i = 0; i < _pending.Count; i++)
            {
                var p = _pending[i];
                if (p.Tick > LastAckedServerTick)
                {
                    _pending[writeIdx++] = p;
                    continue;
                }

                if (p.IsFull)
                {
                    ConfirmedIds.Clear();
                    TicksSinceLastSent.Clear();
                    foreach (var id in p.Included)
                    {
                        ConfirmedIds.Add(id);
                        TicksSinceLastSent[id] = 0;
                    }
                }
                else
                {
                    foreach (var id in p.Included)
                    {
                        ConfirmedIds.Add(id);
                        if (!TicksSinceLastSent.ContainsKey(id)) TicksSinceLastSent[id] = 0;
                    }
                    foreach (var id in p.Removed)
                    {
                        ConfirmedIds.Remove(id);
                        TicksSinceLastSent.Remove(id);
                    }
                }

                ReturnSet(p.Included);
                ReturnSet(p.Removed);
            }
            if (writeIdx < _pending.Count) _pending.RemoveRange(writeIdx, _pending.Count - writeIdx);
        }
        
        public void Clear()
        {
            LastAckedServerTick = 0;
            ConfirmedIds.Clear();
            TicksSinceLastSent.Clear();
            for (int i = 0; i < _pending.Count; i++)
            {
                ReturnSet(_pending[i].Included);
                ReturnSet(_pending[i].Removed);
            }
            _pending.Clear();
        }
    }
}
