using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using TerrariaModder.Core.Logging;

namespace TerrariaModder.Core.Permissions
{
    /// <summary>
    /// Server-side permission tracking.
    /// Maps player slot → Role for the current session.
    /// In H&P: in-memory only (reset each session).
    /// In dedicated server: persisted admin GUIDs in core/permissions.json.
    /// </summary>
    public static class PermissionService
    {
        public enum PlayerRole { Player, Admin }

        private static ILogger _log;
        private static string _corePath;

        // Session state: slot → role (reset on map clear / restart)
        private static readonly PlayerRole[] _slotRoles = new PlayerRole[256];

        // GUID → role mapping (persisted for dedicated server)
        private static readonly Dictionary<string, PlayerRole> _guidRoles =
            new Dictionary<string, PlayerRole>(StringComparer.OrdinalIgnoreCase);

        // GUID → slot (current session)
        private static readonly Dictionary<string, int> _guidToSlot =
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        // Slot → GUID (current session)
        private static readonly string[] _slotGuids = new string[256];

        // Per-mod grants: GUID → set of modIds the player has explicit access to
        private static readonly Dictionary<string, HashSet<string>> _modGrants =
            new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

        // The one-time reqop key printed at startup (server only)
        private static string _reqopKey;

        public static string ReqopKey => _reqopKey;

        // ---- Initialization ----

        public static void Initialize(string corePath, ILogger log)
        {
            _log = log;
            _corePath = corePath;

            // Generate reqop key
            _reqopKey = GenerateReqopKey();
            _log?.Info($"[Permissions] Reqop key: {_reqopKey}  (players type /reqop {_reqopKey} in game chat)");

            // Initialize ban list (both H&P and dedicated)
            BanService.Initialize(corePath, log);

            // Load persisted admin GUIDs (dedicated server only)
            try
            {
                bool isDedicated = System.Environment.GetEnvironmentVariable("TERRARIA_MODDER_DEDSERV") == "1";

                if (isDedicated)
                    LoadPermissionsFile();
            }
            catch { }
        }

        // ---- Session management ----

        /// <summary>
        /// Called when a client connects and sends IdentityAnnounce.
        /// Returns the role assigned to this player for the session.
        /// </summary>
        public static PlayerRole OnClientConnect(int slot, string guid, string remoteAddress)
        {
            // Clean up stale data from previous occupant of this slot
            string previousGuid = _slotGuids[slot];
            if (!string.IsNullOrEmpty(previousGuid))
                _guidToSlot.Remove(previousGuid);
            _slotRoles[slot] = PlayerRole.Player;

            // Validate GUID to prevent path traversal via malicious values
            if (!string.IsNullOrEmpty(guid) &&
                (guid.Contains("..") || guid.Contains("/") || guid.Contains("\\") || guid.Length > 40))
            {
                _log?.Warn($"[PermissionService] Rejected invalid GUID from slot {slot}: {guid}");
                guid = null;
            }

            _slotGuids[slot] = guid;
            if (!string.IsNullOrEmpty(guid))
                _guidToSlot[guid] = slot;

            // Determine role:
            // 1. Localhost → always admin (H&P host, server operator on same machine)
            // 2. Persisted admin GUID → admin
            // 3. Default → player
            PlayerRole role = PlayerRole.Player;

            if (IsLocalhost(remoteAddress))
            {
                role = PlayerRole.Admin;
                _log?.Info($"[Permissions] Slot {slot} ({guid}) auto-admin (localhost)");
            }
            else if (!string.IsNullOrEmpty(guid) && _guidRoles.TryGetValue(guid, out PlayerRole persistedRole))
            {
                role = persistedRole;
                _log?.Info($"[Permissions] Slot {slot} ({guid}) role from persistence: {role}");
            }
            else
            {
                _log?.Info($"[Permissions] Slot {slot} ({guid}) assigned Player role");
            }

            _slotRoles[slot] = role;
            return role;
        }

        /// <summary>Called when a client disconnects.</summary>
        public static void OnClientDisconnect(int slot)
        {
            string guid = _slotGuids[slot];
            if (!string.IsNullOrEmpty(guid))
                _guidToSlot.Remove(guid);
            _slotGuids[slot] = null;
            _slotRoles[slot] = PlayerRole.Player;
        }

        // ---- Role queries ----

        public static PlayerRole GetRole(int slot)
            => (slot >= 0 && slot < 256) ? _slotRoles[slot] : PlayerRole.Player;

        public static bool IsAdmin(int slot)
        {
            if (GetRole(slot) == PlayerRole.Admin) return true;
            // H&P host: identity handshake doesn't complete (custom packets don't work
            // via H&P loopback), so the host slot is never registered as admin.
            // Skip on dedServ — Netplay.IsHostAndPlay touches Main which crashes headless.
            if (Environment.GetEnvironmentVariable("TERRARIA_MODDER_DEDSERV") != "1")
            {
                try { if (Terraria.Netplay.IsHostAndPlay && slot == 0) return true; } catch { }
            }
            return false;
        }

