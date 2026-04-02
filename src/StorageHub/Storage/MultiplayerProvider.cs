using System;
using Terraria;
using StorageHub.Config;
using TerrariaModder.Core.Logging;

namespace StorageHub.Storage
{
    /// <summary>
    /// Multiplayer (host) implementation of IStorageProvider.
    /// Inherits singleplayer direct-access logic and adds packet broadcasts
    /// so connected clients see chest and inventory changes in real time.
    ///
    /// Packet contracts (verified from Terraria 1.4.5 decomp):
    ///   Packet 32 (SyncChestItem): broadcasts one chest slot to all clients.
    ///     Server does NOT require chest to be "open" to accept.
    ///     Format: chestIndex(short), slot(byte), stack(short), prefix(byte), type(short).
    ///     Send: NetMessage.TrySendData(32, -1, -1, null, chestIndex, slot)
    ///     → sends slot data from Main.chest[chestIndex].item[slot].
    ///   Packet 5 (PlayerInventorySlot): broadcasts one player inventory slot.
    ///     Format: playerIndex(byte), slot(short), stack(short), prefix(byte), type(short).
    ///     Send: NetMessage.TrySendData(5, -1, -1, null, playerIndex, slot)
    ///
    /// Only works when Main.netMode == 2 (host/server). Clients (netMode == 1) do not
    /// use this provider — StorageHub shows a "host only" message instead.
    ///
    /// Banks (piggy, safe, forge, void) are skipped for packet sync because:
    ///   - Negative chest indices are rejected by vanilla packet 32 handler.
    ///   - Bank items are client-side personal storage, not server-side.
    ///   - They sync via save file, not live packets.
    /// </summary>
    public class MultiplayerProvider : SingleplayerProvider
    {
        private readonly ILogger _log;

        // Collects every chest index modified during a DepositItem call so we can
        // broadcast ALL of them, not just the last one (SingleplayerProvider spreads
        // items across multiple chests in two passes).
        private readonly System.Collections.Generic.HashSet<int> _depositModifiedChests
            = new System.Collections.Generic.HashSet<int>();

        public MultiplayerProvider(ILogger log, ChestRegistry registry, StorageHubConfig config)
            : base(log, registry, config)
        {
            _log = log;
        }

        /// <summary>
        /// Take item from storage. After the base write, broadcasts the modified
        /// slot to all clients:
        ///   - Chest slots (chestIndex >= 0): packet 32 (SyncChestItem)
        ///   - Player inventory slots (chestIndex == -1): packet 5 (PlayerInventorySlot)
        ///   - Bank slots (chestIndex &lt;= -2): skipped — banks are client-side personal
        ///     storage (player.bankX.item[]), packet 32 rejects negative chest indices,
        ///     and banks sync via save file rather than live packets.
        /// </summary>
        public override bool TakeItem(int sourceChestIndex, int sourceSlot, int count, out ItemSnapshot taken)
        {
            bool result = base.TakeItem(sourceChestIndex, sourceSlot, count, out taken);

            if (result && sourceChestIndex >= 0)
            {
                // Broadcast the updated chest slot (may now be empty)
                BroadcastChestSlot(sourceChestIndex, sourceSlot);
            }
            else if (result && sourceChestIndex == SourceIndex.PlayerInventory
                     && sourceSlot >= 0 && sourceSlot < 50)
            {
                // Broadcast the consumed inventory slot so clients see the updated stack.
                // This fires when crafting consumes materials that came from player inventory.
                BroadcastInventorySlot(sourceSlot);
            }

            return result;
        }

        /// <summary>
        /// Collect every chest index written during a DepositItem call.
        /// Base class calls this at each write point (stacking pass and empty-slot pass),
        /// so we capture all chests that were modified — not just the last one.
        /// </summary>
        protected override void OnChestModified(int chestIndex)
        {
            if (chestIndex >= 0)
                _depositModifiedChests.Add(chestIndex);
        }

        /// <summary>
        /// Deposit item into chests. After the base write, broadcasts all slots of
        /// EVERY modified chest to clients via packet 32. SingleplayerProvider can spread
        /// items across multiple chests in two passes; we must broadcast each one.
        /// </summary>
        public override int DepositItem(ItemSnapshot item, out int depositedToChest)
        {
            _depositModifiedChests.Clear();
            int result = base.DepositItem(item, out depositedToChest);

            if (result > 0)
            {
                foreach (int idx in _depositModifiedChests)
                    BroadcastAllChestSlots(idx);
            }

            _depositModifiedChests.Clear();
            return result;
        }

