using System;

namespace Orleans.Serialization.TypeSystem
{
    /// <summary>
    /// Provides methods for resolving a <see cref="Type"/> from a string.
    /// </summary>
    public abstract class TypeResolver
    {
        /// <summary>
        /// Returns the <see cref="Type"/> corresponding to the provided <paramref name="name"/>, throwing an exception if resolution fails.
        /// </summary>
        /// <param name="name">The type name.</param>
        /// <returns>The <see cref="Type"/> corresponding to the provided <paramref name="name"/>.</returns>
        public abstract Type ResolveType(string name);

        /// <summary>
        /// Resolves the <see cref="Type"/> corresponding to the provided <paramref name="name" />, returning true if resolution succeeded and false otherwise.
        /// </summary>
        /// <param name="name">The type name.</param>
        /// <param name="type">The resolved type.</param>
        /// <returns><see langword="true"/> if resolution succeeded; <see langword="false"/> otherwise.</returns>
        public abstract bool TryResolveType(string name, out Type type);
    }
}