        /// <summary>True if player has access to a specific mod (admin or explicit grant).</summary>
        public static bool HasModAccess(int slot, string modId)
        {
            if (IsAdmin(slot)) return true;
            string guid = _slotGuids[slot];
            if (guid == null) return false;
            return _modGrants.TryGetValue(guid, out var grants) && grants.Contains(modId);
        }

        /// <summary>Get the GUID associated with a connected slot (may be null).</summary>
        public static string GetGuid(int slot)
            => (slot >= 0 && slot < 256) ? _slotGuids[slot] : null;

        /// <summary>Find a connected slot by GUID.</summary>
        public static int FindSlotByGuid(string guid)
        {
            if (string.IsNullOrEmpty(guid)) return -1;
            return _guidToSlot.TryGetValue(guid, out int slot) ? slot : -1;
        }

        // ---- Role mutations ----

        public static bool Promote(int slot)
        {
            if (slot < 0 || slot >= 256) return false;
            _slotRoles[slot] = PlayerRole.Admin;
            string guid = _slotGuids[slot];
            if (!string.IsNullOrEmpty(guid))
            {
                _guidRoles[guid] = PlayerRole.Admin;
                SavePermissionsFile();
            }
            _log?.Info($"[Permissions] Slot {slot} promoted to Admin");
            return true;
        }

        public static bool Demote(int slot)
        {
            if (slot < 0 || slot >= 256) return false;
            _slotRoles[slot] = PlayerRole.Player;
            string guid = _slotGuids[slot];
            if (!string.IsNullOrEmpty(guid))
            {
                _guidRoles.Remove(guid);
                SavePermissionsFile();
            }
            _log?.Info($"[Permissions] Slot {slot} demoted to Player");
            return true;
        }

        /// <summary>Handle /reqop key from player chat.</summary>
        public static bool TryReqop(int slot, string key)
        {
            if (!string.Equals(key, _reqopKey, StringComparison.Ordinal)) return false;
            Promote(slot);
            return true;
        }

        public static void SetModGrant(string guid, string modId, bool enabled)
        {
            if (!_modGrants.TryGetValue(guid, out var grants))
            {
                grants = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                _modGrants[guid] = grants;
            }
            if (enabled) grants.Add(modId);
            else grants.Remove(modId);
        }

        // ---- Player list ----

        // Cached reflection fields for server-mode fallback (Main.player is inaccessible via
        // direct compile-time reference when TerrariaServer.exe triggers a TypeInitializationException).
        private static Array _playerArrayCache;
        private static FieldInfo _playerActiveField;
        private static FieldInfo _playerNameField;
        private static bool _playerReflectionDone;

        private static Array GetPlayerArray()
        {
            // Try direct compile-time reference first (fast path, works in client/H&P mode)
            try
            {
                var arr = Terraria.Main.player;
                if (arr != null) return arr;
            }
            catch { }

            // Server-mode fallback: compile-time Terraria reference may resolve to Terraria.exe
            // (loaded into default Load context) while the running server types are in TerrariaServer.exe
            // (loaded into LoadFrom context). Both have assembly name "Terraria". Identify the correct
            // one by checking Main.dedServ == true (only true in the actual running server instance).
            if (!_playerReflectionDone)
            {
                _playerReflectionDone = true;
                try
                {
                    Array fallback = null;
                    foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                    {
                        var asmN = asm.GetName().Name;
                        if (asmN != "Terraria" && asmN != "TerrariaServer") continue;
                        try
                        {
                            var mainType = asm.GetType("Terraria.Main");
                            if (mainType == null) continue;
                            var dedServField = mainType.GetField("dedServ", BindingFlags.Public | BindingFlags.Static);
                            var playerField  = mainType.GetField("player",  BindingFlags.Public | BindingFlags.Static);
                            if (playerField == null) continue;
                            var arr = playerField.GetValue(null) as Array;
                            if (arr == null) continue;
                            bool isDedServ = dedServField != null && (bool)dedServField.GetValue(null);
                            if (isDedServ) { _playerArrayCache = arr; break; } // found server assembly
                            fallback ??= arr; // keep as fallback in case dedServ is never true
                        }
                        catch { }
                    }
                    _playerArrayCache ??= fallback;
                }
                catch { }
            }
            return _playerArrayCache;
        }

        private static (bool active, string name) GetPlayerInfo(object p)
        {
            if (_playerActiveField == null)
            {
                _playerActiveField = p.GetType().GetField("active", BindingFlags.Public | BindingFlags.Instance);
                _playerNameField   = p.GetType().GetField("name",   BindingFlags.Public | BindingFlags.Instance);
            }
            bool active = _playerActiveField != null && (bool)_playerActiveField.GetValue(p);
            string name = (string)_playerNameField?.GetValue(p) ?? "";
            return (active, name);
        }

        public static List<(int slot, string name, string guid, PlayerRole role)> GetConnectedPlayers()
        {
            var result = new List<(int, string, string, PlayerRole)>();
            try
            {
                var players = GetPlayerArray();
                if (players == null) return result;
                for (int i = 0; i < 255 && i < players.Length; i++)
                {
                    object p = players.GetValue(i);
                    if (p == null) continue;
                    var (active, name) = GetPlayerInfo(p);
                    if (!active) continue;
                    string guid = _slotGuids[i] ?? "";
                    result.Add((i, name, guid, _slotRoles[i]));
                }
            }
            catch { }
            return result;
        }

