using System;
using System.Buffers.Text;
using System.Runtime.InteropServices;
using Orleans.Metadata;
using Orleans.Runtime;

namespace Orleans.BroadcastChannel
{
    /// <summary>
    /// The default <see cref="IChannelIdMapper"/> implementation.
    /// </summary>
    public class DefaultChannelIdMapper : IChannelIdMapper
    {
        /// <summary>
        /// The name of this stream identity mapper.
        /// </summary>
        public const string Name = "default";

        /// <inheritdoc />
        public IdSpan GetGrainKeyId(GrainBindings grainBindings, ChannelId streamId)
        {
            string keyType = null;
            bool includeNamespaceInGrainId = false;

            foreach (var grainBinding in grainBindings.Bindings)
            {
                if (!grainBinding.TryGetValue(WellKnownGrainTypeProperties.BindingTypeKey, out var type)
                        || !string.Equals(type, WellKnownGrainTypeProperties.BroadcastChannelBindingTypeValue, StringComparison.Ordinal))
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

        private static IdSpan GetGuidKey(ChannelId streamId, bool includeNamespaceInGrainId)
        {
            var key = streamId.Key.Span;
            if (!Utf8Parser.TryParse(key, out Guid guidKey, out var len, 'N') || len < key.Length) throw new ArgumentException(nameof(streamId));

            return includeNamespaceInGrainId
                ? GrainIdKeyExtensions.CreateGuidKey(guidKey, streamId.GetNamespace())
                : GrainIdKeyExtensions.CreateGuidKey(guidKey);
        }

        private static IdSpan GetIntegerKey(ChannelId streamId, bool includeNamespaceInGrainId)
        {
            var key = streamId.Key.Span;
            if (!Utf8Parser.TryParse(key, out int intKey, out var len) || len < key.Length) throw new ArgumentException(nameof(streamId));

            return includeNamespaceInGrainId
                ? GrainIdKeyExtensions.CreateIntegerKey(intKey, streamId.GetNamespace())
                : GrainIdKeyExtensions.CreateIntegerKey(intKey);
        }

        private static IdSpan GetKey(ChannelId streamId)
        {
            var key = streamId.Key;
            return MemoryMarshal.TryGetArray(key, out var seg) && seg.Offset == 0 && seg.Count == seg.Array.Length
                ? new IdSpan(seg.Array)
                : new IdSpan(key.ToArray());
        }
    }
}
