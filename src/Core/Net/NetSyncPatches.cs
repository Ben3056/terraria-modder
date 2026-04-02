using System;
using System.Reflection;
using HarmonyLib;
using TerrariaModder.Core.Logging;

namespace TerrariaModder.Core.Net
{
    /// <summary>
    /// Harmony patches for the TerrariaModder net sync system.
    ///
    /// Patch 1 (Prefix): MessageBuffer.GetData — intercepts packet ID 250.
    ///   readBuffer[start] = packet ID. Our prefix fires before Terraria's switch.
    ///   If packetId == 250, we handle and return false (skip vanilla).
    ///   Unknown IDs >= MessageID.Count (162) are silently dropped by vanilla at line 137;
    ///   our prefix fires first so 250 is intercepted before that guard.
    ///
    /// Patch 2 (Postfix): MessageBuffer.GetData — detects State=1 transitions (M3).
    ///   After vanilla processes packet 1 (version handshake, no password) or packet 38
    ///   (password response), if Clients[whoAmI].State is now 1, we send ServerConfigSync.
    ///   Postfixes run even when prefix returned false; we guard against that case.
    ///
    /// Threading: GetData is called on Terraria's main XNA thread via CheckBytes().
    /// </summary>
    public static class NetSyncPatches
    {
        private static ILogger _log;
        private static bool _applied;

        // Reflected fields cached after first postfix invocation
        private static FieldInfo _readBufferField;
        private static FieldInfo _whoAmIField;
        private static FieldInfo _netModeField;     // Main.netMode (static int)
        private static FieldInfo _clientsField;     // Netplay.Clients (static array)
        private static FieldInfo _clientStateField; // RemoteClient.State
        private static bool _postfixReflected;

        // ---- Assembly helpers ----

        /// <summary>
        /// Find the correct Terraria assembly.
        ///
        /// In dedicated server mode, AppDomain contains BOTH Terraria.exe (client build, has broken
        /// XNA cctor) and TerrariaServer.exe (server build, what actually runs).  Both have assembly
        /// name "Terraria".  We identify the server assembly by:
        ///   1. Location path ends with "TerrariaServer.exe"  ← reliable at any point in startup
        ///   2. Main.dedServ == true                          ← reliable after server init completes
        /// In client/H&amp;P mode there is only one "Terraria" assembly; the fallback is used.
        /// </summary>
        private static Assembly FindTerrariaAssembly()
        {
            // In dedicated server mode, AppDomain contains BOTH Terraria.exe (client, has XNA refs,
            // loaded from disk) and TerrariaServer.exe (headless, loaded from memory by injector).
            // Both have assembly name "Terraria". We identify the server assembly by:
            //   1. Location / ManifestModule name ends with "TerrariaServer.exe"
            //   2. Location is empty → loaded from memory (Assembly.Load(byte[])) → TerrariaServer.exe
            //   3. Assembly references don't include any XNA / FNA framework (server is headless)
            //   4. Main.dedServ == true (only available after server init completes)
            // In client mode there's only one "Terraria" assembly; fallback is used.

            Assembly fallback = null;
            Assembly noLocationMatch = null; // memory-loaded → TerrariaServer.exe
            Assembly noXnaMatch = null;
            Assembly dedServMatch = null;

            var terrariaAssemblies = new System.Collections.Generic.List<string>();

            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                var asmName = asm.GetName().Name;
                // TerrariaServer.exe has assembly name "TerrariaServer" (not "Terraria")
                if (asmName != "Terraria" && asmName != "TerrariaServer") continue;
                // If explicitly named "TerrariaServer", it IS the server assembly — return immediately
                if (asmName == "TerrariaServer") return asm;

                string loc = "";
                try { loc = asm.Location ?? ""; } catch { }
                string mod = "";
                try { mod = asm.ManifestModule?.Name ?? ""; } catch { }
                string scp = "";
                try { scp = asm.ManifestModule?.ScopeName ?? ""; } catch { }

                terrariaAssemblies.Add($"loc='{loc}' mod='{mod}' scp='{scp}'");

                // Check 1: filename / module name
                if ((!string.IsNullOrEmpty(loc) && loc.EndsWith("TerrariaServer.exe", StringComparison.OrdinalIgnoreCase)) ||
                    mod.Equals("TerrariaServer.exe", StringComparison.OrdinalIgnoreCase) ||
                    scp.Equals("TerrariaServer.exe", StringComparison.OrdinalIgnoreCase))
                    return asm;

                // Check 2: empty Location → loaded from memory → this is TerrariaServer.exe
                if (string.IsNullOrEmpty(loc) && noLocationMatch == null)
                {
                    noLocationMatch = asm;
                    continue;
                }

                // Check 3: absence of XNA/FNA references (TerrariaServer.exe is headless, no graphics)
                if (noXnaMatch == null)
                {
                    try
                    {
                        bool hasXna = false;
                        foreach (var refName in asm.GetReferencedAssemblies())
                        {
                            if (refName.Name.StartsWith("Microsoft.Xna.Framework", StringComparison.OrdinalIgnoreCase) ||
                                refName.Name.StartsWith("FNA", StringComparison.OrdinalIgnoreCase))
                            {
                                hasXna = true;
                                break;
                            }
                        }
                        if (!hasXna) { noXnaMatch = asm; continue; }
                    }
                    catch { }
                }

                // Check 4: Main.dedServ == true (reliable at runtime, not at Apply() time)
                if (dedServMatch == null)
                {
                    try
                    {
                        var mainType = asm.GetType("Terraria.Main");
                        var dedServField = mainType?.GetField("dedServ",
                            BindingFlags.Public | BindingFlags.Static);
                        if (dedServField != null && (bool)dedServField.GetValue(null))
                        {
                            dedServMatch = asm;
                            continue;
                        }
                    }
                    catch { }
                }

                fallback ??= asm;
            }

