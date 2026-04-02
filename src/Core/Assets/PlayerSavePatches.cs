using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Terraria;
using Terraria.IO;
using TerrariaModder.Core.Logging;

namespace TerrariaModder.Core.Assets
{
    /// <summary>
    /// Save interception for player files.
    ///
    /// Prefix on Player.SavePlayer:
    ///   1. Scan all ~352 player item slots for custom items (type >= VanillaItemCount)
    ///   2. Extract custom items to moddata, replace with air
    ///   3. Let vanilla save proceed (writes clean .plr)
    ///   H4: When netMode==1 (dedicated server client), send CustomItemSave packet instead of
    ///       writing to disk — server is authoritative for player item data.
    ///
    /// Postfix on Player.SavePlayer:
    ///   4. Restore custom items to memory for continued play
    ///
    /// Postfix on Player.LoadPlayer:
    ///   5. Migrate legacy sidecar if needed (one-time, H3)
    ///   6. Read moddata, resolve string IDs → runtime types, inject items
    ///   H4: When netMode==1, skip injection — wait for CustomItemSync from server.
    ///
    /// Preservation (H2): Items from mods that are NOT currently loaded are stored in
    /// _preservedItems and written back to the moddata file on save, preventing item
    /// loss when mods are temporarily uninstalled.
    /// </summary>
    internal static class PlayerSavePatches
    {
        private static Harmony _harmony;
        private static ILogger _log;
        private static bool _applied;

        // Temporary storage for extracted items during save (slotKey → Item)
        private static readonly Dictionary<string, Item> _extractedItems = new Dictionary<string, Item>();

        // Items from unloaded mods — preserved across save/load cycles (H2)
        private static List<ModdataFile.ItemEntry> _preservedItems = new List<ModdataFile.ItemEntry>();

        public static void Initialize(ILogger logger)
        {
            _log = logger;
            _harmony = new Harmony("com.terrariamodder.assets.v3.playersave");
        }

        public static void ApplyPatches()
        {
            if (_applied) return;

            try
            {
                PatchSavePlayer();
                _log?.Info("[PlayerSavePatches] SavePlayer patched");
                PatchLoadPlayer();
                _log?.Info("[PlayerSavePatches] LoadPlayer patched");
                _applied = true;
                _log?.Info("[PlayerSavePatches] Applied successfully");
            }
            catch (Exception ex)
            {
                _log?.Error($"[PlayerSavePatches] Failed: {ex.Message}\n{ex.StackTrace}");
            }
        }

        private static void PatchSavePlayer()
        {
            var saveMethod = typeof(Player).GetMethod("SavePlayer",
                BindingFlags.Public | BindingFlags.Static, null,
                new[] { typeof(PlayerFileData), typeof(bool) }, null);

            if (saveMethod == null)
            {
                _log?.Warn("[PlayerSavePatches] Player.SavePlayer not found");
                return;
            }

            _harmony.Patch(saveMethod,
                prefix: new HarmonyMethod(typeof(PlayerSavePatches), nameof(SavePlayer_Prefix)),
                postfix: new HarmonyMethod(typeof(PlayerSavePatches), nameof(SavePlayer_Postfix)));
        }

        private static void PatchLoadPlayer()
        {
            var loadMethod = typeof(Player).GetMethod("LoadPlayer",
                BindingFlags.Public | BindingFlags.Static, null,
                new[] { typeof(string), typeof(bool) }, null);

            if (loadMethod == null)
            {
                _log?.Warn("[PlayerSavePatches] Player.LoadPlayer not found");
                return;
            }

            _harmony.Patch(loadMethod,
                postfix: new HarmonyMethod(typeof(PlayerSavePatches), nameof(LoadPlayer_Postfix)));
        }

        // ── Save prefix: extract custom items, write moddata, replace with air ──

