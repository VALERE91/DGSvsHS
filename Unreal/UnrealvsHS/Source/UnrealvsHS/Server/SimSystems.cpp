#include "SimSystems.h"
#include "Gameplay/UvHSConstants.h"
#include "Mass/UvHSMassTypes.h"
#include "Server/UvHSEnemyBody.h"
#include "MassEntityManager.h"
#include "MassEntityQuery.h"
#include "MassExecutionContext.h"

namespace UnrealvsHS::Server::Sim
{
	using namespace Constants;
	using namespace Wire;

	// ---------- Step 1: TickAdvance ----------

	void TickAdvance(FSimContext& Ctx)
	{
		Ctx.Tick++;
		Ctx.FireEvents.Reset();
	}

	// ---------- Step 2: RoundDirector ----------

	static int32 TargetEnemiesForRound(int32 ForRound)
	{
		if (ForRound < 1) return 0;
		const float Scaled = (float)Constants::BaseEnemiesPerRound * FMath::Pow(Constants::EnemyScalingPerRound, (float)(ForRound - 1));
		return FMath::RoundToInt(Scaled);
	}

	static void StartWave(FRoundState& Round)
	{
		const int32 Target = TargetEnemiesForRound(Round.Round);
		Round.SpawnTarget      = Target;
		Round.SpawnsRemaining  = Target;
		Round.SpawnInterval    = Constants::RoundSpawnWindowSec / FMath::Max(1, Target);
		Round.SpawnAccumulator = 0.0f;
	}

	static void SpawnOneEnemy(FSimContext& Ctx)
	{
		const float Angle = Ctx.Rng.NextRange(0.0f, 2.0f * PI);
		const float R     = Constants::ArenaRadius - Constants::EnemyRadius - 0.1f;
		const uint16 Id   = Ctx.NextEnemyId++;
		const FVector2D Pos(FMath::Cos(Angle) * R, FMath::Sin(Angle) * R);
		Ctx.SpawnEnemy(Id, Pos);
	}
	
	static int32 CountEnemies(const FSimContext& Ctx)
	{
		if (!Ctx.MassEntityManager.IsValid()) return 0;
		
		FMassEntityQuery Q(Ctx.MassEntityManager);
		Q.AddTagRequirement<FUvHSEnemyTag>(EMassFragmentPresence::All);
		int32 Total = 0;
		FMassExecutionContext ExecContext(*Ctx.MassEntityManager);
		Q.ForEachEntityChunk(ExecContext, [&Total](FMassExecutionContext& ExecCtx)
		{
			Total += ExecCtx.GetNumEntities();
		});
		return Total;
	}

	static void TickWave(FSimContext& Ctx)
	{
		FRoundState& R = Ctx.Round;
		if (R.SpawnsRemaining <= 0) return;
		R.SpawnAccumulator += Constants::SimDt;
		while (R.SpawnAccumulator >= R.SpawnInterval && R.SpawnsRemaining > 0)
		{
			R.SpawnAccumulator -= R.SpawnInterval;
			SpawnOneEnemy(Ctx);
			R.SpawnsRemaining--;
		}
	}

	static bool AllConnectedPlayersDisabled(const FSimContext& Ctx)
	{
		int32 Total = 0, Disabled = 0;
		for (const FPlayerState& P : Ctx.Players)
		{
			if (!P.bAlive) continue;
			++Total;
			if (P.DisableTimer > 0.0f) ++Disabled;
		}
		return Total > 0 && Disabled == Total;
	}

	static void ResetToRoundOne(FSimContext& Ctx)
	{
		Ctx.Round.Round            = 0;
		Ctx.Round.Phase            = ERoundPhase::InterRound;
		Ctx.Round.InterRoundTimer  = Constants::InterRoundDelaySec;
		Ctx.Round.RoundTimer       = 0.0f;
		Ctx.Round.SpawnTarget      = 0;
		Ctx.Round.SpawnsRemaining  = 0;
		Ctx.Round.SpawnInterval    = 0.0f;
		Ctx.Round.SpawnAccumulator = 0.0f;
		Ctx.DestroyAllEnemies();
		for (FPlayerState& P : Ctx.Players)
		{
			P.bAlive       = true;
			P.DisableTimer = 0.0f;
			P.FireCooldown = 0.0f;
		}
	}

