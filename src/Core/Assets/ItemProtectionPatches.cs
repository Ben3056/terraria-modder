using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Terraria;
using TerrariaModder.Core.Logging;

namespace TerrariaModder.Core.Assets
{
    /// <summary>
    /// Safety patches for custom items (type >= VanillaItemCount) in vanilla Sort/QuickStack.
    ///
    /// Patches:
    ///   1. ItemSorting.Sort — extract custom items before sort, compact after vanilla items
    ///   2. ItemSorting.GetSortingLayerIndex — bounds check for custom types
    ///   3. QuickStacking.MatchingItemTypeDestinationList.firstEntryForType — resize array
    ///
    /// Sort crashes on custom items because they don't match any sorting layer whitelist
    /// (built by SetupWhiteLists which only iterates to ItemID.Count). Uncategorized items
    /// cause ArgumentOutOfRangeException in the placement loop (_sort_counts[0] on empty list).
    ///
    /// QuickStack works natively (arrays resized by TypeExtension + firstEntryForType resize).
    /// </summary>
    internal static class ItemProtectionPatches
    {
        private static Harmony _harmony;
        private static ILogger _log;
        private static bool _applied;

        [ThreadStatic]
        private static List<Item> _sortStash;

        public static void Initialize(ILogger logger)
        {
            _log = logger;
            _harmony = new Harmony("com.terrariamodder.assets.protection");
        }

        public static void ApplyPatches()
        {
            if (_applied) return;

            try
            {
                int patchCount = 0;
                patchCount += PatchSort();
                patchCount += PatchGetSortingLayerIndex();
                patchCount += PatchSetupWhiteLists();
                patchCount += PatchQuickStackAllChests();
                ResizeQuickStackArray();
                ResizeSortingLayerIndexArray();

                _applied = true;
                _log?.Info($"[ItemProtectionPatches] Applied {patchCount} patches + array resizes");
            }
            catch (Exception ex)
            {
                _log?.Error($"[ItemProtectionPatches] Failed: {ex.Message}\n{ex.StackTrace}");
            }
        }

        // ── 1. Sort Protection ──
        // Custom items crash Sort because they're not in any layer whitelist.
        // Extract before, let vanilla sort vanilla items, then compact custom
        // items right after the last vanilla item (same visual result as if
        // they were sorted together).

        private static int PatchSort()
        {
            try
            {
                var sortingType = typeof(Main).Assembly.GetType("Terraria.UI.ItemSorting");
                if (sortingType == null) return 0;

                var method = sortingType.GetMethod("Sort",
                    BindingFlags.NonPublic | BindingFlags.Static, null,
                    new[] { typeof(bool), typeof(Item[]), typeof(int[]) }, null);

                if (method == null)
                {
                    _log?.Warn("[ItemProtectionPatches] ItemSorting.Sort not found");
                    return 0;
                }

                _harmony.Patch(method,
                    prefix: new HarmonyMethod(typeof(ItemProtectionPatches), nameof(Sort_Prefix)),
                    postfix: new HarmonyMethod(typeof(ItemProtectionPatches), nameof(Sort_Postfix)));

                _log?.Debug("[ItemProtectionPatches] Patched ItemSorting.Sort");
                return 1;
            }
            catch (Exception ex)
            {
                _log?.Warn($"[ItemProtectionPatches] Sort patch failed: {ex.Message}");
                return 0;
            }
        }

        private static void Sort_Prefix(Item[] inv, int[] ignoreSlots)
        {
            if (inv == null) return;
            _sortStash = null;

            var ignoreSet = new HashSet<int>();
            if (ignoreSlots != null)
                foreach (int s in ignoreSlots)
                    ignoreSet.Add(s);

            for (int i = 0; i < inv.Length; i++)
            {
                if (ignoreSet.Contains(i)) continue;
                var item = inv[i];
                if (item == null || item.IsAir || item.type < ItemRegistry.VanillaItemCount) continue;

                if (_sortStash == null)
                    _sortStash = new List<Item>();

                _sortStash.Add(item.Clone());
                inv[i] = new Item();
            }

            if (_sortStash != null)
                _log?.Info($"[ItemProtectionPatches] Sort: extracted {_sortStash.Count} custom items");
        }

