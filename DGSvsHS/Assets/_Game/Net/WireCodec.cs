using System;
using System.Collections.Generic;
using System.IO;
using DGSvsHS.Gameplay;
using UnityEngine;

namespace DGSvsHS.Net
{
    public static class WireCodec
    {
        public const uint ProtocolVersion = 3;

        public const byte MsgClientHello   = 0x01;
        public const byte MsgServerWelcome = 0x02;
        public const byte MsgInput         = 0x10;
        public const byte MsgSnapshot      = 0x20;
        public const byte MsgDisconnect    = 0xF0;

        // ---------- ClientHello ----------

        public static void WriteClientHello(BinaryWriter w, byte capabilities)
        {
            w.Write(ProtocolVersion);
            w.Write(capabilities);
        }

        public static void ReadClientHello(BinaryReader r, out uint version, out byte capabilities)
        {
            version = r.ReadUInt32();
            capabilities = r.ReadByte();
        }

        // ---------- ServerWelcome ----------

        public static void WriteServerWelcome(BinaryWriter w, byte playerId, uint serverTick)
        {
            w.Write(ProtocolVersion);
            w.Write(playerId);
            w.Write(serverTick);
            w.Write((ushort)Constants.SimTickMs);              // tick period in ms (integer)
            w.Write((ushort)Constants.SnapshotEveryNTicks);    // snapshot interval in ticks
        }

        public static void ReadServerWelcome(
            BinaryReader r,
            out uint version, out byte playerId, out uint serverTick,
            out ushort simTickMs, out ushort snapshotEveryNTicks)
        {
            version = r.ReadUInt32();
            playerId = r.ReadByte();
            serverTick = r.ReadUInt32();
            simTickMs = r.ReadUInt16();
            snapshotEveryNTicks = r.ReadUInt16();
        }

        // ---------- Input ----------

        ///InputCmd wire size: 4 (tick) + 4 (ack) + 16 (move+aim f32) + 1 (flags) = 25 bytes.
        public const int InputCmdWireBytes = 25;

        public static void WriteInputBatch(BinaryWriter w, InputCmd[] cmds, int count)
        {
            if (count < 1 || count > 4) throw new ArgumentOutOfRangeException(nameof(count));
            w.Write((byte)count);
            for (int i = 0; i < count; i++) WriteOneInput(w, cmds[i]);
        }

        public static int ReadInputBatch(BinaryReader r, InputCmd[] outCmds)
        {
            byte count = r.ReadByte();
            if (count < 1 || count > 4) throw new InvalidDataException("input batch count out of range");
            if (outCmds.Length < count) throw new InvalidDataException("output buffer too small");
            for (int i = 0; i < count; i++) outCmds[i] = ReadOneInput(r);
            return count;
        }

        private static void WriteOneInput(BinaryWriter w, in InputCmd cmd)
        {
            w.Write(cmd.Tick);
            w.Write(cmd.LastAckedServerTick);
            w.Write(cmd.Move.x);
            w.Write(cmd.Move.y);
            w.Write(cmd.Aim.x);
            w.Write(cmd.Aim.y);
            w.Write((byte)cmd.Flags);
        }

        private static InputCmd ReadOneInput(BinaryReader r)
        {
            return new InputCmd
            {
                Tick = r.ReadUInt32(),
                LastAckedServerTick = r.ReadUInt32(),
                Move = new Vector2(r.ReadSingle(), r.ReadSingle()),
                Aim = new Vector2(r.ReadSingle(), r.ReadSingle()),
                Flags = (InputFlags)r.ReadByte(),
            };
        }

        // ---------- Quantization helpers ----------

        private static short QuantPosition(float meters)
        {
            int q = Mathf.RoundToInt(meters * Constants.PositionScale);
            if (q > short.MaxValue) q = short.MaxValue;
            else if (q < short.MinValue) q = short.MinValue;
            return (short)q;
        }
        private static float DequantPosition(short q) => q / (float)Constants.PositionScale;

        private static short QuantAngle(Vector2 dir)
        {
            // dir expected unit-length; if degenerate, use 0 radians (east).
            float a = (dir.sqrMagnitude > 0.0001f) ? Mathf.Atan2(dir.y, dir.x) : 0f;
            int q = Mathf.RoundToInt(a * Constants.AngleScale);
            if (q > short.MaxValue) q = short.MaxValue;
            else if (q < short.MinValue) q = short.MinValue;
            return (short)q;
        }
        private static Vector2 DequantAngle(short q)
        {
            float a = q / (float)Constants.AngleScale;
            return new Vector2(Mathf.Cos(a), Mathf.Sin(a));
        }

