using System;

namespace TerrariaModder.Core.Config
{
    /// <summary>
    /// Restricts a string config property to a fixed set of options.
    /// Renders as a left/right arrow selector in the UI (like enum fields).
    /// </summary>
    [AttributeUsage(AttributeTargets.Property, AllowMultiple = false)]
    public sealed class OptionsAttribute : Attribute
    {
        public string[] Values { get; }

        public OptionsAttribute(params string[] values)
        {
            Values = values;
        }
    }
}
