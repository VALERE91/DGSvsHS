
#pragma once

namespace UnrealvsHS::Constants
{
	// ---------- Simulation ----------
	constexpr int   SimTickMs              = 16;
	constexpr float SimDt                  = SimTickMs / 1000.0f;
	constexpr float TicksPerSecond         = 1000.0f / SimTickMs;

	// ---------- Networking ----------
	constexpr int   SnapshotEveryNTicks    = 1;
	constexpr float InterpolationBufferMs  = 100.0f;
	constexpr int   SnapshotHistoryTicks   = 64;
	constexpr int   MaxDeltaDepth          = 32;
	constexpr int   SnapshotByteBudget     = 1200;
	constexpr int   ProtocolVersion        = 4;

	// ---------- Wire quantization ----------
	constexpr int   PositionScale          = 1000;
	constexpr int   AngleScale             = 10430;

	// ---------- Priority / staleness ----------
	constexpr float StalenessWeight        = 0.5f;
	constexpr int   MaxSpawnsPerSnapshot   = 30;

	// ---------- Arena ----------
	constexpr float ArenaRadius            = 25.0f;

	// ---------- Player ----------
	constexpr float PlayerSpeed            = 6.0f;
	constexpr float PlayerRadius           = 0.4f;
	constexpr float PlayerFireCooldownSec  = 0.12f;
	constexpr float PlayerKillRadius       = 0.5f;
	constexpr float DisableDurationSec     = 10.0f;
	constexpr int   MaxPlayers             = 4;

	// ---------- Laser (hitscan, piercing) ----------
	constexpr float BulletMaxRange         = 50.0f;
	constexpr float BeamRadius             = 0.2f;

	// ---------- Enemy ----------
	constexpr float EnemySpeed             = 2.5f;
	constexpr float EnemyRadius            = 0.35f;
	constexpr int   EnemyMaxHp             = 1;
	constexpr int   MaxEnemies             = 1000000;

	// ---------- Physics (Chaos 3D, XY-plane constrained) ----------
	constexpr float EnemyMass              = 1.0f;
	constexpr float EnemyLinearDamping     = 10.0f;
	constexpr float EnemyDriveForce        = EnemySpeed * EnemyLinearDamping; // ≈ 25
	constexpr float PlayerLinearDamping    = 0.0f;

	// ---------- Rounds ----------
	constexpr int   TotalRounds            = 10;
	constexpr float InterRoundDelaySec     = 3.0f;
	constexpr int   BaseEnemiesPerRound    = 1700;
	constexpr float EnemyScalingPerRound   = 1.4f;
	constexpr float RoundSpawnWindowSec    = 18.0f;

	// ---------- Spatial grid ----------
	constexpr float GridCellSize           = 1.0f;
	constexpr int   GridHalfCells          = 28;
}
