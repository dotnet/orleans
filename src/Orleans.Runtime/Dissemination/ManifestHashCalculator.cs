using System;
using System.Buffers.Binary;
using System.Linq;
using System.Security.Cryptography;
using Orleans.Metadata;

namespace Orleans.Runtime.Dissemination;

internal static class ManifestHashCalculator
{
    private const int EncodingVersion = 1;

    public static ManifestHash ComputeHash(GrainManifest manifest)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AppendToken(hash, HashToken.Manifest);
        AppendInt32(hash, EncodingVersion);

        AppendToken(hash, HashToken.Grains);
        AppendInt32(hash, manifest.Grains.Count);
        foreach (var grain in manifest.Grains.OrderBy(static entry => entry.Key))
        {
            AppendToken(hash, HashToken.GrainEntry);
            AppendToken(hash, HashToken.Type);
            AppendBytes(hash, GrainType.UnsafeGetArray(grain.Key));
            AppendProperties(hash, grain.Value.Properties);
            AppendToken(hash, HashToken.EndEntry);
        }

        AppendToken(hash, HashToken.Interfaces);
        AppendInt32(hash, manifest.Interfaces.Count);
        foreach (var grainInterface in manifest.Interfaces.OrderBy(static entry => entry.Key.Value))
        {
            AppendToken(hash, HashToken.InterfaceEntry);
            AppendToken(hash, HashToken.Type);
            AppendBytes(hash, IdSpan.UnsafeGetArray(grainInterface.Key.Value));
            AppendProperties(hash, grainInterface.Value.Properties);
            AppendToken(hash, HashToken.EndEntry);
        }

        AppendToken(hash, HashToken.EndManifest);
        return new ManifestHash(Convert.ToHexString(hash.GetHashAndReset()));
    }

    private static void AppendProperties(IncrementalHash hash, System.Collections.Immutable.ImmutableDictionary<string, string> properties)
    {
        AppendToken(hash, HashToken.Properties);
        AppendInt32(hash, properties.Count);
        foreach (var property in properties.OrderBy(static entry => entry.Key, StringComparer.Ordinal))
        {
            AppendToken(hash, HashToken.PropertyEntry);
            AppendToken(hash, HashToken.Key);
            AppendString(hash, property.Key);
            AppendToken(hash, HashToken.Value);
            AppendString(hash, property.Value);
            AppendToken(hash, HashToken.EndEntry);
        }
    }

    private static void AppendToken(IncrementalHash hash, HashToken token)
    {
        hash.AppendData(stackalloc byte[] { (byte)token });
    }

    private static void AppendString(IncrementalHash hash, string value)
    {
        if (value is null)
        {
            AppendToken(hash, HashToken.NullString);
            return;
        }

        AppendToken(hash, HashToken.String);
        AppendInt32(hash, value.Length);
        Span<byte> character = stackalloc byte[sizeof(char)];
        foreach (var ch in value)
        {
            BinaryPrimitives.WriteUInt16BigEndian(character, ch);
            hash.AppendData(character);
        }
    }

    private static void AppendBytes(IncrementalHash hash, byte[]? value)
    {
        if (value is null)
        {
            AppendToken(hash, HashToken.NullBytes);
            return;
        }

        AppendToken(hash, HashToken.Bytes);
        AppendInt32(hash, value.Length);
        hash.AppendData(value);
    }

    private static void AppendInt32(IncrementalHash hash, int value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32BigEndian(bytes, value);
        hash.AppendData(bytes);
    }

    private enum HashToken : byte
    {
        Manifest = 1,
        Grains = 2,
        GrainEntry = 3,
        Interfaces = 4,
        InterfaceEntry = 5,
        Type = 6,
        Properties = 7,
        PropertyEntry = 8,
        Key = 9,
        Value = 10,
        EndEntry = 11,
        EndManifest = 12,
        NullString = 13,
        String = 14,
        NullBytes = 15,
        Bytes = 16,
    }
}
