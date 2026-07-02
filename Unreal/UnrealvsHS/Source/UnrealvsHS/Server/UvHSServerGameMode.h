
#pragma once

#include "CoreMinimal.h"
#include "GameFramework/GameModeBase.h"
#include "SimContext.h"
#include "SimRunner.h"
#include "StatsWriter.h"
#include "WorldStateHistory.h"
#include "Net/QuicServer.h"
#include "UvHSServerGameMode.generated.h"

UCLASS()
class UNREALVSHS_API AUvHSServerGameMode : public AGameModeBase
{
	GENERATED_BODY()

public:
	AUvHSServerGameMode();

	// CLI / project-settings configurable.
	UPROPERTY(EditAnywhere, Category="Server")
	int32 ListenPort = 7777;

	UPROPERTY(EditAnywhere, Category="Server")
	int32 SimulatedClients = 0;          // bootstrap N players without real network (Phase 2A trial)

#if WITH_EDITORONLY_DATA
	UPROPERTY(EditAnywhere, Category="Server")
	bool bAutoSmokeTestInEditor = true;  // editor-only: if SimulatedClients==0, force 1 sweep-firing bot so PIE auto-runs rounds 1→10 with no client. Not present in Shipping.
#endif

	UPROPERTY(EditAnywhere, Category="Server")
	int32 RngSeed = (int32)0xC0FFEE;     // truncated u64 for inspector; full seed used at runtime

	UPROPERTY(EditAnywhere, Category="Server")
	bool bGodMode = true;                 // disable contact-disable for compute-only trials

	UPROPERTY(EditAnywhere, Category="Server")
	bool bUseChaosPhysics = false;        // false = hand-rolled force integration (no Chaos, no contacts, O(N)); true = Chaos rigid bodies. CLI: -UseChaos=true|false

	UPROPERTY(EditAnywhere, Category="Server")
	float HeartbeatIntervalSec = 1.0f;    // console log cadence (separate from /tmp/stats.log @ 20 Hz)

	virtual void BeginPlay() override;
	virtual void Tick(float DeltaSeconds) override;
	virtual void EndPlay(const EEndPlayReason::Type Reason) override;

private:
	void ParseCommandLineOverrides();
	void LogHeartbeat(float Elapsed);

	UnrealvsHS::Server::FSimContext        Ctx;
	UnrealvsHS::Server::FSimRunner         Runner;
	UnrealvsHS::Server::FStatsWriter       Stats;
	UnrealvsHS::Server::FWorldStateHistory History {UnrealvsHS::Constants::SnapshotHistoryTicks};
	UnrealvsHS::Net::FQuicServer           Quic;

	float        HeartbeatAcc       = 0.0f;
	double       StartupSeconds     = 0.0;
};
