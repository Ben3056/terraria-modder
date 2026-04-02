namespace TerrariaModder.Core
{
    /// <summary>
    /// Optional interface for mods that want lifecycle callbacks.
    /// Kept separate from IMod for backward compatibility — old mods that only
    /// implement IMod will still load without these methods.
    /// </summary>
    public interface IModLifecycle
    {
        /// <summary>
        /// Called after all mods have finished Initialize() and runtime type IDs have been
        /// assigned. Use this lifecycle point to look up items registered by other mods
        /// (via context.GetItemType / context.TryGetItemType) and to register cross-mod
        /// recipes or content that depends on another mod's items.
        /// </summary>
        void OnContentReady(ModContext context);

        /// <summary>
        /// Called when a world is loaded/entered.
        /// </summary>
        void OnWorldLoad();

        /// <summary>
        /// Called when leaving a world.
        /// </summary>
        void OnWorldUnload();
    }
}
