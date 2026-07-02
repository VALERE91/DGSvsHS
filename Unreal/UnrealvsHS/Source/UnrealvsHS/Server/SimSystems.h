
#pragma once

#include "CoreMinimal.h"
#include "SimContext.h"

namespace UnrealvsHS::Server::Sim
{
	void TickAdvance(FSimContext& Ctx);
	
	void RoundDirector(FSimContext& Ctx);

#if !UE_BUILD_SHIPPING
	void SimulatedClientInput(FSimContext& Ctx);   // dev/editor smoke test only
#endif
	
	void PlayerInput(FSimContext& Ctx);
	
	void ResolveFiresCurrentTick(FSimContext& Ctx);
	
	void RewindResolve(FSimContext& Ctx);
	
	void RewindRecord(FSimContext& Ctx);
	
	void EnemySeek(FSimContext& Ctx);

	void EnemyIntegrate(FSimContext& Ctx);
	
	void SyncChaosToFragments(FSimContext& Ctx);
	
	void PlayerEnemyContact(FSimContext& Ctx);
	
	void CaptureSnapshotFull(const FSimContext& Ctx, Wire::FSnapshot& OutSnap);
}