	void RoundDirector(FSimContext& Ctx)
	{
		if (Ctx.Players.Num() == 0 && Ctx.Round.Phase != ERoundPhase::PreGame)
		{
			Ctx.Round = FRoundState();
			return;
		}

		switch (Ctx.Round.Phase)
		{
			case ERoundPhase::PreGame:
				break;

			case ERoundPhase::InterRound:
				Ctx.Round.InterRoundTimer -= Constants::SimDt;
				if (Ctx.Round.InterRoundTimer <= 0.0f)
				{
					Ctx.Round.Round++;
					if (Ctx.Round.Round > Constants::TotalRounds)
					{
						Ctx.Round.Phase = ERoundPhase::Victory;
					}
					else
					{
						Ctx.Round.Phase      = ERoundPhase::InRound;
						Ctx.Round.RoundTimer = 0.0f;
						StartWave(Ctx.Round);
					}
				}
				break;

			case ERoundPhase::InRound:
				Ctx.Round.RoundTimer += Constants::SimDt;
				TickWave(Ctx);
				if (AllConnectedPlayersDisabled(Ctx))
				{
					ResetToRoundOne(Ctx);
				}
				else if (Ctx.Round.SpawnsRemaining == 0 && CountEnemies(Ctx) == 0)
				{
					Ctx.Round.Phase           = ERoundPhase::InterRound;
					Ctx.Round.InterRoundTimer = Constants::InterRoundDelaySec;
				}
				break;

			case ERoundPhase::Victory:
			case ERoundPhase::Defeat:
				break;
		}
	}

	// ---------- Step 3: PlayerInput ----------

#if !UE_BUILD_SHIPPING
	// Dev/editor loopback smoke-test: each spawned player stands still and sweeps a
	// full-circle hitscan every tick (no cooldown gate in PlayerInput), so all
	// enemies get cleared and rounds auto-advance 1→10 with no network client.
	// Gated by Ctx.bSimulatedInput (set from SimulatedClients > 0). Pair with
	// GodMode so contact never disables the still player and firing never stops.
	// Compiled out of Shipping entirely.
	void SimulatedClientInput(FSimContext& Ctx)
	{
		if (!Ctx.bSimulatedInput || Ctx.Players.Num() == 0) return;

		constexpr float SweepRevsPerSec = 1.0f;
		constexpr float TwoPi = 2.0f * (float)PI;
		Ctx.SimulatedAimAngle = FMath::Fmod(
			Ctx.SimulatedAimAngle + (TwoPi * SweepRevsPerSec * SimDt), TwoPi);
		const FVector2D Aim(FMath::Cos(Ctx.SimulatedAimAngle), FMath::Sin(Ctx.SimulatedAimAngle));

		for (const FPlayerState& P : Ctx.Players)
		{
			FTickInput In;
			In.PlayerId = P.Id;
			In.Tick     = Ctx.Tick;
			In.Move     = FVector2D::ZeroVector;
			In.Aim      = Aim;
			In.Flags    = EInputFlags::Fire;
			Ctx.TickInputs.Add(In);
		}
	}
#endif // !UE_BUILD_SHIPPING

	void PlayerInput(FSimContext& Ctx)
	{
		TArray<int32> LatestIdx; LatestIdx.Init(INDEX_NONE, Constants::MaxPlayers);
		for (int32 i = 0; i < Ctx.TickInputs.Num(); ++i)
		{
			const FTickInput& In = Ctx.TickInputs[i];
			if (In.PlayerId >= Constants::MaxPlayers) continue;
			if (LatestIdx[In.PlayerId] == INDEX_NONE ||
			    Ctx.TickInputs[LatestIdx[In.PlayerId]].Tick < In.Tick)
			{
				LatestIdx[In.PlayerId] = i;
			}
		}

		struct FSnap { FVector2D Pos; FVector2D Aim; bool bAlive; };
		TArray<FSnap> Snap; Snap.Init({FVector2D::ZeroVector, FVector2D(1,0), false}, Constants::MaxPlayers);
		for (const FPlayerState& P : Ctx.Players)
		{
			if (P.Id < Constants::MaxPlayers) Snap[P.Id] = { P.Position, P.Aim, P.bAlive };
		}

		for (const FTickInput& In : Ctx.TickInputs)
		{
			if (((uint8)In.Flags & (uint8)EInputFlags::Fire) == 0) continue;
			if (In.PlayerId >= Constants::MaxPlayers) continue;
			const FSnap& S = Snap[In.PlayerId];
			if (!S.bAlive) continue;

			FVector2D Dir = In.Aim;
			if (Dir.SquaredLength() <= 1e-4) Dir = S.Aim;
			else Dir = Dir.GetSafeNormal();

			FPendingFire PF;
			PF.PlayerId        = In.PlayerId;
			PF.ClientInputTick = In.Tick;
			PF.Origin          = S.Pos;
			PF.Direction       = Dir;
			Ctx.PendingFires.Add(PF);
		}

		const float MaxR = Constants::ArenaRadius - Constants::PlayerRadius;
		for (FPlayerState& P : Ctx.Players)
		{
			const int32 Idx = (P.Id < Constants::MaxPlayers) ? LatestIdx[P.Id] : INDEX_NONE;
			if (P.bAlive && Idx != INDEX_NONE)
			{
				const FTickInput& In = Ctx.TickInputs[Idx];
				FVector2D Mv = In.Move;
				const double Mag = Mv.Length();
				if (Mag > 1.0) Mv /= Mag;

				FVector2D NewPos = P.Position + Mv * (double)(Constants::PlayerSpeed * Constants::SimDt);
				const double R = NewPos.Length();
				if (R > MaxR) NewPos *= (double)MaxR / R;
				P.Position = NewPos;

				if (In.Aim.SquaredLength() > 1e-4) P.Aim = In.Aim.GetSafeNormal();

				if (P.Id < Constants::MaxPlayers) Ctx.ProcessedInputTick[P.Id] = In.Tick;
			}
			P.FireCooldown = FMath::Max(0.0f, P.FireCooldown - Constants::SimDt);
			P.DisableTimer = FMath::Max(0.0f, P.DisableTimer - Constants::SimDt);
		}

		Ctx.TickInputs.Reset();
	}

