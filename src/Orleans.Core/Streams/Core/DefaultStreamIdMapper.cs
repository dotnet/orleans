using System;
using Orleans.Metadata;
using Orleans.Runtime;
using Orleans.Utilities;

namespace Orleans.Streams
{
    public class DefaultStreamIdMapper : IStreamIdMapper
    {
        private CachedReadConcurrentDictionary<GrainBindings, Func<StreamId, IdSpan>> mappers = new CachedReadConcurrentDictionary<GrainBindings, Func<StreamId, IdSpan>>();

        public GrainId GetGrainId(GrainBindings grainBindings, StreamId streamId)
        {
            var func = this.mappers.GetOrAdd(grainBindings, MapperFactory);
            return GrainId.Create(grainBindings.GrainType, func(streamId));
        }

        private Func<StreamId, IdSpan> MapperFactory(GrainBindings grainBindings)
        {
            string keyType = null;
            bool includeNamespaceInGrainId = false;

            foreach (var grainBinding in grainBindings.Bindings)
            {
                if (!grainBinding.TryGetValue(WellKnownGrainTypeProperties.BindingTypeKey, out var type)
                        || !string.Equals(type, WellKnownGrainTypeProperties.StreamBindingTypeValue, StringComparison.Ordinal))
                {
                    continue;
                }

                if (grainBinding.TryGetValue(WellKnownGrainTypeProperties.LegacyGrainKeyType, out keyType))
                {
                    if (grainBinding.TryGetValue(WellKnownGrainTypeProperties.StreamBindingIncludeNamespaceKey, out var value)
                        && string.Equals(value, "true", StringComparison.OrdinalIgnoreCase))
                    {
                        includeNamespaceInGrainId = true;
                    }
                }
            }

            switch (keyType)
            {
                case nameof(Guid):
                    if (includeNamespaceInGrainId)
                        return GetGuidCompoundKey;
                    else
                        return GetGuidKey;
                case nameof(Int32):
                    if (includeNamespaceInGrainId)
                        return GetIntegerCompoundKey;
                    else
                        return GetIntegerKey;
                default: // null or string
                    return GetKey;
            }
        }

        private static IdSpan GetGuidKey(StreamId streamId)
        {
            var guidKey = Guid.Parse(streamId.GetKeyAsString());
            return GrainIdKeyExtensions.CreateGuidKey(guidKey);
        }

        private static IdSpan GetGuidCompoundKey(StreamId streamId)
        {
            var guidKey = Guid.Parse(streamId.GetKeyAsString());
            return GrainIdKeyExtensions.CreateGuidKey(guidKey, streamId.GetNamespace());
        }

        private static IdSpan GetIntegerKey(StreamId streamId)
        {
            var intKey = int.Parse(streamId.GetKeyAsString());
            return GrainIdKeyExtensions.CreateIntegerKey(intKey);
        }

        private static IdSpan GetIntegerCompoundKey(StreamId streamId)
        {
            var intKey = int.Parse(streamId.GetKeyAsString());
            return GrainIdKeyExtensions.CreateIntegerKey(intKey, streamId.GetNamespace());
        }

        private static IdSpan GetKey(StreamId streamId) => new IdSpan(streamId.Key.ToArray());
    }
}
