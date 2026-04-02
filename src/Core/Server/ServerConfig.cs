using System;
using System.IO;

namespace TerrariaModder.Core.Server
{
    /// <summary>
    /// Dedicated server configuration loaded from core/server-config.json at startup.
    /// Only relevant when running as TerrariaServer.exe (Main.dedServ == true).
    ///
    /// File location: {corePath}/server-config.json
    /// Created with defaults on first run if not present.
    /// </summary>
    public class ServerConfig
    {
        private static ServerConfig _instance;
        public static ServerConfig Instance => _instance ?? (_instance = new ServerConfig());

        /// <summary>Port for the HTTP management API (Phase 10). Default 7879.</summary>
        public int ManagementApiPort { get; set; } = 7879;

        /// <summary>
        /// Bearer token required for HTTP management API requests.
        /// null or empty = API is disabled (no token, no access).
        /// </summary>
        public string ManagementApiKey { get; set; }

        /// <summary>
        /// If true, requests from 127.0.0.1 bypass the bearer token check.
        /// Useful for local admin scripts without embedding the key everywhere.
        /// </summary>
        public bool ManagementApiLocalhostExempt { get; set; } = true;

        /// <summary>
        /// One-time key printed at startup for /reqop self-promotion.
        /// Overrides the randomly generated key if set here.
        /// Leave empty to use the per-session generated key.
        /// </summary>
        public string ReqopKey { get; set; }

        // ---- Game server startup fields ----

        /// <summary>
        /// World name to auto-select at startup (filename without .wld extension).
        /// TerrariaInjector resolves this to a full path using platform-standard save locations.
        /// Leave null to use Terraria's interactive world selection wizard.
        /// </summary>
        public string World { get; set; }

        /// <summary>
        /// Explicit full path to the .wld file. Takes priority over World (name-based lookup).
        /// Use this for headless servers, custom save locations, or any non-standard setup.
        /// Example: "/home/user/terraria/worlds/MyWorld.wld"
        /// </summary>
        public string WorldFilePath { get; set; }

        /// <summary>
        /// 1-based index into the DedServ world list to auto-select.
        /// Takes priority over World (name-based match) when > 0.
        /// </summary>
        public int WorldIndex { get; set; } = 0;

        /// <summary>Game server port. Default 7777.</summary>
        public int Port { get; set; } = 7777;

        /// <summary>Maximum number of players. Default 8.</summary>
        public int MaxPlayers { get; set; } = 8;

        /// <summary>Server password. Empty string for no password.</summary>
        public string Password { get; set; } = "";

        /// <summary>
        /// Message shown to connecting players in chat after PermissionSync.
        /// Leave null or empty for no MOTD.
        /// </summary>
        public string Motd { get; set; }

        /// <summary>True if World/Port/MaxPlayers are configured for unattended startup.</summary>
        public bool HasStartupConfig => !string.IsNullOrEmpty(World) || WorldIndex > 0;

