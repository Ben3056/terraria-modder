using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using TerrariaModder.Core.Logging;

namespace TerrariaModder.Core.Permissions
{
    /// <summary>
    /// Persistent ban list stored in core/banlist.json.
    /// Works for both H&P and dedicated server.
    /// Bans are matched by GUID and/or IP address.
    /// </summary>
    public static class BanService
    {
        public struct BanEntry
        {
            public string Guid;
            public string Name;
            public string Ip;
            public string Reason;
            public string BannedAt;
        }

        private static readonly List<BanEntry> _bans = new List<BanEntry>();
        private static string _corePath;
        private static ILogger _log;

        public static void Initialize(string corePath, ILogger log)
        {
            _corePath = corePath;
            _log = log;
            Load();
        }

        /// <summary>True if the given GUID or IP matches any ban entry.</summary>
        public static bool IsBanned(string guid, string ip)
            => GetMatchedBan(guid, ip).HasValue;

        /// <summary>Returns the first matching ban entry, or null if not banned.</summary>
        public static BanEntry? GetMatchedBan(string guid, string ip)
        {
            foreach (var b in _bans)
            {
                if (!string.IsNullOrEmpty(guid) && !string.IsNullOrEmpty(b.Guid)
                    && b.Guid.Equals(guid, StringComparison.OrdinalIgnoreCase))
                    return b;

                if (!string.IsNullOrEmpty(ip) && !string.IsNullOrEmpty(b.Ip)
                    && ip.Equals(b.Ip, StringComparison.OrdinalIgnoreCase))
                    return b;
            }
            return null;
        }

        /// <summary>Add a ban entry and persist it immediately.</summary>
        public static void AddBan(string guid, string name, string ip, string reason)
        {
            // Remove existing entry for this GUID first (prevent duplicates)
            _bans.RemoveAll(b => !string.IsNullOrEmpty(guid)
                && b.Guid.Equals(guid, StringComparison.OrdinalIgnoreCase));

            _bans.Add(new BanEntry
            {
                Guid = guid ?? "",
                Name = name ?? "",
                Ip = ip ?? "",
                Reason = reason ?? "Banned",
                BannedAt = DateTime.UtcNow.ToString("yyyy-MM-dd")
            });

            Save();
            _log?.Info($"[Bans] Banned {name} (guid={guid}, ip={ip}): {reason}");
        }

        /// <summary>Remove a ban by GUID. Returns true if found and removed.</summary>
        public static bool RemoveBan(string guid)
        {
            int removed = _bans.RemoveAll(b =>
                b.Guid.Equals(guid, StringComparison.OrdinalIgnoreCase));

            if (removed > 0)
            {
                Save();
                _log?.Info($"[Bans] Unbanned GUID {guid}");
                return true;
            }
            return false;
        }

        public static IReadOnlyList<BanEntry> GetBans() => _bans.AsReadOnly();

        // ---- Persistence ----

        private static void Load()
        {
            if (string.IsNullOrEmpty(_corePath)) return;
            string path = Path.Combine(_corePath, "banlist.json");
            if (!File.Exists(path)) return;

            try
            {
                string json = File.ReadAllText(path);
                _bans.Clear();

                // Find "bans" array
                int arrStart = json.IndexOf('[');
                int arrEnd = json.LastIndexOf(']');
                if (arrStart < 0 || arrEnd < 0) return;

                string arr = json.Substring(arrStart + 1, arrEnd - arrStart - 1);

                // Parse each {...} object in the array
                int pos = 0;
                while (pos < arr.Length)
                {
                    int objStart = arr.IndexOf('{', pos);
                    if (objStart < 0) break;
                    int objEnd = arr.IndexOf('}', objStart);
                    if (objEnd < 0) break;

                    string obj = arr.Substring(objStart + 1, objEnd - objStart - 1);
                    var entry = new BanEntry
                    {
                        Guid = ReadField(obj, "guid"),
                        Name = ReadField(obj, "name"),
                        Ip = ReadField(obj, "ip"),
                        Reason = ReadField(obj, "reason"),
                        BannedAt = ReadField(obj, "bannedAt")
                    };

                    if (!string.IsNullOrEmpty(entry.Guid) || !string.IsNullOrEmpty(entry.Ip))
                        _bans.Add(entry);

                    pos = objEnd + 1;
                }

                _log?.Info($"[Bans] Loaded {_bans.Count} ban(s) from banlist.json");
            }
            catch (Exception ex)
            {
                _log?.Warn($"[Bans] Failed to load banlist.json: {ex.Message}");
            }
        }

        private static void Save()
        {
            if (string.IsNullOrEmpty(_corePath)) return;
            try
            {
                var sb = new StringBuilder();
                sb.AppendLine("{");
                sb.AppendLine("  \"bans\": [");
                for (int i = 0; i < _bans.Count; i++)
                {
                    var b = _bans[i];
                    sb.Append("    {");
                    sb.Append($"\"guid\": \"{Escape(b.Guid)}\", ");
                    sb.Append($"\"name\": \"{Escape(b.Name)}\", ");
                    sb.Append($"\"ip\": \"{Escape(b.Ip)}\", ");
                    sb.Append($"\"reason\": \"{Escape(b.Reason)}\", ");
                    sb.Append($"\"bannedAt\": \"{Escape(b.BannedAt)}\"");
                    sb.Append(i < _bans.Count - 1 ? "},\n" : "}\n");
                }
                sb.AppendLine("  ]");
                sb.AppendLine("}");
                string target = Path.Combine(_corePath, "banlist.json");
                string temp = target + ".tmp";
                File.WriteAllText(temp, sb.ToString());
                if (File.Exists(target)) File.Delete(target);
                File.Move(temp, target);
            }
            catch (Exception ex)
            {
                _log?.Warn($"[Bans] Failed to save banlist.json: {ex.Message}");
            }
        }

        private static string ReadField(string obj, string key)
        {
            string pattern = $"\"{key}\"";
            int idx = obj.IndexOf(pattern, StringComparison.OrdinalIgnoreCase);
            if (idx < 0) return "";
            idx += pattern.Length;
            while (idx < obj.Length && (obj[idx] == ' ' || obj[idx] == ':')) idx++;
            if (idx >= obj.Length || obj[idx] != '"') return "";
            idx++;
            int start = idx;
            while (idx < obj.Length)
            {
                if (obj[idx] == '\\') { idx += 2; continue; }
                if (obj[idx] == '"') break;
                idx++;
            }
            return obj.Substring(start, idx - start);
        }

        private static string Escape(string s) => (s ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"");
    }
}
