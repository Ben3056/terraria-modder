using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using TerrariaModder.Core.Logging;

namespace TerrariaModder.Core.Server
{
    /// <summary>
    /// Manages server-side pending item grants for players.
    ///
    /// On a dedicated server, items can be granted to players via the management API.
    /// Grants are stored in {corePath}/player-data/{guid}.json and delivered to the
    /// player on their next join via ItemGrant packet (Phase H).
    ///
    /// File format:
    /// {
    ///   "version": 1,
    ///   "guid": "...",
    ///   "playerName": "...",
    ///   "grants": [
    ///     { "fullId": "mod-id:item-name", "stack": 1, "prefix": 0, "grantedAt": "2026-..." }
    ///   ]
    /// }
    /// </summary>
    public static class PlayerDataStore
    {
        private static ILogger _log;
        private static string _playerDataPath;

        public class PendingGrant
        {
            public string FullId { get; set; }      // "mod-id:item-name"
            public int Stack { get; set; } = 1;
            public int Prefix { get; set; }
            public string GrantedAt { get; set; }
        }

        public class PlayerData
        {
            public string Guid { get; set; }
            public string PlayerName { get; set; }
            public List<PendingGrant> Grants { get; set; } = new List<PendingGrant>();
        }

        public static void Initialize(ILogger logger, string corePath)
        {
            _log = logger;
            _playerDataPath = Path.Combine(corePath, "player-data");
            try
            {
                if (!Directory.Exists(_playerDataPath))
                    Directory.CreateDirectory(_playerDataPath);
            }
            catch (Exception ex)
            {
                _log?.Warn($"[PlayerDataStore] Could not create player-data dir: {ex.Message}");
            }
        }