        /// <summary>
        /// Move item from storage to player inventory.
        /// TakeItem (overridden above) handles the chest-side packet 32.
        /// This method additionally broadcasts the modified inventory slots via packet 5.
        /// </summary>
        public override bool MoveToInventory(int sourceChestIndex, int sourceSlot, int count)
        {
            // Snapshot player inventory before the operation
            var player = GetPlayer();
            var beforeSlots = SnapshotInventory(player);

            bool result = base.MoveToInventory(sourceChestIndex, sourceSlot, count);

            if (result && player != null)
            {
                // Broadcast any inventory slots that changed
                BroadcastChangedInventorySlots(player, beforeSlots);
            }

            return result;
        }

        // ── Packet helpers ──────────────────────────────────────────────────────

        private void BroadcastChestSlot(int chestIndex, int slot)
        {
            try
            {
                // NetMessage.TrySendData(32, remoteClient=-1, ignoreClient=-1, ...)
                // reads Main.chest[chestIndex].item[slot] and sends to all clients.
                NetMessage.TrySendData(32, -1, -1, null, chestIndex, slot);
            }
            catch (Exception ex)
            {
                _log.Warn($"[StorageHub] BroadcastChestSlot({chestIndex},{slot}) failed: {ex.Message}");
            }
        }

        private void BroadcastInventorySlot(int slot)
        {
            try
            {
                NetMessage.TrySendData(5, -1, -1, null, Main.myPlayer, slot);
            }
            catch (Exception ex)
            {
                _log.Warn($"[StorageHub] BroadcastInventorySlot({slot}) failed: {ex.Message}");
            }
        }

        private void BroadcastAllChestSlots(int chestIndex)
        {
            try
            {
                var chests = Main.chest;
                if (chests == null || chestIndex < 0 || chestIndex >= chests.Length) return;

                var chest = chests[chestIndex];
                if (chest?.item == null) return;

                for (int s = 0; s < chest.item.Length; s++)
                    NetMessage.TrySendData(32, -1, -1, null, chestIndex, s);
            }
            catch (Exception ex)
            {
                _log.Warn($"[StorageHub] BroadcastAllChestSlots({chestIndex}) failed: {ex.Message}");
            }
        }

        private void BroadcastChangedInventorySlots(Player player, (int type, int stack, int prefix)[] before)
        {
            try
            {
                if (before == null) return;
                var inv = player?.inventory;
                if (inv == null) return;

                int limit = Math.Min(before.Length, Math.Min(inv.Length, 50));
                for (int i = 0; i < limit; i++)
                {
                    var slot = inv[i];
                    if (slot == null) continue;

                    int t = slot.type, s = slot.stack, p = slot.prefix;
                    if (t != before[i].type || s != before[i].stack || p != before[i].prefix)
                    {
                        // NetMessage.TrySendData(5, ...) reads player.inventory[slot] and sends to all clients.
                        NetMessage.TrySendData(5, -1, -1, null, Main.myPlayer, i);
                    }
                }
            }
            catch (Exception ex)
            {
                _log.Warn($"[StorageHub] BroadcastChangedInventorySlots failed: {ex.Message}");
            }
        }

        // ── Snapshot helper ─────────────────────────────────────────────────────

        private static Player GetPlayer()
        {
            try
            {
                int idx = Main.myPlayer;
                var players = Main.player;
                if (players == null || idx < 0 || idx >= players.Length) return null;
                return players[idx];
            }
            catch { return null; }
        }

        private static (int type, int stack, int prefix)[] SnapshotInventory(Player player)
        {
            try
            {
                var inv = player?.inventory;
                if (inv == null) return null;

                int limit = Math.Min(inv.Length, 50);
                var snap = new (int, int, int)[limit];
                for (int i = 0; i < limit; i++)
                {
                    var slot = inv[i];
                    snap[i] = slot != null ? (slot.type, slot.stack, slot.prefix) : (0, 0, 0);
                }
                return snap;
            }
            catch { return null; }
        }
    }
}
