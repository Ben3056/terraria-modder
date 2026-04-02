using System;

namespace TerrariaModder.Core.Config
{
    /// <summary>
    /// Tooltip description for a config property in the UI.
    /// </summary>
    [AttributeUsage(AttributeTargets.Property, AllowMultiple = false)]
    public sealed class DescriptionAttribute : Attribute
    {
        public string Text { get; }

        public DescriptionAttribute(string text)
        {
            Text = text;
        }
    }
}
