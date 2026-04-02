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
    /// Save interception for world files.
    ///
    /// Scans all world chests (Main.chest[0-7999]) for custom items.
    /// Same pattern as player: extract → write moddata → air → vanilla save → restore.
    ///
    /// H1/H2: Uses new mod-keyed format at Main.SavePath/TerrariaModder/worlds/{Name}.json
    /// H3: One-time migration from legacy .wld.moddata sidecar on first load.
    /// H2: Preserves items from unloaded mods across saves.
    /// </summary>
    internal static class WorldSavePatches
    {
        private static Harmony _harmony;
        private static ILogger _log;
        private static bool _applied;
        private static bool _worldItemsInjected;

        private static readonly Dictionary<string, Item> _extractedItems = new Dictionary<string, Item>();

        // Items from unloaded mods — preserved across save/load cycles (H2)
        private static List<ModdataFile.ItemEntry> _preservedItems = new List<ModdataFile.ItemEntry>();

        public static void Initialize(ILogger logger)
        {
            _log = logger;
            _harmony = new Harmony("com.terrariamodder.assets.v3.worldsave");
        }

        public static void ApplyPatches()
        {
            if (_applied) return;

            try
            {
                PatchSaveWorld();
                PatchLoadWorld();
                _applied = true;
                _log?.Info("[WorldSavePatches] Applied successfully");
            }
            catch (Exception ex)
            {
                _log?.Error($"[WorldSavePatches] Failed: {ex.Message}");
            }
        }

        private static void PatchSaveWorld()
        {
            var worldFileType = typeof(Terraria.IO.WorldFile);
            // Try all known SaveWorld signatures (varies by Terraria version)
            var saveMethod = worldFileType.GetMethod("SaveWorld",
                BindingFlags.Public | BindingFlags.Static, null,
                new[] { typeof(bool), typeof(bool), typeof(bool) }, null);

            if (saveMethod == null)
                saveMethod = worldFileType.GetMethod("SaveWorld",
                    BindingFlags.Public | BindingFlags.Static, null,
                    new[] { typeof(bool), typeof(bool) }, null);

            if (saveMethod == null)
                saveMethod = worldFileType.GetMethod("SaveWorld",
                    BindingFlags.Public | BindingFlags.Static, null,
                    Type.EmptyTypes, null);

            if (saveMethod == null)
            {
                _log?.Warn("[WorldSavePatches] WorldFile.SaveWorld not found");
                return;
            }

            _harmony.Patch(saveMethod,
                prefix: new HarmonyMethod(typeof(WorldSavePatches), nameof(SaveWorld_Prefix)),
                postfix: new HarmonyMethod(typeof(WorldSavePatches), nameof(SaveWorld_Postfix)));
        }

        private static void PatchLoadWorld()
        {
            var worldFileType = typeof(Terraria.IO.WorldFile);
            var loadMethod = worldFileType.GetMethod("LoadWorld",
                BindingFlags.Public | BindingFlags.Static, null,
                Type.EmptyTypes, null);

            if (loadMethod == null)
            {
                _log?.Warn("[WorldSavePatches] WorldFile.LoadWorld() not found");
                return;
            }

            _harmony.Patch(loadMethod,
                postfix: new HarmonyMethod(typeof(WorldSavePatches), nameof(LoadWorld_Postfix)));
        }

        // ── Save prefix: extract custom items from chests ──

        private static void SaveWorld_Prefix()
        {
            _extractedItems.Clear();

            try
            {
                var customItems = new List<ModdataFile.ItemEntry>();

                // Scan all world chests
                for (int c = 0; c < Main.maxChests; c++)
                {
                    var chest = Main.chest[c];
                    if (chest?.item == null) continue;

                    for (int s = 0; s < chest.item.Length; s++)
                    {
                        var item = chest.item[s];
                        if (item == null || item.IsAir) continue;

                        // Custom items (any slot) — extract to prevent vanilla save corruption
                        if (item.type >= ItemRegistry.VanillaItemCount)
                        {
                            string fullId = ItemRegistry.GetFullId(item.type);
                            if (fullId == null)
                            {
                                _log?.Warn($"[Save] Custom item type {item.type} in chest_{c}[{s}] has no registered ID - item will be lost on save");
                                continue;
                            }

                            string key = $"chest_{c}:{s}";
                            customItems.Add(new ModdataFile.ItemEntry
                            {
                                Location = $"chest_{c}",
                                Slot = s,
                                ItemId = fullId,
                                Stack = item.stack,
                                Prefix = item.prefix,
                                Favorited = false
                            });
                            _extractedItems[key] = item;
                        }
                    }
                }

                // Include pending world items in moddata so they persist
                customItems.AddRange(PendingItemStore.GetWorldModdataEntries());

                // Determine world path early (needed for both write and cleanup)
                string worldPath = GetCurrentWorldPath();
                if (worldPath == null)
                {
                    _log?.Warn("[WorldSavePatches] Could not determine world path");
                    RestoreAll();
                    return;
                }

                string moddataPath = ModdataFile.GetWorldModdataPath(worldPath);
                if (moddataPath == null)
                {
                    _log?.Warn("[WorldSavePatches] Could not determine world moddata path");
                    RestoreAll();
                    return;
                }

                if (customItems.Count == 0 && _preservedItems.Count == 0)
                {
                    // Delete stale moddata so deleted pending items don't reappear on next load
                    ModdataFile.Delete(moddataPath);
                    _log?.Debug("[WorldSavePatches] No custom items in chests, cleaned up moddata");
                    return;
                }

                if (!ModdataFile.Write(moddataPath, customItems, _preservedItems))
                {
                    _log?.Error("[WorldSavePatches] Failed to write moddata");
                    RestoreAll();
                    return;
                }

                // Replace extracted items with air so vanilla save doesn't see them
                foreach (var kvp in _extractedItems)
                {
                    var parts = kvp.Key.Split(':');
                    string chestKey = parts[0]; // "chest_N"
                    int slot = int.Parse(parts[1]);
                    int chestIdx = int.Parse(chestKey.Substring(6)); // after "chest_"
                    Main.chest[chestIdx].item[slot] = new Item();
                }
                _log?.Info($"[WorldSavePatches] Extracted {customItems.Count} items from chests" +
                    (_preservedItems.Count > 0 ? $" ({_preservedItems.Count} preserved from unloaded mods)" : ""));
            }
            catch (Exception ex)
            {
                _log?.Error($"[WorldSavePatches] Prefix error: {ex.Message}");
                RestoreAll();
            }
        }

        // ── Save postfix: restore items ──

        private static void SaveWorld_Postfix()
        {
            if (_extractedItems.Count == 0) return;

            try
            {
                RestoreAll();
                _log?.Debug("[WorldSavePatches] Restored items after save");
            }
            catch (Exception ex)
            {
                _log?.Error($"[WorldSavePatches] Postfix error: {ex.Message}");
            }
            finally
            {
                _extractedItems.Clear();
            }
        }

        // ── Load postfix: inject items into chests ──

        private static void LoadWorld_Postfix()
        {
            if (_worldItemsInjected)
            {
                _log?.Debug("[WorldSavePatches] Skipping duplicate injection — items already injected for this world load");
                return;
            }

            try
            {
                string worldPath = GetCurrentWorldPath();
                if (worldPath == null) { _worldItemsInjected = true; return; }

                // H3: One-time migration from legacy sidecar
                string v2Path = ModdataFile.GetWorldModdataPath(worldPath);
                string v1Path = ModdataFile.GetLegacyWorldModdataPath(worldPath);
                if (v2Path != null && v1Path != null)
                    ModdataFile.MigrateIfNeeded(v2Path, v1Path);

                if (v2Path == null) { _worldItemsInjected = true; return; }

                // Get loaded mod IDs
                var loadedModIds = new HashSet<string>(
                    ItemRegistry.AllIds.Select(id =>
                    {
                        int c = id.IndexOf(':');
                        return c > 0 ? id.Substring(0, c) : null;
                    }).Where(m => m != null),
                    StringComparer.OrdinalIgnoreCase);

                var items = ModdataFile.Read(v2Path, loadedModIds, out var preserved);
                _preservedItems = preserved ?? new List<ModdataFile.ItemEntry>();

                if (_preservedItems.Count > 0)
                    _log?.Info($"[WorldSavePatches] Preserving {_preservedItems.Count} item(s) from unloaded mod(s)");

                if (items.Count == 0) { _worldItemsInjected = true; return; }

                // Clear previous pending world items
                PendingItemStore.ClearWorld();

                int injected = 0, skipped = 0;

                foreach (var entry in items)
                {
                    try
                    {
                        // Pending world items from previous session — re-add to store
                        if (entry.Location == "pending_world")
                        {
                            int rt = ItemRegistry.GetRuntimeType(entry.ItemId);
                            if (rt >= 0)
                            {
                                PendingItemStore.AddWorldItem(new PendingItemStore.PendingItem
                                {
                                    ItemId = entry.ItemId,
                                    RuntimeType = rt,
                                    Stack = entry.Stack,
                                    Prefix = entry.Prefix,
                                    Favorited = false
                                });
                            }
                            skipped++;
                            continue;
                        }

                        // Parse chest index from location "chest_N"
                        if (!entry.Location.StartsWith("chest_")) { skipped++; continue; }
                        if (!int.TryParse(entry.Location.Substring(6), out int chestIdx)) { skipped++; continue; }
                        if (chestIdx < 0 || chestIdx >= Main.maxChests || Main.chest[chestIdx]?.item == null) { skipped++; continue; }

                        int runtimeType;
                        bool isVanillaOverflow = entry.ItemId.StartsWith("vanilla:");

                        if (isVanillaOverflow)
                        {
                            // Vanilla overflow item: "vanilla:{typeId}"
                            if (!int.TryParse(entry.ItemId.Substring(8), out runtimeType) || runtimeType <= 0)
                            { skipped++; continue; }
                        }
                        else
                        {
                            runtimeType = ItemRegistry.GetRuntimeType(entry.ItemId);
                            if (runtimeType < 0) { skipped++; continue; }

                            // Log alias resolution
                            string resolvedId = ItemRegistry.GetFullId(runtimeType);
                            if (resolvedId != null && !string.Equals(resolvedId, entry.ItemId, StringComparison.OrdinalIgnoreCase))
                                _log?.Info($"[Moddata] Resolved alias \"{entry.ItemId}\" → \"{resolvedId}\"");
                        }

                        var item = new Item();
                        item.SetDefaults(runtimeType);
                        item.stack = entry.Stack;
                        item.prefix = (byte)entry.Prefix;
                        if (entry.Prefix > 0) item.Prefix(entry.Prefix);

                        var chest = Main.chest[chestIdx];
                        if (entry.Slot >= 0 && entry.Slot < chest.item.Length &&
                            (chest.item[entry.Slot] == null || chest.item[entry.Slot].IsAir))
                        {
                            chest.item[entry.Slot] = item;
                            injected++;
                        }
                        else
                        {
                            // Find empty slot in same chest
                            bool placed = false;
                            for (int s = 0; s < chest.item.Length; s++)
                            {
                                if (chest.item[s] == null || chest.item[s].IsAir)
                                {
                                    chest.item[s] = item;
                                    injected++;
                                    placed = true;
                                    break;
                                }
                            }
                            if (!placed)
                            {
                                _log?.Info($"[WorldSavePatches] No slot for {entry.ItemId} in chest {chestIdx} — added to pending items");
                                PendingItemStore.AddWorldItem(new PendingItemStore.PendingItem
                                {
                                    ItemId = entry.ItemId,
                                    RuntimeType = runtimeType,
                                    Stack = entry.Stack,
                                    Prefix = entry.Prefix,
                                    Favorited = false
                                });
                                skipped++;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _log?.Error($"[WorldSavePatches] Failed to inject {entry.ItemId}: {ex.Message}");
                        skipped++;
                    }
                }

                _log?.Info($"[WorldSavePatches] Injected {injected} items into chests, skipped {skipped}");
                _worldItemsInjected = true;
            }
            catch (Exception ex)
            {
                _log?.Error($"[WorldSavePatches] Load postfix error: {ex.Message}");
            }
        }

        /// <summary>
        /// Re-inject custom items from world sidecar if they're missing from chests.
        /// Called from OnWorldLoad events as a safety net — on the H&P server, LoadWorld runs
        /// before Harmony patches are applied, so LoadWorld_Postfix never fires. This method
        /// provides the same injection, callable at any time after world load.
        /// Idempotent: guarded by _worldItemsInjected flag to prevent duplicate injection.
        /// </summary>
        public static void EnsureWorldItemsInjected()
        {
            LoadWorld_Postfix();
        }

        /// <summary>
        /// Reset world load injection state. Must be called on every world exit
        /// (singleplayer SaveAndQuit AND multiplayer disconnect) so the next world
        /// load can inject items fresh.
        /// </summary>
        public static void OnWorldUnload()
        {
            _worldItemsInjected = false;
            _preservedItems.Clear();
        }

        // ── Helpers ──

        private static void RestoreAll()
        {
            foreach (var kvp in _extractedItems)
            {
                try
                {
                    var parts = kvp.Key.Split(':');
                    string chestKey = parts[0];
                    int slot = int.Parse(parts[1]);
                    int chestIdx = int.Parse(chestKey.Substring(6));
                    if (chestIdx >= 0 && chestIdx < Main.maxChests && Main.chest[chestIdx]?.item != null)
                        Main.chest[chestIdx].item[slot] = kvp.Value;
                }
                catch { }
            }
        }

        private static string GetCurrentWorldPath()
        {
            try
            {
                // Try Main.ActiveWorldFileData.Path
                var worldFileData = Main.ActiveWorldFileData;
                if (worldFileData != null)
                {
                    var pathProp = worldFileData.GetType().GetProperty("Path");
                    if (pathProp != null)
                    {
                        return pathProp.GetValue(worldFileData) as string;
                    }
                }

                // Fallback: Main.worldPathName (getter-only property)
                var worldPathProp = typeof(Main).GetProperty("worldPathName", BindingFlags.Public | BindingFlags.Static);
                return worldPathProp?.GetValue(null) as string;
            }
            catch
            {
                return null;
            }
        }
    }
}
