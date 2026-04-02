using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using Terraria;
using Terraria.IO;
using TerrariaModder.Core.Logging;

namespace DebugTools
{
    /// <summary>
    /// Programmatic menu navigation for automating the title screen → world entry flow.
    /// Uses direct state manipulation (same approach as Terraria's own QuickLoad testing class)
    /// rather than simulated mouse clicks, for maximum reliability.
    /// </summary>
    public sealed class MenuNavigator
    {
        private readonly ILogger _log;

        /// <summary>Maximum allowed timeout for any blocking operation (2 minutes).</summary>
        private const int MaxTimeoutMs = 120_000;

        /// <summary>Lock to prevent concurrent navigation operations from corrupting game state.
        /// Static so all MenuNavigator instances (console commands + HTTP API) share the same lock.</summary>
        private static readonly object _navigationLock = new object();

        public MenuNavigator(ILogger logger)
        {
            _log = logger;
        }

        /// <summary>
        /// Get current menu state including mode, available characters/worlds, and selection indices.
        /// </summary>
        public MenuState GetMenuState()
        {
            var state = new MenuState
            {
                InMenu = Main.gameMenu,
                MenuMode = Main.menuMode,
                MenuDescription = DescribeMenuMode(Main.menuMode),
                InWorld = !Main.gameMenu && Main.LocalPlayer != null
            };

            if (state.InWorld)
            {
                state.WorldName = Main.worldName ?? "";
            }

            if (state.InMenu)
            {
                try
                {
                    state.PlayerCount = Main.PlayerList?.Count ?? 0;
                    state.WorldCount = Main.WorldList?.Count ?? 0;

                    var players = new List<CharacterInfo>();
                    if (Main.PlayerList != null)
                    {
                        for (int i = 0; i < Main.PlayerList.Count; i++)
                        {
                            var pfd = Main.PlayerList[i];
                            players.Add(new CharacterInfo
                            {
                                Index = i,
                                Name = pfd.Player?.name ?? "Unknown",
                                Difficulty = pfd.Player?.difficulty ?? 0
                            });
                        }
                    }
                    state.Players = players;

                    var worlds = new List<WorldInfo>();
                    if (Main.WorldList != null)
                    {
                        for (int i = 0; i < Main.WorldList.Count; i++)
                        {
                            var wfd = Main.WorldList[i];
                            worlds.Add(new WorldInfo
                            {
                                Index = i,
                                Name = wfd.Name ?? "Unknown",
                                Seed = wfd.SeedText ?? "",
                                IsHardMode = wfd.IsHardMode,
                                GameMode = wfd.GameMode
                            });
                        }
                    }
                    state.Worlds = worlds;
                }
                catch (Exception ex)
                {
                    _log.Debug($"[MenuNavigator] Error reading player/world lists: {ex.Message}");
                }
            }

            return state;
        }

        /// <summary>
        /// Navigate to a specific menu target.
        /// </summary>
        public NavigationResult Navigate(string target)
        {
            if (!Main.gameMenu)
                return NavigationResult.Fail("Not in menu - already in game");

            // Prevent concurrent navigation operations from corrupting game state
            if (!Monitor.TryEnter(_navigationLock))
                return NavigationResult.Fail("Another navigation operation is already in progress");

            try
            {
                switch (target.ToLowerInvariant())
                {
                    case "singleplayer":
                        return NavigateToSingleplayer();
                    case "back":
                    case "title":
                        return NavigateBack();
                    default:
                        if (target.StartsWith("character_", StringComparison.OrdinalIgnoreCase))
                        {
                            if (int.TryParse(target.Substring(10), out int charIdx))
                                return SelectCharacter(charIdx);
                            return NavigationResult.Fail($"Invalid character index in: {target}");
                        }
                        if (target.StartsWith("world_", StringComparison.OrdinalIgnoreCase))
                        {
                            if (int.TryParse(target.Substring(6), out int worldIdx))
                                return SelectWorld(worldIdx);
                            return NavigationResult.Fail($"Invalid world index in: {target}");
                        }
                        if (target == "play")
                            return PlaySelectedWorld();

                        if (target == "submit")
                            return SubmitTextPrompt();

                        return NavigationResult.Fail($"Unknown navigation target: {target}. Use: singleplayer, character_N, world_N, play, back, title, submit");
                }
            }
            finally
            {
                Monitor.Exit(_navigationLock);
            }
        }

