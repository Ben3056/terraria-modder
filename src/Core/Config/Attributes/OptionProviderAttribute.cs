using System;

namespace TerrariaModder.Core.Config
{
    /// <summary>
    /// Resolves a string config property's allowed options from a method at runtime.
    ///
    /// This complements <see cref="OptionsAttribute"/> for cases where the valid
    /// option list is not known at compile time, such as assets discovered during
    /// mod initialisation or values supplied by another registry.
    ///
    /// The provider method must be parameterless and return either string[] or any
    /// <see cref="System.Collections.Generic.IEnumerable{T}"/> of string values.
    /// It may be static or an instance method on the <see cref="ModConfig"/> subclass.
    /// </summary>
    [AttributeUsage(AttributeTargets.Property, AllowMultiple = false)]
    public sealed class OptionProviderAttribute : Attribute
    {
        /// <summary>
        /// Name of the parameterless method on the config type that returns the
        /// current option list.
        /// </summary>
        public string MethodName { get; }

        public OptionProviderAttribute(string methodName)
        {
            MethodName = methodName;
        }
    }
}