        private static ushort QuantDisableTimer(float seconds)
        {
            int t = Mathf.RoundToInt(seconds * Constants.TicksPerSecond);
            if (t < 0) t = 0;
            else if (t > ushort.MaxValue) t = ushort.MaxValue;
            return (ushort)t;
        }
        private static float DequantDisableTimer(ushort t) => t / Constants.TicksPerSecond;

        // ---------- Snapshot: header ----------

        public const int SnapshotHeaderBytes = 1 /*kind*/ + 4 /*tick*/ + 4 /*baseline*/ + 4 /*lpi*/
                                              + 2 /*round*/ + 4 /*roundT*/ + 4 /*interT*/ + 1 /*phase*/;

        public static void WriteSnapshotHeader(BinaryWriter w, Snapshot s)
        {
            w.Write((byte)s.Kind);
            w.Write(s.Tick);
            w.Write(s.BaselineTick);
            w.Write(s.LastProcessedInputTick);
            w.Write((ushort)s.Round);
            w.Write(s.RoundTimer);
            w.Write(s.InterRoundTimer);
            w.Write((byte)s.Phase);
        }

        public static void ReadSnapshotHeader(BinaryReader r,
            out SnapshotKind kind, out uint tick, out uint baselineTick,
            out uint lastProcessedInputTick, out int round,
            out float roundTimer, out float interRoundTimer, out RoundPhase phase)
        {
            byte k = r.ReadByte();
            if (k > 1) throw new InvalidDataException("snapshot kind out of range");
            kind = (SnapshotKind)k;
            tick = r.ReadUInt32();
            baselineTick = r.ReadUInt32();
            lastProcessedInputTick = r.ReadUInt32();
            round = r.ReadUInt16();
            roundTimer = r.ReadSingle();
            interRoundTimer = r.ReadSingle();
            byte p = r.ReadByte();
            phase = (RoundPhase)p;
        }

        // ---------- Snapshot: full ----------

        public const int PlayerSnapFullBytes = 1 + 2 + 2 + 2 + 1 + 2;  // 10
        public const int EnemySnapFullBytes  = 2 + 2 + 2;               // 6 — v3 drops velocity
        public const int EnemyDeltaEntryBytes = 2 + 2 + 2;              // 6 — id + pos (no mask byte; position is the only delta field)
        public const int FireEventBytes      = 4 + 1 + 2 + 2 + 2 + 2 + 1; // 14

        private static void WritePlayerSnapFull(BinaryWriter w, in PlayerSnap p)
        {
            w.Write(p.Id);
            w.Write(QuantPosition(p.Position.x));
            w.Write(QuantPosition(p.Position.y));
            w.Write(QuantAngle(p.Aim));
            w.Write((byte)(p.Alive ? 1 : 0));
            w.Write(QuantDisableTimer(p.DisableTimer));
        }

        private static PlayerSnap ReadPlayerSnapFull(BinaryReader r)
        {
            byte id = r.ReadByte();
            short px = r.ReadInt16();
            short py = r.ReadInt16();
            short aim = r.ReadInt16();
            byte alive = r.ReadByte();
            ushort dt = r.ReadUInt16();
            return new PlayerSnap
            {
                Id = id,
                Position = new Vector2(DequantPosition(px), DequantPosition(py)),
                Aim = DequantAngle(aim),
                Alive = alive != 0,
                DisableTimer = DequantDisableTimer(dt),
            };
        }

        private static void WriteEnemySnapFull(BinaryWriter w, in EnemySnap e)
        {
            w.Write(e.Id);
            w.Write(QuantPosition(e.Position.x));
            w.Write(QuantPosition(e.Position.y));
        }

        private static EnemySnap ReadEnemySnapFull(BinaryReader r)
        {
            ushort id = r.ReadUInt16();
            short px = r.ReadInt16();
            short py = r.ReadInt16();
            return new EnemySnap
            {
                Id = id,
                Position = new Vector2(DequantPosition(px), DequantPosition(py)),
            };
        }

