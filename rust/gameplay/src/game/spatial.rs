// Hand-rolled uniform spatial grid and beam-vs-circle math. Mirrors the
// Unity DOTS `SpatialGrid.cs` so the two implementations produce identical
// hit results for the same world state (per CLAUDE.md §9 / WireFormat.md §7).
//
// Per-entity cost is just an `Enemy` marker + 8-byte `Pos2D`. The grid is
// one `Vec<Vec<Entity>>` resource, rebuilt from the ECS in PostStartup
// (static stress) and PreUpdate (once the sim moves).

use bevy::prelude::*;

use super::constants::{GRID_CELL_SIZE, GRID_HALF_CELLS};

#[derive(Component, Copy, Clone, Debug, Default)]
pub struct Pos2D {
    pub x: f32,
    pub y: f32,
}

#[derive(Component, Copy, Clone, Debug, Default)]
pub struct Vel2D {
    pub x: f32,
    pub y: f32,
}

pub struct SpatialPlugin;

impl Plugin for SpatialPlugin {
    fn build(&self, app: &mut App) {
        app.insert_resource(EnemyGrid::new())
            .add_systems(PostStartup, rebuild_grid);
    }
}

/// Uniform-grid geometry: cell math shared by `EnemyGrid` (current-world
/// entities) and the rewind ring's per-frame buckets (historical positions),
/// so both index identically. Pure — no entity storage.
#[derive(Copy, Clone, Debug)]
pub struct GridGeom {
    pub cells_per_side: i32,
    pub half_cells: i32,
    pub cell_size: f32,
}

impl GridGeom {
    pub fn from_constants() -> Self {
        Self {
            cells_per_side: GRID_HALF_CELLS * 2,
            half_cells: GRID_HALF_CELLS,
            cell_size: GRID_CELL_SIZE,
        }
    }

    pub fn cell_count(&self) -> usize {
        (self.cells_per_side * self.cells_per_side) as usize
    }

    pub fn cell_coords(&self, x: f32, y: f32) -> Option<(i32, i32)> {
        let cx = (x / self.cell_size).floor() as i32 + self.half_cells;
        let cy = (y / self.cell_size).floor() as i32 + self.half_cells;
        if cx < 0 || cy < 0 || cx >= self.cells_per_side || cy >= self.cells_per_side {
            return None;
        }
        Some((cx, cy))
    }

    pub fn cell_idx(&self, cx: i32, cy: i32) -> usize {
        (cy * self.cells_per_side + cx) as usize
    }

    /// Inclusive cell range `(cxmin, cymin, cxmax, cymax)` for every cell whose
    /// AABB overlaps the segment A→B expanded by `radius`. `None` if the
    /// expanded AABB falls entirely outside the grid.
    pub fn segment_cell_range(
        &self,
        ax: f32,
        ay: f32,
        bx: f32,
        by: f32,
        radius: f32,
    ) -> Option<(i32, i32, i32, i32)> {
        let half_world = self.half_cells as f32 * self.cell_size;
        // Clamp the segment AABB into the grid (and shave 1 mm off the upper
        // bound so floor() doesn't land on the past-the-end cell).
        let xmin = (ax.min(bx) - radius).max(-half_world);
        let xmax = (ax.max(bx) + radius).min(half_world - 0.001);
        let ymin = (ay.min(by) - radius).max(-half_world);
        let ymax = (ay.max(by) + radius).min(half_world - 0.001);
        if xmin > xmax || ymin > ymax {
            return None;
        }
        let (cxmin, cymin) = self.cell_coords(xmin, ymin)?;
        let (cxmax, cymax) = self.cell_coords(xmax, ymax)?;
        Some((cxmin, cymin, cxmax, cymax))
    }
}

#[derive(Resource)]
pub struct EnemyGrid {
    /// Row-major: index = cy * cells_per_side + cx
    cells: Vec<Vec<Entity>>,
    geom: GridGeom,
}

impl EnemyGrid {
    pub fn new() -> Self {
        let geom = GridGeom::from_constants();
        Self {
            cells: (0..geom.cell_count()).map(|_| Vec::new()).collect(),
            geom,
        }
    }

