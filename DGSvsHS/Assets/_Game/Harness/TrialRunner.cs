using System;
using System.Globalization;
using System.IO;
using System.Text;
using DGSvsHS.Client;
using DGSvsHS.Gameplay;
using UnityEngine;

namespace DGSvsHS.Harness
{
    /// <summary>
    /// Benchmark trial coordinator. One TrialRunner lives alongside the ClientMain on each
    /// client process. Reads CLI args, wires up the autopilot, logs per-tick NDJSON metrics
    /// to a file, and shuts the process down at the end of the trial.
    ///
    /// <para><b>CLI args.</b> Parsed from <c>Environment.GetCommandLineArgs()</c>. Convention:
    /// <c>--key value</c> pairs.</para>
    /// <list type="bullet">
    ///   <item><c>--server &lt;host&gt;</c></item>
    ///   <item><c>--port &lt;ushort&gt;</c></item>
    ///   <item><c>--bot-id &lt;0..3&gt;</c> (omit for human input)</item>
    ///   <item><c>--seed &lt;u64&gt;</c></item>
    ///   <item><c>--duration &lt;seconds&gt;</c> (trial duration; default 300)</item>
    ///   <item><c>--warmup &lt;seconds&gt;</c> (logged-as-warmup time; default 30)</item>
    ///   <item><c>--output &lt;path&gt;</c> (NDJSON log; default trial_{botId}.ndjson)</item>
    /// </list>
    ///
    /// <para><b>NDJSON schema.</b> One line per second of trial time, with: wall time, client tick,
    /// server tick estimate, RTT, snapshot count seen, fire-event count seen, predicted vs server
    /// position delta (the reconciliation error), connection state. Schema is informal and may
    /// extend without breaking; downstream analysis selects fields by name.</para>
    /// </summary>
    public sealed class TrialRunner : MonoBehaviour
    {
        public ClientMain Client;

        // CLI-derived config
        private string _serverHost = "127.0.0.1";
        private ushort _port = 7777;
        private int _botId = -1;
        private ulong _seed = 1;
        private float _durationSec = 300f;
        private float _warmupSec = 30f;
        private string _outputPath;

        // Runtime
        private float _trialStartTime;
        private float _nextLogTime;
        private int _snapshotsSinceLastLog;
        private int _fireEventsSinceLastLog;
        private StreamWriter _log;
        private bool _ended;

        private void Awake()
        {
            ParseCli();

            if (Client == null) Client = FindFirstObjectByType<ClientMain>();
            if (Client == null) throw new InvalidOperationException("TrialRunner needs a ClientMain in the scene.");

            // Don't auto-connect; we want to control connection timing ourselves so the trial
            // start lines up with a confirmed connection.
            Client.AutoConnect = false;
            Client.Host = _serverHost;
            Client.Port = _port;

            if (_botId >= 0 && _botId < Constants.MaxPlayers)
                Client.AutoPilot = new AutoPilot((byte)_botId, _seed);

            // Open log file. Append rather than overwrite — if a trial is restarted, evidence persists.
            string path = _outputPath ?? $"trial_{(_botId >= 0 ? _botId.ToString() : "human")}.ndjson";
            _log = new StreamWriter(path, append: true) { AutoFlush = false };
            _log.WriteLine(JsonHeader());
        }

        private void Start()
        {
            Client.NetworkClient.Connected += OnConnected;
            Client.NetworkClient.SnapshotReceived += _ => _snapshotsSinceLastLog++;
            Client.NetworkClient.Connect(_serverHost, _port);
        }

        private void OnConnected()
        {
            _trialStartTime = Time.realtimeSinceStartup;
            _nextLogTime = _trialStartTime + 1f;
            Debug.Log($"[TrialRunner] Connected as player {Client.NetworkClient.LocalPlayerId}, bot id {_botId}, seed {_seed}, duration {_durationSec}s");
        }

