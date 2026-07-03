using System.Collections.Generic;
using DGSvsHS.Client.Views;
using DGSvsHS.Gameplay;
using DGSvsHS.Net;
using UnityEngine;

using DGSvsHS.Net.Quic;

namespace DGSvsHS.Client
{
    public sealed class ClientMain : MonoBehaviour
    {
        [Header("Connection")]
        [Tooltip("Server host. Use 127.0.0.1 when running server and client on the same machine.")]
        public string Host = "127.0.0.1";
        // Default port is picked per build flavor (see BuildModeSwitcher). Each server flavor
        // listens on its own UDP port so side-by-side trials don't collide.
        //   WITH_DGS         → 7777 (Unity DOTS DedicatedServerMain)
        //   HS_TARGET_ARCH   → 7778 (csharp_arch_server)
        //   HS_TARGET_BEVY   → 4433 (rust/gameplay Bevy server, hardcoded in plugin.rs)
        //   (BareBone uses 7779 but never has a client connecting to it.)
        // Still settable in the Inspector — this is just the right out-of-box default.
#if HS_TARGET_BEVY
        [Tooltip("Server port. Default 4433 (Rust/Bevy server).")]
        public ushort Port = 4433;
#elif HS_TARGET_ARCH
        [Tooltip("Server port. Default 7778 (C#/Arch server).")]
        public ushort Port = 7778;
#else
        [Tooltip("Server port. Default 7777 (Unity DOTS server).")]
        public ushort Port = 7777;
#endif
        [Tooltip("If true, attempts to connect on Start. Disable when a harness (TrialRunner) drives connection lifecycle.")]
        public bool AutoConnect = true;

        [Header("Rendering")]
        [Tooltip("Sprite used for both players and enemies. If null, generates a circle texture at runtime.")]
        public Sprite UnitSprite;
        public Color LocalPlayerColor = new Color(0.3f, 0.9f, 1f);
        public Color RemotePlayerColor = new Color(1f, 0.9f, 0.3f);
        public Color EnemyColor = new Color(1f, 0.3f, 0.3f);
        public Color BeamColor = new Color(0.5f, 1f, 0.8f);

        [Header("Harness hook")]
        [Tooltip("If set, this autopilot supplies inputs instead of the keyboard/mouse. Wired up by TrialRunner.")]
        public IAutoPilot AutoPilot;

        [Header("Enemy correction (live-tunable in Play mode)")]
        [Tooltip("Master switch. When off, enemies render at their raw snapshot position with zero correction (good for A/B comparison).")]
        public bool UseEnemyCorrection = true;
        [Tooltip("Spring stiffness. Higher = render tracks the interpolated target more aggressively. Critical damping: C = 2·sqrt(K).")]
        [Range(0f, 10000f)] public float K = Constants.EnemyCorrectionK;
        [Tooltip("Spring damping. Auto-scales by sqrt(K_eff/K) so the damping ratio stays put as K ramps up.")]
        [Range(0f, 3000f)] public float C = Constants.EnemyCorrectionC;
        [Tooltip("Hard teleport fallback. Distance ramping makes this rare in practice.")]
        [Range(0f, 1000f)] public float SnapDistance = Constants.EnemyCorrectionSnapDistance;
        [Tooltip("Max multiplier applied to K as error approaches SnapDistance. K_eff = K · (1 + (mult−1) · (error/snap)²). Higher = harder to ever reach SnapDistance.")]
        [Range(1f, 100f)] public float KMaxMultiplier = Constants.EnemyCorrectionKMaxMultiplier;
        [Tooltip("Render-time delay behind wall-clock now (ms). Render target = position at wallNow − this. ~3 ticks (48 ms) absorbs jitter while staying responsive.")]
        [Range(0f, 300f)] public float BufferLatencyMs = Constants.EnemyCorrectionBufferLatencyMs;

        // ---------- Runtime ----------

        private INetworkClient _net;
        private ClientSimulation _sim;
        private EnemyCorrector _enemyCorrector;
        private PlayerInputReader _inputReader;
        private Camera _camera;

        private SpriteViewPool _playerPool;
        private SpriteViewPool _enemyPool;
        private BeamViewPool _beamPool;

        private uint _clientTick;
        private float _tickAccumulator;
        private float _serverTickEpochWallTime;
        private uint _serverTickEpoch;

        public INetworkClient NetworkClient => _net;
        public ClientSimulation Simulation => _sim;
        public uint ClientTick => _clientTick;

        // ---------- Lifecycle ----------