	// ---------- Step 4: hitscan helpers (shared by ResolveFiresCurrentTick + RewindResolve) ----------

	static FORCEINLINE float ComputeViewTickF(uint32 ServerTick, float OneWayMs)
	{
		return (float)ServerTick
		       - (OneWayMs / 1000.0f) * Constants::TicksPerSecond
		       - (Constants::InterpolationBufferMs / 1000.0f) * Constants::TicksPerSecond;
	}

	static bool FindBracketingSlots(
		const FRewindRing& Ring, float ViewTickF,
		int32& OutFloorSlot, int32& OutCeilSlot, float& OutAlpha)
	{
		OutFloorSlot = INDEX_NONE; OutCeilSlot = INDEX_NONE; OutAlpha = 0.0f;
		if (Ring.Count == 0) return false;

		const uint32 ViewFloor = (uint32)FMath::Max(0.0f, FMath::FloorToFloat(ViewTickF));
		const uint32 ViewCeil  = ViewFloor + 1;

		for (int32 i = 0; i < Ring.Count; ++i)
		{
			const int32 Slot = (Ring.Head - 1 - i + Ring.Slots) % Ring.Slots;
			const FRewindFrameHeader& H = Ring.Headers[Slot];
			if (H.Tick == ViewFloor) OutFloorSlot = Slot;
			if (H.Tick == ViewCeil)  OutCeilSlot  = Slot;
		}
		if (OutFloorSlot == INDEX_NONE)
		{
			const int32 Oldest = (Ring.Head - Ring.Count + Ring.Slots) % Ring.Slots;
			OutFloorSlot = Oldest; OutCeilSlot = Oldest; OutAlpha = 0.0f;
			return true;
		}
		if (OutCeilSlot == INDEX_NONE)
		{
			OutCeilSlot = OutFloorSlot; OutAlpha = 0.0f;
			return true;
		}
		OutAlpha = FMath::Clamp(ViewTickF - (float)ViewFloor, 0.0f, 1.0f);
		return true;
	}

	static FORCEINLINE bool SegmentHits(
		const FVector2D& Origin, const FVector2D& Dir, float MaxRange,
		const FVector2D& EnemyPos, float HitRadiusSq)
	{
		const FVector2D ToEnemy = EnemyPos - Origin;
		const float T = (float)(ToEnemy.X * Dir.X + ToEnemy.Y * Dir.Y);
		if (T < 0.0f || T > MaxRange) return false;
		const FVector2D Closest = Origin + Dir * (double)T;
		const FVector2D D = EnemyPos - Closest;
		return (float)(D.X * D.X + D.Y * D.Y) <= HitRadiusSq;
	}