    pub fn clear(&mut self) {
        for cell in &mut self.cells {
            cell.clear();
        }
    }

    pub fn insert(&mut self, entity: Entity, pos: Pos2D) {
        if let Some((cx, cy)) = self.geom.cell_coords(pos.x, pos.y) {
            let i = self.geom.cell_idx(cx, cy);
            self.cells[i].push(entity);
        }
    }

    /// Collect every entity in cells whose AABB overlaps the segment A→B
    /// expanded by `radius`. Conservative — caller must still narrow-phase
    /// with `segment_circle_hit`.
    pub fn collect_along_segment(
        &self,
        ax: f32,
        ay: f32,
        bx: f32,
        by: f32,
        radius: f32,
        out: &mut Vec<Entity>,
    ) {
        crate::hot_span!("grid_collect_along_segment");
        out.clear();
        let Some((cxmin, cymin, cxmax, cymax)) =
            self.geom.segment_cell_range(ax, ay, bx, by, radius)
        else {
            return;
        };
        for cy in cymin..=cymax {
            for cx in cxmin..=cxmax {
                let i = self.geom.cell_idx(cx, cy);
                out.extend_from_slice(&self.cells[i]);
            }
        }
    }
}

impl Default for EnemyGrid {
    fn default() -> Self {
        Self::new()
    }
}

pub fn rebuild_grid(mut grid: ResMut<EnemyGrid>, q: Query<(Entity, &Pos2D)>) {
    grid.clear();
    for (e, p) in q.iter() {
        grid.insert(e, *p);
    }
}

// ---------- Beam math ----------

/// Smallest t ∈ [0, 1] for which the segment A→B intersects a circle at C
/// of radius `radius`. None if the segment never touches the circle.
/// If the origin starts strictly inside the circle, returns 0.0.
pub fn segment_circle_hit(
    ax: f32,
    ay: f32,
    bx: f32,
    by: f32,
    cx: f32,
    cy: f32,
    radius: f32,
) -> Option<f32> {
    let dx = bx - ax;
    let dy = by - ay;
    let fx = ax - cx;
    let fy = ay - cy;
    let a = dx * dx + dy * dy;
    if a == 0.0 {
        // Zero-length segment: only a hit if A is inside the circle.
        return if fx * fx + fy * fy <= radius * radius {
            Some(0.0)
        } else {
            None
        };
    }
    let b = 2.0 * (fx * dx + fy * dy);
    let c = fx * fx + fy * fy - radius * radius;
    // Origin is on or inside the circle ⇒ already touching ⇒ t = 0.
    if c <= 0.0 {
        return Some(0.0);
    }
    let disc = b * b - 4.0 * a * c;
    if disc < 0.0 {
        return None;
    }
    let sq = disc.sqrt();
    let inv2a = 0.5 / a;
    let t1 = (-b - sq) * inv2a;
    let t2 = (-b + sq) * inv2a;
    if t1 >= 0.0 && t1 <= 1.0 {
        return Some(t1);
    }
    if t2 >= 0.0 && t2 <= 1.0 {
        return Some(t2);
    }
    None
}

#[derive(Copy, Clone, Debug)]
pub struct BeamHit {
    pub entity: Entity,
    pub t: f32,
}

/// Piercing beam — appends every enemy whose collider the beam intersects.
/// Caller supplies a position lookup (typically `|e| query.get(e).ok().copied()`)
/// and reusable scratch buffers.
pub fn beam_hits(
    grid: &EnemyGrid,
    pos_lookup: impl Fn(Entity) -> Option<Pos2D>,
    origin_x: f32,
    origin_y: f32,
    dir_x: f32,
    dir_y: f32,
    length: f32,
    radius: f32,
    cell_scratch: &mut Vec<Entity>,
    out: &mut Vec<BeamHit>,
) {
    crate::hot_span!("beam_hits");
    let end_x = origin_x + dir_x * length;
    let end_y = origin_y + dir_y * length;
    grid.collect_along_segment(origin_x, origin_y, end_x, end_y, radius, cell_scratch);
    out.clear();
    for &entity in cell_scratch.iter() {
        let Some(p) = pos_lookup(entity) else {
            continue;
        };
        if let Some(t) =
            segment_circle_hit(origin_x, origin_y, end_x, end_y, p.x, p.y, radius)
        {
            out.push(BeamHit { entity, t });
        }
    }
}

