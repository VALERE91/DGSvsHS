#include "UvHSServerGameMode.h"
#include "Gameplay/UvHSConstants.h"
#include "Net/WireTypes.h"
#include "Misc/CommandLine.h"
#include "Misc/Parse.h"
#include "HAL/PlatformTime.h"
#include "MassEntitySubsystem.h"
#include "MassEntityManager.h"
#include "Engine/Engine.h"

using namespace UnrealvsHS;
using namespace UnrealvsHS::Server;

AUvHSServerGameMode::AUvHSServerGameMode()
{
	PrimaryActorTick.bCanEverTick = true;
	PrimaryActorTick.TickGroup    = TG_PrePhysics;
}

void AUvHSServerGameMode::ParseCommandLineOverrides()
{
	const TCHAR* Cmd = FCommandLine::Get();
	int32 Port;     if (FParse::Value(Cmd, TEXT("QuicPort="), Port))         ListenPort = Port;
	int32 SimN;     if (FParse::Value(Cmd, TEXT("SimulatedClients="), SimN)) SimulatedClients = FMath::Clamp(SimN, 0, Constants::MaxPlayers);
	int32 SeedArg;  if (FParse::Value(Cmd, TEXT("Seed="), SeedArg))          RngSeed = SeedArg;
	bool  bGod;     if (FParse::Bool(Cmd, TEXT("GodMode="), bGod))           bGodMode = bGod;
	bool  bChaos;   if (FParse::Bool(Cmd, TEXT("UseChaos="), bChaos))        bUseChaosPhysics = bChaos;
}

void AUvHSServerGameMode::BeginPlay()
{
	Super::BeginPlay();
	ParseCommandLineOverrides();

#if WITH_EDITOR
	// Zero-config PIE smoke test: with no real client (no QUIC on Windows), force
	// one simulated player that stands still and sweep-fires so rounds auto-run.
	if (bAutoSmokeTestInEditor && SimulatedClients <= 0)
	{
		SimulatedClients = 1;
		UE_LOG(LogTemp, Display, TEXT("[UvHSServer] editor smoke test: forcing SimulatedClients=1 (sweep-fire bot)"));
	}
#endif

	if (GEngine) GEngine->SetMaxFPS(125.0f);

	const uint64 Seed = ((uint64)(uint32)RngSeed) | (((uint64)(uint32)RngSeed) << 32);
	Ctx.Initialize(Seed, bGodMode);
	Ctx.bUseChaosPhysics = bUseChaosPhysics;   // select enemy physics backend before any enemy spawns

	if (UMassEntitySubsystem* MassSub = GetWorld()->GetSubsystem<UMassEntitySubsystem>())
	{
		Ctx.AttachMass(MassSub->GetMutableEntityManager().AsShared());
		UE_LOG(LogTemp, Display, TEXT("[UvHSServer] Mass Entity attached (enemy archetype: FUvHSEnemyTag + Id/Pos/Vel/Force/ChaosBody)"));
	}
	else
	{
		UE_LOG(LogTemp, Error, TEXT("[UvHSServer] UMassEntitySubsystem not found — enemy storage will be uninitialized"));
	}
	
	Ctx.AttachWorld(GetWorld());

	StartupSeconds = FPlatformTime::Seconds();

	UE_LOG(LogTemp, Display, TEXT("[UvHSServer] boot port=%d seed=0x%llX godMode=%d simClients=%d sim=%dms (%.1f Hz)"),
		ListenPort, Seed, bGodMode ? 1 : 0, SimulatedClients,
		Constants::SimTickMs, Constants::TicksPerSecond);
	UE_LOG(LogTemp, Display, TEXT("[UvHSServer] enemy physics backend: %s"),
		bUseChaosPhysics ? TEXT("Chaos rigid bodies") : TEXT("hand-rolled force integration (no Chaos)"));

	if (SimulatedClients > 0)
	{
#if !UE_BUILD_SHIPPING
		Ctx.bSimulatedInput = true;   // sweep-fire smoke test (dev/editor only)
#endif
		for (uint8 i = 0; i < SimulatedClients; ++i) Ctx.SpawnPlayer(i);
		Ctx.Round.Phase           = Wire::ERoundPhase::InterRound;
		Ctx.Round.Round           = 0;
		Ctx.Round.InterRoundTimer = Constants::InterRoundDelaySec;
		Ctx.State                 = EServerLifecycle::Running;
		UE_LOG(LogTemp, Display, TEXT("[UvHSServer] state: Booting → Running (spawned %d sim clients)"), SimulatedClients);
	}
	else
	{
		Ctx.State = EServerLifecycle::Idle;
		UE_LOG(LogTemp, Display, TEXT("[UvHSServer] state: Booting → Idle (waiting for QUIC clients on port %d)"), ListenPort);
	}
	
	Quic.OnClientConnected = [this](uint8 PlayerId)
	{
		Ctx.SpawnPlayer(PlayerId);
		UE_LOG(LogTemp, Display, TEXT("[UvHSServer] client connected → slot %u (players=%d)"), PlayerId, Ctx.Players.Num());
		if (Ctx.State == EServerLifecycle::Idle)
		{
			Ctx.Round.Phase           = Wire::ERoundPhase::InterRound;
			Ctx.Round.Round           = 0;
			Ctx.Round.InterRoundTimer = Constants::InterRoundDelaySec;
			Ctx.State                 = EServerLifecycle::Running;
			UE_LOG(LogTemp, Display, TEXT("[UvHSServer] state: Idle → Running"));
		}
	};
	Quic.OnClientDisconnected = [this](uint8 PlayerId)
	{
		Ctx.DespawnPlayer(PlayerId);
		UE_LOG(LogTemp, Display, TEXT("[UvHSServer] client disconnected ← slot %u (players=%d)"), PlayerId, Ctx.Players.Num());
		if (Ctx.Players.Num() == 0 && Ctx.State == EServerLifecycle::Running)
		{
			Ctx.State = EServerLifecycle::Resetting;
			History.Clear();
			Ctx.ResetForIdle();
			UE_LOG(LogTemp, Display, TEXT("[UvHSServer] state: Running → Resetting (last client gone)"));
			
			Ctx.State = EServerLifecycle::Idle;
			UE_LOG(LogTemp, Display, TEXT("[UvHSServer] state: Resetting → Idle (ready for next client)"));
		}
	};
	Quic.OnInputReceived = [this](uint8 PlayerId, const Wire::FInputCmd& Cmd)
	{
		FTickInput In;
		In.PlayerId            = PlayerId;
		In.Tick                = Cmd.Tick;
		In.LastAckedServerTick = Cmd.LastAckedServerTick;
		In.Move                = Cmd.Move;
		In.Aim                 = Cmd.Aim;
		In.Flags               = Cmd.Flags;
		Ctx.TickInputs.Add(In);
	};

	const bool bQuicOk = Quic.Start((uint16)ListenPort);
	if (!bQuicOk)
	{
		UE_LOG(LogTemp, Error, TEXT("[UvHSServer] QuicServer start failed — server will run sim but accept no clients (check msquic.dll deployment)"));
	}
}

