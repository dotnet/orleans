using System;
using System.Buffers.Text;
using System.Runtime.InteropServices;
using Orleans.Metadata;
using Orleans.Runtime;

namespace Orleans.Streams
{
    public class DefaultStreamIdMapper : IStreamIdMapper
    {
        public const string Name = "default";

        public IdSpan GetGrainKeyId(GrainBindings grainBindings, StreamId streamId)
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

            return keyType switch
            {
                nameof(Guid) => GetGuidKey(streamId, includeNamespaceInGrainId),
                nameof(Int64) => GetIntegerKey(streamId, includeNamespaceInGrainId),
                _ => GetKey(streamId), // null or string
            };
        }

        private static IdSpan GetGuidKey(StreamId streamId, bool includeNamespaceInGrainId)
        {
            var key = streamId.Key.Span;
            if (!Utf8Parser.TryParse(key, out Guid guidKey, out var len, 'N') || len < key.Length) throw new ArgumentException(nameof(streamId));

            return includeNamespaceInGrainId
                ? GrainIdKeyExtensions.CreateGuidKey(guidKey, streamId.GetNamespace())
                : GrainIdKeyExtensions.CreateGuidKey(guidKey);
        }

        private static IdSpan GetIntegerKey(StreamId streamId, bool includeNamespaceInGrainId)
        {
            var key = streamId.Key.Span;
            if (!Utf8Parser.TryParse(key, out int intKey, out var len) || len < key.Length) throw new ArgumentException(nameof(streamId));

            return includeNamespaceInGrainId
                ? GrainIdKeyExtensions.CreateIntegerKey(intKey, streamId.GetNamespace())
                : GrainIdKeyExtensions.CreateIntegerKey(intKey);
        }

        private static IdSpan GetKey(StreamId streamId)
        {
            var key = streamId.Key;
            return MemoryMarshal.TryGetArray(key, out var seg) && seg.Offset == 0 && seg.Count == seg.Array.Length
                ? new IdSpan(seg.Array)
                : new IdSpan(key.ToArray());
        }
    }
}
