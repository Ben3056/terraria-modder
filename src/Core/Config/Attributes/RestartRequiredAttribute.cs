using System;

namespace TerrariaModder.Core.Config
{
    /// <summary>
    /// Marks a config property as requiring a game restart to take effect.
    /// The UI shows a restart warning when the value changes from baseline.
    /// </summary>
    [AttributeUsage(AttributeTargets.Property, AllowMultiple = false)]
    public sealed class RestartRequiredAttribute : Attribute
    {
    }
}
