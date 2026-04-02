using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using TerrariaModder.Core.Debug;
using TerrariaModder.Core.Logging;

namespace TerrariaModder.Core.Server
{
    /// <summary>
    /// Intercepts dedicated server console input to route /commands to CommandRegistry.
    ///
    /// Approach: Replace Console.In with a custom TextReader that:
    ///   - First drains a pre-queued answer queue (for unattended startup via server-config.json)
    ///   - Intercepts lines starting with '/'
    ///   - Routes them to CommandRegistry.Execute()
    ///   - Returns an empty string for Terraria's own input handler (harmless no-op)
    ///   - Passes all other lines through unmodified to Terraria's handler
    ///
    /// This avoids Harmony patching the server's DedServ loop entirely.
    ///
    /// Auto-wizard: When server-config.json has World/Port/MaxPlayers configured,
    /// a Harmony prefix on Main.startDedServ pre-populates the answer queue with
    /// the correct responses for Terraria's startup wizard — preventing any interactive prompts.
    ///
    /// Wizard answer order (matches Terraria 1.4.5 DedServ):
    ///   1. World number (1-based index into sorted .wld file list)
    ///   2. Max players
    ///   3. Port
    ///   4. "n" (no UPnP auto-forward)
    ///   5. Password (empty = none)
    ///
    /// Server commands registered here: mods, players, op, deop, reqop, time, stop
    /// </summary>
    public static class ServerConsole
    {
        private static ILogger _log;
        private static bool _initialized;
        private static CommandInterceptingReader _interceptingReader;
        private static bool _wizardAnswersQueued;

        public static void Initialize(ILogger log)
        {
            if (_initialized) return;
            _initialized = true;
            _log = log;

            try
            {
                // Replace Console.In with our intercepting reader
                var original = Console.In;
                _interceptingReader = new CommandInterceptingReader(original, log);
                Console.SetIn(_interceptingReader);

                // Register server-specific slash commands
                RegisterServerCommands();

                // If server-config.json has startup values, pre-populate the wizard answer queue.
                // We do this immediately (not in a startDedServ prefix) because:
                //   1. TerrariaServer.exe may not have a patchable startDedServ method.
                //   2. Our LoadPlugins() runs during Main.ctor, before LaunchInitializer sets Main.WorldPath,
                //      so we use a fallback path when Main.WorldPath is empty.
                //   3. If -world arg already bypasses the wizard, the queued answers are consumed harmlessly
                //      by the post-world command loop.
                if (ServerConfig.Instance.HasStartupConfig)
                {
                    PatchStartDedServ(log);  // still try for timing safety
                    QueueWizardAnswers(log); // pre-queue immediately as reliable fallback
                }

                log?.Info("[ServerConsole] Console input interceptor active — prefix commands with /");
            }
            catch (Exception ex)
            {
                log?.Warn($"[ServerConsole] Initialize failed: {ex.Message}");
            }
        }

        // ---- Harmony patch: pre-populate wizard queue before startDedServ runs ----