        /// <summary>
        /// Full sequence: enter a world from any state. Handles current state detection.
        /// </summary>
        public NavigationResult EnterWorld(int characterIndex = 0, int worldIndex = 0, int timeoutMs = 30000, bool multiplayer = false)
        {
            // Clamp timeout to prevent indefinite blocking
            if (timeoutMs <= 0) timeoutMs = 30000;
            if (timeoutMs > MaxTimeoutMs) timeoutMs = MaxTimeoutMs;

            // Already in world — save and quit to title, then re-enter
            if (!Main.gameMenu)
            {
                _log.Info("[MenuNavigator] EnterWorld: already in world, calling WorldGen.SaveAndQuit...");
                try
                {
                    WorldGen.SaveAndQuit();
                }
                catch (Exception ex)
                {
                    _log.Warn($"[MenuNavigator] SaveAndQuit threw: {ex.Message}");
                }

                // Wait up to 15s for gameMenu to become true (world saved + title screen shown)
                var exitSw = Stopwatch.StartNew();
                while (!Main.gameMenu && exitSw.ElapsedMilliseconds < 15000)
                    Thread.Sleep(100);

                if (!Main.gameMenu)
                    return NavigationResult.Fail("Timed out waiting for world exit after SaveAndQuit");

                // Brief pause for title screen to stabilize
                Thread.Sleep(500);
                _log.Info("[MenuNavigator] EnterWorld: world exited, proceeding to enter world");
            }

            // Wait for splash screen to complete — Initialize_AlmostEverything() is called only when
            // showSplash transitions to false. Until then, Main.player[1-254] are null and
            // playWorldCallBack will NRE on its first loop.
            if (Main.showSplash)
            {
                _log.Info("[MenuNavigator] EnterWorld: waiting for splash screen (Initialize_AlmostEverything not yet called)...");
                var splashSw = Stopwatch.StartNew();
                while (Main.showSplash && splashSw.ElapsedMilliseconds < 30000)
                    Thread.Sleep(200);
                if (Main.showSplash)
                    return NavigationResult.Fail("Timed out waiting for splash screen to complete (game not fully initialized)");
                _log.Info("[MenuNavigator] EnterWorld: splash complete, player array initialized");
            }

            // Prevent concurrent navigation operations from corrupting game state
            if (!Monitor.TryEnter(_navigationLock))
                return NavigationResult.Fail("Another navigation operation is already in progress");

            try
            {
                return EnterWorldInternal(characterIndex, worldIndex, timeoutMs, multiplayer);
            }
            finally
            {
                Monitor.Exit(_navigationLock);
            }
        }

        /// <summary>
        /// Join a running server via IP. Handles SaveAndQuit if already in a world.
        /// </summary>
        public NavigationResult JoinWorld(string ip = "127.0.0.1", int characterIndex = 0, int timeoutMs = 30000)
        {
            if (timeoutMs <= 0) timeoutMs = 30000;
            if (timeoutMs > MaxTimeoutMs) timeoutMs = MaxTimeoutMs;

            // Already in world — save and quit first
            if (!Main.gameMenu)
            {
                _log.Info("[MenuNavigator] JoinWorld: already in world, calling SaveAndQuit...");
                try { WorldGen.SaveAndQuit(); } catch (Exception ex) { _log.Warn($"[MenuNavigator] SaveAndQuit threw: {ex.Message}"); }
                var exitSw = Stopwatch.StartNew();
                while (!Main.gameMenu && exitSw.ElapsedMilliseconds < 15000) Thread.Sleep(100);
                if (!Main.gameMenu) return NavigationResult.Fail("Timed out waiting for world exit");
                Thread.Sleep(500);
            }

            if (!Monitor.TryEnter(_navigationLock))
                return NavigationResult.Fail("Another navigation operation is already in progress");

            try { return JoinWorldInternal(ip, characterIndex, timeoutMs); }
            finally { Monitor.Exit(_navigationLock); }
        }

