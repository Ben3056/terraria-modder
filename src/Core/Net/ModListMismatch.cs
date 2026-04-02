namespace TerrariaModder.Core.Net
{
    /// <summary>
    /// Tracks the result of a ModListExchange on the client side.
    /// Set during HandleModListExchange; read by PluginLoader's title-screen overlay.
    /// </summary>
    internal static class ModListMismatch
    {
        /// <summary>True if the client was blocked due to missing required mods.</summary>
        public static bool IsBlocked { get; private set; }

        /// <summary>Human-readable reason shown in the block overlay.</summary>
        public static string BlockedReason { get; private set; }

        /// <summary>
        /// Warning text queued for in-world chat display (optional mods missing).
        /// Null if no optional mismatch.
        /// </summary>
        public static string OptionalWarning { get; private set; }

        public static void SetBlocked(string reason)
        {
            IsBlocked = true;
            BlockedReason = reason;
        }

        public static void SetOptionalWarning(string warning)
        {
            OptionalWarning = warning;
        }

        /// <summary>Reset all state (call when player returns to menu or reconnects).</summary>
        public static void Clear()
        {
            IsBlocked = false;
            BlockedReason = null;
            OptionalWarning = null;
        }
    }
}