	// ---------- Step 4 (Phase 2A fallback): resolve fires against CURRENT positions ----------
	void ResolveFiresCurrentTick(FSimContext& Ctx)
	{
		if (Ctx.PendingFires.Num() == 0 || !Ctx.MassEntityManager.IsValid()) return;
		const float Clearance = Constants::EnemyRadius + Constants::BeamRadius;
		const float ClearanceSq = Clearance * Clearance;

		for (const FPendingFire& PF : Ctx.PendingFires)
		{
			TArray<FMassEntityHandle> Kills;
			float MaxDist = 0.0f;

			FMassEntityQuery Q(Ctx.MassEntityManager);
			Q.AddTagRequirement<FUvHSEnemyTag>(EMassFragmentPresence::All);
			Q.AddRequirement<FUvHSEnemyPositionFragment>(EMassFragmentAccess::ReadOnly);

			FMassExecutionContext ExecContext(*Ctx.MassEntityManager);
			Q.ForEachEntityChunk(ExecContext,
				[&Kills, &MaxDist, &PF, ClearanceSq](FMassExecutionContext& ExecCtx)
			{
				const int32 N = ExecCtx.GetNumEntities();
				const auto Positions = ExecCtx.GetFragmentView<FUvHSEnemyPositionFragment>();
				for (int32 i = 0; i < N; ++i)
				{
					if (SegmentHits(PF.Origin, PF.Direction, Constants::BulletMaxRange,
						Positions[i].Position, ClearanceSq))
					{
						const float D = (float)(Positions[i].Position - PF.Origin).Length();
						if (D > MaxDist) MaxDist = D;
						Kills.Add(ExecCtx.GetEntity(i));
					}
				}
			});

			if (Kills.Num() > 0)
			{
				Ctx.MassEntityManager->BatchDestroyEntities(Kills);
				if (Ctx.CachedEnemyCount >= Kills.Num()) Ctx.CachedEnemyCount -= Kills.Num();
			}

			Wire::FFireEvent Fe;
			Fe.Tick       = Ctx.Tick;
			Fe.ShooterId  = PF.PlayerId;
			Fe.Origin     = PF.Origin;
			Fe.Direction  = PF.Direction;
			Fe.Distance   = FMath::Min(MaxDist, Constants::BulletMaxRange);
			Fe.KillCount  = (uint8)FMath::Min(255, Kills.Num());
			Ctx.FireEvents.Add(Fe);
		}
		Ctx.PendingFires.Reset();
	}

	// ---------- Step 4 (Phase 2B-4): RewindResolve ----------

