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
        private volatile Dictionary<uint, Type> _wellKnownTypes = new();
        private volatile Dictionary<Type, uint> _wellKnownTypeToIdMap = new();

        /// <summary>
        /// Initializes a new instance of the <see cref="WellKnownTypeCollection"/> class.
        /// </summary>
        /// <param name="config">The configuration.</param>
        public WellKnownTypeCollection(IOptions<TypeManifestOptions> config)
        {
            if (config is null)
            {
                throw new ArgumentNullException(nameof(config));
            }

            Rebuild(config.Value.WellKnownTypeIds);
        }

        /// <summary>
        /// Refreshes the type id maps after a hot reload metadata update.
        /// </summary>
        internal void OnManifestUpdated(TypeManifestOptions options) => Rebuild(options.WellKnownTypeIds);

        private void Rebuild(Dictionary<uint, Type> wellKnownTypeIds)
        {
            // Snapshot the id map rather than aliasing the live options dictionary so that later manifest
            // merges cannot race lock-free readers of this collection.
            var wellKnownTypes = new Dictionary<uint, Type>(wellKnownTypeIds);
            var wellKnownTypeToIdMap = new Dictionary<Type, uint>(wellKnownTypes.Count);
            foreach (var item in wellKnownTypes)
            {
                wellKnownTypeToIdMap[item.Value] = item.Key;
            }

            _wellKnownTypes = wellKnownTypes;
            _wellKnownTypeToIdMap = wellKnownTypeToIdMap;
        }

        /// <summary>
        /// Gets the type corresponding to the provided type identifier.
        /// </summary>
        /// <param name="typeId">The type identifier.</param>
        /// <returns>A type.</returns>
        public Type? GetWellKnownType(uint typeId)
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
        public bool TryGetWellKnownType(uint typeId, out Type? type)
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
