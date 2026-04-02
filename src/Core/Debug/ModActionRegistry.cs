using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace TerrariaModder.Core.Debug
{
    /// <summary>
    /// Central registry for mod action providers.
    /// Mods register via ModContext.RegisterActionProvider().
    /// DebugHttpServer queries via GetProvider() and dispatches actions.
    /// </summary>
    public static class ModActionRegistry
    {
        private static readonly ConcurrentDictionary<string, IModActionProvider> _providers =
            new ConcurrentDictionary<string, IModActionProvider>(StringComparer.OrdinalIgnoreCase);

        public static void Register(string modId, IModActionProvider provider)
        {
            _providers[modId] = provider;
        }

        public static IModActionProvider GetProvider(string modId)
        {
            _providers.TryGetValue(modId, out var p);
            return p;
        }

        public static void Unregister(string modId)
        {
            IModActionProvider _;
            _providers.TryRemove(modId, out _);
        }

        public static IReadOnlyDictionary<string, IModActionProvider> All => _providers;
    }
}