        private static void SavePlayer_Prefix(PlayerFileData playerFile)
        {
            if (playerFile?.Player == null) return;
            _extractedItems.Clear();

            try
            {
                var player = playerFile.Player;
                var customItems = new List<ModdataFile.ItemEntry>();

                // Scan all player storage locations
                ScanArray(player.inventory, "inventory", customItems, _extractedItems);
                ScanArray(player.armor, "armor", customItems, _extractedItems);
                ScanArray(player.dye, "dye", customItems, _extractedItems);
                ScanArray(player.miscEquips, "misc_equips", customItems, _extractedItems);
                ScanArray(player.miscDyes, "misc_dyes", customItems, _extractedItems);

                if (player.bank?.item != null)
                    ScanArray(player.bank.item, "bank", customItems, _extractedItems);
                if (player.bank2?.item != null)
                    ScanArray(player.bank2.item, "bank2", customItems, _extractedItems);
                if (player.bank3?.item != null)
                    ScanArray(player.bank3.item, "bank3", customItems, _extractedItems);
                if (player.bank4?.item != null)
                    ScanArray(player.bank4.item, "bank4", customItems, _extractedItems);

                // Loadouts
                if (player.Loadouts != null)
                {
                    for (int l = 0; l < player.Loadouts.Length && l < 3; l++)
                    {
                        if (player.Loadouts[l]?.Armor != null)
                            ScanArray(player.Loadouts[l].Armor, $"loadout_{l}_armor", customItems, _extractedItems);
                        if (player.Loadouts[l]?.Dye != null)
                            ScanArray(player.Loadouts[l].Dye, $"loadout_{l}_dye", customItems, _extractedItems);
                    }
                }

                // Special slots
                ScanSingleSlot(ref Main.mouseItem, "mouse", 0, customItems, _extractedItems);
                ScanSingleSlot(ref Main.guideItem, "guide", 0, customItems, _extractedItems);
                ScanSingleSlot(ref Main.reforgeItem, "reforge", 0, customItems, _extractedItems);

                // Trash slot
                if (player.trashItem != null && !player.trashItem.IsAir && player.trashItem.type >= ItemRegistry.VanillaItemCount)
                {
                    string trashFullId = ItemRegistry.GetFullId(player.trashItem.type);
                    if (trashFullId != null)
                    {
                        customItems.Add(new ModdataFile.ItemEntry
                        {
                            Location = "trash",
                            Slot = 0,
                            ItemId = trashFullId,
                            Stack = player.trashItem.stack,
                            Prefix = player.trashItem.prefix,
                            Favorited = player.trashItem.favorited
                        });
                        _extractedItems["trash:0"] = player.trashItem;
                    }
                }

                // Include pending items so they persist across save/quit
                customItems.AddRange(PendingItemStore.GetPlayerModdataEntries());

                // H4: When connected to a dedicated server (netMode==1), the server is
                // authoritative for player data — send items via packet instead of local write.
                if (Main.netMode == 1)
                {
                    // Send CustomItemSave to server; server will validate and persist
                    Net.NetSync.SendCustomItemSave(customItems);
                    _log?.Debug($"[PlayerSavePatches] netMode==1: sent {customItems.Count} items to server (CustomItemSave)");

                    // Still replace with air so vanilla saves a clean .plr (no custom items)
                    foreach (var kvp in _extractedItems)
                    {
                        var parts = kvp.Key.Split(':');
                        SetAir(player, parts[0], int.Parse(parts[1]));
                    }
                    return;
                }

                // Determine moddata path
                string moddataPath = ModdataFile.GetPlayerModdataPath(playerFile.Path);
                if (moddataPath == null)
                {
                    _log?.Warn("[PlayerSavePatches] Could not determine moddata path");
                    RestoreAll(player);
                    return;
                }

                if (customItems.Count == 0 && _preservedItems.Count == 0)
                {
                    // Delete stale moddata so deleted pending items don't reappear on next load
                    ModdataFile.Delete(moddataPath);
                    _log?.Debug("[PlayerSavePatches] No custom items, cleaned up moddata");
                    return;
                }

                // Write moddata FIRST (crash safety — data persisted before mutation)
                if (!ModdataFile.Write(moddataPath, customItems, _preservedItems))
                {
                    _log?.Error("[PlayerSavePatches] Failed to write moddata, aborting extraction");
                    RestoreAll(player);
                    return;
                }

                // Now replace custom items with air in memory
                foreach (var kvp in _extractedItems)
                {
                    var parts = kvp.Key.Split(':');
                    SetAir(player, parts[0], int.Parse(parts[1]));
                }

                _log?.Info($"[PlayerSavePatches] Extracted {customItems.Count} custom items before save" +
                    (_preservedItems.Count > 0 ? $" ({_preservedItems.Count} preserved from unloaded mods)" : ""));
            }
            catch (Exception ex)
            {
                _log?.Error($"[PlayerSavePatches] Prefix error: {ex.Message}");
                RestoreAll(playerFile.Player);
            }
        }

