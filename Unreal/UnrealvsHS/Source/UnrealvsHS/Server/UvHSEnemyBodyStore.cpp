#include "UvHSEnemyBodyStore.h"
#include "Gameplay/UvHSConstants.h"
#include "Engine/World.h"
#ifdef min
#undef min
#endif
#ifdef max
#undef max
#endif

#include "Physics/Experimental/PhysScene_Chaos.h"
#include "PBDRigidsSolver.h"
#include "PhysicsProxy/SingleParticlePhysicsProxy.h"
#include "Chaos/ParticleHandle.h"
#include "Chaos/Sphere.h"
#include "Chaos/ImplicitObject.h"
#include "Chaos/CollisionFilterData.h"

namespace UnrealvsHS::Server
{
	using namespace Chaos;

	static FORCEINLINE FPBDRigidsSolver* AsSolver(void* S) { return static_cast<FPBDRigidsSolver*>(S); }

	void FUvHSEnemyBodyStore::Initialize(UWorld* World)
	{
		if (!World) return;
		FPhysScene* Scene = World->GetPhysicsScene();
		Solver = Scene ? Scene->GetSolver() : nullptr;
	}

	int32 FUvHSEnemyBodyStore::AllocSlot(const FBody& Body)
	{
		if (FreeList.Num() > 0)
		{
			const int32 Idx = FreeList.Pop(EAllowShrinking::No);
			Bodies[Idx] = Body;
			return Idx;
		}
		return Bodies.Add(Body);
	}

	int32 FUvHSEnemyBodyStore::CreateSphere(FVector2D PosM, bool bKinematic)
	{
		FPBDRigidsSolver* S = AsSolver(Solver);
		if (!S) return INDEX_NONE;
		
		FSingleParticlePhysicsProxy* Proxy =
			FSingleParticlePhysicsProxy::Create(FPBDRigidParticle::CreateParticle());

		FRigidBodyHandle_External& Body = Proxy->GetGameThreadAPI();
		
		const FReal Radius = bKinematic ? (FReal)Constants::PlayerRadius : (FReal)Constants::EnemyRadius;
		FImplicitObjectPtr Geom = MakeImplicitObjectPtr<TSphere<FReal, 3>>(FVec3(0.0), Radius);
		Body.SetGeometry(Geom);
		
		{
			FCollisionFilterData SimFilter;
			SimFilter.Word0 = 0xFFFFFFFFu;
			SimFilter.Word1 = 0xFFFFFFFFu;
			SimFilter.Word2 = 0xFFFFFFFFu;
			SimFilter.Word3 = 0xFFFFFFFFu;
			for (const TUniquePtr<FPerShapeData>& Shape : Body.ShapesArray())
			{
				if (!Shape) continue;
				Shape->SetSimEnabled(true);
				Shape->SetQueryEnabled(false);
				Shape->SetSimData(SimFilter);
			}
		}

		Body.SetX(FVec3(PosM.X, PosM.Y, 0.0));
		Body.SetR(FRotation3::FromIdentity());
		Body.SetGravityEnabled(false);

		if (bKinematic)
		{
			Body.SetObjectState(EObjectStateType::Kinematic);
		}
		else
		{
			const FReal M = (FReal)Constants::EnemyMass;
			Body.SetM(M);
			Body.SetInvM(M > 0 ? 1.0 / M : 0.0);
			Body.SetI(TVec3<FRealSingle>(1, 1, 1));
			Body.SetInvI(TVec3<FRealSingle>(0, 0, 0));
			Body.SetLinearEtherDrag((FRealSingle)Constants::EnemyLinearDamping);
			Body.SetObjectState(EObjectStateType::Dynamic);
		}

		S->RegisterObject(Proxy);

		return AllocSlot({ Proxy, bKinematic });
	}

	int32 FUvHSEnemyBodyStore::CreateEnemy(FVector2D PosM)  { return CreateSphere(PosM, /*bKinematic*/ false); }
	int32 FUvHSEnemyBodyStore::CreatePlayer(FVector2D PosM) { return CreateSphere(PosM, /*bKinematic*/ true);  }

	void FUvHSEnemyBodyStore::Destroy(int32 Handle)
	{
		if (!ValidHandle(Handle)) return;
		FPBDRigidsSolver* S = AsSolver(Solver);
		if (S && Bodies[Handle].Proxy)
		{
			S->UnregisterObject(Bodies[Handle].Proxy);
		}
		Bodies[Handle].Proxy = nullptr;
		FreeList.Add(Handle);
	}

	void FUvHSEnemyBodyStore::ApplyForce(int32 Handle, FVector2D ForceN)
	{
		if (!ValidHandle(Handle) || Bodies[Handle].bKinematic) return;
		FRigidBodyHandle_External& Body = Bodies[Handle].Proxy->GetGameThreadAPI();
		Body.AddForce(FVec3(ForceN.X, ForceN.Y, 0.0));
	}

	void FUvHSEnemyBodyStore::SetKinematicPosition(int32 Handle, FVector2D PosM)
	{
		if (!ValidHandle(Handle)) return;
		FRigidBodyHandle_External& Body = Bodies[Handle].Proxy->GetGameThreadAPI();
		Body.SetX(FVec3(PosM.X, PosM.Y, 0.0));
	}

	bool FUvHSEnemyBodyStore::GetState(int32 Handle, FVector2D& OutPosM, FVector2D& OutVelM) const
	{
		if (!ValidHandle(Handle)) return false;
		const FRigidBodyHandle_External& Body = Bodies[Handle].Proxy->GetGameThreadAPI();
		const FVec3 X = Body.X();
		const FVec3 V = Body.V();
		OutPosM = FVector2D(X.X, X.Y);
		OutVelM = FVector2D(V.X, V.Y);
		return true;
	}

	bool FUvHSEnemyBodyStore::IsSleeping(int32 Handle) const
	{
		if (!ValidHandle(Handle) || Bodies[Handle].bKinematic) return false;
		const FRigidBodyHandle_External& Body = Bodies[Handle].Proxy->GetGameThreadAPI();
		return Body.ObjectState() == EObjectStateType::Sleeping;
	}

	void FUvHSEnemyBodyStore::Shutdown()
	{
		FPBDRigidsSolver* S = AsSolver(Solver);
		if (S)
		{
			for (FBody& B : Bodies)
			{
				if (B.Proxy) S->UnregisterObject(B.Proxy);
				B.Proxy = nullptr;
			}
		}
		Bodies.Reset();
		FreeList.Reset();
		Solver = nullptr;
	}
}