        private NavigationResult JoinWorldInternal(string ip, int characterIndex, int timeoutMs)
        {
            _log.Info($"[MenuNavigator] JoinWorld: ip={ip}, character={characterIndex}, timeout={timeoutMs}ms");

            try { Main.LoadPlayers(); }
            catch (Exception ex) { return NavigationResult.Fail($"JoinWorld: LoadPlayers failed: {ex.Message}"); }

            if (Main.PlayerList == null || Main.PlayerList.Count == 0)
                return NavigationResult.Fail("JoinWorld: no characters available");
            if (characterIndex < 0 || characterIndex >= Main.PlayerList.Count)
                return NavigationResult.Fail($"JoinWorld: character index {characterIndex} out of range");

            var playerData = Main.PlayerList[characterIndex];
            if (playerData.Player == null || playerData.Player.loadStatus != 0)
                return NavigationResult.Fail($"JoinWorld: character at index {characterIndex} failed to load");

            _log.Info($"[MenuNavigator] JoinWorld: selecting character '{playerData.Player.name}', connecting to {ip}");

            // Bypass SelectPlayer (UI-thread-dependent) — set state directly, same approach as H&P flow.
            // This avoids fancy-UI (menuMode=888) main-loop interference when called from HTTP thread.
            Main.myPlayer = 0;
            Main.ServerSideCharacter = false;
            Main.ClearPendingPlayerSelectCallbacks();
            try { playerData.SetAsActive(); }
            catch (Exception ex) { return NavigationResult.Fail($"JoinWorld: SetAsActive failed: {ex.Message}"); }

            // Set IP and start TCP connection
            if (!Netplay.SetRemoteIP(ip))
                return NavigationResult.Fail($"JoinWorld: SetRemoteIP failed for '{ip}'");

            Main.menuMultiplayer = true;
            Main.autoJoin = false; // we're handling it manually
            Netplay.StartTcpClient();
            Main.menuMode = 10; // loading/connecting screen

            _log.Info($"[MenuNavigator] JoinWorld: TCP connect started (menuMode=10), waiting up to {timeoutMs}ms for world...");

            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                if (!Main.gameMenu && Main.LocalPlayer != null)
                {
                    _log.Info($"[MenuNavigator] JoinWorld: entered world '{Main.worldName}' in {sw.ElapsedMilliseconds}ms");
                    return NavigationResult.Ok($"Joined world: {Main.worldName}", Main.worldName);
                }
                if (Main.menuMode == 200 || Main.menuMode == 201)
                    return NavigationResult.Fail($"JoinWorld: connection failed (menuMode={Main.menuMode})");
                Thread.Sleep(100);
            }

            Main.autoJoin = false;
            return NavigationResult.Fail($"JoinWorld: timed out after {timeoutMs}ms waiting for world");
        }