        // ── Save postfix: restore extracted items to memory ──

        private static void SavePlayer_Postfix(PlayerFileData playerFile)
        {
            if (playerFile?.Player == null || _extractedItems.Count == 0) return;

            try
            {
                RestoreAll(playerFile.Player);
                _log?.Debug($"[PlayerSavePatches] Restored {_extractedItems.Count} items after save");
            }
            catch (Exception ex)
            {
                _log?.Error($"[PlayerSavePatches] Postfix error: {ex.Message}");
            }
            finally
            {
                _extractedItems.Clear();
            }
        }

        // ── Load postfix: read moddata, inject custom items ──

        private static void LoadPlayer_Postfix(string playerPath, PlayerFileData __result)
        {
            if (__result?.Player == null) return;

            try
            {
                // H4: When connected to a REMOTE dedicated server, skip loading local moddata.
                // CustomItemSync from the server will inject the authoritative items.
                // Exception: H&P host (IsHostAndPlay) uses local moddata since the server
                // was just started and doesn't have the player's sidecar data yet.
                if (Main.netMode == 1 && !Terraria.Netplay.IsHostAndPlay)
                {
                    _log?.Debug("[PlayerSavePatches] netMode==1 (remote server): skipping local moddata load (awaiting CustomItemSync)");
                    return;
                }

                // H3: One-time migration from legacy sidecar format
                string v2Path = ModdataFile.GetPlayerModdataPath(playerPath);
                string v1Path = ModdataFile.GetLegacyPlayerModdataPath(playerPath);
                if (v2Path != null && v1Path != null)
                    ModdataFile.MigrateIfNeeded(v2Path, v1Path);

                if (v2Path == null)
                {
                    _log?.Warn("[PlayerSavePatches] Could not determine moddata path");
                    return;
                }

                // Get loaded mod IDs (mods that have registered custom items)
                var loadedModIds = new HashSet<string>(
                    ItemRegistry.AllIds.Select(id =>
                    {
                        int c = id.IndexOf(':');
                        return c > 0 ? id.Substring(0, c) : null;
                    }).Where(m => m != null),
                    StringComparer.OrdinalIgnoreCase);

                // Read moddata: active items (loaded mods) + preserved items (unloaded mods)
                var items = ModdataFile.Read(v2Path, loadedModIds, out var preserved);
                _preservedItems = preserved ?? new List<ModdataFile.ItemEntry>();

                if (_preservedItems.Count > 0)
                    _log?.Info($"[PlayerSavePatches] Preserving {_preservedItems.Count} item(s) from unloaded mod(s)");

                if (items.Count == 0)
                {
                    _log?.Debug("[PlayerSavePatches] No active moddata items to inject");
                    return;
                }

                var player = __result.Player;
                int injected = 0, skipped = 0;

                // Clear previous pending items for this player
                PendingItemStore.ClearPlayer();

                foreach (var entry in items)
                {
                    try
                    {
                        // Resolve string ID to runtime type (checks canonical + alias map)
                        int runtimeType = ItemRegistry.GetRuntimeType(entry.ItemId);
                        if (runtimeType < 0)
                        {
                            _log?.Debug($"[PlayerSavePatches] Unresolvable item: {entry.ItemId} (mod not loaded?)");
                            skipped++;
                            continue;
                        }

                        // Log alias resolution for transparency
                        string resolvedId = ItemRegistry.GetFullId(runtimeType);
                        if (resolvedId != null && !string.Equals(resolvedId, entry.ItemId, StringComparison.OrdinalIgnoreCase))
                            _log?.Info($"[Moddata] Resolved alias \"{entry.ItemId}\" → \"{resolvedId}\"");

                        // Pending items from previous session — re-add to store
                        if (entry.Location == "pending")
                        {
                            PendingItemStore.AddPlayerItem(new PendingItemStore.PendingItem
                            {
                                ItemId = resolvedId ?? entry.ItemId,
                                RuntimeType = runtimeType,
                                Stack = entry.Stack,
                                Prefix = entry.Prefix,
                                Favorited = entry.Favorited
                            });
                            skipped++;
                            continue;
                        }

                        // Create item via SetDefaults (our prefix handles custom types)
                        var item = new Item();
                        item.SetDefaults(runtimeType);
                        item.stack = entry.Stack;
                        item.prefix = (byte)entry.Prefix;
                        item.favorited = entry.Favorited;
                        if (entry.Prefix > 0) item.Prefix(entry.Prefix);

                        // Try to inject at saved slot
                        if (IsSlotEmpty(player, entry.Location, entry.Slot))
                        {
                            SetItem(player, entry.Location, entry.Slot, item);
                            injected++;
                        }
                        else
                        {
                            // Saved slot occupied — find alternative
                            int alt = FindEmptySlot(player, entry.Location);
                            if (alt >= 0)
                            {
                                SetItem(player, entry.Location, alt, item);
                                injected++;
                            }
                            else
                            {
                                // Overflow: try inventory, then banks
                                bool placed = false;
                                foreach (var overflow in new[] { "inventory", "bank", "bank2", "bank3", "bank4" })
                                {
                                    alt = FindEmptySlot(player, overflow);
                                    if (alt >= 0)
                                    {
                                        SetItem(player, overflow, alt, item);
                                        injected++;
                                        placed = true;
                                        break;
                                    }
                                }
                                if (!placed)
                                {
                                    _log?.Info($"[PlayerSavePatches] No slot for {entry.ItemId} — added to pending items");
                                    PendingItemStore.AddPlayerItem(new PendingItemStore.PendingItem
                                    {
                                        ItemId = resolvedId ?? entry.ItemId,
                                        RuntimeType = runtimeType,
                                        Stack = entry.Stack,
                                        Prefix = entry.Prefix,
                                        Favorited = entry.Favorited
                                    });
                                    skipped++;
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _log?.Error($"[PlayerSavePatches] Failed to inject {entry.ItemId}: {ex.Message}");
                        skipped++;
                    }
                }

                int pending = PendingItemStore.PlayerItems.Count;
                _log?.Info($"[PlayerSavePatches] Injected {injected} items, skipped {skipped}" +
                    (pending > 0 ? $", {pending} pending (overflow)" : ""));
            }
            catch (Exception ex)
            {
                _log?.Error($"[PlayerSavePatches] Load postfix error: {ex.Message}");
            }
        }

        // ── Called by NetSync when CustomItemSync arrives from server (H4) ──

        /// <summary>
        /// Inject custom items received from server (H4 server-authoritative flow).
        /// Clears any locally-loaded custom items first, then injects server items.
        /// </summary>
        internal static void InjectFromServer(Player player, List<ModdataFile.ItemEntry> serverItems)
        {
            if (player == null || serverItems == null) return;

            try
            {
                // H&P host: local moddata was already loaded during LoadPlayer. Don't overwrite
                // with server data (server may not have the sidecar files yet).
                if (Terraria.Netplay.IsHostAndPlay)
                {
                    _log?.Info("[PlayerSavePatches] H&P host: keeping locally-loaded custom items (skipping server sync)");
                    return;
                }

                // Clear any custom items already in inventory (from local moddata load)
                ClearCustomItems(player);
                PendingItemStore.ClearPlayer();

                int injected = 0;
                foreach (var entry in serverItems)
                {
                    int runtimeType = ItemRegistry.GetRuntimeType(entry.ItemId);
                    if (runtimeType < 0)
                        runtimeType = ItemRegistry.GetKnownUnknownType(entry.ItemId);
                    if (runtimeType < 0)
                    {
                        _log?.Debug($"[PlayerSavePatches] Server sent unknown item: {entry.ItemId}");
                        continue;
                    }

                    var item = new Item();
                    item.SetDefaults(runtimeType);
                    item.stack = entry.Stack;
                    item.prefix = (byte)entry.Prefix;
                    item.favorited = entry.Favorited;
                    if (entry.Prefix > 0) item.Prefix(entry.Prefix);

                    if (IsSlotEmpty(player, entry.Location, entry.Slot))
                    {
                        SetItem(player, entry.Location, entry.Slot, item);
                        injected++;
                    }
                    else
                    {
                        int alt = FindEmptySlot(player, entry.Location);
                        if (alt >= 0) { SetItem(player, entry.Location, alt, item); injected++; }
                        else
                        {
                            // Last resort: inventory or pending
                            bool placed = false;
                            foreach (var overflow in new[] { "inventory", "bank", "bank2", "bank3", "bank4" })
                            {
                                alt = FindEmptySlot(player, overflow);
                                if (alt >= 0) { SetItem(player, overflow, alt, item); injected++; placed = true; break; }
                            }
                            if (!placed)
                            {
                                PendingItemStore.AddPlayerItem(new PendingItemStore.PendingItem
                                {
                                    ItemId = entry.ItemId,
                                    RuntimeType = runtimeType,
                                    Stack = entry.Stack,
                                    Prefix = entry.Prefix,
                                    Favorited = entry.Favorited
                                });
                            }
                        }
                    }
                }

                _log?.Info($"[PlayerSavePatches] Injected {injected} server-authoritative items (CustomItemSync)");
            }
            catch (Exception ex)
            {
                _log?.Error($"[PlayerSavePatches] InjectFromServer error: {ex.Message}");
            }
        }

        private static void ClearCustomItems(Player player)
        {
            void ClearArray(Item[] arr) {
                if (arr == null) return;
                for (int i = 0; i < arr.Length; i++)
                    if (arr[i] != null && !arr[i].IsAir && arr[i].type >= ItemRegistry.VanillaItemCount)
                        arr[i] = new Item();
            }

            ClearArray(player.inventory);
            ClearArray(player.armor);
            ClearArray(player.dye);
            ClearArray(player.miscEquips);
            ClearArray(player.miscDyes);
            ClearArray(player.bank?.item);
            ClearArray(player.bank2?.item);
            ClearArray(player.bank3?.item);
            ClearArray(player.bank4?.item);
            if (player.Loadouts != null)
                foreach (var lo in player.Loadouts) { ClearArray(lo?.Armor); ClearArray(lo?.Dye); }
            if (player.trashItem != null && !player.trashItem.IsAir && player.trashItem.type >= ItemRegistry.VanillaItemCount)
                player.trashItem = new Item();
        }

        // ── Scanning helpers ──

        private static void ScanArray(Item[] items, string location,
            List<ModdataFile.ItemEntry> moddataList, Dictionary<string, Item> extractMap)
        {
            if (items == null) return;
            for (int i = 0; i < items.Length; i++)
            {
                var item = items[i];
                if (item == null || item.IsAir || item.type < ItemRegistry.VanillaItemCount) continue;

                string fullId = ItemRegistry.GetFullId(item.type);
                if (fullId == null)
                {
                    // Check KnownUnknowns — placeholder for Optional mod item from server
                    if (ItemRegistry.IsKnownUnknown(item.type, out string kuFullId))
                        fullId = kuFullId;
                    else
                    {
                        _log?.Warn($"[Save] Custom item type {item.type} in {location}[{i}] has no registered ID - item will be lost on save");
                        continue;
                    }
                }

                moddataList.Add(new ModdataFile.ItemEntry
                {
                    Location = location,
                    Slot = i,
                    ItemId = fullId,
                    Stack = item.stack,
                    Prefix = item.prefix,
                    Favorited = item.favorited
                });

                extractMap[$"{location}:{i}"] = item;
            }
        }

        private static void ScanSingleSlot(ref Item item, string location, int slot,
            List<ModdataFile.ItemEntry> moddataList, Dictionary<string, Item> extractMap)
        {
            if (item == null || item.IsAir || item.type < ItemRegistry.VanillaItemCount) return;

            string fullId = ItemRegistry.GetFullId(item.type);
            if (fullId == null)
            {
                if (ItemRegistry.IsKnownUnknown(item.type, out string kuFullId))
                    fullId = kuFullId;
                else
                    return;
            }

            moddataList.Add(new ModdataFile.ItemEntry
            {
                Location = location,
                Slot = slot,
                ItemId = fullId,
                Stack = item.stack,
                Prefix = item.prefix,
                Favorited = item.favorited
            });

            extractMap[$"{location}:{slot}"] = item;
        }

        // ── Slot access helpers ──

        private static Item GetItem(Player player, string location, int slot)
        {
            try
            {
                switch (location)
                {
                    case "inventory": return slot < player.inventory.Length ? player.inventory[slot] : null;
                    case "armor": return slot < player.armor.Length ? player.armor[slot] : null;
                    case "dye": return slot < player.dye.Length ? player.dye[slot] : null;
                    case "misc_equips": return slot < player.miscEquips.Length ? player.miscEquips[slot] : null;
                    case "misc_dyes": return slot < player.miscDyes.Length ? player.miscDyes[slot] : null;
                    case "bank": return player.bank?.item != null && slot < player.bank.item.Length ? player.bank.item[slot] : null;
                    case "bank2": return player.bank2?.item != null && slot < player.bank2.item.Length ? player.bank2.item[slot] : null;
                    case "bank3": return player.bank3?.item != null && slot < player.bank3.item.Length ? player.bank3.item[slot] : null;
                    case "bank4": return player.bank4?.item != null && slot < player.bank4.item.Length ? player.bank4.item[slot] : null;
                    case "mouse": return Main.mouseItem;
                    case "guide": return Main.guideItem;
                    case "reforge": return Main.reforgeItem;
                    case "trash": return player.trashItem;
                    default:
                        if (location.StartsWith("loadout_"))
                        {
                            var parts = location.Split('_');
                            if (parts.Length == 3 && int.TryParse(parts[1], out int l) && l < player.Loadouts?.Length)
                            {
                                if (parts[2] == "armor") return slot < player.Loadouts[l].Armor.Length ? player.Loadouts[l].Armor[slot] : null;
                                if (parts[2] == "dye") return slot < player.Loadouts[l].Dye.Length ? player.Loadouts[l].Dye[slot] : null;
                            }
                        }
                        return null;
                }
            }
            catch { return null; }
        }

        private static void SetItem(Player player, string location, int slot, Item item)
        {
            switch (location)
            {
                case "inventory": if (slot < player.inventory.Length) player.inventory[slot] = item; break;
                case "armor": if (slot < player.armor.Length) player.armor[slot] = item; break;
                case "dye": if (slot < player.dye.Length) player.dye[slot] = item; break;
                case "misc_equips": if (slot < player.miscEquips.Length) player.miscEquips[slot] = item; break;
                case "misc_dyes": if (slot < player.miscDyes.Length) player.miscDyes[slot] = item; break;
                case "bank": if (player.bank?.item != null && slot < player.bank.item.Length) player.bank.item[slot] = item; break;
                case "bank2": if (player.bank2?.item != null && slot < player.bank2.item.Length) player.bank2.item[slot] = item; break;
                case "bank3": if (player.bank3?.item != null && slot < player.bank3.item.Length) player.bank3.item[slot] = item; break;
                case "bank4": if (player.bank4?.item != null && slot < player.bank4.item.Length) player.bank4.item[slot] = item; break;
                case "mouse": Main.mouseItem = item; break;
                case "guide": Main.guideItem = item; break;
                case "reforge": Main.reforgeItem = item; break;
                case "trash": player.trashItem = item; break;
                default:
                    if (location.StartsWith("loadout_"))
                    {
                        var parts = location.Split('_');
                        if (parts.Length == 3 && int.TryParse(parts[1], out int l) && l < player.Loadouts?.Length)
                        {
                            if (parts[2] == "armor" && slot < player.Loadouts[l].Armor.Length) player.Loadouts[l].Armor[slot] = item;
                            if (parts[2] == "dye" && slot < player.Loadouts[l].Dye.Length) player.Loadouts[l].Dye[slot] = item;
                        }
                    }
                    break;
            }
        }

        private static void SetAir(Player player, string location, int slot)
        {
            SetItem(player, location, slot, new Item());
        }

        private static bool IsSlotEmpty(Player player, string location, int slot)
        {
            var item = GetItem(player, location, slot);
            return item == null || item.type == 0 || item.IsAir;
        }

        private static int FindEmptySlot(Player player, string location)
        {
            int max = GetSlotCount(player, location);
            for (int i = 0; i < max; i++)
            {
                if (IsSlotEmpty(player, location, i)) return i;
            }
            return -1;
        }

        private static int GetSlotCount(Player player, string location)
        {
            switch (location)
            {
                case "inventory": return Math.Min(player.inventory.Length, 50); // main inventory only
                case "armor": return player.armor.Length;
                case "dye": return player.dye.Length;
                case "bank": return player.bank?.item?.Length ?? 0;
                case "bank2": return player.bank2?.item?.Length ?? 0;
                case "bank3": return player.bank3?.item?.Length ?? 0;
                case "bank4": return player.bank4?.item?.Length ?? 0;
                default: return 0;
            }
        }

        private static void RestoreAll(Player player)
        {
            foreach (var kvp in _extractedItems)
            {
                var parts = kvp.Key.Split(':');
                if (parts.Length == 2 && int.TryParse(parts[1], out int slot))
                    SetItem(player, parts[0], slot, kvp.Value);
            }
        }

    }
}
