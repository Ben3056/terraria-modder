using System;
using System.Collections.Generic;
using TerrariaModder.Core.Assets;
using TerrariaModder.Core.Config;
using TerrariaModder.Core.Debug;
using TerrariaModder.Core.Input;
using TerrariaModder.Core.Logging;
using TerrariaModder.Core.Manifest;

namespace TerrariaModder.Core
{
    /// <summary>
    /// Context provided to each mod during initialization.
    /// Contains per-mod services like logging and configuration.
    /// </summary>
    public class ModContext
    {
        /// <summary>
        /// Per-mod logger instance.
        /// </summary>
        public ILogger Logger { get; }

        /// <summary>
        /// Mod configuration (null if mod has no ModConfig subclass).
        /// </summary>
        public ModConfig Config { get; }

        /// <summary>
        /// Path to the mod's folder.
        /// </summary>
        public string ModFolder { get; }

        /// <summary>
        /// True if this process is a dedicated server (TerrariaServer.exe).
        /// Mods can use this to skip UI initialization blocks.
        /// </summary>
        public bool IsServer => System.Environment.GetEnvironmentVariable("TERRARIA_MODDER_DEDSERV") == "1";

        /// <summary>
        /// The parsed manifest for this mod.
        /// </summary>
        public ModManifest Manifest { get; }

        private readonly List<Keybind> _registeredKeybinds = new List<Keybind>();

        /// <summary>
        /// Get the typed config instance for this mod. Returns null if no config or wrong type.
        /// </summary>
        public T GetConfig<T>() where T : ModConfig => Config as T;

        public ModContext(ILogger logger, string modFolder, ModManifest manifest, ModConfig config = null)
        {
            Logger = logger;
            ModFolder = modFolder;
            Manifest = manifest;
            Config = config;

            // Register mod folder for texture loading
            AssetSystem.RegisterModFolder(manifest.Id, modFolder);
        }

        /// <summary>
        /// Register a keybind for this mod.
        /// </summary>
        /// <param name="keybindId">Unique ID within this mod</param>
        /// <param name="label">Display label</param>
        /// <param name="description">Description for tooltip</param>
        /// <param name="defaultKey">Default key combo (e.g., "F5", "Ctrl+G")</param>
        /// <param name="callback">Action to execute when pressed</param>
        public Keybind RegisterKeybind(string keybindId, string label, string description, string defaultKey, Action callback)
        {
            var keybind = KeybindManager.Register(Manifest.Id, keybindId, label, description, defaultKey, callback);
            _registeredKeybinds.Add(keybind);
            return keybind;
        }

        /// <summary>
        /// Register a keybind with a KeyCombo.
        /// </summary>
        public Keybind RegisterKeybind(string keybindId, string label, string description, KeyCombo defaultKey, Action callback)
        {
            var keybind = KeybindManager.Register(Manifest.Id, keybindId, label, description, defaultKey, callback);
            _registeredKeybinds.Add(keybind);
            return keybind;
        }

        /// <summary>
        /// Get a keybind registered by this mod.
        /// </summary>
        public Keybind GetKeybind(string keybindId)
        {
            return KeybindManager.GetKeybind($"{Manifest.Id}.{keybindId}");
        }

        /// <summary>
        /// Get all keybinds registered by this mod.
        /// </summary>
        public IEnumerable<Keybind> GetKeybinds()
        {
            return KeybindManager.GetKeybindsForMod(Manifest.Id);
        }

        #region Custom Items

        /// <summary>
        /// Register a custom modded item.
        /// </summary>
        /// <param name="itemName">Unique item name within this mod (e.g., "fire-sword").</param>
        /// <param name="definition">The item definition with all properties.</param>
        /// <returns>True if registered successfully.</returns>
        public bool RegisterItem(string itemName, ItemDefinition definition)
        {
            return AssetSystem.RegisterItem(Manifest.Id, itemName, definition);
        }

        /// <summary>
        /// Get all custom item names registered by this mod.
        /// </summary>
        public IEnumerable<string> GetItems()
        {
            return ItemRegistry.GetItemsForMod(Manifest.Id);
        }

        #endregion

        #region Custom Recipes

        /// <summary>
        /// Register a crafting recipe.
        /// </summary>
        public void RegisterRecipe(RecipeDefinition recipe)
        {
            AssetSystem.RegisterRecipe(recipe);
        }

        #endregion

        #region Tooltip Modifiers

        /// <summary>
        /// Register a global tooltip modifier — applied to every custom item's tooltip.
        /// Callback: (int itemType, List&lt;string&gt; lines) — modify lines in place.
        /// Client-only; has no effect when called on a dedicated server.
        /// </summary>
        public void RegisterTooltipModifier(Action<int, List<string>> modifier)
        {
            AssetSystem.RegisterTooltipModifier(modifier);
        }

        #endregion

        #region NPC Shops

