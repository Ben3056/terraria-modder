using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using TerrariaModder.Core.Assets;
using TerrariaModder.Core.Logging;

namespace TerrariaModder.Core.Server
{
    /// <summary>
    /// Server-side storage for custom item snapshots (H4 — server-authoritative moddata).
    ///
    /// Uses the same mod-keyed ModdataFile format (version 2) as client moddata.
    ///
    /// Paths:
    ///   Player items: {CorePath}/player-data/moddata/{guid}.json
    ///   World items:  {CorePath}/world-data/{WorldName}.json
    ///
    /// The server owns these files. Clients have no filesystem access to the server's CorePath.
    /// Items from mods the server doesn't recognise are rejected in HandleCustomItemSave.
    /// </summary>
    public static class ServerModdataStore
    {
        private static ILogger _log;
        private static string _playerDataPath;
        private static string _worldDataPath;

        /// <summary>
        /// Items from unloaded mods, keyed by player GUID.
        /// Preserved across read/write cycles so items from mods the server doesn't have aren't lost.
        /// </summary>
        private static readonly Dictionary<string, List<ModdataFile.ItemEntry>> _preservedItems =
            new Dictionary<string, List<ModdataFile.ItemEntry>>(StringComparer.OrdinalIgnoreCase);

        public static void Initialize(ILogger logger, string corePath)
        {
            _log = logger;

            _playerDataPath = Path.Combine(corePath, "player-data", "moddata");
            _worldDataPath = Path.Combine(corePath, "world-data");

            TryCreateDir(_playerDataPath);
            TryCreateDir(_worldDataPath);

            _log?.Info($"[ServerModdataStore] Initialized. Player path: {_playerDataPath}");
        }

        // ─── Player items ─────────────────────────────────────────────────────

        public static List<ModdataFile.ItemEntry> ReadPlayer(string guid)
        {
            if (string.IsNullOrEmpty(guid) || _playerDataPath == null)
                return new List<ModdataFile.ItemEntry>();

            string path = GetPlayerPath(guid);
            var items = ModdataFile.Read(path, GetLoadedModIds(), out var preservedItems);

            if (preservedItems != null && preservedItems.Count > 0)
            {
                _preservedItems[guid] = preservedItems;
                _log?.Info($"[ServerModdataStore] Preserved {preservedItems.Count} items from unloaded mods for GUID {guid}");
            }
            else
            {
                _preservedItems.Remove(guid);
            }

            return items;
        }

        public static void WritePlayer(string guid, List<ModdataFile.ItemEntry> items)
        {
            if (string.IsNullOrEmpty(guid) || _playerDataPath == null) return;

            string path = GetPlayerPath(guid);
            List<ModdataFile.ItemEntry> preserved = null;
            _preservedItems.TryGetValue(guid, out preserved);
            ModdataFile.Write(path, items ?? new List<ModdataFile.ItemEntry>(), preserved);
        }

        // ─── World items ──────────────────────────────────────────────────────

        public static string GetWorldPath(string worldName)
        {
            if (string.IsNullOrEmpty(worldName) || _worldDataPath == null) return null;
            return Path.Combine(_worldDataPath, worldName + ".json");
        }

        // ─── Helpers ──────────────────────────────────────────────────────────

        private static string GetPlayerPath(string guid) =>
            Path.Combine(_playerDataPath, guid + ".json");

        private static ICollection<string> GetLoadedModIds()
        {
            return new HashSet<string>(
                ItemRegistry.AllIds.Select(id =>
                {
                    int c = id.IndexOf(':');
                    return c > 0 ? id.Substring(0, c) : null;
                }).Where(m => m != null),
                StringComparer.OrdinalIgnoreCase);
        }

        private static void TryCreateDir(string dir)
        {
            try
            {
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);
            }
            catch (Exception ex)
            {
                _log?.Warn($"[ServerModdataStore] Could not create directory {dir}: {ex.Message}");
            }
        }
    }
}
