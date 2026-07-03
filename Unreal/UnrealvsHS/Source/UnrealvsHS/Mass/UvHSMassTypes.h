// Mass Entity fragments + tags for UnrealvsHS. Each enemy is a Mass entity
// with these fragments + the FUvHSEnemyTag tag. Players stay in TArray on
// FSimContext (only 4 max, ECS overhead isn't worth it).
//
// Mirrors:
//   rust/gameplay/src/game/sim/components.rs  (Enemy, EnemyId, Pos2D, Vel2D)
//   csharp_arch_server/Server/Components.cs   (EnemyTag, EnemyId, Position2D, Velocity2D)
//   DGSvsHS/Assets/_Game/Server/Dots/Components.cs

#pragma once

#include "CoreMinimal.h"
#include "MassEntityTypes.h"
#include "UObject/WeakObjectPtr.h"
#include "UvHSMassTypes.generated.h"


USTRUCT()
struct FUvHSEnemyTag : public FMassTag
{
	GENERATED_BODY()
};

USTRUCT()
struct FUvHSEnemyIdFragment : public FMassFragment
{
	GENERATED_BODY()
	UPROPERTY()
	uint16 Id = 0;
};

USTRUCT()
struct FUvHSEnemyPositionFragment : public FMassFragment
{
	GENERATED_BODY()
	UPROPERTY()
	FVector2D Position = FVector2D::ZeroVector;
};

USTRUCT()
struct FUvHSEnemyVelocityFragment : public FMassFragment
{
	GENERATED_BODY()
	UPROPERTY()
	FVector2D Velocity = FVector2D::ZeroVector;
};


USTRUCT()
struct FUvHSEnemyForceFragment : public FMassFragment
{
	GENERATED_BODY()
	UPROPERTY()
	FVector2D Force = FVector2D::ZeroVector;
};

USTRUCT()
struct FUvHSEnemyChaosBodyFragment : public FMassFragment
{
	GENERATED_BODY()
	// Handle into FSimContext::BodyStore — a raw Chaos particle, NOT an AActor.
	// INDEX_NONE when no physics body (no-Chaos backend, or spawn failed).
	UPROPERTY()
	int32 BodyHandle = -1;
};
