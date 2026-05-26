using UnityEngine;

namespace DGSvsHS.Gameplay
{
    public struct PlayerState
    {
        public byte Id;
        public Vector2 Position;
        public Vector2 Aim;
        public float FireCooldown;
        public float DisableTimer;
        public bool Alive;

        public bool IsDisabled => DisableTimer > 0f;

        public static PlayerState Spawn(byte id, Vector2 position) => new PlayerState
        {
            Id = id,
            Position = position,
            Aim = Vector2.right,
            FireCooldown = 0f,
            DisableTimer = 0f,
            Alive = true,
        };
    }
    
    public struct FireEvent
    {
        public uint Tick;
        public byte ShooterId;
        public Vector2 Origin;
        public Vector2 Direction;
        public float Distance;
        public byte KillCount;
    }
    
    public enum RoundPhase : byte
    {
        PreGame = 0,
        InRound = 1,
        InterRound = 2,
        Victory = 3,
        Defeat = 4,
    }
}
