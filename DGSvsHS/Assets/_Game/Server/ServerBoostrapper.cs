using UnityEngine;

namespace DGSvsHS.Server
{
    public class ServerBootstrapper : MonoBehaviour
    {
        [Header("Shared Settings")]
        public float HeartbeatIntervalSec = 0.5f;

        [Header("DGS Settings")]
        public ushort Port = 7777;
        public ulong Seed = 0xC0FFEE_F00DUL;
        public bool AutoStartMatch = true;
        public bool GodMode = false;

        private void Awake()
        {
#if WITH_DGS
            // Dynamically add the DGS script and pass the inspector variables down
            var dgs = gameObject.AddComponent<DedicatedServerMain>();
            dgs.Port = Port;
            dgs.Seed = Seed;
            dgs.AutoStartMatch = AutoStartMatch;
            dgs.GodMode = GodMode;
            dgs.HeartbeatIntervalSec = HeartbeatIntervalSec;
            
#elif WITH_BAREBONE
            // Dynamically add the Barebone script and pass the inspector variables down
            var bb = gameObject.AddComponent<DGSvsHS.Server.BareBone.BareBoneServerMain>();
            bb.HeartbeatIntervalSec = HeartbeatIntervalSec;
            
#else
            Debug.LogWarning("[ServerBootstrapper] No server mode defined! Neither WITH_DGS nor WITH_BAREBONE is active.");
#endif
        }
    }
}