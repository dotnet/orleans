using Microsoft.Extensions.Options;
using Orleans.Serialization.Configuration;
using System;
using System.Collections.Generic;

namespace Orleans.Serialization.Session
{
    public sealed class WellKnownTypeCollection
    {
        private readonly Dictionary<uint, Type> _wellKnownTypes;
        private readonly Dictionary<Type, uint> _wellKnownTypeToIdMap;

        public WellKnownTypeCollection(IOptions<TypeManifestOptions> config)
        {
            _wellKnownTypes = config?.Value.WellKnownTypeIds ?? throw new ArgumentNullException(nameof(config));
            _wellKnownTypeToIdMap = new Dictionary<Type, uint>();
            foreach (var item in _wellKnownTypes)
            {
                _wellKnownTypeToIdMap[item.Value] = item.Key;
            }
        }

        public Type GetWellKnownType(uint typeId)
        {
            if (typeId == 0)
            {
                return null;
            }

            return _wellKnownTypes[typeId];
        }

        public bool TryGetWellKnownType(uint typeId, out Type type)
        {
            if (typeId == 0)
            {
                type = null;
                return true;
            }

            return _wellKnownTypes.TryGetValue(typeId, out type);
        }

        public bool TryGetWellKnownTypeId(Type type, out uint typeId)
        {
            if (type is null)
            {
                typeId = 0;
                return true;
            }

            return _wellKnownTypeToIdMap.TryGetValue(type, out typeId);
        }
    }
}