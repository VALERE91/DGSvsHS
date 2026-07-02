#[derive(Clone, Debug)]
pub struct IdBitSet {
    bits: [u64; 1024],
}

impl IdBitSet {
    pub fn new() -> Self {
        Self { bits: [0; 1024] }
    }

    #[inline]
    pub fn insert(&mut self, id: u16) {
        let i = id as usize;
        self.bits[i >> 6] |= 1 << (i & 63);
    }

    #[inline]
    pub fn remove(&mut self, id: u16) {
        let i = id as usize;
        self.bits[i >> 6] &= !(1 << (i & 63));
    }

    #[inline]
    pub fn contains(&self, id: u16) -> bool {
        let i = id as usize;
        (self.bits[i >> 6] & (1 << (i & 63))) != 0
    }

    pub fn clear(&mut self) {
        self.bits.fill(0);
    }

    pub fn iter(&self) -> impl Iterator<Item = u16> + '_ {
        self.bits.iter().enumerate().flat_map(|(i, &word)| {
            let mut w = word;
            std::iter::from_fn(move || {
                if w == 0 {
                    None
                } else {
                    let tz = w.trailing_zeros();
                    w &= w - 1; // clear lowest set bit
                    Some(((i << 6) + tz as usize) as u16)
                }
            })
        })
    }

    /// Iterate ids present in `self` but not in `other` (set difference),
    /// scanning at the word level so cost is `O(1024 words + result)` rather
    /// than `O(id space)`. Used to pull the "spawn" set (current − confirmed)
    /// without touching every enemy.
    pub fn iter_diff<'a>(&'a self, other: &'a IdBitSet) -> impl Iterator<Item = u16> + 'a {
        self.bits
            .iter()
            .zip(other.bits.iter())
            .enumerate()
            .flat_map(|(i, (&a, &b))| {
                let mut w = a & !b;
                std::iter::from_fn(move || {
                    if w == 0 {
                        None
                    } else {
                        let tz = w.trailing_zeros();
                        w &= w - 1;
                        Some(((i << 6) + tz as usize) as u16)
                    }
                })
            })
    }

    #[cfg(test)]
    pub fn len(&self) -> usize {
        self.bits.iter().map(|w| w.count_ones() as usize).sum()
    }
    
    pub fn is_empty(&self) -> bool {
        self.bits.iter().all(|&w| w == 0)
    }
}

impl Default for IdBitSet {
    fn default() -> Self {
        Self::new()
    }
}

impl PartialEq for IdBitSet {
    fn eq(&self, other: &Self) -> bool {
        self.bits == other.bits
    }
}
impl Eq for IdBitSet {}
