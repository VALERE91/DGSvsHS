using System;
using DGSvsHS.Gameplay;

namespace DGSvsHS.Net
{
    public interface INetworkClient : IDisposable
    {
        ConnectionState State { get; }
        
        byte LocalPlayerId { get; }
        float OneWayLatencyMs { get; }

        event Action Connected;
        event Action<DisconnectReason> Disconnected;
        event Action<Snapshot> SnapshotReceived;
        
        void Connect(string host, ushort port);
        
        void SendInput(in InputCmd cmd);
        
        void Poll();
    }

    public enum ConnectionState : byte
    {
        Disconnected = 0,
        Connecting = 1,
        Connected = 2,
        Disconnecting = 3,
    }

    public enum DisconnectReason : byte
    {
        Unknown = 0,
        ClientRequested = 1,
        ServerShutdown = 2,
        Timeout = 3,
        ProtocolError = 4,
        ServerFull = 5,
    }
}
