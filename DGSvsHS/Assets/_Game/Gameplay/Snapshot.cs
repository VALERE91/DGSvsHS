using System.Collections.Generic;
using UnityEngine;

namespace DGSvsHS.Gameplay
{
    public sealed class Snapshot
    {
        public SnapshotKind Kind;
        public uint Tick;
        public uint BaselineTick;
        public uint LastProcessedInputTick;
        public int Round;
        public float RoundTimer;
        public float InterRoundTimer;
        public RoundPhase Phase;
        public uint EnemyTotalInWorld;

        public readonly List<PlayerSnap> Players = new List<PlayerSnap>(Constants.MaxPlayers);
        public readonly List<EnemySnap> Enemies = new List<EnemySnap>(1024);
        public readonly List<FireEvent> RecentFireEvents = new List<FireEvent>(16);

        public void Clear()
        {
            Kind = SnapshotKind.Full;
            Tick = 0;
            BaselineTick = 0;
            LastProcessedInputTick = 0;
            Round = 0;
            RoundTimer = 0f;
            InterRoundTimer = 0f;
            Phase = RoundPhase.PreGame;
            EnemyTotalInWorld = 0;
            Players.Clear();
            Enemies.Clear();
            RecentFireEvents.Clear();
        }
        
        public void CopyFrom(Snapshot src)
        {
            Clear();
            Kind = src.Kind;
            Tick = src.Tick;
            BaselineTick = src.BaselineTick;
            LastProcessedInputTick = src.LastProcessedInputTick;
            Round = src.Round;
            RoundTimer = src.RoundTimer;
            InterRoundTimer = src.InterRoundTimer;
            Phase = src.Phase;
            EnemyTotalInWorld = src.EnemyTotalInWorld;
            for (int i = 0; i < src.Players.Count; i++) Players.Add(src.Players[i]);
            for (int i = 0; i < src.Enemies.Count; i++) Enemies.Add(src.Enemies[i]);
            for (int i = 0; i < src.RecentFireEvents.Count; i++) RecentFireEvents.Add(src.RecentFireEvents[i]);
        }
    }

    public enum SnapshotKind : byte
    {
        Full = 0,
        Delta = 1,
    }

    public struct PlayerSnap
    {
        public byte Id;
        public Vector2 Position;
        public Vector2 Aim;
        public bool Alive;
        public float DisableTimer;
    }

    public struct EnemySnap
    {
        public ushort Id;
        public Vector2 Position;
    }
    
    public struct EnemyDeltaEntry
    {
        public ushort Id;
        public Vector2 Position;
    }
}
