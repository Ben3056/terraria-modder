using System;

namespace TerrariaModder.Core.Config
{
    /// <summary>
    /// Marks a config property as server-controlled (synced in multiplayer).
    /// Properties tagged [Server] appear in the Server inner tab.
    /// </summary>
    [AttributeUsage(AttributeTargets.Property, AllowMultiple = false)]
    public sealed class ServerAttribute : Attribute
    {
    }
}