        private static void Sort_Postfix(Item[] inv, int[] ignoreSlots)
        {
            if (_sortStash == null || inv == null) return;

            var ignoreSet = new HashSet<int>();
            if (ignoreSlots != null)
                foreach (int s in ignoreSlots)
                    ignoreSet.Add(s);

            // Sort custom items by type so identical items group together
            _sortStash.Sort((a, b) => a.type.CompareTo(b.type));

            // Find where vanilla items end (first empty non-ignored slot after sorted items)
            // Vanilla sort compacts items to the start, so empty slots are at the end.
            // Place custom items right after the last vanilla item.
            int firstEmpty = -1;
            for (int i = 0; i < inv.Length; i++)
            {
                if (ignoreSet.Contains(i)) continue;
                if (inv[i] == null || inv[i].IsAir)
                {
                    firstEmpty = i;
                    break;
                }
            }

            // If no empty slot found, try from the end
            if (firstEmpty < 0)
                firstEmpty = 0;

            int restored = 0;
            int slot = firstEmpty;
            for (int si = 0; si < _sortStash.Count; si++)
            {
                // Find next available slot from current position
                while (slot < inv.Length && (ignoreSet.Contains(slot) || (inv[slot] != null && !inv[slot].IsAir)))
                    slot++;

                if (slot < inv.Length)
                {
                    inv[slot] = _sortStash[si];
                    restored++;
                    slot++;
                }
                else
                {
                    _log?.Warn($"[ItemProtectionPatches] Sort: no empty slot for custom item type {_sortStash[si].type}");
                }
            }

            _log?.Info($"[ItemProtectionPatches] Sort: restored {restored}/{_sortStash.Count} custom items (after vanilla items)");
            _sortStash = null;
        }

        // ── 2. GetSortingLayerIndex bounds check ──

        private static int PatchGetSortingLayerIndex()
        {
            try
            {
                var sortingType = typeof(Main).Assembly.GetType("Terraria.UI.ItemSorting");
                if (sortingType == null) return 0;

                var method = sortingType.GetMethod("GetSortingLayerIndex",
                    BindingFlags.Public | BindingFlags.Static, null,
                    new[] { typeof(int) }, null);

                if (method == null)
                {
                    _log?.Warn("[ItemProtectionPatches] GetSortingLayerIndex not found");
                    return 0;
                }

                _harmony.Patch(method,
                    prefix: new HarmonyMethod(typeof(ItemProtectionPatches), nameof(GetSortingLayerIndex_Prefix)));

                _log?.Debug("[ItemProtectionPatches] Patched GetSortingLayerIndex");
                return 1;
            }
            catch (Exception ex)
            {
                _log?.Warn($"[ItemProtectionPatches] GetSortingLayerIndex patch failed: {ex.Message}");
                return 0;
            }
        }

        private static bool GetSortingLayerIndex_Prefix(int itemType, ref int __result)
        {
            if (itemType >= ItemRegistry.VanillaItemCount)
            {
                __result = 0;
                return false;
            }
            return true;
        }

        // ── 3. Postfix on SetupWhiteLists to re-resize _layerIndexForItemType ──
        // SetupWhiteLists() re-creates _layerIndexForItemType at vanilla size (ItemID.Count).
        // This runs after our initial resize, undoing it. Postfix re-resizes every time.

        private static int PatchSetupWhiteLists()
        {
            try
            {
                var sortingType = typeof(Main).Assembly.GetType("Terraria.UI.ItemSorting");
                if (sortingType == null) return 0;

                var method = sortingType.GetMethod("SetupWhiteLists",
                    BindingFlags.Public | BindingFlags.Static);

                if (method == null)
                {
                    _log?.Warn("[ItemProtectionPatches] SetupWhiteLists not found");
                    return 0;
                }

                _harmony.Patch(method,
                    postfix: new HarmonyMethod(typeof(ItemProtectionPatches), nameof(SetupWhiteLists_Postfix)));

                _log?.Debug("[ItemProtectionPatches] Patched SetupWhiteLists");
                return 1;
            }
            catch (Exception ex)
            {
                _log?.Warn($"[ItemProtectionPatches] SetupWhiteLists patch failed: {ex.Message}");
                return 0;
            }
        }

