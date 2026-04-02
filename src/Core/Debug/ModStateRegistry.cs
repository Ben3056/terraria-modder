using System.Collections.Concurrent;
using System.Collections.Generic;

namespace TerrariaModder.Core.Debug
{
    /// <summary>
    /// Central registry for mod state providers.
    /// Mods register via ModContext.RegisterStateProvider().
    /// DebugHttpServer queries via GetProvider().
    /// </summary>
    public static class ModStateRegistry
    {
        private static readonly ConcurrentDictionary<string, IModStateProvider> _providers =
            new ConcurrentDictionary<string, IModStateProvider>(System.StringComparer.OrdinalIgnoreCase);

        public static void Register(string modId, IModStateProvider provider)
        {
            _providers[modId] = provider;
        }

        public static IModStateProvider GetProvider(string modId)
        {
            _providers.TryGetValue(modId, out var p);
            return p;
        }

        public static void Unregister(string modId)
        {
            IModStateProvider _;
            _providers.TryRemove(modId, out _);
        }

        public static IReadOnlyDictionary<string, IModStateProvider> All => _providers;
    }
}