        private void Awake()
        {
#if HS_TARGET_BEVY
      const string flavor = "HS_TARGET_BEVY";
#elif HS_TARGET_ARCH
            const string flavor = "HS_TARGET_ARCH";
#elif WITH_DGS
      const string flavor = "WITH_DGS";
#else
      const string flavor = "(none)";
#endif
            // Parse --server/--port/--bot-id from the process command line. Lets the
            // Bot Harness editor window spawn N copies of this exe with deterministic
            // autopilots without needing TrialRunner in the scene.
            ApplyCommandLineArgs();
            Debug.Log($"[ClientMain] Awake flavor={flavor} Host={Host} Port={Port}");

            _camera = Camera.main;
            if (_camera == null)
            {
                var camGo = new GameObject("Main Camera");
                _camera = camGo.AddComponent<Camera>();
                _camera.tag = "MainCamera";
                _camera.orthographic = true;
                _camera.orthographicSize = Constants.ArenaRadius + 2f;
                _camera.transform.position = new Vector3(0, 0, -10);
                _camera.backgroundColor = new Color(0.03f, 0.03f, 0.06f);
                _camera.clearFlags = CameraClearFlags.SolidColor;
            }

            if (UnitSprite == null) UnitSprite = MakeCircleSprite(64);

            var poolsRoot = new GameObject("Views").transform;
            poolsRoot.SetParent(transform, false);
            _playerPool = new SpriteViewPool(poolsRoot, UnitSprite, RemotePlayerColor, Constants.PlayerRadius * 2f);
            _enemyPool  = new SpriteViewPool(poolsRoot, UnitSprite, EnemyColor,        Constants.EnemyRadius  * 2f);
            _beamPool   = new BeamViewPool(poolsRoot, BeamColor);

            _sim = new ClientSimulation();
            _enemyCorrector = new EnemyCorrector();
            _inputReader = new PlayerInputReader(_camera);

            _net = new QuicNetworkClient();
            _net.Connected += OnConnected;
            _net.Disconnected += OnDisconnected;
            _net.SnapshotReceived += OnSnapshot;

            StartCoroutine(StateWatcher());
        }

        private void Start()
        {
            if (AutoConnect)
            {
                Debug.Log($"[ClientMain] Start — auto-connecting to {Host}:{Port}");
                _net.Connect(Host, Port);
            }
            else
            {
                Debug.Log("[ClientMain] Start — AutoConnect is OFF, not connecting");
            }
        }

        /// <summary>
        /// Parse `--server H`, `--port P`, `--bot-id N`, `--seed S` from the process
        /// command line. Overrides Inspector values when present. Called from Awake
        /// before any networking/autopilot setup so the values are live by Start().
        ///
        /// Args use the `--key value` convention to match TrialRunner. Unknown args
        /// are ignored (Unity itself injects a few like `-screen-width`).
        /// </summary>
        private void ApplyCommandLineArgs()
        {
            string[] args = System.Environment.GetCommandLineArgs();
            int botId = -1;
            ulong seed = 1;
            for (int i = 0; i < args.Length - 1; i++)
            {
                string k = args[i];
                string v = args[i + 1];
                switch (k)
                {
                    case "--server": Host = v; break;
                    case "--port":   if (ushort.TryParse(v, out var p)) Port = p; break;
                    case "--bot-id": if (int.TryParse(v, out var b)) botId = b; break;
                    case "--seed":   if (ulong.TryParse(v, out var s)) seed = s; break;
                }
            }
            if (botId >= 0 && botId < Constants.MaxPlayers)
            {
                AutoPilot = new AutoPilot((byte)botId, seed);
                Debug.Log($"[ClientMain] CLI: bot-id={botId} seed={seed} → AutoPilot attached");
            }
        }

        // Diagnostic: prints connection state once per second so we can see
        // whether it's stuck in Connecting (handshake hung), flipping to
        // Disconnected (handshake rejected), or sitting in Connected.
        private System.Collections.IEnumerator StateWatcher()
        {
            var wait = new WaitForSeconds(1f);
            while (true)
            {
                Debug.Log($"[ClientMain] state={_net?.State} rtt={_net?.OneWayLatencyMs}ms");
                yield return wait;
            }
        }

        private void OnDestroy()
        {
            if (_net != null)
            {
                _net.Connected -= OnConnected;
                _net.Disconnected -= OnDisconnected;
                _net.SnapshotReceived -= OnSnapshot;
                _net.Dispose();
            }
        }

        // ---------- Network event handlers ----------

        private void OnConnected()
        {
            _sim.SetLocalPlayerId(_net.LocalPlayerId);
            _enemyCorrector.Clear();
        }

        private void OnDisconnected(DisconnectReason reason)
        {
            Debug.Log($"[ClientMain] Disconnected: {reason}");
            _enemyCorrector.Clear();
        }

        private void OnSnapshot(Snapshot s)
        {
            float wall = Time.realtimeSinceStartup;

            // Establish/refresh server-tick → wall-time epoch for interpolation timing.
            if (_serverTickEpoch == 0 || s.Tick < _serverTickEpoch)
            {
                _serverTickEpoch = s.Tick;
                _serverTickEpochWallTime = wall;
            }

            _sim.OnSnapshotReceived(s, wall);
            _enemyCorrector.IngestSnapshot(s, wall);
        }

        // ---------- Main loop ----------

        private void Update()
        {
            _net?.Poll();

            // Sim ticks at fixed rate, independent of frame rate.
            _tickAccumulator += Time.unscaledDeltaTime;
            while (_tickAccumulator >= Constants.SimDt)
            {
                _tickAccumulator -= Constants.SimDt;
                SimTick();
            }

            Render();
        }

