using System;
using DGSvsHS.Gameplay;

namespace DGSvsHS.Net
{
    public interface INetworkServer : IDisposable
    {
        bool IsRunning { get; }
        
        event Action<byte> ClientConnected;
        event Action<byte, DisconnectReason> ClientDisconnected;
        
        event Action<byte, InputCmd> InputReceived;
        
        float GetRttMs(byte playerId);

        void SetServerTick(uint tick);

        void Start(ushort port);
        
        bool TryDequeueInput(byte playerId, out InputCmd cmd);
        
        void Broadcast(Snapshot current, WorldStateHistory history);
        
        void Poll();
    }
}
