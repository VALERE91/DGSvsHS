using System.Collections.Generic;

namespace DGSvsHS.Gameplay
{
    public sealed class WorldStateHistory
    {
        private readonly Snapshot[] _ring;
        private readonly int _capacity;
        private int _count;
        private int _head;

        public WorldStateHistory(int capacity)
        {
            _capacity = capacity;
            _ring = new Snapshot[capacity];
            for (int i = 0; i < capacity; i++) _ring[i] = new Snapshot();
        }

        public int Capacity => _capacity;
        public int Count => _count;
        
        public void Record(Snapshot snapshot)
        {
            var slot = _ring[_head];
            slot.CopyFrom(snapshot);
            _head = (_head + 1) % _capacity;
            if (_count < _capacity) _count++;
        }

        public bool TryGet(uint tick, out Snapshot snap)
        {
            for (int i = 0; i < _count; i++)
            {
                int idx = (_head - 1 - i + _capacity) % _capacity;
                if (_ring[idx].Tick == tick)
                {
                    snap = _ring[idx];
                    return true;
                }
            }
            snap = null;
            return false;
        }
        
        public uint NewestTick
        {
            get
            {
                if (_count == 0) return 0;
                int idx = (_head - 1 + _capacity) % _capacity;
                return _ring[idx].Tick;
            }
        }
        
        public uint OldestTick
        {
            get
            {
                if (_count == 0) return 0;
                int idx = (_head - _count + _capacity) % _capacity;
                return _ring[idx].Tick;
            }
        }
        
        public void Clear()
        {
            _count = 0;
            _head = 0;
        }
    }
}