        private void SimTick()
        {
            _clientTick++;

            if (_net.State != ConnectionState.Connected) return;

            // Sample input. Autopilot wins if attached (for harness/benchmark runs).
            Vector2 predictedPos = _sim.HasLocalPlayer ? _sim.GetPredictedLocalPlayer().Position : Vector2.zero;
            InputCmd cmd = AutoPilot != null
                ? AutoPilot.Sample(_clientTick, predictedPos, _sim)
                : _inputReader.Sample(_clientTick, predictedPos);

            // Piggy-back the latest fully-reconstructed snapshot tick onto the outbound input
            // so the server can build a delta against it. v2 ack channel.
            cmd.LastAckedServerTick = _sim.LastAckedServerTick;

            _sim.PushPredictedInput(cmd);
            _net.SendInput(in cmd);
        }

        private void Render()
        {
            float wall = Time.realtimeSinceStartup;

            _playerPool.Begin();
            _enemyPool.Begin();

            // Local player — predicted position.
            if (_sim.HasLocalPlayer)
            {
                var lp = _sim.GetPredictedLocalPlayer();
                var sr = _playerPool.Rent(out var tr);
                // Alpha: 0.4 when disabled (weapon offline, invulnerable) or dead, 1.0 otherwise.
                bool faded = !lp.Alive || lp.DisableTimer > 0f;
                sr.color = faded
                    ? new Color(LocalPlayerColor.r, LocalPlayerColor.g, LocalPlayerColor.b, 0.4f)
                    : LocalPlayerColor;
                tr.position = new Vector3(lp.Position.x, lp.Position.y, 0f);
            }

            // Remote entities — only render what's in the latest snapshot (with interpolation).
            if (_sim.HasLatestSnapshot)
            {
                var latest = _sim.LatestSnapshot;

                // Other players
                for (int i = 0; i < latest.Players.Count; i++)
                {
                    byte pid = latest.Players[i].Id;
                    if (pid == _sim.LocalPlayerId) continue;
                    if (!_sim.TryGetInterpolatedPlayer(pid, wall, out var pos, out _, out bool alive, out bool disabled)) continue;
                    var sr = _playerPool.Rent(out var tr);
                    bool faded = !alive || disabled;
                    sr.color = faded
                        ? new Color(RemotePlayerColor.r, RemotePlayerColor.g, RemotePlayerColor.b, 0.4f)
                        : RemotePlayerColor;
                    tr.position = new Vector3(pos.x, pos.y, 0f);
                }
                
                if (UseEnemyCorrection)
                {
                    _enemyCorrector.K = K;
                    _enemyCorrector.C = C;
                    _enemyCorrector.SnapDistance = SnapDistance;
                    _enemyCorrector.KMaxMultiplier = KMaxMultiplier;
                    _enemyCorrector.BufferLatencyMs = BufferLatencyMs;
                    _enemyCorrector.Step(wall, Time.unscaledDeltaTime);
                    for (int i = 0; i < latest.Enemies.Count; i++)
                    {
                        ushort eid = latest.Enemies[i].Id;
                        if (!_enemyCorrector.TryGet(eid, out var pos)) continue;
                        var sr = _enemyPool.Rent(out var tr);
                        sr.color = EnemyColor;
                        tr.position = new Vector3(pos.x, pos.y, 0f);
                    }
                }
                else
                {
                    for (int i = 0; i < latest.Enemies.Count; i++)
                    {
                        var e = latest.Enemies[i];
                        var sr = _enemyPool.Rent(out var tr);
                        sr.color = EnemyColor;
                        tr.position = new Vector3(e.Position.x, e.Position.y, 0f);
                    }
                }
            }

            _playerPool.End();
            _enemyPool.End();
            
            for (int i = 0; i < _sim.NewPredictedFires.Count; i++)
                _beamPool.Spawn(_sim.NewPredictedFires[i], wall);
            _sim.NewPredictedFires.Clear();

            for (int i = 0; i < _sim.NewFireEvents.Count; i++)
                _beamPool.Spawn(_sim.NewFireEvents[i], wall);
            _sim.NewFireEvents.Clear();
            _beamPool.Tick(wall);
        }

        // ---------- Runtime sprite generation (so the project runs with zero asset setup) ----------

        private static Sprite MakeCircleSprite(int size)
        {
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
            tex.filterMode = FilterMode.Bilinear;
            var pixels = new Color32[size * size];
            float r = size * 0.5f;
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float dx = x - r + 0.5f;
                    float dy = y - r + 0.5f;
                    float d = Mathf.Sqrt(dx * dx + dy * dy);
                    byte a = d <= r ? (byte)255 : (byte)0;
                    pixels[y * size + x] = new Color32(255, 255, 255, a);
                }
            }
            tex.SetPixels32(pixels);
            tex.Apply();
            return Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), size);
        }
    }
    
    public interface IAutoPilot
    {
        InputCmd Sample(uint clientTick, Vector2 localPlayerPredictedPos, ClientSimulation sim);
    }
}