        /// <summary>
        /// Add a modded item to an NPC's shop.
        /// </summary>
        public void AddShopItem(ShopDefinition shopItem)
        {
            AssetSystem.RegisterShopItem(shopItem);
        }

        #endregion

        #region Enemy Drops

        /// <summary>
        /// Register a modded item drop from an NPC.
        /// </summary>
        public void RegisterDrop(DropDefinition drop)
        {
            AssetSystem.RegisterDrop(drop);
        }

        /// <summary>
        /// Register a shimmer transform: InputId item shimmers into OutputId item.
        /// Both IDs accept "modid:itemname" (custom) or vanilla item name/type.
        /// Server-safe — shimmer transforms run server-side in multiplayer.
        /// Call from Initialize() or OnContentReady().
        /// </summary>
        public void RegisterShimmer(ShimmerDefinition shimmer)
        {
            AssetSystem.RegisterShimmer(shimmer);
        }

        // ── Cross-mod query API (valid from OnContentReady onward) ──

        /// <summary>
        /// Returns the runtime type ID for a custom item registered by any mod.
        /// Only valid from OnContentReady() or later — returns -1 with a WARN if called during Initialize().
        /// </summary>
        /// <param name="modId">The mod that registered the item.</param>
        /// <param name="itemName">The item name (without mod prefix).</param>
        public int GetItemType(string modId, string itemName)
        {
            if (!ItemRegistry.TypesAssigned)
            {
                Logger.Warn($"[{Manifest.Id}] GetItemType called before types assigned — call from OnContentReady(), not Initialize()");
                return -1;
            }
            return ItemRegistry.GetRuntimeType($"{modId}:{itemName}");
        }

        /// <summary>
        /// Returns true and sets type if the item exists and type IDs are assigned.
        /// Only valid from OnContentReady() or later.
        /// </summary>
        public bool TryGetItemType(string modId, string itemName, out int type)
        {
            type = GetItemType(modId, itemName);
            return type >= 0;
        }

        /// <summary>
        /// Returns true if the mod with the given ID is currently loaded.
        /// </summary>
        public bool IsModLoaded(string modId)
        {
            return PluginLoader.IsModLoaded(modId);
        }

        /// <summary>
        /// Returns all item names registered by the given mod.
        /// Only valid from OnContentReady() or later — returns empty if called during Initialize().
        /// </summary>
        public IEnumerable<string> GetModItemNames(string modId)
        {
            if (!ItemRegistry.TypesAssigned)
            {
                Logger.Warn($"[{Manifest.Id}] GetModItemNames called before types assigned — call from OnContentReady(), not Initialize()");
                return System.Array.Empty<string>();
            }
            return ItemRegistry.GetItemsForMod(modId);
        }

        #endregion

        #region Mod State Provider

        /// <summary>
        /// Register a state provider for this mod, exposing runtime state via
        /// GET /api/mods/{mod-id}/state on the debug HTTP server.
        /// </summary>
        public void RegisterStateProvider(IModStateProvider provider)
        {
            ModStateRegistry.Register(Manifest.Id, provider);
        }

        #endregion

        #region Mod Action Provider

        /// <summary>
        /// Register an action provider for this mod, exposing executable actions via
        /// POST /api/mod-action on the debug HTTP server. Use query("capabilities")
        /// to discover all available actions.
        /// </summary>
        public void RegisterActionProvider(IModActionProvider provider)
        {
            ModActionRegistry.Register(Manifest.Id, provider);
        }

        #endregion

        #region Debug Commands

        /// <summary>
        /// Register a debug command for this mod.
        /// The command will be namespaced as "modid.name".
        /// </summary>
        /// <param name="name">Command name (lowercase, no spaces, e.g., "status").</param>
        /// <param name="description">Human-readable description of what the command does.</param>
        /// <param name="callback">Callback invoked with parsed arguments.</param>
        /// <returns>True if registered successfully, false if name is invalid or already taken.</returns>
        public bool RegisterCommand(string name, string description, Action<string[]> callback)
        {
            if (string.IsNullOrEmpty(name))
            {
                Logger.Warn("Cannot register command with empty name");
                return false;
            }

            // Validate: lowercase, no spaces
            if (name != name.ToLowerInvariant())
            {
                Logger.Warn($"Command name '{name}' must be lowercase");
                return false;
            }

            if (name.Contains(" "))
            {
                Logger.Warn($"Command name '{name}' cannot contain spaces");
                return false;
            }

            return CommandRegistry.RegisterForMod(Manifest.Id, name, description, callback);
        }

        /// <summary>
        /// Get all debug commands registered by this mod.
        /// </summary>
        public IReadOnlyList<CommandInfo> GetCommands()
        {
            return CommandRegistry.GetCommandsForMod(Manifest.Id);
        }

        #endregion

        // ChestLoot removed in v3 — world chest items are handled via save interception
    }
}