        /// <summary>
        /// Add a pending item grant for a player. Creates or appends to their data file.
        /// </summary>
        public static bool AddGrant(string guid, string playerName, string fullId, int stack, int prefix)
        {
            if (string.IsNullOrEmpty(guid) || string.IsNullOrEmpty(fullId)) return false;
            if (_playerDataPath == null) return false;

            try
            {
                var data = ReadData(guid) ?? new PlayerData { Guid = guid, PlayerName = playerName };
                if (!string.IsNullOrEmpty(playerName))
                    data.PlayerName = playerName; // keep name up to date
                data.Grants.Add(new PendingGrant
                {
                    FullId = fullId,
                    Stack = Math.Max(1, stack),
                    Prefix = prefix,
                    GrantedAt = DateTime.UtcNow.ToString("o")
                });
                WriteData(guid, data);
                _log?.Info($"[PlayerDataStore] Grant added: guid={guid} item={fullId} stack={stack} prefix={prefix}");
                return true;
            }
            catch (Exception ex)
            {
                _log?.Warn($"[PlayerDataStore] AddGrant failed for {guid}: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Get pending grants for a player. Returns empty list if none.
        /// </summary>
        public static List<PendingGrant> GetPendingGrants(string guid)
        {
            if (string.IsNullOrEmpty(guid) || _playerDataPath == null) return new List<PendingGrant>();
            try
            {
                var data = ReadData(guid);
                return data?.Grants ?? new List<PendingGrant>();
            }
            catch (Exception ex)
            {
                _log?.Warn($"[PlayerDataStore] GetPendingGrants failed for {guid}: {ex.Message}");
                return new List<PendingGrant>();
            }
        }

        /// <summary>
        /// Clear all pending grants for a player (call after delivery).
        /// </summary>
        public static void ClearGrants(string guid)
        {
            if (string.IsNullOrEmpty(guid) || _playerDataPath == null) return;
            try
            {
                string path = GetFilePath(guid);
                if (File.Exists(path)) File.Delete(path);
                _log?.Debug($"[PlayerDataStore] Cleared grants for {guid}");
            }
            catch (Exception ex)
            {
                _log?.Warn($"[PlayerDataStore] ClearGrants failed for {guid}: {ex.Message}");
            }
        }

        /// <summary>
        /// Get all pending grants grouped by mod ID. Used by the audit endpoint.
        /// Returns: Dictionary[modId] → list of (guid, playerName, itemCount)
        /// </summary>
        public static Dictionary<string, List<(string guid, string playerName, int itemCount)>> GetAllGrantsByMod()
        {
            var result = new Dictionary<string, List<(string, string, int)>>(StringComparer.OrdinalIgnoreCase);
            if (_playerDataPath == null) return result;

            try
            {
                foreach (var file in Directory.GetFiles(_playerDataPath, "*.json"))
                {
                    try
                    {
                        string guid = Path.GetFileNameWithoutExtension(file);
                        var data = ReadData(guid);
                        if (data == null || data.Grants == null || data.Grants.Count == 0) continue;

                        // Count grants per mod
                        var modCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                        foreach (var grant in data.Grants)
                        {
                            if (string.IsNullOrEmpty(grant.FullId)) continue;
                            int colon = grant.FullId.IndexOf(':');
                            if (colon <= 0) continue;
                            string modId = grant.FullId.Substring(0, colon);
                            modCounts[modId] = modCounts.TryGetValue(modId, out int c) ? c + 1 : 1;
                        }

                        foreach (var kvp in modCounts)
                        {
                            if (!result.TryGetValue(kvp.Key, out var list))
                                result[kvp.Key] = list = new List<(string, string, int)>();
                            list.Add((data.Guid ?? guid, data.PlayerName ?? "", kvp.Value));
                        }
                    }
                    catch { }
                }
            }
            catch (Exception ex)
            {
                _log?.Warn($"[PlayerDataStore] GetAllGrantsByMod failed: {ex.Message}");
            }

            return result;
        }

        // ─── Private helpers ──────────────────────────────────────────────────

        private static string GetFilePath(string guid) =>
            Path.Combine(_playerDataPath, guid + ".json");

        private static PlayerData ReadData(string guid)
        {
            string path = GetFilePath(guid);
            if (!File.Exists(path)) return null;
            string json = File.ReadAllText(path, Encoding.UTF8);
            return ParseData(json);
        }

        private static void WriteData(string guid, PlayerData data)
        {
            string path = GetFilePath(guid);
            string dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            var sb = new StringBuilder();
            sb.Append("{\"version\":1");
            sb.Append($",\"guid\":\"{Esc(data.Guid ?? guid)}\"");
            sb.Append($",\"playerName\":\"{Esc(data.PlayerName ?? "")}\"");
            sb.Append(",\"grants\":[");
            bool first = true;
            foreach (var g in data.Grants)
            {
                if (!first) sb.Append(',');
                first = false;
                sb.Append($"{{\"fullId\":\"{Esc(g.FullId)}\",\"stack\":{g.Stack},\"prefix\":{g.Prefix},\"grantedAt\":\"{Esc(g.GrantedAt)}\"}}");
            }
            sb.Append("]}");

            // Atomic write
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, sb.ToString(), Encoding.UTF8);
            if (File.Exists(path)) File.Delete(path);
            File.Move(tmp, path);
        }

        private static PlayerData ParseData(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            var data = new PlayerData();
            data.Guid = ExtractString(json, "guid");
            data.PlayerName = ExtractString(json, "playerName");
            data.Grants = new List<PendingGrant>();

            // Parse grants array manually
            int arrStart = json.IndexOf("\"grants\"");
            if (arrStart < 0) return data;
            arrStart = json.IndexOf('[', arrStart);
            if (arrStart < 0) return data;
            int arrEnd = json.LastIndexOf(']');
            if (arrEnd <= arrStart) return data;

            string arrContent = json.Substring(arrStart + 1, arrEnd - arrStart - 1).Trim();
            if (string.IsNullOrEmpty(arrContent)) return data;

            // Split on object boundaries
            int depth = 0, objStart = -1;
            for (int i = 0; i < arrContent.Length; i++)
            {
                if (arrContent[i] == '{') { if (depth == 0) objStart = i; depth++; }
                else if (arrContent[i] == '}')
                {
                    depth--;
                    if (depth == 0 && objStart >= 0)
                    {
                        string obj = arrContent.Substring(objStart, i - objStart + 1);
                        var grant = new PendingGrant
                        {
                            FullId = ExtractString(obj, "fullId"),
                            Stack = Math.Max(1, ExtractInt(obj, "stack", 1)),
                            Prefix = ExtractInt(obj, "prefix", 0),
                            GrantedAt = ExtractString(obj, "grantedAt")
                        };
                        if (!string.IsNullOrEmpty(grant.FullId))
                            data.Grants.Add(grant);
                        objStart = -1;
                    }
                }
            }

            return data;
        }

        private static string ExtractString(string json, string key)
        {
            var m = Regex.Match(json, $"\"{Regex.Escape(key)}\"\\s*:\\s*\"([^\"]*)\"");
            return m.Success ? m.Groups[1].Value : "";
        }

        private static int ExtractInt(string json, string key, int defaultVal = 0)
        {
            var m = Regex.Match(json, $"\"{Regex.Escape(key)}\"\\s*:\\s*(-?\\d+)");
            return m.Success && int.TryParse(m.Groups[1].Value, out int v) ? v : defaultVal;
        }

        private static string Esc(string s)
        {
            if (s == null) return "";
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r");
        }
    }
}
