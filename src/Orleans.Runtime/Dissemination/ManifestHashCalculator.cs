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
        AppendToken(hash, FrameToken.ManifestStart);
        AppendToken(hash, FrameToken.EncodingVersion);
        AppendInt32(hash, EncodingVersion);

        AppendCollectionStart(hash, FrameToken.GrainsStart, manifest.Grains.Count);
        foreach (var grain in manifest.Grains.OrderBy(static entry => entry.Key))
        {
            AppendToken(hash, FrameToken.GrainEntry);
            AppendToken(hash, FrameToken.IdentifierField);
            AppendIdentifier(hash, GrainType.UnsafeGetArray(grain.Key));
            AppendProperties(hash, grain.Value.Properties);
            AppendToken(hash, FrameToken.EndEntry);
        }

        AppendToken(hash, FrameToken.EndCollection);

        AppendCollectionStart(hash, FrameToken.InterfacesStart, manifest.Interfaces.Count);
        foreach (var grainInterface in manifest.Interfaces.OrderBy(static entry => entry.Key.Value))
        {
            AppendToken(hash, FrameToken.InterfaceEntry);
            AppendToken(hash, FrameToken.IdentifierField);
            AppendIdentifier(hash, IdSpan.UnsafeGetArray(grainInterface.Key.Value));
            AppendProperties(hash, grainInterface.Value.Properties);
            AppendToken(hash, FrameToken.EndEntry);
        }

        AppendToken(hash, FrameToken.EndCollection);
        AppendToken(hash, FrameToken.EndManifest);
        return new ManifestHash(Convert.ToHexString(hash.GetHashAndReset()));
    }

    private static void AppendProperties(IncrementalHash hash, System.Collections.Immutable.ImmutableDictionary<string, string> properties)
    {
        AppendCollectionStart(hash, FrameToken.PropertiesStart, properties.Count);
        foreach (var property in properties.OrderBy(static entry => entry.Key, StringComparer.Ordinal))
        {
            AppendToken(hash, FrameToken.PropertyEntry);
            AppendToken(hash, FrameToken.PropertyKeyField);
            AppendString(hash, property.Key);
            AppendToken(hash, FrameToken.PropertyValueField);
            AppendString(hash, property.Value);
            AppendToken(hash, FrameToken.EndEntry);
        }

        AppendToken(hash, FrameToken.EndCollection);
    }

    private static void AppendCollectionStart(IncrementalHash hash, FrameToken token, int count)
    {
        AppendToken(hash, token);
        AppendToken(hash, FrameToken.CollectionCount);
        AppendInt32(hash, count);
    }

    private static void AppendToken(IncrementalHash hash, FrameToken token) =>
        hash.AppendData(stackalloc byte[] { (byte)token });

    private static void AppendString(IncrementalHash hash, string? value)
    {
        if (value is null)
        {
            AppendToken(hash, FrameToken.NullString);
            return;
        }

        if (value.Length == 0)
        {
            AppendToken(hash, FrameToken.EmptyString);
            return;
        }

        AppendToken(hash, FrameToken.StringValue);
        AppendInt32(hash, value.Length);
        Span<byte> codeUnit = stackalloc byte[sizeof(char)];
        foreach (var character in value)
        {
            BinaryPrimitives.WriteUInt16BigEndian(codeUnit, character);
            hash.AppendData(codeUnit);
        }
    }

    private static void AppendIdentifier(IncrementalHash hash, byte[]? value)
    {
        if (value is null)
        {
            AppendToken(hash, FrameToken.DefaultIdentifier);
            return;
        }

        if (value.Length == 0)
        {
            AppendToken(hash, FrameToken.EmptyIdentifier);
            return;
        }

        AppendToken(hash, FrameToken.IdentifierValue);
        AppendInt32(hash, value.Length);
        hash.AppendData(value);
    }

    private static void AppendInt32(IncrementalHash hash, int value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32BigEndian(bytes, value);
        hash.AppendData(bytes);
    }

    private enum FrameToken : byte
    {
        ManifestStart = 1,
        EncodingVersion = 2,
        GrainsStart = 3,
        InterfacesStart = 4,
        PropertiesStart = 5,
        GrainEntry = 6,
        InterfaceEntry = 7,
        PropertyEntry = 8,
        IdentifierField = 9,
        PropertyKeyField = 10,
        PropertyValueField = 11,
        CollectionCount = 12,
        EndCollection = 13,
        EndEntry = 14,
        EndManifest = 15,
        NullString = 16,
        EmptyString = 17,
        StringValue = 18,
        DefaultIdentifier = 19,
        EmptyIdentifier = 20,
        IdentifierValue = 21,
    }
}
