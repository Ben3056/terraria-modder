using System.Collections.Generic;

namespace TerrariaModder.Core.Assets
{
    /// <summary>
    /// Coordinator for all content type registries.
    /// Each content type (items, tiles, NPCs) registers an IContentRegistry instance here.
    /// AssetSystem calls AssignAllRuntimeTypes() instead of calling ItemRegistry directly,
    /// so future content types are automatically included without changing AssetSystem.
    /// </summary>
    internal static class ContentRegistry
    {
        private static readonly List<IContentRegistry> _registries = new List<IContentRegistry>();

        /// <summary>Register a content registry. Call from Initialize().</summary>
        public static void Register(IContentRegistry registry)
        {
            if (registry != null)
                _registries.Add(registry);
        }

        /// <summary>Assign runtime types across all registered content types.</summary>
        public static void AssignAllRuntimeTypes()
        {
            foreach (var registry in _registries)
                registry.AssignRuntimeTypes();
        }

        /// <summary>True if any registered content type has custom content for the given mod.</summary>
        public static bool AnyModHasCustomContent(string modId)
        {
            foreach (var registry in _registries)
                if (registry.HasCustomContent(modId)) return true;
            return false;
        }

        /// <summary>Total number of registered custom content entries across all types.</summary>
        public static int TotalCount
        {
            get
            {
                int total = 0;
                foreach (var registry in _registries) total += registry.Count;
                return total;
            }
        }
    }
}