        private static void WriteFireEvent(BinaryWriter w, in FireEvent f)
        {
            w.Write(f.Tick);
            w.Write(f.ShooterId);
            w.Write(QuantPosition(f.Origin.x));
            w.Write(QuantPosition(f.Origin.y));
            w.Write(QuantAngle(f.Direction));
            w.Write(QuantPosition(f.Distance));
            w.Write(f.KillCount);
        }

        private static FireEvent ReadFireEvent(BinaryReader r)
        {
            uint tick = r.ReadUInt32();
            byte sid = r.ReadByte();
            short ox = r.ReadInt16();
            short oy = r.ReadInt16();
            short da = r.ReadInt16();
            short dist = r.ReadInt16();
            byte kills = r.ReadByte();
            return new FireEvent
            {
                Tick = tick,
                ShooterId = sid,
                Origin = new Vector2(DequantPosition(ox), DequantPosition(oy)),
                Direction = DequantAngle(da),
                Distance = DequantPosition(dist),
                KillCount = kills,
            };
        }

        // ---------- Snapshot: full body ----------
        
        public static void WriteFullSnapshotBody(
            BinaryWriter w,
            IReadOnlyList<PlayerSnap> players,
            IReadOnlyList<EnemySnap> enemies,
            uint enemyTotalInWorld,
            IReadOnlyList<FireEvent> fires)
        {
            w.Write((byte)players.Count);
            for (int i = 0; i < players.Count; i++) WritePlayerSnapFull(w, players[i]);

            int enemyCount = enemies.Count;
            if (enemyCount > ushort.MaxValue) enemyCount = ushort.MaxValue;
            w.Write((ushort)enemyCount);
            w.Write(enemyTotalInWorld);
            for (int i = 0; i < enemyCount; i++) WriteEnemySnapFull(w, enemies[i]);

            int fireCount = Math.Min(16, fires.Count);
            w.Write((byte)fireCount);
            for (int i = 0; i < fireCount; i++) WriteFireEvent(w, fires[i]);
        }

        public static void ReadFullSnapshotBody(BinaryReader r, Snapshot s)
        {
            byte playerCount = r.ReadByte();
            if (playerCount > Constants.MaxPlayers)
                throw new InvalidDataException("player count > MaxPlayers");
            for (int i = 0; i < playerCount; i++) s.Players.Add(ReadPlayerSnapFull(r));

            ushort enemyCount = r.ReadUInt16();
            uint enemyTotal = r.ReadUInt32();
            s.EnemyTotalInWorld = enemyTotal;
            for (int i = 0; i < enemyCount; i++) s.Enemies.Add(ReadEnemySnapFull(r));

            byte fireCount = r.ReadByte();
            if (fireCount > 16) throw new InvalidDataException("fire event count > 16");
            for (int i = 0; i < fireCount; i++) s.RecentFireEvents.Add(ReadFireEvent(r));
        }

        // ---------- Snapshot: delta body ----------
        
        public static bool EnemyPositionChanged(in EnemySnap baseline, in EnemySnap current)
        {
            return QuantPosition(baseline.Position.x) != QuantPosition(current.Position.x)
                || QuantPosition(baseline.Position.y) != QuantPosition(current.Position.y);
        }

        ///Fixed size of a delta enemy entry: u16 id + i16 pos_x + i16 pos_y.
        public const int EnemyChangedEntrySize = 6;
        
        public static void WriteDeltaSnapshotBody(
            BinaryWriter w,
            IReadOnlyList<PlayerSnap> players,
            IReadOnlyList<EnemyDeltaEntry> changed,
            IReadOnlyList<ushort> removed,
            IReadOnlyList<EnemySnap> added,
            uint enemyTotalInWorld,
            IReadOnlyList<FireEvent> fires)
        {
            w.Write((byte)players.Count);
            for (int i = 0; i < players.Count; i++) WritePlayerSnapFull(w, players[i]);

            w.Write((ushort)changed.Count);
            for (int i = 0; i < changed.Count; i++)
            {
                var e = changed[i];
                w.Write(e.Id);
                w.Write(QuantPosition(e.Position.x));
                w.Write(QuantPosition(e.Position.y));
            }

            w.Write((ushort)removed.Count);
            for (int i = 0; i < removed.Count; i++) w.Write(removed[i]);

            w.Write((ushort)added.Count);
            for (int i = 0; i < added.Count; i++) WriteEnemySnapFull(w, added[i]);

            w.Write(enemyTotalInWorld);

            int fireCount = Math.Min(16, fires.Count);
            w.Write((byte)fireCount);
            for (int i = 0; i < fireCount; i++) WriteFireEvent(w, fires[i]);
        }
        
