using System;
using System.Collections.Generic;

namespace TerrariaModder.Core.Assets
{
    /// <summary>
    /// Global tooltip modifier registry (Phase J1).
    /// Mods register callbacks that are applied to every custom item's tooltip,
    /// after the per-item ModifyTooltips hook fires.
    ///
    /// Registration: context.RegisterTooltipModifier((itemType, lines) => { ... })
    /// Modifiers receive the custom item's runtime type ID and a mutable List&lt;string&gt;.
    /// They can add, edit, or remove lines. Modifiers run in mod load order.
    /// Client-only — no tooltip rendering on dedicated server.
    /// </summary>
    public static class TooltipRegistry
    {
        private static readonly List<Action<int, List<string>>> _modifiers
            = new List<Action<int, List<string>>>();

        /// <summary>
        /// Register a global tooltip modifier callback.
        /// (int itemType, List&lt;string&gt; lines) — modify lines in place.
        /// Called after per-item ModifyTooltips, in registration order.
        /// </summary>
        public static void Register(Action<int, List<string>> modifier)
        {
            if (modifier != null)
                _modifiers.Add(modifier);
        }

        /// <summary>Returns the current list of registered modifiers (read-only view).</summary>
        public static IReadOnlyList<Action<int, List<string>>> GetModifiers() => _modifiers;

        /// <summary>Clear all modifiers (for testing/reload).</summary>
        public static void Clear() => _modifiers.Clear();
    }
}