        private void Update()
        {
            if (_ended) return;
            if (_trialStartTime <= 0f) return;

            float now = Time.realtimeSinceStartup;
            float elapsed = now - _trialStartTime;

            // Count fire events surfaced through ClientSimulation since last poll.
            // Both local-predicted and server-confirmed are tallied so the per-second
            // fire rate reflects total beams rendered (representative of player workload).
            _fireEventsSinceLastLog += Client.Simulation.NewFireEvents.Count
                                     + Client.Simulation.NewPredictedFires.Count;
            // We don't consume them — ClientMain.Render does that on the same frame.

            if (now >= _nextLogTime)
            {
                LogLine(elapsed);
                _nextLogTime += 1f;
                _snapshotsSinceLastLog = 0;
                _fireEventsSinceLastLog = 0;
            }

            if (elapsed >= _durationSec) EndTrial();
        }

        private void OnDestroy()
        {
            if (!_ended) EndTrial();
        }

        private void EndTrial()
        {
            if (_ended) return;
            _ended = true;
            _log?.Flush();
            _log?.Dispose();
            Debug.Log("[TrialRunner] Trial complete; quitting.");
            // Headless-friendly shutdown.
#if UNITY_EDITOR
            UnityEditor.EditorApplication.isPlaying = false;
#else
            Application.Quit(0);
#endif
        }

        // ---------- Logging ----------

        private void LogLine(float elapsed)
        {
            var sim = Client.Simulation;
            string phase = elapsed < _warmupSec ? "warmup" : "measure";

            var sb = new StringBuilder(256);
            sb.Append('{');
            AppendField(sb, "t",          elapsed.ToString("F3", CultureInfo.InvariantCulture)); sb.Append(',');
            AppendField(sb, "phase",      Quote(phase));                                          sb.Append(',');
            AppendField(sb, "bot_id",     _botId.ToString(CultureInfo.InvariantCulture));         sb.Append(',');
            AppendField(sb, "seed",       _seed.ToString(CultureInfo.InvariantCulture));          sb.Append(',');
            AppendField(sb, "client_tick", Client.ClientTick.ToString(CultureInfo.InvariantCulture)); sb.Append(',');
            AppendField(sb, "server_tick", sim.LatestServerTick.ToString(CultureInfo.InvariantCulture)); sb.Append(',');
            AppendField(sb, "rtt_ms",     (Client.NetworkClient.OneWayLatencyMs * 2f).ToString("F2", CultureInfo.InvariantCulture)); sb.Append(',');
            AppendField(sb, "snaps_1s",   _snapshotsSinceLastLog.ToString(CultureInfo.InvariantCulture)); sb.Append(',');
            AppendField(sb, "fires_1s",   _fireEventsSinceLastLog.ToString(CultureInfo.InvariantCulture)); sb.Append(',');
            AppendField(sb, "state",      Quote(Client.NetworkClient.State.ToString()));
            sb.Append('}');
            _log.WriteLine(sb.ToString());
            _log.Flush();
        }

        private static void AppendField(StringBuilder sb, string key, string value)
        {
            sb.Append('"').Append(key).Append('"').Append(':').Append(value);
        }

        private static string Quote(string s) => "\"" + s.Replace("\"", "\\\"") + "\"";

        private string JsonHeader()
        {
            // Marker line at file start for downstream parsers.
            return "{\"type\":\"header\",\"protocol\":\"dgsvshs-trial-v1\",\"started_at_unix\":" +
                   DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture) + "}";
        }

        // ---------- CLI parsing ----------

        private void ParseCli()
        {
            string[] args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; i++)
            {
                string k = args[i];
                string v = args[i + 1];
                switch (k)
                {
                    case "--server":   _serverHost = v; break;
                    case "--port":     if (ushort.TryParse(v, out var p)) _port = p; break;
                    case "--bot-id":   if (int.TryParse(v, out var b)) _botId = b; break;
                    case "--seed":     if (ulong.TryParse(v, out var s)) _seed = s; break;
                    case "--duration": if (float.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) _durationSec = d; break;
                    case "--warmup":   if (float.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var w)) _warmupSec = w; break;
                    case "--output":   _outputPath = v; break;
                }
            }
        }
    }
}
