#pragma once

#include "CoreMinimal.h"

class UWorld;

namespace Chaos { class FSingleParticlePhysicsProxy; }

namespace UnrealvsHS::Server
{
	class FUvHSEnemyBodyStore
	{
	public:
		void Initialize(UWorld* World);
		
		void Shutdown();

		bool IsValid() const { return Solver != nullptr; }
		
		int32 CreateEnemy(FVector2D PosM);
		
		int32 CreatePlayer(FVector2D PosM);

		void Destroy(int32 Handle);
		
		void ApplyForce(int32 Handle, FVector2D ForceN);
		
		void SetKinematicPosition(int32 Handle, FVector2D PosM);
		
		bool GetState(int32 Handle, FVector2D& OutPosM, FVector2D& OutVelM) const;

		// True when Chaos has put this dynamic body to sleep (jammed core with
		// ~zero velocity). Used to gate EnemySeek's drive so sleeping bodies stay
		// asleep and drop out of the solve island. Kinematic/invalid → false.
		bool IsSleeping(int32 Handle) const;

	private:
		struct FBody
		{
			Chaos::FSingleParticlePhysicsProxy* Proxy = nullptr;
			bool bKinematic = false;
		};

		int32 CreateSphere(FVector2D PosM, bool bKinematic);
		int32 AllocSlot(const FBody& Body);
		bool  ValidHandle(int32 Handle) const
		{
			return Bodies.IsValidIndex(Handle) && Bodies[Handle].Proxy != nullptr;
		}

		void*       Solver = nullptr;
		TArray<FBody> Bodies;
		TArray<int32> FreeList;
	};
}