        /// <summary>Load server-config.json from the given core path. Creates defaults if missing.</summary>
        public static void Load(string corePath)
        {
            string path = Path.Combine(corePath, "server-config.json");

            try
            {
                if (File.Exists(path))
                {
                    string json = File.ReadAllText(path);
                    ParseJson(json, Instance);
                }
                else
                {
                    // Write default file
                    Save(corePath);
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[TerrariaModder] ServerConfig.Load failed: {ex.Message}");
            }

            // Validate and warn about common misconfigurations
            if (string.IsNullOrEmpty(Instance.ManagementApiKey))
                Console.WriteLine("[TerrariaModder] Note: ManagementApiKey not set in server-config.json — HTTP management API is disabled. Set a key to enable remote administration.");

            if (Instance.ManagementApiPort < 1 || Instance.ManagementApiPort > 65535)
            {
                Console.WriteLine($"[TerrariaModder] Warning: ManagementApiPort {Instance.ManagementApiPort} is invalid — resetting to 7879.");
                Instance.ManagementApiPort = 7879;
            }

            if (Instance.Port < 1 || Instance.Port > 65535)
            {
                Console.WriteLine($"[TerrariaModder] Warning: Port {Instance.Port} is invalid — resetting to 7777.");
                Instance.Port = 7777;
            }
        }

        private static void Save(string corePath)
        {
            string path = Path.Combine(corePath, "server-config.json");
            try
            {
                string json =
                    "{\n" +
                    $"  \"world\": null,\n" +
                    $"  \"worldFilePath\": null,\n" +
                    $"  \"worldIndex\": 0,\n" +
                    $"  \"port\": {Instance.Port},\n" +
                    $"  \"maxPlayers\": {Instance.MaxPlayers},\n" +
                    $"  \"password\": \"\",\n" +
                    $"  \"managementApiPort\": {Instance.ManagementApiPort},\n" +
                    $"  \"managementApiKey\": null,\n" +
                    $"  \"managementApiLocalhostExempt\": true,\n" +
                    $"  \"reqopKey\": null,\n" +
                    $"  \"motd\": null\n" +
                    "}\n";
                File.WriteAllText(path, json);
            }
            catch { /* non-fatal */ }
        }

        private static void ParseJson(string json, ServerConfig cfg)
        {
            // Minimal JSON parser for the few fields we need.
            cfg.ManagementApiPort = ReadInt(json, "managementApiPort", cfg.ManagementApiPort);
            cfg.ManagementApiLocalhostExempt = ReadBool(json, "managementApiLocalhostExempt", cfg.ManagementApiLocalhostExempt);
            cfg.ManagementApiKey = ReadString(json, "managementApiKey");
            cfg.ReqopKey = ReadString(json, "reqopKey");
            // Game server startup
            cfg.World = ReadString(json, "world");
            cfg.WorldFilePath = ReadString(json, "worldFilePath");
            cfg.WorldIndex = ReadInt(json, "worldIndex", cfg.WorldIndex);
            cfg.Port = ReadInt(json, "port", cfg.Port);
            cfg.MaxPlayers = ReadInt(json, "maxPlayers", cfg.MaxPlayers);
            cfg.Password = ReadString(json, "password") ?? "";
            cfg.Motd = ReadString(json, "motd");
        }

        private static int ReadInt(string json, string key, int fallback)
        {
            string pattern = $"\"{key}\"";
            int idx = json.IndexOf(pattern, StringComparison.OrdinalIgnoreCase);
            if (idx < 0) return fallback;
            idx += pattern.Length;
            while (idx < json.Length && (json[idx] == ' ' || json[idx] == ':')) idx++;
            int start = idx;
            while (idx < json.Length && (char.IsDigit(json[idx]) || json[idx] == '-')) idx++;
            if (idx > start && int.TryParse(json.Substring(start, idx - start), out int v)) return v;
            return fallback;
        }

        private static bool ReadBool(string json, string key, bool fallback)
        {
            string pattern = $"\"{key}\"";
            int idx = json.IndexOf(pattern, StringComparison.OrdinalIgnoreCase);
            if (idx < 0) return fallback;
            idx += pattern.Length;
            while (idx < json.Length && (json[idx] == ' ' || json[idx] == ':')) idx++;
            if (json.Length - idx >= 4 && json.Substring(idx, 4) == "true") return true;
            if (json.Length - idx >= 5 && json.Substring(idx, 5) == "false") return false;
            return fallback;
        }

        private static string ReadString(string json, string key)
        {
            string pattern = $"\"{key}\"";
            int idx = json.IndexOf(pattern, StringComparison.OrdinalIgnoreCase);
            if (idx < 0) return null;
            idx += pattern.Length;
            while (idx < json.Length && (json[idx] == ' ' || json[idx] == ':')) idx++;
            if (idx >= json.Length || json[idx] != '"') return null; // null or non-string value
            idx++; // skip opening quote
            int start = idx;
            while (idx < json.Length)
            {
                if (json[idx] == '\\') { idx += 2; continue; }
                if (json[idx] == '"') break;
                idx++;
            }
            return json.Substring(start, idx - start);
        }
    }
}