        private NavigationResult EnterWorldInternal(int characterIndex, int worldIndex, int timeoutMs, bool multiplayer = false)
        {
            _log.Info($"[MenuNavigator] EnterWorld: character={characterIndex}, world={worldIndex}, timeout={timeoutMs}ms, multiplayer={multiplayer}");

            if (multiplayer)
                return EnterWorldInternalHnP(characterIndex, worldIndex, timeoutMs);

            // Step 1: Load players and select character
            try
            {
                Main.LoadPlayers();
            }
            catch (Exception ex)
            {
                return NavigationResult.Fail($"Failed to load players: {ex.Message}");
            }

            if (Main.PlayerList == null || Main.PlayerList.Count == 0)
                return NavigationResult.Fail("No characters available");

            if (characterIndex < 0 || characterIndex >= Main.PlayerList.Count)
                return NavigationResult.Fail($"Character index {characterIndex} out of range (0-{Main.PlayerList.Count - 1})");

            var playerData = Main.PlayerList[characterIndex];
            string characterName = playerData.Player?.name ?? "Unknown";

            // Validate player loaded successfully - SelectPlayer throws if loadStatus != Ok
            if (playerData.Player == null)
                return NavigationResult.Fail($"Character at index {characterIndex} has no player data");

            int loadStatus = playerData.Player.loadStatus;
            if (loadStatus != 0) // StatusID.Ok == 0
                return NavigationResult.Fail($"Character '{characterName}' failed to load (loadStatus={loadStatus}). File may be corrupt or from a newer version.");

            _log.Info($"[MenuNavigator] Selecting character: {characterName} (index {characterIndex}, multiplayer={multiplayer})");

            // Set multiplayer flag before SelectPlayer — controls whether H&P or singleplayer path is used
            Main.menuMultiplayer = multiplayer;

            try
            {
                Main.SelectPlayer(playerData);
            }
            catch (Exception ex)
            {
                return NavigationResult.Fail($"Failed to select character: {ex.Message}");
            }

            // SelectPlayer calls LoadWorlds() synchronously and sets menuMode = 6 (world select)
            // Verify we're in the expected state
            int postSelectMode = Main.menuMode;
            if (postSelectMode != 6)
            {
                _log.Warn($"[MenuNavigator] Unexpected menuMode after SelectPlayer: {postSelectMode} ({DescribeMenuMode(postSelectMode)}), expected 6 (world_select)");
                return NavigationResult.Fail($"Character selection ended in unexpected state: menuMode={postSelectMode} ({DescribeMenuMode(postSelectMode)}). Expected world_select (6).");
            }

            // Step 2: Select and enter world
            if (Main.WorldList == null || Main.WorldList.Count == 0)
                return NavigationResult.Fail("No worlds available");

            if (worldIndex < 0 || worldIndex >= Main.WorldList.Count)
                return NavigationResult.Fail($"World index {worldIndex} out of range (0-{Main.WorldList.Count - 1})");

            var worldData = Main.WorldList[worldIndex];
            string worldName = worldData.Name ?? "Unknown";
            _log.Info($"[MenuNavigator] Selecting world: {worldName} (index {worldIndex})");

            try
            {
                worldData.SetAsActive();
            }
            catch (Exception ex)
            {
                return NavigationResult.Fail($"Failed to set world active: {ex.Message}");
            }

            // Step 3: Start world loading (same as Terraria's QuickLoad)
            try
            {
                WorldGen.playWorld();
                Main.menuMode = 10; // loading screen
            }
            catch (Exception ex)
            {
                return NavigationResult.Fail($"Failed to start world loading: {ex.Message}");
            }

            _log.Info($"[MenuNavigator] World loading started, waiting up to {timeoutMs}ms...");

            // Step 4: Wait for world to load
            // playWorld() queues work on ThreadPool. On success, sets gameMenu=false.
            // On failure, sets menuMode to 200 (load failed, backup available) or 201 (load failed, no backup).
            var sw = Stopwatch.StartNew();
            int lastLoggedMode = 10;
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                if (!Main.gameMenu && Main.LocalPlayer != null)
                {
                    _log.Info($"[MenuNavigator] Successfully entered world: {worldName} ({sw.ElapsedMilliseconds}ms)");
                    return NavigationResult.Ok($"Entered world: {worldName}", worldName);
                }

                // Detect world load failure - background thread sets these on corrupt/missing world files
                int currentMode = Main.menuMode;
                if (currentMode == 200)
                {
                    _log.Error($"[MenuNavigator] World load failed for '{worldName}' - backup file available");
                    return NavigationResult.Fail($"World '{worldName}' failed to load (corrupt). A backup file exists.");
                }
                if (currentMode == 201)
                {
                    _log.Error($"[MenuNavigator] World load failed for '{worldName}' - no backup available");
                    return NavigationResult.Fail($"World '{worldName}' failed to load (corrupt). No backup file available.");
                }

                // Log unexpected menuMode transitions during loading
                if (currentMode != lastLoggedMode && currentMode != 10)
                {
                    _log.Warn($"[MenuNavigator] Unexpected menuMode during world load: {currentMode} ({DescribeMenuMode(currentMode)})");
                    lastLoggedMode = currentMode;
                }

                Thread.Sleep(250);
            }