	void RewindResolve(FSimContext& Ctx)
	{
		if (Ctx.PendingFires.Num() == 0 || !Ctx.MassEntityManager.IsValid()) return;

		const FRewindRing& Ring = Ctx.Rewind;
		const float HitR = Constants::EnemyRadius + Constants::BeamRadius;
		const float HitR2 = HitR * HitR;
		const float MaxRange = Constants::BulletMaxRange;

		// Aggregate killed ids across all fires this tick.
		TSet<uint16> Killed;

		// Scratch reused across fires. Ceil-frame id→index map + floor-frame id set
		// make floor↔ceil matching O(N) per fire instead of the old nested-scan
		// O(N²) that pinned CPU at high enemy counts (the round-10 blowup).
		TMap<uint16, int32> CeilIdxById;
		TSet<uint16>        FloorIdSet;

		for (const FPendingFire& F : Ctx.PendingFires)
		{
			const float OneWayMs = (F.PlayerId < Ctx.PlayerRttMs.Num() ? Ctx.PlayerRttMs[F.PlayerId] : 60.0f) * 0.5f;
			const float ViewTickF = ComputeViewTickF(Ctx.Tick, OneWayMs);

			int32 FloorSlot, CeilSlot;
			float Alpha;
			if (!FindBracketingSlots(Ring, ViewTickF, FloorSlot, CeilSlot, Alpha)) continue;

			const FRewindFrameHeader& FloorHdr = Ring.Headers[FloorSlot];
			const FRewindFrameHeader& CeilHdr  = Ring.Headers[CeilSlot];
			const int32 FloorStart = FloorSlot * Ring.Stride;
			const int32 CeilStart  = CeilSlot  * Ring.Stride;

			int32 Kills = 0;

			// Build ceil-frame id→index once (O(CeilCount)) so floor→ceil lookup is O(1).
			CeilIdxById.Reset();
			CeilIdxById.Reserve(CeilHdr.Count);
			for (int32 j = 0; j < CeilHdr.Count; ++j)
			{
				CeilIdxById.Add(Ring.Ids[CeilStart + j], j);
			}

			// Lane 1: ids in floor — lerp to ceil if matched.
			for (int32 i = 0; i < FloorHdr.Count; ++i)
			{
				const uint16 Id  = Ring.Ids[FloorStart + i];
				const FVector2D FPos = Ring.Positions[FloorStart + i];
				FVector2D Pos = FPos;
				if (const int32* CeilJ = CeilIdxById.Find(Id))
				{
					const FVector2D CPos = Ring.Positions[CeilStart + *CeilJ];
					Pos = FMath::Lerp(FPos, CPos, (double)Alpha);
				}
				if (SegmentHits(F.Origin, F.Direction, MaxRange, Pos, HitR2))
				{
					bool bAlreadyIn = false;
					Killed.Add(Id, &bAlreadyIn);
					if (!bAlreadyIn) ++Kills;
				}
			}

			// Lane 2: ids only in ceil (spawned mid-window), gated alpha ≥ 0.5.
			if (Alpha >= 0.5f)
			{
				// Floor id set once (O(FloorCount)) → O(1) "is in floor?" per ceil enemy.
				FloorIdSet.Reset();
				FloorIdSet.Reserve(FloorHdr.Count);
				for (int32 i = 0; i < FloorHdr.Count; ++i)
				{
					FloorIdSet.Add(Ring.Ids[FloorStart + i]);
				}
				for (int32 j = 0; j < CeilHdr.Count; ++j)
				{
					const uint16 Id = Ring.Ids[CeilStart + j];
					if (FloorIdSet.Contains(Id)) continue;
					const FVector2D Pos = Ring.Positions[CeilStart + j];
					if (SegmentHits(F.Origin, F.Direction, MaxRange, Pos, HitR2))
					{
						bool bAlreadyIn = false;
						Killed.Add(Id, &bAlreadyIn);
						if (!bAlreadyIn) ++Kills;
					}
				}
			}

			Wire::FFireEvent Fe;
			Fe.Tick       = Ctx.Tick;
			Fe.ShooterId  = F.PlayerId;
			Fe.Origin     = F.Origin;
			Fe.Direction  = F.Direction;
			Fe.Distance   = MaxRange;
			Fe.KillCount  = (uint8)FMath::Min(255, Kills);
			Ctx.FireEvents.Add(Fe);
		}

		// Map killed ids → current Mass handles, then BatchDestroy.
		if (Killed.Num() > 0)
		{
			TArray<FMassEntityHandle> ToDestroy;
			ToDestroy.Reserve(Killed.Num());

			FMassEntityQuery Q(Ctx.MassEntityManager);
			Q.AddTagRequirement<FUvHSEnemyTag>(EMassFragmentPresence::All);
			Q.AddRequirement<FUvHSEnemyIdFragment>(EMassFragmentAccess::ReadOnly);

			FMassExecutionContext ExecContext(*Ctx.MassEntityManager);
			Q.ForEachEntityChunk(ExecContext,
				[&Killed, &ToDestroy](FMassExecutionContext& ExecCtx)
			{
				const int32 N = ExecCtx.GetNumEntities();
				const auto Ids = ExecCtx.GetFragmentView<FUvHSEnemyIdFragment>();
				for (int32 i = 0; i < N; ++i)
				{
					if (Killed.Contains(Ids[i].Id)) ToDestroy.Add(ExecCtx.GetEntity(i));
				}
			});

			if (ToDestroy.Num() > 0)
			{
				Ctx.MassEntityManager->BatchDestroyEntities(ToDestroy);
				if (Ctx.CachedEnemyCount >= ToDestroy.Num()) Ctx.CachedEnemyCount -= ToDestroy.Num();
			}
		}

		Ctx.PendingFires.Reset();
	}

	// Nearest-target seek direction (unit vector), deterministic given a slot-sorted
	// target list. Shared by the Chaos and no-Chaos EnemySeek paths.
	static FVector2D NearestSeekDir(const FVector2D& EPos, const TArray<FVector2D>& Targets)
	{
		double BestSq = TNumericLimits<double>::Max();
		FVector2D Best = Targets[0];
		for (const FVector2D& T : Targets)
		{
			const double Sq = (T - EPos).SquaredLength();
			if (Sq < BestSq) { BestSq = Sq; Best = T; }
		}
		const double Len = FMath::Sqrt(BestSq);
		if (Len <= 1e-4) return FVector2D::ZeroVector;
		return (Best - EPos) / Len;
	}

