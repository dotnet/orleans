using System;

namespace Orleans.Runtime
{
    /// <summary>
    /// Provides methods for resolving a <see cref="Type"/> from a string.
    /// </summary>
    public interface ITypeResolver
    {
        /// <summary>
        /// Returns the <see cref="Type"/> corresponding to the provided <paramref name="name"/>, throwing an exception if resolution fails.
        /// </summary>
        /// <param name="name">The type name.</param>
        /// <returns>The <see cref="Type"/> corresponding to the provided <paramref name="name"/>.</returns>
        Type ResolveType(string name);

        /// <summary>
        /// Resolves the <see cref="Type"/> corresponding to the provided <paramref name="name" />, returning true if resolution succeeded and false otherwise.
        /// </summary>
        /// <param name="name">The type name.</param>
        /// <param name="type">The resolved type.</param>
        /// <returns>true if resolution succeeded and false otherwise.</returns>
        bool TryResolveType(string name, out Type type);
    }
}