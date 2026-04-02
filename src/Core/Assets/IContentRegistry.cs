namespace TerrariaModder.Core.Assets
{
    /// <summary>
    /// Implemented by each content type registry (items, tiles, NPCs, ...).
    /// The ContentRegistry coordinator calls these in sequence during ApplyPatches().
    /// </summary>
    internal interface IContentRegistry
    {
        /// <summary>Assign deterministic runtime type IDs to all registered content.</summary>
        void AssignRuntimeTypes();

        /// <summary>Number of registered entries of this content type.</summary>
        int Count { get; }

        /// <summary>Returns true if the given mod has registered any content of this type.</summary>
        bool HasCustomContent(string modId);
    }
}
