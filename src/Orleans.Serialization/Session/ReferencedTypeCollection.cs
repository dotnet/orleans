using System;
using System.Collections.Generic;

namespace Orleans.Serialization.Session
{
    /// <summary>
    /// Collection of referenced <see cref="Type"/> instances.
    /// </summary>
    public sealed class ReferencedTypeCollection
    {
        private readonly Dictionary<uint, Type> _referencedTypes = new Dictionary<uint, Type>();
        private readonly Dictionary<Type, uint> _referencedTypeToIdMap = new Dictionary<Type, uint>();

        /// <summary>
        /// Gets the type with the specified reference id.
        /// </summary>
        /// <param name="reference">The reference id.</param>
        /// <returns>The referenced type.</returns>
        public Type GetReferencedType(uint reference) => _referencedTypes[reference];

        /// <summary>
        /// Gets the type with the specified reference id.
        /// </summary>
        /// <param name="reference">The reference id.</param>
        /// <param name="type">The referenced type.</param>
        /// <returns><see langword="true" /> if the referenced type was found, <see langword="false" /> otherwise.</returns>
        public bool TryGetReferencedType(uint reference, out Type type) => _referencedTypes.TryGetValue(reference, out type);

        /// <summary>
        /// Gets the identifier for the specified type.
        /// </summary>
        /// <param name="type">The type.</param>
        /// <param name="reference">The reference.</param>
        /// <returns><see langword="true" /> if the type has been previoulsy referenced, <see langword="false" /> otherwise.</returns>
        public bool TryGetTypeReference(Type type, out uint reference) => _referencedTypeToIdMap.TryGetValue(type, out reference);

        /// <summary>
        /// Resets this instance.
        /// </summary>
        public void Reset()
        {
            if (_referencedTypes.Count > 0)
            {
                _referencedTypes.Clear();
            }

            if (_referencedTypeToIdMap.Count > 0)
            {
                _referencedTypeToIdMap.Clear();
            }
        }
    }
}