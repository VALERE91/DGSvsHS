using System;
using System.IO;
using System.Runtime.InteropServices;
using DGSvsHS.Gameplay;
using UnityEngine;

namespace DGSvsHS.Net.Quic
{
    /// <summary>
    /// Build 2 client transport: P/Invoke shim onto the Rust game-socket cdylib.
    ///
    /// <para>Owns no networking logic of its own. The Rust library handles the
    /// QUIC handshake, congestion control, datagram vs. stream selection, and
    /// keepalive/RTT measurement. This shim:</para>
    /// <list type="number">
    ///   <item>Opens a connection.</item>
    ///   <item>Posts encoded input bytes (from <see cref="WireCodec"/>) into the Rust side.</item>
    ///   <item>Polls inbound messages each frame and dispatches them by message type.</item>
    /// </list>
    ///
    /// <para><b>Native ABI contract.</b> The Rust crate must build a dylib named
    /// <c>dgsvshs_socket</c> (loaded as <c>libdgsvshs_socket.so</c> / <c>.dylib</c> /
    /// <c>dgsvshs_socket.dll</c> by Unity) exporting the functions declared below.
    /// Buffer ownership rules are documented inline; getting them wrong will leak
    /// or crash, so they're tightly specified.</para>
    /// </summary>
    public sealed class QuicNetworkClient : INetworkClient
    {
        // ------------------------------------------------------------------
        // Native ABI (Rust cdylib: dgsvshs_socket)
        // ------------------------------------------------------------------
        // All functions return:
        //   - i32 >= 0 on success (byte length for read ops, 0 for status ops)
        //   - i32 < 0 on error (negated NetErrorCode)
        //
        // Threading: the Rust library runs its own runtime (tokio) and must be
        // safe to call from Unity's main thread. dgs_poll() drains inbound
        // messages onto a single-producer queue and returns one message per
        // call; the C# side loops until it returns 0.

        private const string DLL = "dgsvshs_socket";

        [DllImport(DLL)] private static extern IntPtr dgs_client_create();
        [DllImport(DLL)] private static extern void   dgs_client_destroy(IntPtr h);
        [DllImport(DLL)] private static extern int    dgs_client_connect(IntPtr h, [MarshalAs(UnmanagedType.LPStr)] string host, ushort port);
        [DllImport(DLL)] private static extern int    dgs_client_state(IntPtr h); // returns ConnectionState
        [DllImport(DLL)] private static extern float  dgs_client_rtt_ms(IntPtr h);

        /// <summary>
        /// Send one outbound message. <c>msg_type</c> is the wire-format tag
        /// (e.g. <see cref="WireCodec.MsgInput"/>). The native side selects
        /// datagram vs. stream based on the tag, per WireFormat.md §1.
        /// Buffer is copied into the native send queue; caller may reuse after return.
        /// </summary>
        [DllImport(DLL)] private static extern int    dgs_client_send(IntPtr h, byte msg_type, byte[] data, int len);

        /// <summary>
        /// Poll one inbound message into the caller's buffer. Returns:
        ///   > 0 — number of bytes written; <c>out_msg_type</c> set
        ///   = 0 — no messages pending
        ///   &lt; 0 — error
        /// </summary>
        [DllImport(DLL)] private static extern int    dgs_client_poll(IntPtr h, out byte out_msg_type, byte[] buf, int buf_len);

        // ------------------------------------------------------------------

        private IntPtr _h;
        private readonly Snapshot _snapshotScratch = new Snapshot();
        private readonly SnapshotDecoder _snapDecoder = new SnapshotDecoder();
        // Sized for worst-case inbound snapshot at MaxEnemies=2048: ~40KB. 64KB gives safe headroom.
        private readonly byte[] _sendScratch = new byte[1024];
        private readonly byte[] _recvScratch = new byte[65536];
        private readonly InputCmd[] _inputRingForRedundancy = new InputCmd[4];
        private int _redundancyFill;

        public ConnectionState State { get; private set; } = ConnectionState.Disconnected;
        public byte LocalPlayerId { get; private set; }
        public float OneWayLatencyMs => _h != IntPtr.Zero ? dgs_client_rtt_ms(_h) * 0.5f : 0f;

        public event Action Connected;
        public event Action<DisconnectReason> Disconnected;
        public event Action<Snapshot> SnapshotReceived;

