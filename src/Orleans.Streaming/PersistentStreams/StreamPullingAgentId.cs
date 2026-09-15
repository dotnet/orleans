using System;
using System.Globalization;
using Orleans.Runtime;

namespace Orleans.Streams;

internal static class StreamPullingAgentId
{
    internal const string GrainTypeName = "Orleans.Streams.PullingAgent";
    internal static readonly GrainType GrainType = GrainType.Create(GrainTypeName);

    internal static GrainId Create(string providerName, QueueId queueId)
    {
        ArgumentException.ThrowIfNullOrEmpty(providerName);
        if (queueId.IsDefault)
        {
            throw new ArgumentException("A pulling agent requires an initialized queue identifier.", nameof(queueId));
        }

        var prefix = queueId.GetStringNamePrefix();
        return GrainId.Create(GrainType, string.Create(CultureInfo.InvariantCulture,
            $"{providerName.Length}:{providerName}{prefix.Length}:{prefix}{queueId.GetNumericId():X8}{queueId.GetUniformHashCode():X8}"));
    }

    internal static (string ProviderName, QueueId QueueId) Parse(GrainId grainId)
    {
        if (grainId.Type != GrainType)
        {
            throw new ArgumentException("Expected a stream pulling-agent grain identifier.", nameof(grainId));
        }

        var key = grainId.Key.ToString().AsSpan();
        var providerName = ReadString(ref key);
        var prefix = ReadString(ref key);
        if (providerName.Length == 0 || key.Length != 16)
        {
            throw new FormatException("Invalid stream pulling-agent grain key.");
        }

        var number = uint.Parse(key[..8], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        var hash = uint.Parse(key[8..], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        return (providerName, QueueId.GetQueueId(prefix, number, hash));
    }

    private static string ReadString(ref ReadOnlySpan<char> key)
    {
        var separator = key.IndexOf(':');
        if (separator <= 0
            || !int.TryParse(key[..separator], NumberStyles.None, CultureInfo.InvariantCulture, out var length)
            || length > key.Length - separator - 1)
        {
            throw new FormatException("Invalid stream pulling-agent grain key.");
        }

        key = key[(separator + 1)..];
        var value = key[..length].ToString();
        key = key[length..];
        return value;
    }
}
