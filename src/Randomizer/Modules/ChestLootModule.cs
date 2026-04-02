using System;
using System.Collections.Generic;
using HarmonyLib;
using Terraria;

namespace Randomizer.Modules
{
    /// <summary>
    /// Randomizes chest loot by swapping item types in all world chests on world load.
    /// </summary>
    public class ChestLootModule : ModuleBase
    {
        public override string Id => "chest_loot";
        public override string Name => "Chest Loot Shuffle";
        public override string Description => "Swap items in all world chests";
        public override string Tooltip => "Swaps item types in all world chests. Same seed always produces the same swaps. Requires world reload to re-randomize.";
        public override bool IsWorldGen => true;

        private static ChestLootModule _instance;

        // Stores original item types so we can revert before re-shuffling
        // Key: (chestIndex, slotIndex), Value: original item type
        private Dictionary<(int, int), int> _originalTypes = new Dictionary<(int, int), int>();

        public override void BuildShuffleMap()
        {
            _instance = this;

            // Build pool of valid item IDs for chest loot (last valid = ItemID.Count - 1)
            int maxItemId = Terraria.ID.ItemID.Count - 1;
            var pool = new List<int>();
            for (int i = 1; i <= maxItemId; i++)
            {
                pool.Add(i);
            }
            ShuffleMap = Seed.BuildShuffleMap(pool, Id);

            // Revert any previous shuffle before applying the new one
            RevertChests();

            // Apply the shuffle to existing chests in the world
            ApplyToWorldChests();
        }

        private void RevertChests()
        {
            if (_originalTypes.Count == 0) return;

            try
            {
                var chests = Main.chest;
                if (chests == null) return;

                foreach (var kv in _originalTypes)
                {
                    int c = kv.Key.Item1;
                    int i = kv.Key.Item2;
                    int origType = kv.Value;

                    if (c < 0 || c >= chests.Length) continue;
                    var chest = chests[c];
                    if (chest?.item == null || i < 0 || i >= chest.item.Length) continue;

                    var item = chest.item[i];
                    if (item == null) continue;

                    int stack = item.stack;
                    item.SetDefaults(origType);
                    item.stack = stack;
                }

                Log.Info($"[Randomizer] Chest Loot: reverted {_originalTypes.Count} items to originals");
                _originalTypes.Clear();
            }
            catch (Exception ex)
            {
                Log.Error($"[Randomizer] Chest Loot revert error: {ex.Message}");
            }
        }

        private void ApplyToWorldChests()
        {
            try
            {
                // Only apply in singleplayer — in MP, chest contents are server-authoritative
                if (Main.netMode != 0) return;

                var chests = Main.chest;
                if (chests == null) return;

                int chestCount = 0;
                int itemCount = 0;

                for (int c = 0; c < chests.Length; c++)
                {
                    var chest = chests[c];
                    if (chest == null) continue;

                    var items = chest.item;
                    if (items == null) continue;

                    bool modified = false;
                    for (int i = 0; i < items.Length; i++)
                    {
                        var item = items[i];
                        if (item == null) continue;

                        int type = item.type;
                        int stack = item.stack;
                        if (type <= 0 || stack <= 0) continue;

                        if (ShuffleMap.TryGetValue(type, out int newType) && newType != type)
                        {
                            // Record original type before first shuffle
                            var key = (c, i);
                            if (!_originalTypes.ContainsKey(key))
                                _originalTypes[key] = type;

                            item.SetDefaults(newType);
                            item.stack = stack; // Preserve stack
                            modified = true;
                            itemCount++;
                        }
                    }
                    if (modified) chestCount++;
                }

                Log.Info($"[Randomizer] Chest Loot: shuffled {itemCount} items in {chestCount} chests");
            }
            catch (Exception ex)
            {
                Log.Error($"[Randomizer] Chest Loot error: {ex.Message}");
            }
        }

        public override void ApplyPatches(Harmony harmony)
        {
            // Chest loot doesn't need runtime patches — it modifies chests on world load
        }

        public override void RemovePatches(Harmony harmony)
        {
            // Revert chest items when module is disabled
            RevertChests();
            // Clear stale mappings to prevent cross-world contamination
            _originalTypes.Clear();
        }
    }
}