        public QuicNetworkClient()
        {
            _h = dgs_client_create();
            if (_h == IntPtr.Zero) throw new InvalidOperationException("dgs_client_create returned null");
        }

        public void Connect(string host, ushort port)
        {
            Debug.Log($"[QuicNetworkClient] Connect({host}:{port}) — calling native dgs_client_connect");
            State = ConnectionState.Connecting;
            int rc = dgs_client_connect(_h, host, port);
            Debug.Log($"[QuicNetworkClient] dgs_client_connect returned rc={rc}");
            if (rc < 0)
            {
                State = ConnectionState.Disconnected;
                Disconnected?.Invoke(DisconnectReason.Unknown);
                return;
            }

            // Send ClientHello immediately. The Rust side queues until the QUIC
            // handshake completes; the server replies with ServerWelcome which
            // surfaces in Poll().
            using var ms = new MemoryStream(_sendScratch);
            using var w = new BinaryWriter(ms);
            WireCodec.WriteClientHello(w, 0);
            int sendRc = dgs_client_send(_h, WireCodec.MsgClientHello, _sendScratch, (int)ms.Position);
            Debug.Log($"[QuicNetworkClient] sent ClientHello ({(int)ms.Position} B) rc={sendRc}");
        }

        public void SendInput(in InputCmd cmd)
        {
            if (State != ConnectionState.Connected) return;

            for (int i = _inputRingForRedundancy.Length - 1; i > 0; i--)
                _inputRingForRedundancy[i] = _inputRingForRedundancy[i - 1];
            _inputRingForRedundancy[0] = cmd;
            if (_redundancyFill < _inputRingForRedundancy.Length) _redundancyFill++;

            using var ms = new MemoryStream(_sendScratch);
            using var w = new BinaryWriter(ms);
            WireCodec.WriteInputBatch(w, _inputRingForRedundancy, _redundancyFill);
            dgs_client_send(_h, WireCodec.MsgInput, _sendScratch, (int)ms.Position);
        }

        public void Poll()
        {
            if (_h == IntPtr.Zero) return;

            // Drain all pending inbound messages this frame.
            while (true)
            {
                int n = dgs_client_poll(_h, out byte msgType, _recvScratch, _recvScratch.Length);
                if (n == 0) break;
                if (n < 0)
                {
                    Disconnect(DisconnectReason.Unknown);
                    break;
                }
                HandleMessage(msgType, n);
            }
        }

        private void HandleMessage(byte msgType, int len)
        {
            using var ms = new MemoryStream(_recvScratch, 0, len);
            using var r = new BinaryReader(ms);

            switch (msgType)
            {
                case WireCodec.MsgServerWelcome:
                {
                    WireCodec.ReadServerWelcome(r, out uint version, out byte pid, out uint _,
                        out ushort simTickMs, out ushort snapshotEveryNTicks);
                    if (version != WireCodec.ProtocolVersion ||
                        simTickMs != Constants.SimTickMs ||
                        snapshotEveryNTicks != Constants.SnapshotEveryNTicks)
                    {
                        Disconnect(DisconnectReason.ProtocolError);
                        return;
                    }
                    LocalPlayerId = pid;
                    State = ConnectionState.Connected;
                    Connected?.Invoke();
                    break;
                }

                case WireCodec.MsgSnapshot:
                    if (_snapDecoder.Decode(r, _snapshotScratch))
                        SnapshotReceived?.Invoke(_snapshotScratch);
                    // else: delta baseline mismatch — drop; next input's ack will resync.
                    break;

                case WireCodec.MsgDisconnect:
                {
                    byte reason = r.ReadByte();
                    Disconnect((DisconnectReason)reason);
                    break;
                }

                default:
                    // Unknown message type — log and drop.
                    Debug.LogWarning($"QuicNetworkClient: unknown msg type 0x{msgType:X2}");
                    break;
            }
        }

        public void Dispose()
        {
            if (_h != IntPtr.Zero)
            {
                dgs_client_destroy(_h);
                _h = IntPtr.Zero;
            }
            State = ConnectionState.Disconnected;
        }

        private void Disconnect(DisconnectReason reason)
        {
            var prev = State;
            State = ConnectionState.Disconnected;
            _snapDecoder.Reset();
            if (prev != ConnectionState.Disconnected) Disconnected?.Invoke(reason);
        }
    }
}
