using System;

namespace TerrariaModder.Core.Config
{
    /// <summary>
    /// Marks a config property as client-side (local, per-install).
    /// Properties tagged [Client] appear in the Client inner tab.
    /// This is the default — untagged properties default to Client with a startup warning.
    /// </summary>
    [AttributeUsage(AttributeTargets.Property, AllowMultiple = false)]
    public sealed class ClientAttribute : Attribute
    {
    }
}