        private static void SetupWhiteLists_Postfix()
        {
            // Re-resize _layerIndexForItemType after vanilla rebuilds it
            ResizeSortingLayerIndexArray();
        }

        // ── 4. QuickStack protection ──
        // Extract custom items before QuickStack to prevent any IndexOutOfRange
        // in vanilla code that may access ItemID.Sets or other fixed-size arrays.

        [ThreadStatic]
        private static List<(int slot, Item item)> _quickStackStash;

        private static int PatchQuickStackAllChests()
        {
            try
            {
                var method = typeof(Player).GetMethod("QuickStackAllChests",
                    BindingFlags.Public | BindingFlags.Instance);

                if (method == null)
                {
                    _log?.Warn("[ItemProtectionPatches] QuickStackAllChests not found");
                    return 0;
                }

                _harmony.Patch(method,
                    prefix: new HarmonyMethod(typeof(ItemProtectionPatches), nameof(QuickStackAllChests_Prefix)),
                    postfix: new HarmonyMethod(typeof(ItemProtectionPatches), nameof(QuickStackAllChests_Postfix)));

                _log?.Debug("[ItemProtectionPatches] Patched QuickStackAllChests");
                return 1;
            }
            catch (Exception ex)
            {
                _log?.Warn($"[ItemProtectionPatches] QuickStackAllChests patch failed: {ex.Message}");
                return 0;
            }
        }

        private static void QuickStackAllChests_Prefix(Player __instance)
        {
            _quickStackStash = null;
            if (__instance?.inventory == null) return;

            var inv = __instance.inventory;
            for (int i = 10; i < 50; i++) // slots 10-49 (skip hotbar 0-9)
            {
                var item = inv[i];
                if (item == null || item.IsAir || item.type < ItemRegistry.VanillaItemCount) continue;

                if (_quickStackStash == null)
                    _quickStackStash = new List<(int, Item)>();

                _quickStackStash.Add((i, item.Clone()));
                inv[i] = new Item();
            }

            if (_quickStackStash != null)
                _log?.Debug($"[ItemProtectionPatches] QuickStack: extracted {_quickStackStash.Count} custom items");
        }

        private static void QuickStackAllChests_Postfix(Player __instance)
        {
            if (_quickStackStash == null || __instance?.inventory == null) return;

            var inv = __instance.inventory;
            int restored = 0;
            foreach (var (slot, item) in _quickStackStash)
            {
                if (inv[slot] == null || inv[slot].IsAir)
                {
                    inv[slot] = item;
                    restored++;
                }
                else
                {
                    // Slot was filled by QuickStack, find another empty slot
                    bool placed = false;
                    for (int i = 10; i < 50; i++)
                    {
                        if (inv[i] == null || inv[i].IsAir)
                        {
                            inv[i] = item;
                            restored++;
                            placed = true;
                            break;
                        }
                    }
                    if (!placed)
                        _log?.Warn($"[ItemProtectionPatches] QuickStack: no slot for custom item type {item.type}");
                }
            }

            _log?.Debug($"[ItemProtectionPatches] QuickStack: restored {restored}/{_quickStackStash.Count} custom items");
            _quickStackStash = null;
        }

        // ── 5. Resize QuickStacking.firstEntryForType array ──

