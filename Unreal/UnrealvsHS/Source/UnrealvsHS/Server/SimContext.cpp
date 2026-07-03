#include "SimContext.h"
#include "Gameplay/UvHSConstants.h"
#include "Mass/UvHSMassTypes.h"
#include "MassEntityManager.h"
#include "MassArchetypeTypes.h"
#include "MassCommonTypes.h"
#include "MassExecutionContext.h"
#include "Engine/World.h"

namespace UnrealvsHS::Server
{
	using namespace Constants;

	// ---------- FRewindRing ----------

	void FRewindRing::Initialize(int32 InSlots, int32 InStride)
	{
		Slots  = FMath::Max(1, InSlots);
		Stride = FMath::Max(1, InStride);
		Head   = 0;
		Count  = 0;
		Headers.Init(FRewindFrameHeader{}, Slots);
		Ids.Init(0, Slots * Stride);
		Positions.Init(FVector2D::ZeroVector, Slots * Stride);
	}

	// ---------- FSimContext ----------
	static constexpr int32 RewindRingStride = 16384;

	void FSimContext::Initialize(uint64 InSeed, bool bInGodMode)
	{
		Seed     = InSeed;
		bGodMode = bInGodMode;
		Rng      = FDeterministicRng::FromSeed(InSeed);

		Tick = 0;
		Round = FRoundState();
		NextEnemyId = 0;
		State = EServerLifecycle::Booting;

		Players.Reset();
		TickInputs.Reset();
		PendingFires.Reset();
		FireEvents.Reset();

		ProcessedInputTick.Init(0u, Constants::MaxPlayers);
		PlayerRttMs.Init(60.0f, Constants::MaxPlayers);
		Rewind.Initialize(Constants::SnapshotHistoryTicks, RewindRingStride);
		CachedEnemyCount = 0;
	}

	void FSimContext::AttachMass(TSharedRef<FMassEntityManager> InManager)
	{
		MassEntityManager = InManager;
		
		TArray<const UScriptStruct*> Fragments;
		Fragments.Add(FUvHSEnemyIdFragment::StaticStruct());
		Fragments.Add(FUvHSEnemyPositionFragment::StaticStruct());
		Fragments.Add(FUvHSEnemyVelocityFragment::StaticStruct());
		Fragments.Add(FUvHSEnemyForceFragment::StaticStruct());
		Fragments.Add(FUvHSEnemyChaosBodyFragment::StaticStruct());

		FMassArchetypeCompositionDescriptor Composition;
		for (const UScriptStruct* F : Fragments) Composition.GetFragments().Add(*F);
		Composition.GetTags().Add(*FUvHSEnemyTag::StaticStruct());

		FMassArchetypeHandle Handle = InManager->CreateArchetype(Composition);
		EnemyArchetype = MakeShared<FMassArchetypeHandle>(Handle);
	}

	void FSimContext::AttachWorld(UWorld* InWorld)
	{
		World = InWorld;
		if (bUseChaosPhysics)
		{
			BodyStore.Initialize(InWorld);
		}
	}

	void FSimContext::ResetForIdle()
	{
		Tick = 0;
		Round = FRoundState();
		Rng = FDeterministicRng::FromSeed(Seed);
		NextEnemyId = 0;
		DestroyAllEnemies();
		TickInputs.Reset();
		PendingFires.Reset();
		FireEvents.Reset();
		for (int32 i = 0; i < ProcessedInputTick.Num(); ++i) ProcessedInputTick[i] = 0;
		for (int32 i = 0; i < PlayerRttMs.Num();         ++i) PlayerRttMs[i]         = 60.0f;
		Rewind.Clear();
		CachedEnemyCount = 0;
	}

