using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Orleans.Serialization.Session
{
    /// <summary>
    /// Collection of referenced <see cref="Type"/> instances.
    /// </summary>
    public sealed class ReferencedTypeCollection
    {
        private readonly Dictionary<uint, Type> _referencedTypes = new();
        private readonly Dictionary<Type, uint> _referencedTypeToIdMap = new();

        private uint _currentReferenceId;

        /// <summary>
        /// Gets the type with the specified reference id.
        /// </summary>
        /// <param name="reference">The reference id.</param>
        /// <returns>The referenced type.</returns>
        public Type GetReferencedType(uint reference)
        {
            if (!_referencedTypes.TryGetValue(reference, out var type))
                ThrowUnknownReferencedType(reference);
            return type;
        }

        private static void ThrowUnknownReferencedType(uint id) => throw new UnknownReferencedTypeException(id);

        /// <summary>
        /// Gets the type with the specified reference id.
        /// </summary>
        /// <param name="reference">The reference id.</param>
        /// <param name="type">The referenced type.</param>
        /// <returns><see langword="true" /> if the referenced type was found, <see langword="false" /> otherwise.</returns>
        public bool TryGetReferencedType(uint reference, out Type type) => _referencedTypes.TryGetValue(reference, out type);

        /// <summary>
        /// Records a type with the specified identifier.
        /// </summary>
        public void RecordReferencedType(Type type) => _referencedTypes.Add(++_currentReferenceId, type);

        /// <summary>
        /// Gets the identifier for the specified type.
        /// </summary>
        /// <param name="type">The type.</param>
        /// <param name="reference">The reference.</param>
        /// <returns><see langword="true" /> if the type has been previoulsy referenced, <see langword="false" /> otherwise.</returns>
        public bool TryGetTypeReference(Type type, out uint reference) => _referencedTypeToIdMap.TryGetValue(type, out reference);

        /// <summary>
        /// Gets or adds the identifier for the specified type.
        /// </summary>
        public bool GetOrAddTypeReference(Type type, out uint reference)
        {
            ref var refValue = ref CollectionsMarshal.GetValueRefOrAddDefault(_referencedTypeToIdMap, type, out var exists);
            if (exists)
            {
                reference = refValue;
                return true;
            }

            refValue = reference = ++_currentReferenceId;
            return false;
        }

        /// <summary>
        /// Resets this instance.
        /// </summary>
        public void Reset()
        {
            _currentReferenceId = 0;
            _referencedTypes.Clear();
            _referencedTypeToIdMap.Clear();
        }
    }
}