using System;

namespace TerrariaModder.Core.Config
{
    /// <summary>
    /// Display name for a config property in the UI.
    /// If omitted, the property name is used (with a warning logged at startup).
    /// </summary>
    [AttributeUsage(AttributeTargets.Property, AllowMultiple = false)]
    public sealed class LabelAttribute : Attribute
    {
        public string Text { get; }

        public LabelAttribute(string text)
        {
            Text = text;
        }
    }
}