        private static void ResizeQuickStackArray()
        {
            try
            {
                var quickStackType = typeof(Main).Assembly.GetType("Terraria.GameContent.QuickStacking");
                if (quickStackType == null)
                {
                    _log?.Warn("[ItemProtectionPatches] QuickStacking type not found");
                    return;
                }

                var nestedType = quickStackType.GetNestedType("MatchingItemTypeDestinationList",
                    BindingFlags.NonPublic);
                if (nestedType == null)
                {
                    _log?.Warn("[ItemProtectionPatches] MatchingItemTypeDestinationList not found");
                    return;
                }

                var scratchField = quickStackType.GetField("matchingItemTypeScratch",
                    BindingFlags.NonPublic | BindingFlags.Static);
                if (scratchField == null)
                {
                    _log?.Warn("[ItemProtectionPatches] matchingItemTypeScratch field not found");
                    return;
                }

                var scratchInstance = scratchField.GetValue(null);
                if (scratchInstance == null)
                {
                    _log?.Warn("[ItemProtectionPatches] matchingItemTypeScratch is null");
                    return;
                }

                var arrayField = nestedType.GetField("firstEntryForType",
                    BindingFlags.NonPublic | BindingFlags.Instance);
                if (arrayField == null)
                {
                    _log?.Warn("[ItemProtectionPatches] firstEntryForType field not found");
                    return;
                }

                var currentArray = arrayField.GetValue(scratchInstance) as int[];
                if (currentArray == null)
                {
                    _log?.Warn("[ItemProtectionPatches] firstEntryForType is null");
                    return;
                }

                // Use ExtendedCount to cover hash-derived type IDs which can be anywhere in [VanillaItemCount, 32767)
                int needed = TypeExtension.ExtendedCount > 0 ? TypeExtension.ExtendedCount : ItemRegistry.VanillaItemCount + ItemRegistry.Count + 64;
                if (currentArray.Length >= needed)
                {
                    _log?.Debug($"[ItemProtectionPatches] firstEntryForType already large enough ({currentArray.Length} >= {needed})");
                    return;
                }

                var newArray = new int[needed];
                Array.Copy(currentArray, newArray, currentArray.Length);
                arrayField.SetValue(scratchInstance, newArray);

                _log?.Info($"[ItemProtectionPatches] Resized firstEntryForType: {currentArray.Length} → {needed}");
            }
            catch (Exception ex)
            {
                _log?.Warn($"[ItemProtectionPatches] firstEntryForType resize failed: {ex.Message}");
            }
        }

        // ── 6. Resize ItemSorting._layerIndexForItemType array ──
        // GetSortingLayerIndex directly indexes _layerIndexForItemType[itemType].
        // The prefix patch prevents OOB for custom types, but if the prefix fails
        // to fire (e.g. inlining), this resize provides a safety net.
        // Also prevents OOB in sorting lambdas that access ItemID.Sets arrays
        // via item.type bounds checks against ItemID.Count.

        private static void ResizeSortingLayerIndexArray()
        {
            try
            {
                var sortingType = typeof(Main).Assembly.GetType("Terraria.UI.ItemSorting");
                if (sortingType == null)
                {
                    _log?.Debug("[ItemProtectionPatches] ItemSorting type not found");
                    return;
                }

                var field = sortingType.GetField("_layerIndexForItemType",
                    BindingFlags.NonPublic | BindingFlags.Static);
                if (field == null)
                {
                    _log?.Debug("[ItemProtectionPatches] _layerIndexForItemType field not found");
                    return;
                }

                var currentArray = field.GetValue(null) as int[];
                if (currentArray == null)
                {
                    _log?.Debug("[ItemProtectionPatches] _layerIndexForItemType is null");
                    return;
                }

                int needed = TypeExtension.ExtendedCount > 0 ? TypeExtension.ExtendedCount : ItemRegistry.VanillaItemCount + ItemRegistry.Count + 64;
                if (currentArray.Length >= needed)
                {
                    _log?.Debug($"[ItemProtectionPatches] _layerIndexForItemType already large enough ({currentArray.Length} >= {needed})");
                    return;
                }

                var newArray = new int[needed];
                Array.Copy(currentArray, newArray, currentArray.Length);
                field.SetValue(null, newArray);

                _log?.Info($"[ItemProtectionPatches] Resized _layerIndexForItemType: {currentArray.Length} → {needed}");
            }
            catch (Exception ex)
            {
                _log?.Warn($"[ItemProtectionPatches] _layerIndexForItemType resize failed: {ex.Message}");
            }
        }
    }
}