        /// <summary>Find a connected slot by player name (case-insensitive).</summary>
        public static int FindSlotByName(string name)
        {
            try
            {
                var players = GetPlayerArray();
                if (players == null) return -1;
                for (int i = 0; i < 255 && i < players.Length; i++)
                {
                    object p = players.GetValue(i);
                    if (p == null) continue;
                    var (active, pname) = GetPlayerInfo(p);
                    if (active && string.Equals(pname, name, StringComparison.OrdinalIgnoreCase))
                        return i;
                }
            }
            catch { }
            return -1;
        }

        // ---- Mod grants (per-mod access for non-admins) ----

        public static HashSet<string> GetModGrants(string guid)
        {
            if (string.IsNullOrEmpty(guid)) return new HashSet<string>();
            return _modGrants.TryGetValue(guid, out var g) ? g : new HashSet<string>();
        }

        // ---- Client-side role ----

        /// <summary>
        /// The local player's role for the current session.
        /// Set by HandlePermissionSync on the client.
        /// In singleplayer always treated as Admin.
        /// </summary>
        public static PlayerRole ClientRole { get; private set; } = PlayerRole.Player;

        /// <summary>Called when the client receives a PermissionSync packet from the server.</summary>
        public static void SetClientRole(PlayerRole role)
        {
            ClientRole = role;
            _log?.Info($"[Permissions] Client role set to: {role}");
        }

        /// <summary>True if the local player has admin rights in the current session.</summary>
        public static bool IsLocalPlayerAdmin()
        {
            try { if (Terraria.Main.netMode == 0) return true; } catch { }
            return ClientRole == PlayerRole.Admin;
        }

        /// <summary>Reset client role (call when disconnecting).</summary>
        public static void ClearClientRole() => ClientRole = PlayerRole.Player;

        // ---- Persistence ----

        private static void LoadPermissionsFile()
        {
            string path = Path.Combine(_corePath, "permissions.json");
            if (!File.Exists(path)) return;

            try
            {
                string json = File.ReadAllText(path);
                // Simple parse: find "adminGuids": ["guid1", "guid2", ...]
                int idx = json.IndexOf("\"adminGuids\"", StringComparison.OrdinalIgnoreCase);
                if (idx < 0) return;

                int arrStart = json.IndexOf('[', idx);
                int arrEnd = json.IndexOf(']', arrStart);
                if (arrStart < 0 || arrEnd < 0) return;

                string arr = json.Substring(arrStart + 1, arrEnd - arrStart - 1);
                int pos = 0;
                while (pos < arr.Length)
                {
                    int q1 = arr.IndexOf('"', pos);
                    if (q1 < 0) break;
                    int q2 = arr.IndexOf('"', q1 + 1);
                    if (q2 < 0) break;
                    string guid = arr.Substring(q1 + 1, q2 - q1 - 1);
                    if (!string.IsNullOrEmpty(guid))
                        _guidRoles[guid] = PlayerRole.Admin;
                    pos = q2 + 1;
                }

                _log?.Info($"[Permissions] Loaded {_guidRoles.Count} admin GUID(s) from permissions.json");
            }
            catch (Exception ex)
            {
                _log?.Warn($"[Permissions] Failed to load permissions.json: {ex.Message}");
            }
        }

        private static void SavePermissionsFile()
        {
            if (string.IsNullOrEmpty(_corePath)) return;
            try
            {
                var admins = _guidRoles.Where(kv => kv.Value == PlayerRole.Admin).Select(kv => kv.Key).ToList();
                var sb = new System.Text.StringBuilder();
                sb.AppendLine("{");
                sb.Append("  \"adminGuids\": [");
                for (int i = 0; i < admins.Count; i++)
                {
                    sb.Append($"\"{admins[i]}\"");
                    if (i < admins.Count - 1) sb.Append(", ");
                }
                sb.AppendLine("]");
                sb.AppendLine("}");
                string target = Path.Combine(_corePath, "permissions.json");
                string temp = target + ".tmp";
                File.WriteAllText(temp, sb.ToString());
                if (File.Exists(target)) File.Delete(target);
                File.Move(temp, target);
            }
            catch (Exception ex)
            {
                _log?.Warn($"[Permissions] Failed to save permissions.json: {ex.Message}");
            }
        }

        // ---- Helpers ----

        private static bool IsLocalhost(string address)
        {
            if (string.IsNullOrEmpty(address)) return false;
            return address.Contains("127.0.0.1") || address.Contains("::1") || address == "localhost";
        }

        private static string GenerateReqopKey()
        {
            var rng = new Random();
            const string chars = "abcdefghijkmnpqrstuvwxyz23456789";
            var key = new char[8];
            for (int i = 0; i < key.Length; i++)
                key[i] = chars[rng.Next(chars.Length)];
            return new string(key);
        }
    }
}