	// ---------- Step 5: EnemySeek (drive toward nearest player) ----------
	//
	// Chaos mode:    applies a steering force to each enemy's rigid body; the Chaos
	//                solver integrates it (with linear damping) in the world tick.
	// No-Chaos mode: adds the drive impulse straight into the Velocity fragment;
	//                Sim::EnemyIntegrate then applies damping + position integration.
	// Both reach the same terminal speed: EnemyDriveForce / EnemyMass / damping =
	// EnemySpeed. Selected by Ctx.bUseChaosPhysics (GameMode bUseChaosPhysics).
	void EnemySeek(FSimContext& Ctx)
	{
		if (!Ctx.MassEntityManager.IsValid()) return;

		struct FTarget { uint8 Slot; FVector2D Pos; };
		TArray<FTarget> Targets;
		for (const FPlayerState& P : Ctx.Players)
		{
			if (P.bAlive && P.DisableTimer <= 0.0f) Targets.Add({P.Id, P.Position});
		}
		if (Targets.Num() == 0) return;  // No active targets → no drive this tick (damping still bleeds velocity in EnemyIntegrate).
		Targets.Sort([](const FTarget& A, const FTarget& B){ return A.Slot < B.Slot; });

		TArray<FVector2D> TargetPos;
		TargetPos.Reserve(Targets.Num());
		for (const FTarget& T : Targets) TargetPos.Add(T.Pos);

		const bool   bChaos  = Ctx.bUseChaosPhysics;
		const double DriveF  = (double)Constants::EnemyDriveForce;
		// No-Chaos: velocity delta this tick = acceleration * dt = (F / m) * dt.
		const double AccelDt = ((double)Constants::EnemyDriveForce / (double)Constants::EnemyMass) * (double)Constants::SimDt;

		FMassEntityQuery Q(Ctx.MassEntityManager);
		Q.AddTagRequirement<FUvHSEnemyTag>(EMassFragmentPresence::All);
		Q.AddRequirement<FUvHSEnemyPositionFragment>(EMassFragmentAccess::ReadOnly);
		if (bChaos) Q.AddRequirement<FUvHSEnemyChaosBodyFragment>(EMassFragmentAccess::ReadWrite);
		else        Q.AddRequirement<FUvHSEnemyVelocityFragment>(EMassFragmentAccess::ReadWrite);

		FMassExecutionContext ExecContext(*Ctx.MassEntityManager);
		Q.ForEachEntityChunk(ExecContext,
			[&TargetPos, bChaos, DriveF, AccelDt](FMassExecutionContext& ExecCtx)
		{
			const int32 N = ExecCtx.GetNumEntities();
			const auto Positions = ExecCtx.GetFragmentView<FUvHSEnemyPositionFragment>();
			if (bChaos)
			{
				const auto Bodies = ExecCtx.GetFragmentView<FUvHSEnemyChaosBodyFragment>();
				for (int32 i = 0; i < N; ++i)
				{
					const FVector2D Dir = NearestSeekDir(Positions[i].Position, TargetPos);
					if (Dir.IsNearlyZero()) continue;
					if (AUvHSEnemyBody* Actor = Bodies[i].Actor.Get())
					{
						Actor->AddPlanarForce(Dir * DriveF);
					}
				}
			}
			else
			{
				const auto Velocities = ExecCtx.GetMutableFragmentView<FUvHSEnemyVelocityFragment>();
				for (int32 i = 0; i < N; ++i)
				{
					const FVector2D Dir = NearestSeekDir(Positions[i].Position, TargetPos);
					if (Dir.IsNearlyZero()) continue;
					Velocities[i].Velocity += Dir * AccelDt;
				}
			}
		});
	}

	// ---------- Step 5b: EnemyIntegrate (no-Chaos backend only) ----------
	//
	// Hand-rolled stand-in for the Chaos physics tick when bUseChaosPhysics is
	// false: applies linear damping and integrates position from velocity for every
	// enemy each tick (even with no target, so residual velocity keeps bleeding —
	// matching a damped rigid body). Contact-free and deterministic. The runner
	// only calls this in the no-Chaos path.
	void EnemyIntegrate(FSimContext& Ctx)
	{
		if (!Ctx.MassEntityManager.IsValid()) return;

		const double Dt         = (double)Constants::SimDt;
		const double DampFactor = 1.0 / (1.0 + (double)Constants::EnemyLinearDamping * Dt);

		FMassEntityQuery Q(Ctx.MassEntityManager);
		Q.AddTagRequirement<FUvHSEnemyTag>(EMassFragmentPresence::All);
		Q.AddRequirement<FUvHSEnemyPositionFragment>(EMassFragmentAccess::ReadWrite);
		Q.AddRequirement<FUvHSEnemyVelocityFragment>(EMassFragmentAccess::ReadWrite);

		FMassExecutionContext ExecContext(*Ctx.MassEntityManager);
		Q.ForEachEntityChunk(ExecContext,
			[DampFactor, Dt](FMassExecutionContext& ExecCtx)
		{
			const int32 N = ExecCtx.GetNumEntities();
			auto Positions  = ExecCtx.GetMutableFragmentView<FUvHSEnemyPositionFragment>();
			auto Velocities = ExecCtx.GetMutableFragmentView<FUvHSEnemyVelocityFragment>();
			for (int32 i = 0; i < N; ++i)
			{
				const FVector2D V = Velocities[i].Velocity * DampFactor;
				Velocities[i].Velocity  = V;
				Positions[i].Position  += V * Dt;
			}
		});
	}

