using System;

namespace TerrariaModder.Core.Config
{
    /// <summary>
    /// Numeric range constraint for int or float config properties.
    /// Values are clamped to [Min, Max] on load and in the UI.
    /// </summary>
    [AttributeUsage(AttributeTargets.Property, AllowMultiple = false)]
    public sealed class RangeAttribute : Attribute
    {
        public double Min { get; }
        public double Max { get; }

        public RangeAttribute(double min, double max)
        {
            Min = min;
            Max = max;
        }
    }
}
