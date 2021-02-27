using System;

namespace Orleans.Serialization.TypeSystem
{
    /// <summary>
    /// Converts between <see cref="Type"/> and <see cref="string"/> representations.
    /// </summary>
    public interface ITypeConverter
    {
        /// <summary>
        /// Formats the provided type as a string.
        /// </summary>
        bool TryFormat(Type type, out string formatted);

        /// <summary>
        /// Parses the provided type.
        /// </summary>
        bool TryParse(string formatted, out Type type);
    }

    public interface ITypeFilter
    {
        bool? IsTypeNameAllowed(string typeName, string assemblyName);
    }
}