        public static void ApplyDeltaSnapshotBody(BinaryReader r, Snapshot baseline, Snapshot outSnap)
        {
            // Players: full replacement.
            byte playerCount = r.ReadByte();
            if (playerCount > Constants.MaxPlayers)
                throw new InvalidDataException("player count > MaxPlayers");
            for (int i = 0; i < playerCount; i++) outSnap.Players.Add(ReadPlayerSnapFull(r));

            // Enemies: start by copying baseline.Enemies into outSnap.
            for (int i = 0; i < baseline.Enemies.Count; i++) outSnap.Enemies.Add(baseline.Enemies[i]);

            // Apply changed entries (in-place by id).
            ushort changedCount = r.ReadUInt16();
            for (int i = 0; i < changedCount; i++)
            {
                ushort id = r.ReadUInt16();
                short px = r.ReadInt16();
                short py = r.ReadInt16();
                Vector2 pos = new Vector2(DequantPosition(px), DequantPosition(py));
                int idx = FindEnemyIndexById(outSnap.Enemies, id);
                if (idx >= 0)
                {
                    var e = outSnap.Enemies[idx];
                    e.Position = pos;
                    outSnap.Enemies[idx] = e;
                }
                // If idx < 0 the baseline didn't have it — drop silently. Reader stays in sync
                // because the entry is fixed size and we already consumed all of it.
            }

            // Apply removals.
            ushort removedCount = r.ReadUInt16();
            for (int i = 0; i < removedCount; i++)
            {
                ushort id = r.ReadUInt16();
                int idx = FindEnemyIndexById(outSnap.Enemies, id);
                if (idx >= 0) outSnap.Enemies.RemoveAt(idx);
            }

            // Apply new entities.
            ushort newCount = r.ReadUInt16();
            for (int i = 0; i < newCount; i++) outSnap.Enemies.Add(ReadEnemySnapFull(r));

            outSnap.EnemyTotalInWorld = r.ReadUInt32();

            byte fireCount = r.ReadByte();
            if (fireCount > 16) throw new InvalidDataException("fire event count > 16");
            for (int i = 0; i < fireCount; i++) outSnap.RecentFireEvents.Add(ReadFireEvent(r));
        }

        private static int FindEnemyIndexById(List<EnemySnap> list, ushort id)
        {
            for (int i = 0; i < list.Count; i++) if (list[i].Id == id) return i;
            return -1;
        }
        
        public static bool ReadSnapshotMessage(BinaryReader r, Snapshot baseline, Snapshot outSnap)
        {
            ReadSnapshotHeader(r,
                out var kind, out uint tick, out uint baselineTick,
                out uint lpi, out int round,
                out float roundTimer, out float interRoundTimer, out var phase);

            outSnap.Clear();
            outSnap.Kind = kind;
            outSnap.Tick = tick;
            outSnap.BaselineTick = baselineTick;
            outSnap.LastProcessedInputTick = lpi;
            outSnap.Round = round;
            outSnap.RoundTimer = roundTimer;
            outSnap.InterRoundTimer = interRoundTimer;
            outSnap.Phase = phase;

            if (kind == SnapshotKind.Full)
            {
                ReadFullSnapshotBody(r, outSnap);
                return true;
            }
            // Delta
            if (baseline == null || baseline.Tick != baselineTick) return false;
            ApplyDeltaSnapshotBody(r, baseline, outSnap);
            return true;
        }
    }
    
    public sealed class SnapshotDecoder
    {
        private readonly Snapshot _baseline = new Snapshot();
        private bool _haveBaseline;
        
        public bool Decode(System.IO.BinaryReader r, Snapshot outSnap)
        {
            var baseline = _haveBaseline ? _baseline : null;
            if (!WireCodec.ReadSnapshotMessage(r, baseline, outSnap)) return false;
            _baseline.CopyFrom(outSnap);
            _haveBaseline = true;
            return true;
        }

        public void Reset()
        {
            _baseline.Clear();
            _haveBaseline = false;
        }
    }
}
