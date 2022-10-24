using Microsoft.Extensions.Options;
using Orleans.Serialization.Configuration;
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

namespace Orleans.Serialization.Session
{
    /// <summary>
    /// Collection of well-known types.
    /// </summary>
    public sealed class WellKnownTypeCollection
    {
        private readonly Dictionary<uint, Type> _wellKnownTypes;
        private readonly Dictionary<Type, uint> _wellKnownTypeToIdMap;

        /// <summary>
        /// Initializes a new instance of the <see cref="WellKnownTypeCollection"/> class.
        /// </summary>
        /// <param name="config">The configuration.</param>
        public WellKnownTypeCollection(IOptions<TypeManifestOptions> config)
        {
            _wellKnownTypes = config?.Value.WellKnownTypeIds ?? throw new ArgumentNullException(nameof(config));
            _wellKnownTypeToIdMap = new Dictionary<Type, uint>(_wellKnownTypes.Count);
            foreach (var item in _wellKnownTypes)
            {
                _wellKnownTypeToIdMap[item.Value] = item.Key;
            }
        }

        /// <summary>
        /// Gets the type corresponding to the provided type identifier.
        /// </summary>
        /// <param name="typeId">The type identifier.</param>
        /// <returns>A type.</returns>
        public Type GetWellKnownType(uint typeId)
        {
            if (typeId == 0)
            {
                return null;
            }

            return _wellKnownTypes[typeId];
        }

        /// <summary>
        /// Tries to get the type corresponding to the provided type identifier.
        /// </summary>
        /// <param name="typeId">The type identifier.</param>
        /// <param name="type">The type.</param>
        /// <returns><see langword="true" /> if the corresponding type was found, <see langword="false" /> otherwise.</returns>
        public bool TryGetWellKnownType(uint typeId, [NotNullWhen(true)] out Type type)
        {
            if (typeId == 0)
            {
                type = null;
                return true;
            }

            return _wellKnownTypes.TryGetValue(typeId, out type);
        }

        /// <summary>
        /// Tries the get the type identifier corresponding to the provided type.
        /// </summary>
        /// <param name="type">The type.</param>
        /// <param name="typeId">The type identifier.</param>
        /// <returns><see langword="true" /> if the type has a well-known identifier, <see langword="false" /> otherwise.</returns>
        public bool TryGetWellKnownTypeId(Type type, out uint typeId) => _wellKnownTypeToIdMap.TryGetValue(type, out typeId);
    }
}