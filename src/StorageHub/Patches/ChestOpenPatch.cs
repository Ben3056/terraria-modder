using System;
using Terraria;
using TerrariaModder.Core.Logging;
using StorageHub.Storage;

namespace StorageHub.Patches
{
    /// <summary>
    /// Detects when the player opens a chest and registers it.
    ///
    /// APPROACH: Instead of Harmony patching (which requires finding exact methods),
    /// we poll the player.chest field each frame and detect changes.
    /// This is simpler and more robust to Terraria version changes.
    ///
    /// When player.chest changes to a positive value (chest index), we register that chest.
    ///
    /// Why manual open is required:
    /// - Prevents accessing world-gen loot chests remotely (cheating)
    /// - Player must physically find and open the chest first
    /// - Natural gameplay - registering chests you already use
    /// </summary>
    public class ChestOpenDetector
    {
        private readonly ILogger _log;
        private readonly ChestRegistry _registry;

        // State tracking
        private int _lastChestIndex = -1;

        public ChestOpenDetector(ILogger log, ChestRegistry registry)
        {
            _log = log;
            _registry = registry;
        }

        public void Initialize()
        {
            _log.Debug("ChestOpenDetector initialized");
        }

        /// <summary>
        /// Called each frame to detect chest opens.
        /// Should be called from FrameEvents.OnPreUpdate.
        /// </summary>
        public void Update()
        {

            try
            {
                int currentChest = GetPlayerChestIndex();

                // Detect change from closed/other to a chest
                if (currentChest != _lastChestIndex)
                {
                    if (currentChest >= 0)
                    {
                        // Player opened a chest (not a bank)
                        OnChestOpened(currentChest);
                    }
                    _lastChestIndex = currentChest;
                }
            }
            catch (Exception ex)
            {
                _log.Error($"ChestOpenDetector update failed: {ex.Message}");
            }
        }

        private int GetPlayerChestIndex()
        {
            try
            {
                if (Main.player == null || Main.myPlayer < 0 || Main.myPlayer >= Main.player.Length)
                    return -1;
                return Main.player[Main.myPlayer].chest;
            }
            catch
            {
                return -1;
            }
        }

        private void OnChestOpened(int chestIndex)
        {
            try
            {
                var chests = Main.chest;
                if (chests == null || chestIndex < 0 || chestIndex >= chests.Length)
                    return;

                var chest = chests[chestIndex];
                if (chest == null) return;

                int x = chest.x;
                int y = chest.y;

                // Check if it's a valid chest position
                if (x < 0 || y < 0) return;

                // Check if locked (we shouldn't register locked chests)
                // For now, assume if player can open it, it's not locked
                // Terraria prevents opening locked chests anyway

                // Register the chest
                if (_registry.RegisterChest(x, y))
                {
                    _log.Debug($"Registered chest at ({x}, {y}) - index {chestIndex}");
                }
            }
            catch (Exception ex)
            {
                _log.Error($"OnChestOpened failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Reset state when world unloads.
        /// </summary>
        public void Reset()
        {
            _lastChestIndex = -1;
        }
    }
}