	// ---------- Step 6: SyncChaosToFragments (read-back from rigid bodies) ----------

	void SyncChaosToFragments(FSimContext& Ctx)
	{
		if (!Ctx.MassEntityManager.IsValid()) return;

		FMassEntityQuery Q(Ctx.MassEntityManager);
		Q.AddTagRequirement<FUvHSEnemyTag>(EMassFragmentPresence::All);
		Q.AddRequirement<FUvHSEnemyPositionFragment>(EMassFragmentAccess::ReadWrite);
		Q.AddRequirement<FUvHSEnemyVelocityFragment>(EMassFragmentAccess::ReadWrite);
		Q.AddRequirement<FUvHSEnemyChaosBodyFragment>(EMassFragmentAccess::ReadOnly);

		FMassExecutionContext ExecContext(*Ctx.MassEntityManager);
		Q.ForEachEntityChunk(ExecContext, [](FMassExecutionContext& ExecCtx)
		{
			const int32 N = ExecCtx.GetNumEntities();
			auto Positions  = ExecCtx.GetMutableFragmentView<FUvHSEnemyPositionFragment>();
			auto Velocities = ExecCtx.GetMutableFragmentView<FUvHSEnemyVelocityFragment>();
			const auto Bodies = ExecCtx.GetFragmentView<FUvHSEnemyChaosBodyFragment>();
			for (int32 i = 0; i < N; ++i)
			{
				if (AUvHSEnemyBody* Actor = Bodies[i].Actor.Get())
				{
					Positions[i].Position = Actor->GetPlanarPosition();
					Velocities[i].Velocity = Actor->GetPlanarVelocity();
				}
			}
		});
	}

	// ---------- Step 7: PlayerEnemyContact ----------

	void PlayerEnemyContact(FSimContext& Ctx)
	{
		if (Ctx.bGodMode || !Ctx.MassEntityManager.IsValid()) return;
		const double KillR = Constants::PlayerKillRadius + Constants::EnemyRadius;
		const double KillR2 = KillR * KillR;

		// Build a flat array of enemy positions once (cheaper than re-iterating
		// the Mass query per player).
		TArray<FVector2D> EnemyPos;
		EnemyPos.Reserve(Ctx.CachedEnemyCount > 0 ? Ctx.CachedEnemyCount : 256);

		FMassEntityQuery Q(Ctx.MassEntityManager);
		Q.AddTagRequirement<FUvHSEnemyTag>(EMassFragmentPresence::All);
		Q.AddRequirement<FUvHSEnemyPositionFragment>(EMassFragmentAccess::ReadOnly);

		FMassExecutionContext ExecContext(*Ctx.MassEntityManager);
		Q.ForEachEntityChunk(ExecContext, [&EnemyPos](FMassExecutionContext& ExecCtx)
		{
			const int32 N = ExecCtx.GetNumEntities();
			const auto Positions = ExecCtx.GetFragmentView<FUvHSEnemyPositionFragment>();
			for (int32 i = 0; i < N; ++i) EnemyPos.Add(Positions[i].Position);
		});

		for (FPlayerState& P : Ctx.Players)
		{
			if (!P.bAlive || P.DisableTimer > 0.0f) continue;
			for (const FVector2D& EP : EnemyPos)
			{
				if ((EP - P.Position).SquaredLength() <= KillR2)
				{
					P.DisableTimer = Constants::DisableDurationSec;
					break;
				}
			}
		}
	}

	// ---------- Step 8: RewindRecord ----------

