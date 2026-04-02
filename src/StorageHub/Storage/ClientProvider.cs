using StorageHub.Config;
using TerrariaModder.Core.Logging;
using TerrariaModder.Core.Net;

namespace StorageHub.Storage
{
    /// <summary>
    /// Multiplayer client (netMode==1) implementation of IStorageProvider.
    ///
    /// READ operations (GetAllItems, GetItemsInRange, etc.) inherit from SingleplayerProvider
    /// and read Main.chest[] directly — Terraria syncs chest contents to all clients
    /// via vanilla packet 32 (SyncChestItem), so client-side chest data is up to date.
    ///
    /// WRITE operations (TakeItem, DepositItem, MoveToInventory) use an optimistic approach:
    /// 1. Execute locally via base SingleplayerProvider (immediate UI feedback).
    /// 2. Send a StorageRequest (0x30) to the server.
    /// 3. Server executes the same operation via its MultiplayerProvider, which broadcasts
    ///    packet 32 to ALL clients (including this one), confirming or correcting the slot.
    ///
    /// This mirrors how vanilla Terraria handles chest operations from clients: client-side
    /// prediction + server broadcast for authoritative state.
    ///
    /// Banks (piggy, safe, forge, void) are skipped: they are personal client-side storage
    /// that sync via save file, not live packets (same exclusion as MultiplayerProvider).
    /// </summary>
    public class ClientProvider : SingleplayerProvider
    {
        public ClientProvider(ILogger log, ChestRegistry registry, StorageHubConfig config)
            : base(log, registry, config)
        {
        }

        /// <summary>
        /// Take items from a chest slot.
        /// Executes locally for immediate feedback, then notifies server to broadcast to others.
        /// </summary>
        public override bool TakeItem(int sourceChestIndex, int sourceSlot, int count, out ItemSnapshot taken)
        {
            bool result = base.TakeItem(sourceChestIndex, sourceSlot, count, out taken);

            if (result && sourceChestIndex >= 0)
            {
                // Notify server: remove from chest and broadcast packet 32 to other clients.
                // Payload: "{chestIndex}:{slot}:{count}"
                NetSync.SendStorageRequest("take", $"{sourceChestIndex}:{sourceSlot}:{count}");
            }

            return result;
        }

        /// <summary>
        /// Deposit items into storage.
        /// Executes locally, then tells server which chest received the deposit.
        /// </summary>
        public override int DepositItem(ItemSnapshot item, out int depositedToChest)
        {
            int result = base.DepositItem(item, out depositedToChest);

            if (result > 0 && depositedToChest >= 0)
            {
                // Notify server: deposit into the same chest and broadcast packet 32.
                // Payload: "{chestIndex}:{itemId}:{count}:{prefix}"
                NetSync.SendStorageRequest("deposit", $"{depositedToChest}:{item.ItemId}:{result}:{item.Prefix}");
            }

            return result;
        }

        /// <summary>
        /// Move items from storage to player inventory.
        /// base.MoveToInventory() calls TakeItem() internally — virtual dispatch hits
        /// ClientProvider.TakeItem which already sends "take" to the server.
        /// No extra request needed here; a second "move" send would be a duplicate.
        /// </summary>
        public override bool MoveToInventory(int sourceChestIndex, int sourceSlot, int count)
        {
            return base.MoveToInventory(sourceChestIndex, sourceSlot, count);
        }
    }
}