        private static void PatchStartDedServ(ILogger log)
        {
            try
            {
                Assembly terraria = null;
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    if (asm.GetName().Name == "Terraria" || asm.GetName().Name == "TerrariaServer") { terraria = asm; break; }
                }
                if (terraria == null) { log?.Warn("[ServerConsole] Terraria assembly not found for startDedServ patch"); return; }

                Type mainType = terraria.GetType("Terraria.Main");
                if (mainType == null) { log?.Warn("[ServerConsole] Main type not found"); return; }

                MethodInfo startDedServ = mainType.GetMethod("startDedServ",
                    BindingFlags.NonPublic | BindingFlags.Instance);
                if (startDedServ == null) { log?.Warn("[ServerConsole] Main.startDedServ not found — wizard auto-answer disabled"); return; }

                // We use a fresh Harmony instance so as not to conflict with PluginLoader's instance
                var harmony = new Harmony("TerrariaModder.ServerConsole.WizardPatch");
                MethodInfo prefix = typeof(ServerConsole).GetMethod(
                    nameof(StartDedServ_Prefix), BindingFlags.NonPublic | BindingFlags.Static);
                harmony.Patch(startDedServ, prefix: new HarmonyMethod(prefix));

                log?.Info("[ServerConsole] startDedServ patched — wizard will be auto-answered from server-config.json");
            }
            catch (Exception ex)
            {
                log?.Warn($"[ServerConsole] startDedServ patch failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Immediately pre-queues wizard answers. Called from Initialize() as a reliable
        /// fallback when startDedServ patching is unavailable or fires too late.
        /// Uses a standard OS fallback for WorldPath since Main.WorldPath may not be
        /// set yet (LoadPlugins runs during Main.ctor, before LaunchInitializer).
        /// </summary>
        private static void QueueWizardAnswers(ILogger log)
        {
            try
            {
                if (_wizardAnswersQueued) return; // prevent double-queuing from Initialize + StartDedServ_Prefix
                var cfg = ServerConfig.Instance;
                if (!cfg.HasStartupConfig) return;
                if (_interceptingReader == null) return;

                int worldIdx = ResolveWorldIndex(cfg);
                if (worldIdx <= 0)
                {
                    log?.Warn("[ServerConsole] QueueWizardAnswers: could not resolve world index — wizard will be interactive");
                    return;
                }

                _interceptingReader.Enqueue(worldIdx.ToString());
                _interceptingReader.Enqueue(cfg.MaxPlayers.ToString());
                _interceptingReader.Enqueue(cfg.Port.ToString());
                _interceptingReader.Enqueue("n");
                _interceptingReader.Enqueue(cfg.Password ?? "");
                _wizardAnswersQueued = true;

                log?.Info($"[ServerConsole] Pre-queued wizard answers: world #{worldIdx}, port {cfg.Port}, maxPlayers {cfg.MaxPlayers}");
            }
            catch (Exception ex)
            {
                log?.Warn($"[ServerConsole] QueueWizardAnswers failed: {ex.Message}");
            }
        }

        private static void StartDedServ_Prefix()
        {
            // Delegate to QueueWizardAnswers which has the dedup guard
            QueueWizardAnswers(_log);
        }

        /// <summary>
        /// Finds the 1-based DedServ world list index for the configured world name/index.
        /// Returns 0 if no match found.
        /// </summary>
        private static int ResolveWorldIndex(ServerConfig cfg)
        {
            if (cfg.WorldIndex > 0)
                return cfg.WorldIndex;

            if (string.IsNullOrEmpty(cfg.World))
                return 0;

            try
            {
                // Terraria's world path: Main.WorldPath (may be null if called before LaunchInitializer runs)
                string worldPath = null;
                try { worldPath = Terraria.Main.WorldPath; } catch { /* TypeInitializationException in server mode */ }

                // Fallback: standard OS documents path (works even before LaunchInitializer sets Main.WorldPath)
                if (string.IsNullOrEmpty(worldPath) || !Directory.Exists(worldPath))
                    worldPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                                             "My Games", "Terraria", "Worlds");

                if (!Directory.Exists(worldPath)) return 0;

                // Collect .wld files (excludes .bak) and sort by filename — matches Terraria's display order
                var files = Directory.GetFiles(worldPath, "*.wld")
                    .Where(f => !f.EndsWith(".bak", StringComparison.OrdinalIgnoreCase))
                    .Select(f => Path.GetFileNameWithoutExtension(f))
                    .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                string target = cfg.World.Trim();
                for (int i = 0; i < files.Count; i++)
                {
                    if (string.Equals(files[i], target, StringComparison.OrdinalIgnoreCase))
                        return i + 1; // 1-based
                }

                _log?.Warn($"[ServerConsole] World '{cfg.World}' not found in {worldPath} — available: {string.Join(", ", files)}");
                return 0;
            }
            catch (Exception ex)
            {
                _log?.Warn($"[ServerConsole] ResolveWorldIndex error: {ex.Message}");
                return 0;
            }
        }

        // ---- Server command registrations ----

        private static void RegisterServerCommands()
        {
            CommandRegistry.Register("mods", "List loaded mods", _ =>
            {
                var mods = PluginLoader.Mods;
                Console.WriteLine($"[Mods] {mods.Count} loaded:");
                foreach (var m in mods)
                    Console.WriteLine($"  {m.Manifest?.Id ?? "?"} v{m.Manifest?.Version ?? "?"} [{m.State}]");
            });

            CommandRegistry.Register("players", "List connected players", _ =>
            {
                var players = Permissions.PermissionService.GetConnectedPlayers();
                if (players.Count == 0) { Console.WriteLine("[Players] No players connected"); return; }
                Console.WriteLine($"[Players] {players.Count} connected:");
                foreach (var (slot, name, guid, role) in players)
                    Console.WriteLine($"  [{slot}] {name} — {role}");
            });

            CommandRegistry.Register("op", "Promote player to admin: op <name>", args =>
            {
                string target = args.Length > 0 ? string.Join(" ", args).Trim() : "";
                if (string.IsNullOrWhiteSpace(target)) { Console.WriteLine("Usage: /op <playerName>"); return; }
                int slot = Permissions.PermissionService.FindSlotByName(target);
                if (slot < 0) { Console.WriteLine($"Player '{target}' is not currently connected"); return; }
                Permissions.PermissionService.Promote(slot);
                var role = Permissions.PermissionService.GetRole(slot);
                var grants = Permissions.PermissionService.GetModGrants(Permissions.PermissionService.GetGuid(slot));
                Net.NetSync.SendPermissionSync(slot, role, grants);
                Net.NetSync.BroadcastPlayerListUpdate();
                Console.WriteLine($"Promoted {target} to Admin");
                _log?.Info($"[ServerConsole] /op {target}");
            });

            CommandRegistry.Register("deop", "Demote player from admin: deop <name>", args =>
            {
                string target = args.Length > 0 ? string.Join(" ", args).Trim() : "";
                if (string.IsNullOrWhiteSpace(target)) { Console.WriteLine("Usage: /deop <playerName>"); return; }
                int slot = Permissions.PermissionService.FindSlotByName(target);
                if (slot < 0) { Console.WriteLine($"Player '{target}' is not currently connected"); return; }
                Permissions.PermissionService.Demote(slot);
                var role = Permissions.PermissionService.GetRole(slot);
                var grants = Permissions.PermissionService.GetModGrants(Permissions.PermissionService.GetGuid(slot));
                Net.NetSync.SendPermissionSync(slot, role, grants);
                Net.NetSync.BroadcastPlayerListUpdate();
                Console.WriteLine($"Demoted {target} from Admin");
                _log?.Info($"[ServerConsole] /deop {target}");
            });

            CommandRegistry.Register("reqop", "Print the current reqop key", _ =>
            {
                Console.WriteLine($"[reqop key] {Permissions.PermissionService.ReqopKey}");
                Console.WriteLine("Players can type: /reqop <key> in game chat to self-promote");
            });

            CommandRegistry.Register("time", "Set world time: time <dawn|noon|dusk|night>", args =>
            {
                string preset = args.Length > 0 ? args[0].ToLowerInvariant() : "";
                double time; bool dayTime;
                switch (preset)
                {
                    case "dawn":  time = 0.0;     dayTime = true;  break;
                    case "noon":  time = 27000.0; dayTime = true;  break;
                    case "dusk":  time = 0.0;     dayTime = false; break;
                    case "night": time = 16200.0; dayTime = false; break;
                    default:
                        Console.WriteLine("Usage: /time <dawn|noon|dusk|night>");
                        return;
                }
                try
                {
                    DedServProxy.SetWorldTime(time, dayTime);
                    Console.WriteLine($"Time set to {preset}");
                    _log?.Info($"[ServerConsole] /time {preset}");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error setting time: {ex.Message}");
                }
            });

            CommandRegistry.Register("stop", "Gracefully shut down the server", _ =>
            {
                Console.WriteLine("[TerrariaModder] Shutting down server...");
                _log?.Info("[ServerConsole] /stop — graceful shutdown initiated");
                try
                {
                    // Disconnect all clients and exit
                    Terraria.Netplay.Disconnect = true;
                }
                catch { /* ignore */ }
                Environment.Exit(0);
            });

            CommandRegistry.Register("myperm", "Show player permissions: myperm [name]", args =>
            {
                string target = args.Length > 0 ? string.Join(" ", args).Trim() : null;
                var players = Permissions.PermissionService.GetConnectedPlayers();

                if (!string.IsNullOrEmpty(target))
                {
                    var match = default((int slot, string name, string guid, Permissions.PermissionService.PlayerRole role));
                    bool found = false;
                    foreach (var p in players)
                    {
                        if (p.name.Equals(target, StringComparison.OrdinalIgnoreCase))
                        { match = p; found = true; break; }
                    }
                    if (!found) { Console.WriteLine($"Player '{target}' not found"); return; }
                    var grants = Permissions.PermissionService.GetModGrants(match.guid);
                    string grantStr = grants.Count > 0 ? $"  Grants: {string.Join(", ", grants)}" : "  No mod grants";
                    Console.WriteLine($"[Permissions] {match.name} (slot {match.slot}): {match.role}");
                    Console.WriteLine(grantStr);
                }
                else
                {
                    if (players.Count == 0) { Console.WriteLine("[Permissions] No players connected"); return; }
                    Console.WriteLine($"[Permissions] {players.Count} connected:");
                    foreach (var (slot, name, guid, role) in players)
                    {
                        var grants = Permissions.PermissionService.GetModGrants(guid);
                        string grantStr = grants.Count > 0 ? $" [grants: {string.Join(", ", grants)}]" : "";
                        Console.WriteLine($"  [{slot}] {name} — {role}{grantStr}");
                    }
                }
            });

            CommandRegistry.Register("config", "Get/set [Server] config: config <modId> [<key> <value>]", args =>
            {
                if (args.Length == 0)
                {
                    Console.WriteLine("Usage: /config <modId> [<key> <value>]");
                    Console.WriteLine("  /config <modId>          — list all [Server] properties for a mod");
                    Console.WriteLine("  /config <modId> <key> <value>  — set a [Server] property");
                    return;
                }

                string modId = args[0];
                var mod = PluginLoader.GetMod(modId);
                if (mod == null) { Console.WriteLine($"Mod not found: {modId}"); return; }
                var config = mod.Context?.Config;
                if (config == null) { Console.WriteLine($"No config for {modId}"); return; }
                var props = config.GetPropertyMetadata().Where(m => m.Scope == Config.ConfigScope.Server).ToList();

                if (args.Length == 1)
                {
                    // List all [Server] properties
                    if (props.Count == 0) { Console.WriteLine($"[Config] {modId}: no [Server] properties"); return; }
                    Console.WriteLine($"[Config] {modId} — {props.Count} [Server] propertie(s):");
                    foreach (var p in props)
                        Console.WriteLine($"  {p.Key} = {p.GetValue(config) ?? "null"}");
                    return;
                }

                if (args.Length < 3) { Console.WriteLine("Usage: /config <modId> <key> <value>"); return; }
                string key = args[1];
                string value = string.Join(" ", args, 2, args.Length - 2);

                var meta = props.FirstOrDefault(p => string.Equals(p.Key, key, StringComparison.OrdinalIgnoreCase));
                if (meta == null) { Console.WriteLine($"Property not found or not [Server]: {key}"); return; }

                try
                {
                    meta.SetValue(config, value);
                    Config.ConfigManager.Save(config);
                    Console.WriteLine($"[Config] {modId}.{meta.Key} = {meta.GetValue(config)}");
                    _log?.Info($"[ServerConsole] /config {modId} {meta.Key} = {value}");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Failed to set {key}: {ex.Message}");
                }
            });

            CommandRegistry.Register("mod", "Enable/disable mods: mod <enable|disable|list> [<id>]", args =>
            {
                if (args.Length == 0 || args[0].Equals("list", StringComparison.OrdinalIgnoreCase))
                {
                    var mods = PluginLoader.Mods;
                    Console.WriteLine($"[Mods] {mods.Count} loaded:");
                    foreach (var m in mods)
                        Console.WriteLine($"  {m.Manifest?.Id ?? "?"} v{m.Manifest?.Version ?? "?"} [{m.State}]");
                    return;
                }

                if (args.Length < 2)
                {
                    Console.WriteLine("Usage: /mod <enable|disable|list> [<modId>]");
                    return;
                }

                string action = args[0].ToLowerInvariant();
                string targetId = args[1];
                bool enable = action == "enable";
                if (action != "enable" && action != "disable")
                {
                    Console.WriteLine("Usage: /mod <enable|disable|list> [<modId>]");
                    return;
                }

                string corePath = Config.CoreConfig.Instance.CorePath;
                string filePath = Path.Combine(corePath, "enabled-mods.server.json");

                // Build current set — if no file, start from all currently loaded mod IDs
                var currentIds = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (File.Exists(filePath))
                {
                    string json = File.ReadAllText(filePath);
                    int i2 = 0;
                    while (i2 < json.Length)
                    {
                        while (i2 < json.Length && json[i2] != '"') i2++;
                        if (i2 >= json.Length) break;
                        i2++;
                        var sb2 = new System.Text.StringBuilder();
                        while (i2 < json.Length && json[i2] != '"')
                        {
                            if (json[i2] == '\\' && i2 + 1 < json.Length) i2++;
                            sb2.Append(json[i2++]);
                        }
                        i2++;
                        string id2 = sb2.ToString();
                        if (!string.IsNullOrEmpty(id2)) currentIds.Add(id2);
                    }
                }
                else
                {
                    // No file = all loaded mods enabled; seed from loaded mods
                    foreach (var m in PluginLoader.Mods)
                        if (m.Manifest?.Id != null) currentIds.Add(m.Manifest.Id);
                }

                if (enable) currentIds.Add(targetId);
                else currentIds.Remove(targetId);

                // Write JSON array
                string newJson = "[" + string.Join(",", currentIds.Select(id => $"\"{id}\"")) + "]";
                File.WriteAllText(filePath, newJson);
                Console.WriteLine($"[Mods] {(enable ? "Enabled" : "Disabled")} {targetId} (effective on next restart)");
                _log?.Info($"[ServerConsole] /mod {action} {targetId}");
            });

            CommandRegistry.Register("kick", "Kick player: kick <name>", args =>
            {
                string target = args.Length > 0 ? string.Join(" ", args).Trim() : "";
                if (string.IsNullOrWhiteSpace(target)) { Console.WriteLine("Usage: /kick <playerName>"); return; }
                int slot = Permissions.PermissionService.FindSlotByName(target);
                if (slot < 0) { Console.WriteLine($"Player '{target}' is not currently connected"); return; }
                try { DedServProxy.KickPlayer(slot); }
                catch (Exception ex) { Console.WriteLine($"Error kicking: {ex.Message}"); return; }
                Console.WriteLine($"Kicked {target}");
                _log?.Info($"[ServerConsole] /kick {target}");
            });

            CommandRegistry.Register("grant", "Grant/revoke per-mod access: grant <name> <modId> <true|false>", args =>
            {
                if (args.Length < 3)
                {
                    Console.WriteLine("Usage: /grant <playerName> <modId> <true|false>");
                    return;
                }
                string target = args[0];
                string modId = args[1];
                bool grant;
                if (!bool.TryParse(args[2], out grant)) { Console.WriteLine("Third argument must be true or false"); return; }
                int slot = Permissions.PermissionService.FindSlotByName(target);
                if (slot < 0) { Console.WriteLine($"Player '{target}' is not currently connected"); return; }
                string guid = Permissions.PermissionService.GetGuid(slot);
                if (string.IsNullOrEmpty(guid)) { Console.WriteLine($"No GUID for player '{target}'"); return; }
                Permissions.PermissionService.SetModGrant(guid, modId, grant);
                var role = Permissions.PermissionService.GetRole(slot);
                var grants = Permissions.PermissionService.GetModGrants(guid);
                Net.NetSync.SendPermissionSync(slot, role, grants);
                Console.WriteLine($"{(grant ? "Granted" : "Revoked")} {modId} for {target}");
                _log?.Info($"[ServerConsole] /grant {target} {modId} {grant}");
            });

            CommandRegistry.Register("help", "List available commands", _ =>
            {
                Console.WriteLine("[Server Commands]");
                Console.WriteLine("  /help              — This list");
                Console.WriteLine("  /mods              — List loaded mods");
                Console.WriteLine("  /players           — List connected players");
                Console.WriteLine("  /op <name>         — Promote to admin");
                Console.WriteLine("  /deop <name>       — Demote from admin");
                Console.WriteLine("  /kick <name>       — Disconnect player");
                Console.WriteLine("  /ban <name>        — Ban + disconnect");
                Console.WriteLine("  /ban --guid <guid> — Ban offline player");
                Console.WriteLine("  /unban <guid>      — Unban (partial GUID OK)");
                Console.WriteLine("  /banlist           — List all bans");
                Console.WriteLine("  /grant <name> <modId> <true|false> — Per-mod access");
                Console.WriteLine("  /reqop             — Print reqop key");
                Console.WriteLine("  /myperm [name]     — Show permissions");
                Console.WriteLine("  /time <preset>     — dawn|noon|dusk|night");
                Console.WriteLine("  /config <modId>    — View/set [Server] config");
                Console.WriteLine("  /mod <enable|disable|list> — Toggle mods");
                Console.WriteLine("  /stop              — Shut down server");
            });

            CommandRegistry.Register("ban", "Ban player: ban <name|--guid <guid>> [reason]", args =>
            {
                if (args.Length == 0)
                {
                    Console.WriteLine("Usage: /ban <playerName> [reason]");
                    Console.WriteLine("       /ban --guid <guid> [reason]   (ban offline player by GUID)");
                    return;
                }

                string reason;
                string guid, name, ip;

                // /ban --guid <guid> [reason]
                if (args[0].Equals("--guid", StringComparison.OrdinalIgnoreCase) && args.Length > 1)
                {
                    guid   = args[1];
                    reason = args.Length > 2 ? string.Join(" ", args, 2, args.Length - 2) : "Banned by admin";
                    name   = "";
                    ip     = "";

                    // If player is currently online, kick them too
                    int onlineSlot = Permissions.PermissionService.FindSlotByGuid(guid);
                    if (onlineSlot >= 0)
                    {
                        name = Permissions.PermissionService.GetConnectedPlayers()
                            .Find(p => p.slot == onlineSlot).name ?? "";
                        ip   = Net.NetSync.GetClientAddress(onlineSlot);
                        try { DedServProxy.KickPlayer(onlineSlot); }
                        catch { }
                    }
                    Permissions.BanService.AddBan(guid, name, ip, reason);
                    Console.WriteLine($"Banned guid {guid}{(string.IsNullOrEmpty(name) ? "" : $" ({name})")}");
                    _log?.Info($"[ServerConsole] /ban --guid {guid}: {reason}");
                    return;
                }

                // /ban <name> [reason]
                name   = args[0];
                reason = args.Length > 1 ? string.Join(" ", args, 1, args.Length - 1) : "Banned by admin";

                int slot = Permissions.PermissionService.FindSlotByName(name);
                if (slot < 0) { Console.WriteLine($"Player '{name}' is not currently connected. Use /ban --guid <guid> to ban offline players."); return; }

                guid = Permissions.PermissionService.GetGuid(slot);
                ip   = Net.NetSync.GetClientAddress(slot);

                Permissions.BanService.AddBan(guid, name, ip, reason);

                try { DedServProxy.KickPlayer(slot); }
                catch { }

                Console.WriteLine($"Banned {name} (guid: {guid ?? "none"}, ip: {ip})");
                _log?.Info($"[ServerConsole] /ban {name}: {reason}");
            });

            CommandRegistry.Register("unban", "Unban by GUID (partial OK): unban <guid>", args =>
            {
                if (args.Length == 0)
                {
                    Console.WriteLine("Usage: /unban <guid>   (use /banlist to see GUIDs)");
                    return;
                }
                string partial = args[0];
                // Try exact match first, then prefix match
                var bans = Permissions.BanService.GetBans();
                var matches = new System.Collections.Generic.List<string>();
                foreach (var b in bans)
                    if (b.Guid.StartsWith(partial, StringComparison.OrdinalIgnoreCase))
                        matches.Add(b.Guid);

                if (matches.Count == 0)
                {
                    Console.WriteLine($"No ban found matching: {partial}");
                    Console.WriteLine("Use /banlist to see all bans.");
                }
                else if (matches.Count > 1)
                {
                    Console.WriteLine($"Ambiguous: {matches.Count} GUIDs match '{partial}':");
                    foreach (var m in matches) Console.WriteLine($"  {m}");
                }
                else
                {
                    if (Permissions.BanService.RemoveBan(matches[0]))
                        Console.WriteLine($"Unbanned: {matches[0]}");
                    else
                        Console.WriteLine($"No ban found for GUID: {matches[0]}");
                }
            });

            CommandRegistry.Register("banlist", "List all bans", _ =>
            {
                var bans = Permissions.BanService.GetBans();
                if (bans.Count == 0) { Console.WriteLine("[Bans] No active bans"); return; }
                Console.WriteLine($"[Bans] {bans.Count} ban(s):");
                foreach (var b in bans)
                    Console.WriteLine($"  {b.Name} | guid: {b.Guid} | ip: {b.Ip} | {b.Reason} [{b.BannedAt}]");
            });
        }

        // ---- TextReader with pre-queued wizard answers ----

        private class CommandInterceptingReader : TextReader
        {
            private readonly TextReader _inner;
            private readonly ILogger _log;
            private readonly Queue<string> _queue = new Queue<string>();

            public CommandInterceptingReader(TextReader inner, ILogger log)
            {
                _inner = inner;
                _log = log;
            }

            /// <summary>Pre-queue an answer to be returned before the real stdin is read.</summary>
            public void Enqueue(string answer) => _queue.Enqueue(answer);

            public override string ReadLine()
            {
                // Serve pre-queued answers first (wizard auto-fill)
                if (_queue.Count > 0)
                {
                    string answer = _queue.Dequeue();
                    _log?.Info($"[ServerConsole] Auto-answer: '{answer}'");
                    return answer;
                }

                string line = _inner.ReadLine();
                if (line != null && line.StartsWith("/"))
                {
                    string command = line.Substring(1).Trim();
                    try
                    {
                        if (!CommandRegistry.Execute(command))
                            Console.WriteLine($"Unknown command: /{command.Split(' ')[0]}. Type /help for available commands.");
                    }
                    catch (Exception ex)
                    {
                        _log?.Warn($"[ServerConsole] Command error: {ex.Message}");
                    }
                    // Return empty string so Terraria's input handler gets a harmless no-op
                    return "";
                }
                return line;
            }

            public override int Read() => _inner.Read();
            public override int Peek() => _inner.Peek();

            protected override void Dispose(bool disposing)
            {
                if (disposing) _inner?.Dispose();
                base.Dispose(disposing);
            }
        }
    }
}