void AUvHSServerGameMode::Tick(float DeltaSeconds)
{
	Super::Tick(DeltaSeconds);
	
	Quic.PollEvents();

	Quic.SetServerTick(Ctx.Tick);
	const int32 InnerSteps = Runner.AdvanceWallTime(Ctx, DeltaSeconds);

	if (InnerSteps > 0)
	{
		History.Record(Runner.GetLastSnapshot());
		Quic.BroadcastSnapshot(Runner.GetLastSnapshot(), History);
	}

	Stats.Update(Ctx, InnerSteps, DeltaSeconds);

	HeartbeatAcc += DeltaSeconds;
	if (HeartbeatIntervalSec > 0.0f && HeartbeatAcc >= HeartbeatIntervalSec)
	{
		LogHeartbeat(HeartbeatAcc);
		HeartbeatAcc = 0.0f;
		Runner.ResetTickStats();
	}
}

void AUvHSServerGameMode::EndPlay(const EEndPlayReason::Type Reason)
{
	UE_LOG(LogTemp, Display, TEXT("[UvHSServer] EndPlay reason=%d tick=%u"), (int32)Reason, Ctx.Tick);
	Ctx.State = EServerLifecycle::ShuttingDown;
	Quic.Stop();
	Super::EndPlay(Reason);
}

void AUvHSServerGameMode::LogHeartbeat(float Elapsed)
{
	const int32 Alive   = Ctx.CachedEnemyCount;
	const int32 Players = Ctx.AlivePlayerCount();
	const double AvgMs  = Runner.GetTickCountSince() > 0
		? Runner.GetTickWallMsSum() / Runner.GetTickCountSince()
		: 0.0;
	const float Uptime  = (float)(FPlatformTime::Seconds() - StartupSeconds);

	const TCHAR* PhaseName =
		Ctx.Round.Phase == Wire::ERoundPhase::PreGame    ? TEXT("PreGame") :
		Ctx.Round.Phase == Wire::ERoundPhase::InterRound ? TEXT("InterRound") :
		Ctx.Round.Phase == Wire::ERoundPhase::InRound    ? TEXT("InRound") :
		Ctx.Round.Phase == Wire::ERoundPhase::Victory    ? TEXT("Victory") :
		                                                   TEXT("Defeat");

	UE_LOG(LogTemp, Display,
		TEXT("[UvHSServer] up=%.1fs tick=%u state=%d phase=%s round=%d alive=%d players=%d ms/tick=%.2f (ticks=%d)"),
		Uptime, Ctx.Tick, (int32)Ctx.State, PhaseName, Ctx.Round.Round, Alive, Players,
		AvgMs, Runner.GetTickCountSince());
}