            return NavigationResult.Fail($"Timeout waiting for world to load after {timeoutMs}ms (menuMode={Main.menuMode}, gameMenu={Main.gameMenu})");
        }

        /// <summary>
        /// Wait for a condition to become true.
        /// </summary>
        public NavigationResult WaitForState(string condition, int timeoutMs = 15000)
        {
            // Clamp timeout to prevent indefinite blocking
            if (timeoutMs <= 0) timeoutMs = 15000;
            if (timeoutMs > MaxTimeoutMs) timeoutMs = MaxTimeoutMs;

            // Validate condition before starting the wait loop
            string normalizedCondition = condition.ToLowerInvariant();
            if (normalizedCondition != "in_world" && normalizedCondition != "in_menu" &&
                normalizedCondition != "title_screen" && normalizedCondition != "loading")
            {
                return NavigationResult.Fail($"Unknown condition: {condition}. Use: in_world, in_menu, title_screen, loading");
            }

            var sw = Stopwatch.StartNew();

            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                bool met = false;
                switch (normalizedCondition)
                {
                    case "in_world":
                        met = !Main.gameMenu && Main.LocalPlayer != null;
                        break;
                    case "in_menu":
                        met = Main.gameMenu;
                        break;
                    case "title_screen":
                        met = Main.gameMenu && Main.menuMode == 0;
                        break;
                    case "loading":
                        met = Main.gameMenu && Main.menuMode == 10;
                        break;
                }

                if (met)
                    return NavigationResult.Ok($"Condition met: {condition}");

                Thread.Sleep(200);
            }

            return NavigationResult.Fail($"Timeout waiting for {condition} after {timeoutMs}ms (menuMode={Main.menuMode}, gameMenu={Main.gameMenu})");
        }

        /// <summary>
        /// H&amp;P flow via direct API calls — same approach as singleplayer (no mouse clicks).
        /// Mirrors what AutoHost() + SelectPlayer() + HostAndPlay() do internally.
        /// </summary>
        private NavigationResult EnterWorldInternalHnP(int characterIndex, int worldIndex, int timeoutMs)
        {
            _log.Info($"[MenuNavigator] H&P flow: char={characterIndex}, world={worldIndex}, timeout={timeoutMs}ms");
            var total = Stopwatch.StartNew();

            // Kill any stale TerrariaServer.exe from previous sessions — it may be holding port 7777
            try
            {
                var stale = Process.GetProcessesByName("TerrariaServer");
                foreach (var p in stale)
                {
                    _log.Info($"[MenuNavigator] H&P: killing stale TerrariaServer.exe (pid={p.Id})");
                    try { p.Kill(); p.WaitForExit(3000); } catch { }
                }
                if (stale.Length > 0) Thread.Sleep(500); // brief wait for port release
            }
            catch (Exception ex) { _log.Warn($"[MenuNavigator] H&P: error killing stale server: {ex.Message}"); }

            // Step 1: Load players
            try { Main.LoadPlayers(); }
            catch (Exception ex) { return NavigationResult.Fail($"H&P: LoadPlayers failed: {ex.Message}"); }

            if (Main.PlayerList == null || Main.PlayerList.Count == 0)
                return NavigationResult.Fail("H&P: no characters available");
            if (characterIndex < 0 || characterIndex >= Main.PlayerList.Count)
                return NavigationResult.Fail($"H&P: character index {characterIndex} out of range (0-{Main.PlayerList.Count - 1})");

            var playerData = Main.PlayerList[characterIndex];
            if (playerData.Player == null || playerData.Player.loadStatus != 0)
                return NavigationResult.Fail($"H&P: character at index {characterIndex} failed to load (status={playerData.Player?.loadStatus})");

            // Step 2: Manual equivalent of SelectPlayer for H&P (mirrors QuickLoad approach):
            // Set myPlayer=0, SetAsActive, LoadWorlds — without menuMultiplayer/menuServer side effects.
            Main.myPlayer = 0;
            Main.ServerSideCharacter = false;
            _log.Info($"[MenuNavigator] H&P: selecting character '{playerData.Player.name}' (loadStatus={playerData.Player.loadStatus})");
            try { playerData.SetAsActive(); }
            catch (Exception ex) { return NavigationResult.Fail($"H&P: SetAsActive(player) failed: {ex.Message}"); }
            _log.Info($"[MenuNavigator] H&P: player[0].name='{Main.player[0]?.name}' ActivePlayer='{Main.ActivePlayerFileData?.Player?.name}'");

            try { Main.LoadWorlds(); }
            catch (Exception ex) { return NavigationResult.Fail($"H&P: LoadWorlds failed: {ex.Message}"); }

            // Step 3: Select world
            if (Main.WorldList == null || Main.WorldList.Count == 0)
                return NavigationResult.Fail("H&P: no worlds available");
            if (worldIndex < 0 || worldIndex >= Main.WorldList.Count)
                return NavigationResult.Fail($"H&P: world index {worldIndex} out of range (0-{Main.WorldList.Count - 1})");

            var worldData = Main.WorldList[worldIndex];
            _log.Info($"[MenuNavigator] H&P: selecting world '{worldData.Name}'");
            try { worldData.SetAsActive(); }
            catch (Exception ex) { return NavigationResult.Fail($"H&P: SetAsActive(world) failed: {ex.Message}"); }

            // Step 4: Trigger HostAndPlay() via the game's own Update loop (main thread safe).
            // Mode 30 (password prompt) calls HostAndPlay() when autoPass=true.
            Netplay.ServerPassword = "";
            string worldPath = Main.ActiveWorldFileData?.Path ?? "(null)";
            bool worldFileExists = System.IO.File.Exists(worldPath);
            _log.Info($"[MenuNavigator] H&P: world path='{worldPath}' exists={worldFileExists} isCloud={Main.ActiveWorldFileData?.IsCloudSave}");
            if (!worldFileExists && Main.ActiveWorldFileData?.IsCloudSave == false)
                return NavigationResult.Fail($"H&P: world file not found: '{worldPath}'");
            // Note: libPath is no longer cleared. The HostAndPlay_Prefix patch in EventPatches
            // replaces the server spawn to use TerrariaInjector.exe directly, so -loadlib is never used.
            Main.autoPass = true;
            Main.menuMode = 30;
            _log.Info($"[MenuNavigator] H&P: set menuMode=30 + autoPass=true (player[0]='{Main.player[0]?.name}')");

            // Step 5: Wait for world to load
            int remaining = Math.Max(15000, (int)(timeoutMs - total.ElapsedMilliseconds));
            _log.Info($"[MenuNavigator] H&P: waiting for world load (up to {remaining}ms)");
            var loadSw = Stopwatch.StartNew();
            int lastMode = -1;
            string expectedPlayerName = playerData.Player.name;
            while (loadSw.ElapsedMilliseconds < remaining)
            {
                if (!Main.gameMenu && Main.LocalPlayer != null)
                {
                    _log.Info($"[MenuNavigator] H&P: entered world '{Main.worldName}' in {total.ElapsedMilliseconds}ms");
                    return NavigationResult.Ok($"Entered multiplayer world: {Main.worldName}", Main.worldName);
                }
                int m = Main.menuMode;
                if (m != lastMode)
                {
                    string pname = Main.player[Main.myPlayer]?.name ?? "(null)";
                    _log.Info($"[MenuNavigator] H&P: mode={m} myPlayer={Main.myPlayer} playerName='{pname}'");
                    // Disconnect/kick detection: went from connecting/loading back to title or menu
                    if (m == 0 && lastMode >= 10)
                        return NavigationResult.Fail($"H&P: disconnected (kicked or connection lost, was mode {lastMode})");
                    lastMode = m;
                }
                if (m == 200) return NavigationResult.Fail("H&P: world load failed (corrupt, backup available)");
                if (m == 201) return NavigationResult.Fail("H&P: world load failed (corrupt, no backup)");

                // While still in mode=30 (waiting for HostAndPlay to fire), a content reload can wipe
                // player[0].name to "". Re-apply player+world selection so HostAndPlay fires with
                // correct data even after the reload completes.
                if (m == 30)
                {
                    string currentName = Main.player[Main.myPlayer]?.name ?? "";
                    if (currentName != expectedPlayerName)
                    {
                        _log.Info($"[MenuNavigator] H&P: content reload detected (name='{currentName}'), re-applying player+world setup");
                        try { playerData.SetAsActive(); } catch { }
                        try { worldData.SetAsActive(); } catch { }
                        Netplay.ServerPassword = "";
                        Main.autoPass = true;
                    }
                }

                Thread.Sleep(250);
            }

            return NavigationResult.Fail($"H&P: timeout after {timeoutMs}ms (mode={Main.menuMode}, gameMenu={Main.gameMenu})");
        }

        private bool WaitForMode(int targetMode, int timeoutMs)
        {
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                if (Main.menuMode == targetMode) return true;
                Thread.Sleep(100);
            }
            return false;
        }

        #region Navigation Helpers

        private NavigationResult NavigateToSingleplayer()
        {
            if (Main.menuMode != 0 && Main.menuMode != 888)
                return NavigationResult.Fail($"Cannot navigate to singleplayer from menuMode {Main.menuMode} ({DescribeMenuMode(Main.menuMode)})");

            try
            {
                Main.LoadPlayers();
                Main.menuMode = 1;
                return NavigationResult.Ok("Navigated to singleplayer/character select");
            }
            catch (Exception ex)
            {
                return NavigationResult.Fail($"Failed to navigate to singleplayer: {ex.Message}");
            }
        }

        private NavigationResult NavigateBack()
        {
            Main.menuMode = 0;
            Main.menuMultiplayer = false;
            return NavigationResult.Ok("Returned to title screen");
        }

        private NavigationResult SelectCharacter(int index)
        {
            if (Main.PlayerList == null || Main.PlayerList.Count == 0)
            {
                try
                {
                    Main.LoadPlayers();
                }
                catch (Exception ex)
                {
                    return NavigationResult.Fail($"Failed to load players: {ex.Message}");
                }
                if (Main.PlayerList == null || Main.PlayerList.Count == 0)
                    return NavigationResult.Fail("No characters available");
            }

            if (index < 0 || index >= Main.PlayerList.Count)
                return NavigationResult.Fail($"Character index {index} out of range (0-{Main.PlayerList.Count - 1})");

            var playerData = Main.PlayerList[index];

            // Validate player loaded successfully - SelectPlayer throws if loadStatus != Ok
            if (playerData.Player == null)
                return NavigationResult.Fail($"Character at index {index} has no player data");

            int loadStatus = playerData.Player.loadStatus;
            if (loadStatus != 0) // StatusID.Ok == 0
                return NavigationResult.Fail($"Character '{playerData.Player.name ?? "Unknown"}' failed to load (loadStatus={loadStatus})");

            // Ensure singleplayer path
            Main.menuMultiplayer = false;

            try
            {
                Main.SelectPlayer(playerData);
                string name = playerData.Player?.name ?? "Unknown";
                return NavigationResult.Ok($"Selected character: {name} (index {index})");
            }
            catch (Exception ex)
            {
                return NavigationResult.Fail($"Failed to select character: {ex.Message}");
            }
        }

        private NavigationResult SelectWorld(int index)
        {
            if (Main.WorldList == null || Main.WorldList.Count == 0)
                return NavigationResult.Fail("No worlds available. Select a character first.");

            if (index < 0 || index >= Main.WorldList.Count)
                return NavigationResult.Fail($"World index {index} out of range (0-{Main.WorldList.Count - 1})");

            try
            {
                Main.WorldList[index].SetAsActive();
                string name = Main.WorldList[index].Name ?? "Unknown";
                return NavigationResult.Ok($"Selected world: {name} (index {index})");
            }
            catch (Exception ex)
            {
                return NavigationResult.Fail($"Failed to select world: {ex.Message}");
            }
        }

        private NavigationResult PlaySelectedWorld()
        {
            if (Main.ActiveWorldFileData == null)
                return NavigationResult.Fail("No world selected. Select a world first.");

            // Validate we're on the world select screen
            int currentMode = Main.menuMode;
            if (currentMode != 6)
            {
                _log.Warn($"[MenuNavigator] PlaySelectedWorld called from menuMode {currentMode} ({DescribeMenuMode(currentMode)}), expected 6 (world_select)");
                return NavigationResult.Fail($"Cannot play world from menuMode {currentMode} ({DescribeMenuMode(currentMode)}). Navigate to world select (menuMode 6) first.");
            }

            try
            {
                WorldGen.playWorld();
                Main.menuMode = 10;
                return NavigationResult.Ok($"Loading world: {Main.ActiveWorldFileData.Name}");
            }
            catch (Exception ex)
            {
                return NavigationResult.Fail($"Failed to play world: {ex.Message}");
            }
        }

        /// <summary>
        /// Post a raw Enter key to the game window — works in WritingText mode
        /// (e.g. server password prompt) where trigger injection does not reach.
        /// </summary>
        private NavigationResult SubmitTextPrompt()
        {
            bool ok = WindowManager.PostEnterKey();
            if (!ok)
                return NavigationResult.Fail("Could not obtain game window handle for raw key injection");
            return NavigationResult.Ok("Submitted text prompt (Enter posted to game window)");
        }

        #endregion

        private static string DescribeMenuMode(int mode)
        {
            switch (mode)
            {
                case 0: return "title_screen";
                case 1: return "character_select";
                case 2: return "new_character";
                case 3: return "character_name";
                case 5: return "character_deletion_confirm";
                case 6: return "world_select";
                case 7: return "world_name";
                case 10: return "loading";
                case 11: return "settings";
                case 12: return "multiplayer";
                case 13: return "server_ip";
                case 14: return "multiplayer_connecting";
                case 15: return "disconnected";
                case 16: return "world_size_select";
                case 200: return "world_load_failed_backup_available";
                case 201: return "world_load_failed_no_backup";
                case 30:  return "text_prompt";
                case 888: return "fancy_ui";
                case 889: return "hp_world_select";
                default: return "unknown_" + mode;
            }
        }

        #region Data Types

        public class MenuState
        {
            public bool InMenu;
            public bool InWorld;
            public int MenuMode;
            public string MenuDescription;
            public string WorldName;
            public int PlayerCount;
            public int WorldCount;
            public List<CharacterInfo> Players;
            public List<WorldInfo> Worlds;
        }

        public class CharacterInfo
        {
            public int Index;
            public string Name;
            public int Difficulty;
        }

        public class WorldInfo
        {
            public int Index;
            public string Name;
            public string Seed;
            public bool IsHardMode;
            public int GameMode;
        }

        public class NavigationResult
        {
            public bool Success;
            public string Message;
            public string WorldName;

            public static NavigationResult Ok(string message, string worldName = null)
                => new NavigationResult { Success = true, Message = message, WorldName = worldName };

            public static NavigationResult Fail(string message)
                => new NavigationResult { Success = false, Message = message };
        }

        #endregion
    }
}
