

#pragma once

#include "CoreMinimal.h"
#include "Gameplay/DeterministicRng.h"
#include "Net/WireTypes.h"
#include "UObject/WeakObjectPtr.h"

struct FMassEntityManager;
struct FMassArchetypeHandle;
class  UWorld;

namespace UnrealvsHS::Server
{
	// ---------- Player ----------
	struct FPlayerState
	{
		uint8     Id              = 0;
		bool      bAlive          = true;
		FVector2D Position        = FVector2D::ZeroVector;
		FVector2D Aim             = FVector2D(1.0, 0.0);
		float     FireCooldown    = 0.0f;
		float     DisableTimer    = 0.0f;
	};
	

	// ---------- Round state ----------
	struct FRoundState
	{
		int32                  Round            = 0;
		Wire::ERoundPhase      Phase            = Wire::ERoundPhase::PreGame;
		float                  RoundTimer       = 0.0f;
		float                  InterRoundTimer  = 0.0f;
		int32                  SpawnTarget      = 0;
		int32                  SpawnsRemaining  = 0;
		float                  SpawnInterval    = 0.0f;
		float                  SpawnAccumulator = 0.0f;
	};

	// ---------- Per-player input buffered between ticks ----------
	struct FTickInput
	{
		uint8                  PlayerId            = 0;
		uint32                 Tick                = 0;
		uint32                 LastAckedServerTick = 0;
		FVector2D              Move                = FVector2D::ZeroVector;
		FVector2D              Aim                 = FVector2D(1.0, 0.0);
		Wire::EInputFlags      Flags               = Wire::EInputFlags::None;
	};

	// ---------- A pending fire to be resolved by RewindResolve / hitscan ----------
	struct FPendingFire
	{
		uint8     PlayerId        = 0;
		uint32    ClientInputTick = 0;
		FVector2D Origin          = FVector2D::ZeroVector;
		FVector2D Direction       = FVector2D(1.0, 0.0);
	};

	// ---------- Lag-comp ring ----------
	struct FRewindFrameHeader
	{
		uint32 Tick   = 0;
		int32  Count  = 0;
	};

	class FRewindRing
	{
	public:
		int32  Slots  = 0;
		int32  Stride = 0;
		int32  Head   = 0;
		int32  Count  = 0;

		TArray<FRewindFrameHeader> Headers;
		TArray<uint16>             Ids;
		TArray<FVector2D>          Positions;

		void Initialize(int32 InSlots, int32 InStride);
		void Clear()
		{
			Head = 0;
			Count = 0;
			for (FRewindFrameHeader& H : Headers) { H.Tick = 0; H.Count = 0; }
		}
	};

	// ---------- Lifecycle ----------
	enum class EServerLifecycle : uint8
	{
		Booting       = 0,
		Idle          = 1,
		Running       = 2,
		Resetting     = 3,
		ShuttingDown  = 4,
	};

	// ---------- Everything the sim systems need ----------
	class FSimContext
	{
	public:
		// World tick (mirrors Arch's SimContext.Tick).
		uint32                Tick = 0;

		FRoundState           Round;
		FDeterministicRng     Rng;
		uint16                NextEnemyId = 0;

		bool                  bGodMode = false;
		uint64                Seed = 0;

		// Enemy physics backend. true = Chaos rigid bodies (AUvHSEnemyBody actors).
		// false = hand-rolled force integration in Sim::EnemySeek + EnemyIntegrate,
		// no Chaos bodies spawned at all (zero contact/solver cost). Set from the
		// server GameMode's bUseChaosPhysics before the sim runs.
		bool                  bUseChaosPhysics = false;

#if !UE_BUILD_SHIPPING
		// Editor/dev loopback smoke-test: when true, spawned players stand still and
		// sweep-fire a full circle every tick so rounds auto-advance with no network
		// client. Set from SimulatedClients > 0 in the server GameMode. Compiled out
		// of Shipping entirely — Shipping serves real clients only.
		bool                  bSimulatedInput   = false;
		float                 SimulatedAimAngle = 0.0f;
#endif

		EServerLifecycle      State = EServerLifecycle::Booting;

		TArray<FPlayerState>  Players;
		
		TSharedPtr<FMassEntityManager>     MassEntityManager;
		TSharedPtr<FMassArchetypeHandle>   EnemyArchetype;
		int32                              CachedEnemyCount = 0;  

		TWeakObjectPtr<UWorld>             World;
		
		TArray<FTickInput>    TickInputs;
		
		TArray<FPendingFire>  PendingFires;
		
		TArray<Wire::FFireEvent> FireEvents;
		
		TArray<uint32>        ProcessedInputTick;
		
		TArray<float>         PlayerRttMs;
		
		FRewindRing           Rewind;

		void Initialize(uint64 InSeed, bool bInGodMode);

		void AttachMass(TSharedRef<FMassEntityManager> InManager);

		void AttachWorld(UWorld* InWorld);

		void ResetForIdle();
		
		int32 PlayerIndexBySlot(uint8 Slot) const;
		void  SpawnPlayer(uint8 Slot);
		void  DespawnPlayer(uint8 Slot);
		int32 AlivePlayerCount() const;
		
		void  SpawnEnemy(uint16 Id, FVector2D Pos);
		
		void  DestroyAllEnemies();
	};
}
