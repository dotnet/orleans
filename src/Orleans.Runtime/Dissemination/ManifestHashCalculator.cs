using System;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Orleans.Metadata;

namespace Orleans.Runtime.Dissemination;

internal static class ManifestHashCalculator
{
    public static ManifestHash ComputeHash(GrainManifest manifest)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AppendSection(hash, "grains", manifest.Grains.Count);
        var grains = manifest.Grains.ToArray();
        Array.Sort(grains, static (left, right) => left.Key.AsSpan().SequenceCompareTo(right.Key.AsSpan()));
        foreach (var grain in grains)
        {
            AppendString(hash, "grain");
            AppendBytes(hash, grain.Key.AsSpan());
            AppendProperties(hash, grain.Value.Properties);
        }

        AppendSection(hash, "interfaces", manifest.Interfaces.Count);
        var interfaces = manifest.Interfaces.ToArray();
        Array.Sort(
            interfaces,
            static (left, right) => left.Key.Value.AsSpan().SequenceCompareTo(right.Key.Value.AsSpan()));
        foreach (var grainInterface in interfaces)
        {
            AppendString(hash, "interface");
            AppendBytes(hash, grainInterface.Key.Value.AsSpan());
            AppendProperties(hash, grainInterface.Value.Properties);
        }

        return new ManifestHash(Convert.ToHexString(hash.GetHashAndReset()));
    }

    private static void AppendProperties(IncrementalHash hash, System.Collections.Immutable.ImmutableDictionary<string, string> properties)
    {
        AppendInt32(hash, properties.Count);
        foreach (var property in properties.OrderBy(static entry => entry.Key, StringComparer.Ordinal))
        {
            AppendString(hash, "property");
            AppendString(hash, property.Key);
            AppendString(hash, property.Value);
        }
    }

    private static void AppendSection(IncrementalHash hash, string section, int count)
    {
        AppendString(hash, section);
        AppendInt32(hash, count);
    }

    private static void AppendString(IncrementalHash hash, string? value)
    {
        if (value is null)
        {
            AppendInt32(hash, -1);
            return;
        }

        var bytes = Encoding.UTF8.GetBytes(value);
        AppendBytes(hash, bytes);
    }

    private static void AppendBytes(IncrementalHash hash, ReadOnlySpan<byte> value)
    {
        AppendInt32(hash, value.Length);
        hash.AppendData(value);
    }

    private static void AppendInt32(IncrementalHash hash, int value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(int)];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
        hash.AppendData(bytes);
    }
}