            _log?.Info($"[NetSyncPatches] FindTerrariaAssembly: found {terrariaAssemblies.Count} 'Terraria' assemblies: [{string.Join("; ", terrariaAssemblies)}]");
            _log?.Info($"[NetSyncPatches] FindTerrariaAssembly: dedServ={dedServMatch != null} noXna={noXnaMatch != null} noLoc={noLocationMatch != null} fallback={fallback != null}");

            return dedServMatch ?? noXnaMatch ?? noLocationMatch ?? fallback;
        }

        public static void Apply(Harmony harmony, ILogger log)
        {
            if (_applied) return;
            _applied = true;
            _log = log;

            try
            {
                Assembly terraria = FindTerrariaAssembly();

                if (terraria == null)
                {
                    log?.Warn("[NetSyncPatches] Terraria assembly not found — net sync disabled");
                    return;
                }

                string asmLabel = !string.IsNullOrEmpty(terraria.Location)
                    ? System.IO.Path.GetFileName(terraria.Location)
                    : (terraria.ManifestModule?.Name ?? terraria.ManifestModule?.ScopeName ?? "(memory-loaded)");
                log?.Info($"[NetSyncPatches] Using assembly: {asmLabel}");

                Type messageBufferType = terraria.GetType("Terraria.MessageBuffer");
                if (messageBufferType == null)
                {
                    log?.Warn("[NetSyncPatches] MessageBuffer type not found — net sync disabled");
                    return;
                }

                // GetData(int start, int length, ref int messageType)
                MethodInfo getDataMethod = messageBufferType.GetMethod("GetData",
                    BindingFlags.Public | BindingFlags.Instance,
                    null,
                    new[] { typeof(int), typeof(int), typeof(int).MakeByRefType() },
                    null);

                if (getDataMethod == null)
                {
                    log?.Warn("[NetSyncPatches] MessageBuffer.GetData not found — net sync disabled");
                    return;
                }

                MethodInfo prefix = typeof(NetSyncPatches).GetMethod(
                    nameof(GetData_Prefix), BindingFlags.NonPublic | BindingFlags.Static);
                MethodInfo postfix = typeof(NetSyncPatches).GetMethod(
                    nameof(GetData_Postfix), BindingFlags.NonPublic | BindingFlags.Static);

                harmony.Patch(getDataMethod,
                    prefix: new HarmonyMethod(prefix),
                    postfix: new HarmonyMethod(postfix));

                log?.Info("[NetSyncPatches] MessageBuffer.GetData patched (prefix + postfix)");

                // Patch ChatHelper.SendChatMessageFromClient to intercept /reqop and other mod chat commands
                PatchChatSend(harmony, terraria, log);
            }
            catch (Exception ex)
            {
                log?.Warn($"[NetSyncPatches] Failed to apply patch: {ex.Message}");
            }
        }

        // ---- Prefix: intercept packet ID 250 ----

