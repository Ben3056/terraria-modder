using System.Collections.Generic;

namespace TerrariaModder.Core.Debug
{
    /// <summary>
    /// Interface for mods to expose their runtime state via the HTTP API.
    /// Implement this and register via ModContext.RegisterStateProvider().
    /// State is served at GET /api/mods/{mod-id}/state.
    /// </summary>
    public interface IModStateProvider
    {
        /// <summary>
        /// Return the mod's current runtime state as key-value pairs.
        /// Values should be bool, int, double, float, or string.
        /// Called from the HTTP server thread — ensure thread safety.
        /// </summary>
        Dictionary<string, object> GetModState();
    }
}
