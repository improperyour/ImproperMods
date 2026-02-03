using System;
using System.IO;
using System.Text;
using System.Collections.Generic;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using UnityEngine;

namespace LatencyLogger
{
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public class LatencyLoggerPlugin : BaseUnityPlugin
    {
        private const string PluginGuid = "com.improperyour.latencylogger";
        private const string PluginName = "LatencyLogger";
        private const string PluginVersion = "0.2.0";

        private const string RPC_PING_REPORT = "LatencyLogger_PingReport";

        // Config
        private ConfigEntry<int> _clientReportIntervalSeconds;
        private ConfigEntry<bool> _includeEndpoint;
        private bool _isClient;
        private bool _isServer;

        private ManualLogSource _log;
        private ConfigEntry<bool> _logToFile;

        // Client state
        private float _nextClientReport;
        private ConfigEntry<string> _outputPath;
        private Dictionary<long, PingData> _playerPings = new Dictionary<long, PingData>();

        // Server state
        private string _resolvedOutputPath = "";
        private bool _wroteHeader;

        private void Awake()
        {
            _log = Logger;

            _clientReportIntervalSeconds = Config.Bind("General", "ClientReportIntervalSeconds", 5,
                "How often clients report their ping to the server (seconds)");

            _logToFile = Config.Bind("General", "LogToFile", true,
                "Write samples to a CSV file (server only)");

            _outputPath = Config.Bind("General", "OutputPath", "BepInEx/config/LatencyLogger/latency.csv",
                "CSV output path (server only)");

            _includeEndpoint = Config.Bind("General", "IncludeEndpoint", false,
                "Include peer endpoint string in logs (server only)");

            _log.LogInfo($"{PluginName} {PluginVersion} loaded.");
        }

        private void Start()
        {
            // Wait a bit for ZNet to initialize
            InvokeRepeating(nameof(Initialize), 2f, 1f);
        }

        private void Update()
        {
            if (_isClient)
            {
                UpdateClient();
            }
        }

        private void Initialize()
        {
            var znet = ZNet.instance;
            if (znet == null) return;

            // Determine if we're client or server
            _isServer = znet.IsServer();
            _isClient = !_isServer;

            if (_isServer)
            {
                InitializeServer();
            }
            else
            {
                InitializeClient();
            }

            CancelInvoke(nameof(Initialize));
        }

        private void InitializeServer()
        {
            _log.LogInfo("Running as SERVER - will receive ping reports from clients");
            
            _resolvedOutputPath = ResolvePath(_outputPath.Value);
            
            if (_logToFile.Value)
            {
                EnsureDirectoryExists(_resolvedOutputPath);
            }

            // Register RPC to receive ping reports from clients
            ZRoutedRpc.instance.Register<int>(RPC_PING_REPORT, RPC_ReceivePingReport);
            
            _log.LogInfo($"Server initialized. Output: {_resolvedOutputPath}");
            
            // Start periodic logging
            InvokeRepeating(nameof(ServerLogPings), 10f, 10f);
        }

        private void InitializeClient()
        {
            _log.LogInfo("Running as CLIENT - will report ping to server");
            _nextClientReport = Time.time + _clientReportIntervalSeconds.Value;
        }

        private void UpdateClient()
        {
            if (Time.time < _nextClientReport) return;
            _nextClientReport = Time.time + _clientReportIntervalSeconds.Value;

            var ping = GetClientPing();
            if (ping >= 0)
            {
                ReportPingToServer(ping);
            }
        }

        private int GetClientPing()
        {
            try
            {
                var znet = ZNet.instance;
                if (znet == null) return -1;

                // Get the server peer (our connection to the server)
                var serverPeer = znet.GetServerPeer();
                if (serverPeer?.m_rpc == null) return -1;

                var rpc = serverPeer.m_rpc;
                var t = rpc.GetType();

                // Try m_timeSinceLastPing - on client this should be more reliable
                var field = t.GetField("m_timeSinceLastPing", 
                    System.Reflection.BindingFlags.Instance | 
                    System.Reflection.BindingFlags.Public | 
                    System.Reflection.BindingFlags.NonPublic);
                
                if (field?.FieldType == typeof(float))
                {
                    float seconds = (float)field.GetValue(rpc);
                    
                    // On client, this value should cycle regularly
                    // Accept values up to 5 seconds as valid
                    if (seconds >= 0 && seconds <= 5f)
                    {
                        int ms = (int)Math.Round(seconds * 1000f);
                        _log.LogDebug($"[{LocalStamp()}] Client ping: {ms}ms (timeSinceLastPing={seconds:F3}s)");
                        return ms;
                    }
                }
            }
            catch (Exception ex)
            {
                _log.LogWarning($"GetClientPing error: {ex.Message}");
            }

            return -1;
        }


        private void ReportPingToServer(int pingMs)
        {
            try
            {
                _log.LogDebug($"[{LocalStamp()}] Reporting ping to server: {pingMs}ms");
        
                // Get server peer ID - it's always the first peer for clients
                var znet = ZNet.instance;
                if (znet == null) return;
        
                var serverPeer = znet.GetServerPeer();
                if (serverPeer == null)
                {
                    _log.LogWarning($"[{LocalStamp()}] Cannot get server peer");
                    return;
                }
        
                long serverPeerID = serverPeer.m_uid;
        
                ZRoutedRpc.instance.InvokeRoutedRPC(serverPeerID, RPC_PING_REPORT, pingMs);
            }
            catch (Exception ex)
            {
                _log.LogWarning($"Failed to report ping to server: {ex.Message}");
            }
        }

        private void RPC_ReceivePingReport(long sender, int pingMs)
        {
            try
            {
                var znet = ZNet.instance;
                if (znet == null) return;

                // Find the peer that sent this
                var peer = znet.GetPeer(sender);
                if (peer == null)
                {
                    // Peer may have disconnected between send and receive.
                    _playerPings.Remove(sender);
                    _log.LogDebug($"Received ping report from unknown/disconnected peer: {sender} (removed cached entry if present)");
                    return;
                }

                var playerName = Safe(peer.m_playerName, "unknown");
                var steamId = peer.m_uid;
                var endpoint = _includeEndpoint.Value ? TryGetEndpoint(peer) : "";

                // Store/update ping data
                if (!_playerPings.ContainsKey(steamId))
                {
                    _playerPings[steamId] = new PingData();
                }

                var data = _playerPings[steamId];
                data.PlayerName = playerName;
                data.PingMs = pingMs;
                data.LastUpdate = DateTime.UtcNow;
                data.Endpoint = endpoint;

                _log.LogDebug($"Received ping report: player={playerName} steamid={steamId} ping={pingMs}ms");
            }
            catch (Exception ex)
            {
                _log.LogError($"RPC_ReceivePingReport error: {ex}");
            }
        }

        private void ServerLogPings()
        {
            try
            {
                var znet = ZNet.instance;
                if (znet == null) return;

                // Remove entries for peers that are no longer connected to avoid logging "phantom" pings.
                try
                {
                    var peers = znet.GetPeers();
                    var connected = new HashSet<long>();
                    foreach (var p in peers)
                    {
                        if (p != null) connected.Add(p.m_uid);
                    }

                    if (connected.Count > 0)
                    {
                        var disconnected = new List<long>();
                        foreach (var steamId in _playerPings.Keys)
                        {
                            if (!connected.Contains(steamId)) disconnected.Add(steamId);
                        }
                        foreach (var id in disconnected)
                        {
                            _playerPings.Remove(id);
                        }
                    }
                }
                catch
                {
                    // Best-effort cleanup; ignore if Valheim API changes.
                }

                var utc = DateTime.UtcNow.ToString("o");
                StringBuilder csv = null;

                if (_logToFile.Value)
                {
                    csv = new StringBuilder(256);

                    if (!_wroteHeader && !File.Exists(_resolvedOutputPath))
                    {
                        csv.AppendLine(_includeEndpoint.Value
                            ? "utc,player,steamid,ping_ms,endpoint"
                            : "utc,player,steamid,ping_ms");
                        _wroteHeader = true;
                    }
                }

                // Clean up stale entries (older than 30 seconds)
                var staleKeys = new List<long>();
                foreach (var kvp in _playerPings)
                {
                    if ((DateTime.UtcNow - kvp.Value.LastUpdate).TotalSeconds > 30)
                    {
                        staleKeys.Add(kvp.Key);
                    }
                }
                foreach (var key in staleKeys)
                {
                    _playerPings.Remove(key);
                }

                // Log current ping data
                if (_playerPings.Count == 0)
                {
                    // No active players, nothing to log
                    return;
                }

                foreach (var kvp in _playerPings)
                {
                    var steamId = kvp.Key;
                    var data = kvp.Value;

                    // Log to console
                    if (_includeEndpoint.Value)
                        _log.LogInfo($"latency utc={utc} player=\"{data.PlayerName}\" steamid={steamId} ping_ms={data.PingMs} endpoint=\"{data.Endpoint}\"");
                    else
                        _log.LogInfo($"latency utc={utc} player=\"{data.PlayerName}\" steamid={steamId} ping_ms={data.PingMs}");

                    // Log to CSV
                    if (csv != null)
                    {
                        if (_includeEndpoint.Value)
                            csv.AppendLine($"{utc},{Csv(data.PlayerName)},{steamId},{data.PingMs},{Csv(data.Endpoint)}");
                        else
                            csv.AppendLine($"{utc},{Csv(data.PlayerName)},{steamId},{data.PingMs}");
                    }
                }

                if (csv != null)
                {
                    EnsureDirectoryExists(_resolvedOutputPath);
                    File.AppendAllText(_resolvedOutputPath, csv.ToString());
                }
            }
            catch (Exception ex)
            {
                _log.LogError($"ServerLogPings error: {ex}");
            }
        }

        private static string LocalStamp()
            => DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss.fff zzz");

        private static string TryGetEndpoint(ZNetPeer peer)
        {
            try
            {
                var sock = peer.m_socket;
                if (sock == null) return "";
                return sock.GetEndPointString();
            }
            catch
            {
                return "";
            }
        }

        private static string ResolvePath(string path)
        {
            if (Path.IsPathRooted(path)) return path;
            return Path.GetFullPath(path);
        }

        private static void EnsureDirectoryExists(string filePath)
        {
            var dir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
        }

        private static string Safe(string s, string fallback)
            => string.IsNullOrWhiteSpace(s) ? fallback : s;

        private static string Csv(string s)
        {
            if (s == null) return "";
            if (s.Contains(",") || s.Contains("\"") || s.Contains("\n"))
                return "\"" + s.Replace("\"", "\"\"") + "\"";
            return s;
        }

        private class PingData
        {
            public string Endpoint;
            public DateTime LastUpdate;
            public int PingMs;
            public string PlayerName;
        }
    }
}