        private static bool GetData_Prefix(object __instance, int start, int length, ref int messageType)
        {
            try
            {
                if (_readBufferField == null)
                {
                    _readBufferField = __instance.GetType().GetField("readBuffer",
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    _whoAmIField = __instance.GetType().GetField("whoAmI",
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                }

                byte[] readBuffer = _readBufferField?.GetValue(__instance) as byte[];
                if (readBuffer == null || start >= readBuffer.Length) return true;

                byte packetId = readBuffer[start];
                if (packetId != PacketIds.TerrariaModder) return true; // not ours

                messageType = packetId;

                int whoAmI = -1;
                if (_whoAmIField != null)
                {
                    object val = _whoAmIField.GetValue(__instance);
                    if (val is int wi) whoAmI = wi;
                }

                int payloadStart = start + 1;
                int payloadLen = length - 1;
                if (payloadLen >= 0)
                    NetSync.HandlePacket(readBuffer, payloadStart, payloadLen, whoAmI);

                return false; // skip vanilla switch
            }
            catch (Exception ex)
            {
                _log?.Warn($"[NetSyncPatches] GetData_Prefix error: {ex.Message}");
                return true;
            }
        }

        // ---- Postfix: detect State=1 transition for M3 ServerConfigSync ----

        private static void GetData_Postfix(object __instance, int start, int length, ref int messageType)
        {
            try
            {
                // Ignore our own packets (already handled in prefix)
                if (messageType == PacketIds.TerrariaModder) return;

                // Only packets 1 and 38 can transition State to 1
                // (packet 1 = version handshake no-password, packet 38 = password response)
                if (messageType != 1 && messageType != 38) return;

                // Lazy-reflect the fields we need
                if (!_postfixReflected) EnsurePostfixReflection();
                if (_clientsField == null || _clientStateField == null || _netModeField == null) return;

                // Only run on server (netMode == 2)
                int netMode = (int)_netModeField.GetValue(null);
                if (netMode != 2) return;

                // Get whoAmI for this MessageBuffer
                int whoAmI = -1;
                if (_whoAmIField != null)
                {
                    object val = _whoAmIField.GetValue(__instance);
                    if (val is int wi) whoAmI = wi;
                }

                if (whoAmI < 0 || whoAmI >= 255) return;

                // Check if State just became 1
                var clients = (Array)_clientsField.GetValue(null);
                if (whoAmI >= clients.Length) return;
                object remoteClient = clients.GetValue(whoAmI);
                int state = (int)_clientStateField.GetValue(remoteClient);

                if (state == 1)
                {
                    _log?.Debug($"[NetSyncPatches] Client {whoAmI} reached State=1, sending ServerConfigSync + ModListExchange");
                    NetSync.SendServerConfigSync(whoAmI);
                    NetSync.SendModListExchange(whoAmI);
                }
            }
            catch (Exception ex)
            {
                _log?.Warn($"[NetSyncPatches] GetData_Postfix error: {ex.Message}");
            }
        }

        // ---- Chat command intercept ----

        private static void PatchChatSend(Harmony harmony, Assembly terraria, ILogger log)
        {
            try
            {
                Type chatHelperType = terraria.GetType("Terraria.Chat.ChatHelper");
                if (chatHelperType == null) { log?.Warn("[NetSyncPatches] ChatHelper not found"); return; }

                MethodInfo sendMethod = chatHelperType.GetMethod("SendChatMessageFromClient",
                    BindingFlags.Public | BindingFlags.Static);
                if (sendMethod == null) { log?.Warn("[NetSyncPatches] SendChatMessageFromClient not found"); return; }

                MethodInfo chatPrefix = typeof(NetSyncPatches).GetMethod(
                    nameof(SendChatMessage_Prefix), BindingFlags.NonPublic | BindingFlags.Static);

                harmony.Patch(sendMethod, prefix: new HarmonyMethod(chatPrefix));
                log?.Info("[NetSyncPatches] ChatHelper.SendChatMessageFromClient patched");
            }
            catch (Exception ex)
            {
                log?.Warn($"[NetSyncPatches] PatchChatSend failed: {ex.Message}");
            }
        }

        private static bool SendChatMessage_Prefix(object message)
        {
            try
            {
                if (message == null) return true;

                // Get text via property (ChatMessage.Text)
                string text = message.GetType().GetProperty("Text")?.GetValue(message) as string;
                if (string.IsNullOrEmpty(text)) return true;

                // Handle /reqop <key>
                if (text.StartsWith("/reqop ", StringComparison.OrdinalIgnoreCase))
                {
                    string key = text.Substring(7).Trim();
                    _log?.Debug($"[NetSyncPatches] /reqop intercepted, sending to server");
                    NetSync.SendServerCommandRequest("reqop", key);
                    return false; // suppress vanilla send
                }
            }
            catch (Exception ex)
            {
                _log?.Warn($"[NetSyncPatches] SendChatMessage_Prefix error: {ex.Message}");
            }
            return true;
        }

        private static void EnsurePostfixReflection()
        {
            _postfixReflected = true;
            try
            {
                Assembly terraria = FindTerrariaAssembly();
                if (terraria == null) return;

                Type mainType = terraria.GetType("Terraria.Main");
                Type netplayType = terraria.GetType("Terraria.Netplay");
                Type remoteClientType = terraria.GetType("Terraria.RemoteClient");

                _netModeField = mainType?.GetField("netMode", BindingFlags.Public | BindingFlags.Static);
                _clientsField = netplayType?.GetField("Clients", BindingFlags.Public | BindingFlags.Static);
                _clientStateField = remoteClientType?.GetField("State", BindingFlags.Public | BindingFlags.Instance);

                _log?.Debug($"[NetSyncPatches] Postfix reflection: netMode={_netModeField != null}, clients={_clientsField != null}, state={_clientStateField != null}");
            }
            catch (Exception ex)
            {
                _log?.Warn($"[NetSyncPatches] Postfix reflection failed: {ex.Message}");
            }
        }
    }
}
