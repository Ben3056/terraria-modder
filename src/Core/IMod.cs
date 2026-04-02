namespace TerrariaModder.Core
{
    /// <summary>
    /// Interface that all mods must implement.
    /// The framework will discover and load classes implementing this interface.
    /// </summary>
    public interface IMod
    {
        /// <summary>
        /// Unique identifier for the mod. Must match the "id" in mod.json.
        /// </summary>
        string Id { get; }

        /// <summary>
        /// Display name of the mod.
        /// </summary>
        string Name { get; }

        /// <summary>
        /// Version string (e.g., "1.0.0").
        /// </summary>
        string Version { get; }

        /// <summary>
        /// Called when the mod is loaded. The ModContext provides access to
        /// logging, configuration, and other framework services.
        /// </summary>
        void Initialize(ModContext context);

        /// <summary>
        /// Called when the mod is being unloaded. Clean up resources here.
        /// </summary>
        void Unload();
    }
}
