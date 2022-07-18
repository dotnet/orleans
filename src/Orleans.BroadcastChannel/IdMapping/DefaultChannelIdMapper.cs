using System;
using System.Buffers.Text;
using Orleans.Metadata;
using Orleans.Runtime;

#nullable enable
namespace Orleans.BroadcastChannel
{
    /// <summary>
    /// The default <see cref="IChannelIdMapper"/> implementation.
    /// </summary>
    public sealed class DefaultChannelIdMapper : IChannelIdMapper
    {
        /// <summary>
        /// The name of this stream identity mapper.
        /// </summary>
        public const string Name = "default";

        /// <inheritdoc />
        public IdSpan GetGrainKeyId(GrainBindings grainBindings, ChannelId streamId)
        {
            string? keyType = null;
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
                _ => streamId.GetKeyIdSpan(), // null or string
            };
        }

        private static IdSpan GetGuidKey(ChannelId streamId, bool includeNamespaceInGrainId)
        {
            var key = streamId.Key.Span;
            if (!Utf8Parser.TryParse(key, out Guid guidKey, out var len, 'N') || len < key.Length) throw new ArgumentException(nameof(streamId));

            if (!includeNamespaceInGrainId)
                return streamId.GetKeyIdSpan();

            var ns = streamId.Namespace.Span;
            return ns.IsEmpty ? streamId.GetKeyIdSpan() : GrainIdKeyExtensions.CreateGuidKey(guidKey, ns);
        }

        private static IdSpan GetIntegerKey(ChannelId streamId, bool includeNamespaceInGrainId)
        {
            var key = streamId.Key.Span;
            if (!Utf8Parser.TryParse(key, out int intKey, out var len) || len < key.Length) throw new ArgumentException(nameof(streamId));

            return includeNamespaceInGrainId
                ? GrainIdKeyExtensions.CreateIntegerKey(intKey, streamId.Namespace.Span)
                : GrainIdKeyExtensions.CreateIntegerKey(intKey);
        }
    }
}