// ---------- Tests ----------

#[cfg(test)]
mod tests {
    use super::*;

    fn approx(a: f32, b: f32) -> bool {
        (a - b).abs() < 1e-4
    }

    #[test]
    fn segment_hits_circle_in_path() {
        // Beam x-axis (0,0)→(10,0), circle (5,0) r=1. Entry at x=4 → t=0.4.
        let t = segment_circle_hit(0.0, 0.0, 10.0, 0.0, 5.0, 0.0, 1.0).unwrap();
        assert!(approx(t, 0.4), "got {}", t);
    }

    #[test]
    fn segment_misses_circle_offset() {
        // Same beam, circle at (5, 5). Perpendicular distance 5 > r=1.
        assert!(segment_circle_hit(0.0, 0.0, 10.0, 0.0, 5.0, 5.0, 1.0).is_none());
    }

    #[test]
    fn segment_grazes_circle_edge() {
        // Beam at y=1, circle (5,0) r=1 → tangent at (5,1), t=0.5.
        let t = segment_circle_hit(0.0, 1.0, 10.0, 1.0, 5.0, 0.0, 1.0).unwrap();
        assert!(approx(t, 0.5), "got {}", t);
    }

    #[test]
    fn segment_starts_inside_circle() {
        // Origin inside r=0.5 circle at origin → t=0.
        let t = segment_circle_hit(0.0, 0.0, 10.0, 0.0, 0.0, 0.0, 0.5).unwrap();
        assert!(approx(t, 0.0), "got {}", t);
    }

    #[test]
    fn circle_beyond_segment_end() {
        // Beam length 1, circle at distance 5 → no hit.
        assert!(segment_circle_hit(0.0, 0.0, 1.0, 0.0, 5.0, 0.0, 0.5).is_none());
    }

    #[test]
    fn beam_pierces_multiple_circles() {
        // Place three circles inline; piercing beam should hit all three.
        let mut grid = EnemyGrid::new();
        let mut positions = std::collections::HashMap::new();
        let mut next_id = 1u32;
        let mut mk = |x: f32, y: f32| {
            let e = Entity::from_raw_u32(next_id).unwrap();
            next_id += 1;
            grid.insert(e, Pos2D { x, y });
            positions.insert(e, Pos2D { x, y });
            e
        };
        let e1 = mk(2.0, 0.0);
        let e2 = mk(5.0, 0.0);
        let e3 = mk(8.0, 0.0);
        // Off-axis decoy: should NOT be hit.
        let e_off = mk(5.0, 3.0);

        let mut scratch = Vec::new();
        let mut hits = Vec::new();
        beam_hits(
            &grid,
            |e| positions.get(&e).copied(),
            0.0,
            0.0,
            1.0,
            0.0,
            10.0,
            0.4,
            &mut scratch,
            &mut hits,
        );

        let hit_ents: std::collections::HashSet<_> = hits.iter().map(|h| h.entity).collect();
        assert!(hit_ents.contains(&e1));
        assert!(hit_ents.contains(&e2));
        assert!(hit_ents.contains(&e3));
        assert!(!hit_ents.contains(&e_off));
    }

    #[test]
    fn grid_skips_out_of_bounds_position() {
        let mut grid = EnemyGrid::new();
        // Place far outside the grid extent (28 m half-width).
        let e = Entity::from_raw_u32(1).unwrap();
        grid.insert(e, Pos2D { x: 1000.0, y: 0.0 });
        // No cell should contain it — total entries across all cells = 0.
        let total: usize = grid.cells.iter().map(|c| c.len()).sum();
        assert_eq!(total, 0);
    }
}