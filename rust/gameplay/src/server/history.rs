// Ring of recent Full snapshots, used as baselines for per-recipient delta
// encoding. Port of csharp_arch_server/Server/WorldStateHistory.cs.

use bevy::prelude::Resource;

use crate::game::Snapshot;

#[derive(Resource)]
pub struct WorldStateHistory {
    ring: Vec<Snapshot>,
    capacity: usize,
    head: usize,
    count: usize,
}

impl WorldStateHistory {
    pub fn new(capacity: usize) -> Self {
        let ring = (0..capacity).map(|_| Snapshot::with_capacity()).collect();
        Self {
            ring,
            capacity,
            head: 0,
            count: 0,
        }
    }

    pub fn capacity(&self) -> usize {
        self.capacity
    }

    pub fn count(&self) -> usize {
        self.count
    }

    /// Copy `snapshot` into the head slot and advance.
    pub fn record(&mut self, snapshot: &Snapshot) {
        crate::hot_span!("history_record");
        self.ring[self.head].copy_from(snapshot);
        self.head = (self.head + 1) % self.capacity;
        if self.count < self.capacity {
            self.count += 1;
        }
    }

    /// Borrow the stored snapshot for `tick`, if still in the ring.
    pub fn try_get(&self, tick: u32) -> Option<&Snapshot> {
        for i in 0..self.count {
            let idx = (self.head + self.capacity - 1 - i) % self.capacity;
            if self.ring[idx].tick == tick {
                return Some(&self.ring[idx]);
            }
        }
        None
    }

    pub fn newest_tick(&self) -> u32 {
        if self.count == 0 {
            return 0;
        }
        let idx = (self.head + self.capacity - 1) % self.capacity;
        self.ring[idx].tick
    }

    pub fn oldest_tick(&self) -> u32 {
        if self.count == 0 {
            return 0;
        }
        let idx = (self.head + self.capacity - self.count) % self.capacity;
        self.ring[idx].tick
    }

    pub fn clear(&mut self) {
        self.head = 0;
        self.count = 0;
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::game::SnapshotKind;

    fn snap(tick: u32) -> Snapshot {
        let mut s = Snapshot::with_capacity();
        s.kind = SnapshotKind::Full;
        s.tick = tick;
        s
    }

    #[test]
    fn records_and_recalls() {
        let mut h = WorldStateHistory::new(4);
        h.record(&snap(10));
        h.record(&snap(11));
        assert_eq!(h.count(), 2);
        assert_eq!(h.newest_tick(), 11);
        assert_eq!(h.oldest_tick(), 10);
        assert!(h.try_get(10).is_some());
        assert!(h.try_get(11).is_some());
        assert!(h.try_get(12).is_none());
    }

    #[test]
    fn overwrites_oldest_when_full() {
        let mut h = WorldStateHistory::new(3);
        for t in 1..=5 {
            h.record(&snap(t));
        }
        assert_eq!(h.count(), 3);
        assert_eq!(h.newest_tick(), 5);
        assert_eq!(h.oldest_tick(), 3);
        assert!(h.try_get(1).is_none());
        assert!(h.try_get(2).is_none());
        assert!(h.try_get(3).is_some());
        assert!(h.try_get(5).is_some());
    }

    #[test]
    fn clear_resets() {
        let mut h = WorldStateHistory::new(4);
        h.record(&snap(10));
        h.clear();
        assert_eq!(h.count(), 0);
        assert!(h.try_get(10).is_none());
    }
}
