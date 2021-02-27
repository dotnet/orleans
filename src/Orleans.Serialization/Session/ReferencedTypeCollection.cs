using System;
using System.Collections.Generic;

namespace Orleans.Serialization.Session
{
    public sealed class ReferencedTypeCollection
    {
        private readonly Dictionary<uint, Type> _referencedTypes = new Dictionary<uint, Type>();
        private readonly Dictionary<Type, uint> _referencedTypeToIdMap = new Dictionary<Type, uint>();

        public Type GetReferencedType(uint reference) => _referencedTypes[reference];
        public bool TryGetReferencedType(uint reference, out Type type) => _referencedTypes.TryGetValue(reference, out type);
        public bool TryGetTypeReference(Type type, out uint reference) => _referencedTypeToIdMap.TryGetValue(type, out reference);

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