using System;
using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using Orleans.Metadata;

namespace Orleans.Runtime.Metadata;

internal static class ManifestHashCalculator
{
    private const int EncodingVersion = 2;
    private static readonly ConditionalWeakTable<GrainManifest, StrongBox<ManifestHash>> Hashes = new();

    public static ManifestHash ComputeHash(GrainManifest manifest) =>
        Hashes.GetValue(manifest, static value => new(ComputeHashCore(value))).Value;

    private static ManifestHash ComputeHashCore(GrainManifest manifest)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AppendInt32(hash, EncodingVersion);

        // Fixed section/field order, collection counts, and value lengths delimit the canonical input.
        AppendInt32(hash, manifest.Grains.Count);
        foreach (var grain in manifest.Grains.OrderBy(static entry => entry.Key))
        {
            AppendIdentifier(hash, GrainType.UnsafeGetArray(grain.Key));
            AppendProperties(hash, grain.Value.Properties);
        }

        AppendInt32(hash, manifest.Interfaces.Count);
        foreach (var grainInterface in manifest.Interfaces.OrderBy(static entry => entry.Key.Value))
        {
            AppendIdentifier(hash, IdSpan.UnsafeGetArray(grainInterface.Key.Value));
            AppendProperties(hash, grainInterface.Value.Properties);
        }

        return new ManifestHash(Convert.ToHexString(hash.GetHashAndReset()));
    }

    private static void AppendProperties(IncrementalHash hash, ImmutableDictionary<string, string> properties)
    {
        AppendInt32(hash, properties.Count);
        foreach (var property in properties.OrderBy(static entry => entry.Key, StringComparer.Ordinal))
        {
            AppendString(hash, property.Key);
            AppendString(hash, property.Value);
        }
    }

    private static void AppendString(IncrementalHash hash, string? value)
    {
        AppendInt32(hash, value?.Length ?? -1);
        if (value is null)
        {
            return;
        }

        Span<byte> codeUnit = stackalloc byte[sizeof(char)];
        foreach (var character in value)
        {
            BinaryPrimitives.WriteUInt16BigEndian(codeUnit, character);
            hash.AppendData(codeUnit);
        }
    }

    private static void AppendIdentifier(IncrementalHash hash, byte[]? value)
    {
        AppendInt32(hash, value?.Length ?? -1);
        if (value is not null)
        {
            hash.AppendData(value);
        }
    }

    private static void AppendInt32(IncrementalHash hash, int value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32BigEndian(bytes, value);
        hash.AppendData(bytes);
    }
}
