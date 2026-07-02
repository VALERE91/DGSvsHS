#if WITH_DGS
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using DGSvsHS.Gameplay;

namespace DGSvsHS.Server.Dots
{
    // ---------- Enemy archetype ----------
    public struct EnemyTag : IComponentData { }
    
    public struct EnemyId : IComponentData
    {
        public ushort Value;
    }
    
    public struct Position2D : IComponentData
    {
        public float2 Value;
    }
    
    public struct Velocity2D : IComponentData
    {
        public float2 Value;
    }

    // ---------- Player archetype ----------
    
    public struct PlayerTag : IComponentData { }
    
    public struct PlayerSlot : IComponentData
    {
        public byte Value;
    }
    
    public struct Aim2D : IComponentData
    {
        public float2 Value;
    }

    public struct FireCooldown : IComponentData
    {
        public float Seconds;
    }

    public struct DisableTimer : IComponentData
    {
        public float Seconds;
    }

    public struct Alive : IComponentData
    {
        public byte Value;
        public bool Bool => Value != 0;
        public static Alive From(bool b) => new Alive { Value = (byte)(b ? 1 : 0) };
    }

    // ---------- Sim-global singletons (one entity, one of each) ----------

    public struct WorldClock : IComponentData
    {
        public uint Tick;
    }

    public struct RoundState : IComponentData
    {
        public int Round;
        public RoundPhase Phase;
        public float RoundTimer;
        public float InterRoundTimer;
        public int SpawnTarget;
        public int SpawnsRemaining;
        public float SpawnInterval;
        public float SpawnAccumulator;
    }

    public struct SimRng : IComponentData
    {
        public DeterministicRng Rng;
    }

    public struct NextEnemyId : IComponentData
    {
        public ushort Value;
    }

    public struct GodModeFlag : IComponentData
    {
        public byte Value;
        public bool On => Value != 0;
    }

    // ---------- Input bridge ----------
    
    public struct TickInput : IBufferElementData
    {
        public byte PlayerId;
        public uint Tick;
        public uint LastAckedServerTick;
        public float2 Move;
        public float2 Aim;
        public InputFlags Flags;
        public bool Fire => (Flags & InputFlags.Fire) != 0;
    }
    
    public struct PendingFire : IBufferElementData
    {
        public byte PlayerId;
        public uint ClientInputTick;
        public float2 Origin;
        public float2 Direction;
    }
    
    public struct FireEventBuf : IBufferElementData
    {
        public uint Tick;
        public byte ShooterId;
        public float2 Origin;
        public float2 Direction;
        public float Distance;
        public byte KillCount;

        public FireEvent ToWire() => new FireEvent
        {
            Tick = Tick,
            ShooterId = ShooterId,
            Origin = new UnityEngine.Vector2(Origin.x, Origin.y),
            Direction = new UnityEngine.Vector2(Direction.x, Direction.y),
            Distance = Distance,
            KillCount = KillCount,
        };
    }

    // ---------- Rewind ring (server-side hit resolution against past world states) ----------
    
    public struct RewindRingMeta : IComponentData
    {
        public int Head;
        public int Count;
        public int Stride;
        public int CellsPerSide;
        public int CellCount;
    }

    public struct RewindFrameHeader : IBufferElementData
    {
        public uint Tick;
        public int Count;
    }

    public struct RewindId : IBufferElementData
    {
        public ushort Value;
    }

    public struct RewindPos : IBufferElementData
    {
        public float2 Value;
    }

    // CSR-style cell offsets per ring slot, mirrors Bevy RewindFrame.cells.
    // Per slot the layout is Offset[0..CellCount] with Offset[c+1] - Offset[c] =
    // number of enemies bucketed into cell c, and entries in [Offset[c]..Offset[c+1])
    // of Ids/Positions belong to that cell. Slot s occupies indices
    // [s*(CellCount+1) .. (s+1)*(CellCount+1)).
    public struct RewindCellOffset : IBufferElementData
    {
        public int Value;
    }
}
#endif