	void FSimContext::SpawnEnemy(uint16 Id, FVector2D Pos)
	{
		if (!MassEntityManager.IsValid() || !EnemyArchetype.IsValid()) return;
		FMassEntityHandle Entity = MassEntityManager->CreateEntity(*EnemyArchetype);

		FUvHSEnemyIdFragment&       IdF  = MassEntityManager->GetFragmentDataChecked<FUvHSEnemyIdFragment>(Entity);
		FUvHSEnemyPositionFragment& PosF = MassEntityManager->GetFragmentDataChecked<FUvHSEnemyPositionFragment>(Entity);
		FUvHSEnemyVelocityFragment& VelF = MassEntityManager->GetFragmentDataChecked<FUvHSEnemyVelocityFragment>(Entity);
		FUvHSEnemyForceFragment&    FrcF = MassEntityManager->GetFragmentDataChecked<FUvHSEnemyForceFragment>(Entity);
		FUvHSEnemyChaosBodyFragment& BodyF = MassEntityManager->GetFragmentDataChecked<FUvHSEnemyChaosBodyFragment>(Entity);
		IdF.Id        = Id;
		PosF.Position = Pos;
		VelF.Velocity = FVector2D::ZeroVector;
		FrcF.Force    = FVector2D::ZeroVector;
		
		// Chaos backend: a raw dynamic Chaos particle in the solver (no AActor). The
		// hand-rolled backend leaves BodyHandle = -1 and moves enemies via Mass only.
		BodyF.BodyHandle = INDEX_NONE;
		if (bUseChaosPhysics && BodyStore.IsValid())
		{
			BodyF.BodyHandle = BodyStore.CreateEnemy(Pos);
		}
		++CachedEnemyCount;
	}

	void FSimContext::DestroyAllEnemies()
	{
		if (!MassEntityManager.IsValid() || !EnemyArchetype.IsValid()) return;
		
		FMassEntityQuery Q(MassEntityManager);
		Q.AddTagRequirement<FUvHSEnemyTag>(EMassFragmentPresence::All);
		Q.AddRequirement<FUvHSEnemyIdFragment>(EMassFragmentAccess::ReadOnly);
		Q.AddRequirement<FUvHSEnemyChaosBodyFragment>(EMassFragmentAccess::ReadOnly);

		TArray<FMassEntityHandle> Handles;
		TArray<int32> BodiesToDestroy;
		FMassExecutionContext ExecContext(*MassEntityManager);
		Q.ForEachEntityChunk(ExecContext, [&Handles, &BodiesToDestroy](FMassExecutionContext& ExecCtx)
		{
			const int32 N = ExecCtx.GetNumEntities();
			const auto Bodies = ExecCtx.GetFragmentView<FUvHSEnemyChaosBodyFragment>();
			for (int32 i = 0; i < N; ++i)
			{
				Handles.Add(ExecCtx.GetEntity(i));
				BodiesToDestroy.Add(Bodies[i].BodyHandle);
			}
		});
		for (const int32 H : BodiesToDestroy)
		{
			if (H != INDEX_NONE) BodyStore.Destroy(H);
		}
		if (Handles.Num() > 0)
		{
			MassEntityManager->BatchDestroyEntities(Handles);
		}
		CachedEnemyCount = 0;
	}

	int32 FSimContext::PlayerIndexBySlot(uint8 Slot) const
	{
		for (int32 i = 0; i < Players.Num(); ++i)
		{
			if (Players[i].Id == Slot) return i;
		}
		return INDEX_NONE;
	}

	void FSimContext::SpawnPlayer(uint8 Slot)
	{
		if (PlayerIndexBySlot(Slot) != INDEX_NONE) return;
		const float Angle = ((float)Slot / (float)Constants::MaxPlayers) * 2.0f * PI;
		const float R     = Constants::ArenaRadius * 0.3f;
		FPlayerState P;
		P.Id           = Slot;
		P.bAlive       = true;
		P.Position     = FVector2D(FMath::Cos(Angle) * R, FMath::Sin(Angle) * R);
		P.Aim          = FVector2D(1.0, 0.0);
		P.FireCooldown = 0.0f;
		P.DisableTimer = 0.0f;
		// Kinematic Chaos body so dynamic enemies collide/pile against the player,
		// matching Bevy (RigidBody::Kinematic + collider) and DGS. Driven by input
		// each tick via Sim::SyncChaosToFragments (SetKinematicPosition).
		if (bUseChaosPhysics && BodyStore.IsValid())
		{
			P.BodyHandle = BodyStore.CreatePlayer(P.Position);
		}
		Players.Add(P);
	}

	void FSimContext::DespawnPlayer(uint8 Slot)
	{
		const int32 Idx = PlayerIndexBySlot(Slot);
		if (Idx != INDEX_NONE)
		{
			if (Players[Idx].BodyHandle != INDEX_NONE) BodyStore.Destroy(Players[Idx].BodyHandle);
			Players.RemoveAt(Idx);
		}
	}

	int32 FSimContext::AlivePlayerCount() const
	{
		int32 N = 0;
		for (const FPlayerState& P : Players) if (P.bAlive) ++N;
		return N;
	}
}
