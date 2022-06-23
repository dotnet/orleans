using System;

namespace Orleans.Serialization
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

    /// <summary>
    /// Functionality for allowing types to be loaded and to participate in serialization, deserialization, etcetera.
    /// </summary>
    public interface ITypeNameFilter
    {
        /// <summary>
        /// Determines whether the specified type name corresponds to a type which is allowed to be loaded, serialized, deserialized, etcetera.
        /// </summary>
        /// <param name="typeName">Name of the type.</param>
        /// <param name="assemblyName">Name of the assembly.</param>
        /// <returns><see langword="true" /> if the specified type is allowed; otherwise, <see langword="false" />.</returns>
        bool? IsTypeNameAllowed(string typeName, string assemblyName);
    }

    /// <summary>
    /// Functionality for allowing types to be loaded and to participate in serialization, deserialization, etcetera.
    /// </summary>
    public interface ITypeFilter
    {
        /// <summary>
        /// Determines whether the specified type is allowed to be serialized, deserialized, etcetera.
        /// </summary>
        /// <param name="type">The type</param>
        /// <returns><see langword="true" /> if the specified type is allowed; otherwise, <see langword="false" />.</returns>
        bool? IsTypeAllowed(Type type);
    }
}