	void RewindRecord(FSimContext& Ctx)
	{
		if (!Ctx.MassEntityManager.IsValid()) return;
		FRewindRing& Ring = Ctx.Rewind;
		if (Ring.Slots == 0) return;

		const int32 Slot = Ring.Head;
		const int32 SlotStart = Slot * Ring.Stride;
		int32 Written = 0;

		FMassEntityQuery Q(Ctx.MassEntityManager);
		Q.AddTagRequirement<FUvHSEnemyTag>(EMassFragmentPresence::All);
		Q.AddRequirement<FUvHSEnemyIdFragment>(EMassFragmentAccess::ReadOnly);
		Q.AddRequirement<FUvHSEnemyPositionFragment>(EMassFragmentAccess::ReadOnly);

		const int32 Stride = Ring.Stride;
		FMassExecutionContext ExecContext(*Ctx.MassEntityManager);
		Q.ForEachEntityChunk(ExecContext,
			[&Ring, &Written, SlotStart, Stride](FMassExecutionContext& ExecCtx)
		{
			const int32 N = ExecCtx.GetNumEntities();
			const auto Ids       = ExecCtx.GetFragmentView<FUvHSEnemyIdFragment>();
			const auto Positions = ExecCtx.GetFragmentView<FUvHSEnemyPositionFragment>();
			for (int32 i = 0; i < N && Written < Stride; ++i)
			{
				Ring.Ids[SlotStart + Written]       = Ids[i].Id;
				Ring.Positions[SlotStart + Written] = Positions[i].Position;
				++Written;
			}
		});

		Ring.Headers[Slot].Tick  = Ctx.Tick;
		Ring.Headers[Slot].Count = Written;
		Ring.Head = (Slot + 1) % Ring.Slots;
		if (Ring.Count < Ring.Slots) ++Ring.Count;
		Ctx.CachedEnemyCount = Written;
	}

	// ---------- Step 9: CaptureSnapshotFull ----------

	void CaptureSnapshotFull(const FSimContext& Ctx, FSnapshot& OutSnap)
	{
		OutSnap.Reset();
		OutSnap.Kind                   = ESnapshotKind::Full;
		OutSnap.Tick                   = Ctx.Tick;
		OutSnap.BaselineTick           = 0;
		OutSnap.LastProcessedInputTick = 0;       // delta-encoding sets per-recipient
		OutSnap.Round                  = (uint16)FMath::Max(0, Ctx.Round.Round);
		OutSnap.RoundTimer             = Ctx.Round.RoundTimer;
		OutSnap.InterRoundTimer        = Ctx.Round.InterRoundTimer;
		OutSnap.Phase                  = Ctx.Round.Phase;

		OutSnap.Players.Reserve(Ctx.Players.Num());
		for (const FPlayerState& P : Ctx.Players)
		{
			FPlayerSnap Ps;
			Ps.Id           = P.Id;
			Ps.Position     = P.Position;
			Ps.Aim          = P.Aim;
			Ps.bAlive       = P.bAlive;
			Ps.DisableTimer = P.DisableTimer;
			OutSnap.Players.Add(Ps);
		}

		if (Ctx.MassEntityManager.IsValid())
		{
			OutSnap.Enemies.Reserve(Ctx.CachedEnemyCount > 0 ? Ctx.CachedEnemyCount : 256);

			FMassEntityQuery Q(Ctx.MassEntityManager);
			Q.AddTagRequirement<FUvHSEnemyTag>(EMassFragmentPresence::All);
			Q.AddRequirement<FUvHSEnemyIdFragment>(EMassFragmentAccess::ReadOnly);
			Q.AddRequirement<FUvHSEnemyPositionFragment>(EMassFragmentAccess::ReadOnly);

			// const cast — Mass queries take a non-const manager but we only read.
			FMassEntityManager& MgrMut = const_cast<FMassEntityManager&>(*Ctx.MassEntityManager);
			FMassExecutionContext ExecContext(MgrMut);
			Q.ForEachEntityChunk(ExecContext, [&OutSnap](FMassExecutionContext& ExecCtx)
			{
				const int32 N = ExecCtx.GetNumEntities();
				const auto Ids       = ExecCtx.GetFragmentView<FUvHSEnemyIdFragment>();
				const auto Positions = ExecCtx.GetFragmentView<FUvHSEnemyPositionFragment>();
				for (int32 i = 0; i < N; ++i)
				{
					FEnemySnap E;
					E.Id       = Ids[i].Id;
					E.Position = Positions[i].Position;
					OutSnap.Enemies.Add(E);
				}
			});
		}
		OutSnap.EnemyTotalInWorld = (uint32)OutSnap.Enemies.Num();
		OutSnap.RecentFireEvents.Append(Ctx.FireEvents);
	}
}
