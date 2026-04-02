using System;

namespace TerrariaModder.Core.Config
{
    /// <summary>
    /// Maps an old JSON key name to this property during migration.
    /// Use when renaming a config property to preserve existing user values.
    /// </summary>
    [AttributeUsage(AttributeTargets.Property, AllowMultiple = true)]
    public sealed class FormerlySerializedAsAttribute : Attribute
    {
        public string OldName { get; }

        public FormerlySerializedAsAttribute(string oldName)
        {
            OldName = oldName;
        }
    }
}
