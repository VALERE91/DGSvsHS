namespace DGSvsHS.Gameplay
{
    public static class Constants
    {
        // ---------- Simulation ----------
        public const int SimTickMs = 16;
        public const float SimDt = SimTickMs / 1000f;         // 0.016 s
        public const float TicksPerSecond = 1000f / SimTickMs; // ≈ 62.5

        // ---------- Networking ----------
        public const int SnapshotEveryNTicks = 1;
        public const int InputRate = (int)TicksPerSecond;
        public const float InterpolationBufferMs = 100f;
        public const int SnapshotHistoryTicks = 64;    // ~1.024 s @ 16 ms
        public const int MaxDeltaDepth = 32;           // 0.512 s — beyond this, send full
        public const int InputHistoryTicks = 128;
        
        public const int SnapshotByteBudget = 1200;

        // ---------- Wire quantization ----------
        public const int PositionScale = 1000;
        public const int AngleScale = 10430;  // 32768 / π ≈ 10430.378

        // ---------- Priority / staleness ----------
        public const float StalenessWeight = 0.5f;
        public const int MaxSpawnsPerSnapshot = 30;

        // ---------- Client-side enemy correction ----------
        public const float EnemyCorrectionK = 400f;
        public const float EnemyCorrectionC = 40f;
        public const float EnemyCorrectionSnapDistance = 10f;
        public const float EnemyCorrectionKMaxMultiplier = 25f;
        public const float EnemyCorrectionBufferLatencyMs = 50f;
        public const int EnemyCorrectionBufferCapacity = 8;

        // ---------- Arena ----------
        public const float ArenaRadius = 25f;

        // ---------- Player ----------
        public const float PlayerSpeed = 6f;
        public const float PlayerRadius = 0.4f;
        public const float PlayerFireCooldownSec = 0.12f;
        public const float PlayerKillRadius = 0.5f;
        public const float DisableDurationSec = 10f;
        public const int MaxPlayers = 4;

        // ---------- Laser (hitscan, piercing) ----------
        public const float BulletMaxRange = 50f;
        public const float BeamRadius = 0.2f;

        // ---------- Enemy ----------
        public const float EnemySpeed = 2.5f;
        public const float EnemyRadius = 0.35f;
        public const int EnemyMaxHp = 1;
        public const int MaxEnemies = 15000;

        // ---------- Rounds ----------
        public const int TotalRounds = 10;
        public const float InterRoundDelaySec = 3f;
        public const int BaseEnemiesPerRound = 700;
        public const float EnemyScalingPerRound = 1.4f;
        public const float RoundSpawnWindowSec = 18f;

        // ---------- Spatial grid ----------
        public const float GridCellSize = 1.0f;
        public const int GridHalfCells = 28;
    }
}
