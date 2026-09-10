using System;
using System.Collections.Immutable;
using System.Linq;
using Orleans.Metadata;
using Orleans.Runtime;
using Orleans.Runtime.Metadata;
using Xunit;

namespace UnitTests.Manifest;

[TestSuite("BVT"), TestProvider("None")]
[TestCategory("BVT"), TestCategory("Manifest")]
public sealed class ManifestHashCalculatorTests
{
    [Fact]
    public void ManifestHashIsIndependentOfDictionaryOrdering()
    {
        var manifest1 = CreateManifest(
            ("grain-b", "placement", "random"),
            ("grain-a", "placement", "local"));
        var manifest2 = CreateManifest(
            ("grain-a", "placement", "local"),
            ("grain-b", "placement", "random"));

        Assert.Equal(ManifestHashCalculator.ComputeHash(manifest1), ManifestHashCalculator.ComputeHash(manifest2));
    }

    [Fact]
    public void ManifestHashIncludesEntryBoundaries()
    {
        var manifest1 = CreateManifestWithProperties(
            ("a", [("b", "c")]),
            ("d", [("e", "f"), ("g", "h")]));
        var manifest2 = CreateManifestWithProperties(
            ("a", [("b", "c"), ("d", "e")]),
            ("f", [("g", "h")]));

        Assert.NotEqual(ManifestHashCalculator.ComputeHash(manifest1), ManifestHashCalculator.ComputeHash(manifest2));
    }

    [Fact]
    public void ManifestHashUsesRawTypeIdentifierBytes()
    {
        var properties = new GrainProperties(
            ImmutableDictionary<string, string>.Empty
                .WithComparers(StringComparer.Ordinal));
        var first = new GrainManifest(
            ImmutableDictionary<GrainType, GrainProperties>.Empty
                .Add(new GrainType([0x80]), properties),
            ImmutableDictionary<GrainInterfaceType, GrainInterfaceProperties>.Empty);
        var second = new GrainManifest(
            ImmutableDictionary<GrainType, GrainProperties>.Empty
                .Add(new GrainType([0x81]), properties),
            ImmutableDictionary<GrainInterfaceType, GrainInterfaceProperties>.Empty);

        Assert.NotEqual(ManifestHashCalculator.ComputeHash(first), ManifestHashCalculator.ComputeHash(second));
    }

    [Fact]
    public void ManifestHashDistinguishesNullAndEmptyPropertyValues()
    {
        var nullProperties = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);
        nullProperties["value"] = null!;
        var emptyProperties = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);
        emptyProperties["value"] = string.Empty;
        var nullManifest = new GrainManifest(
            ImmutableDictionary<GrainType, GrainProperties>.Empty
                .Add(GrainType.Create("grain"), new GrainProperties(nullProperties.ToImmutable())),
            ImmutableDictionary<GrainInterfaceType, GrainInterfaceProperties>.Empty);
        var emptyManifest = new GrainManifest(
            ImmutableDictionary<GrainType, GrainProperties>.Empty
                .Add(GrainType.Create("grain"), new GrainProperties(emptyProperties.ToImmutable())),
            ImmutableDictionary<GrainInterfaceType, GrainInterfaceProperties>.Empty);

        Assert.NotEqual(
            ManifestHashCalculator.ComputeHash(nullManifest),
            ManifestHashCalculator.ComputeHash(emptyManifest));
    }

    [Fact]
    public void ManifestHashIncludesCanonicalFramingVersion()
    {
        var manifest = CreatePhaseOneGrainManifest(
            new GrainType([0x80, 0x00]),
            ("k", "\uD800"));

        var actual = ManifestHashCalculator.ComputeHash(manifest);

        Assert.Equal("96E0D7D5A908928A474E3E484D716E594C2C53E918F41C939B733A1639F0284D", actual.Value);

        // This is the complete frame for the fixed manifest above with only its version changed from 1 to 2.
        byte[] versionTwoFrame =
        [
            1, 2, 0, 0, 0, 2,
            3, 12, 0, 0, 0, 1,
            6, 9, 21, 0, 0, 0, 2, 0x80, 0x00,
            5, 12, 0, 0, 0, 1,
            8, 10, 18, 0, 0, 0, 1, 0, 0x6B,
            11, 18, 0, 0, 0, 1, 0xD8, 0,
            14, 13, 14, 13,
            4, 12, 0, 0, 0, 0, 13, 15,
        ];
        var versionTwoHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(versionTwoFrame));

        Assert.Equal("853EB4C529561E20463769249D312D2F0D08B0257E178229B3F996960A8C89D6", versionTwoHash);
        Assert.NotEqual(actual.Value, versionTwoHash);
    }

    [Fact]
    public void ManifestHashDistinguishesNestedStructureBoundaries()
    {
        var first = CreatePhaseOneGrainManifest(
            (new GrainType([0x61]), [("b", "c")]),
            (new GrainType([0x64]), [("e", "f"), ("g", "h")]));
        var second = CreatePhaseOneGrainManifest(
            (new GrainType([0x61]), [("b", "c"), ("d", "e")]),
            (new GrainType([0x66]), [("g", "h")]));

        var firstHash = ManifestHashCalculator.ComputeHash(first);
        var secondHash = ManifestHashCalculator.ComputeHash(second);

        Assert.NotEqual(firstHash, secondHash);
        Assert.Equal(firstHash, ManifestHashCalculator.ComputeHash(first));
        Assert.Equal(secondHash, ManifestHashCalculator.ComputeHash(second));
    }

    [Fact]
    public void ManifestHashDistinguishesStructuralTokenTypes()
    {
        byte[] identifier = [0x78, 0x00, 0x80];
        var grainManifest = CreatePhaseOneGrainManifest(
            new GrainType(identifier),
            ("field", "value"));
        var interfaceManifest = CreatePhaseOneInterfaceManifest(
            new GrainInterfaceType(new IdSpan(identifier)),
            ("field", "value"));

        var grainHash = ManifestHashCalculator.ComputeHash(grainManifest);
        var interfaceHash = ManifestHashCalculator.ComputeHash(interfaceManifest);

        Assert.NotEqual(grainHash, interfaceHash);
        Assert.Equal(grainHash, ManifestHashCalculator.ComputeHash(grainManifest));
        Assert.Equal(interfaceHash, ManifestHashCalculator.ComputeHash(interfaceManifest));
    }

    [Fact]
    public void ManifestHashPreservesInvalidUtf16CodeUnits()
    {
        var first = CreatePhaseOneGrainManifest(
            GrainType.Create("invalid-utf16"),
            ("value", "\uD800x\uDC00"));
        var second = CreatePhaseOneGrainManifest(
            GrainType.Create("invalid-utf16"),
            ("value", "\uD801x\uDC01"));

        var firstHash = ManifestHashCalculator.ComputeHash(first);
        var secondHash = ManifestHashCalculator.ComputeHash(second);

        Assert.NotEqual(firstHash, secondHash);
        Assert.Equal(firstHash, ManifestHashCalculator.ComputeHash(first));
        Assert.Equal(secondHash, ManifestHashCalculator.ComputeHash(second));
    }

    [Fact]
    public void ManifestHashUsesArbitraryRawGrainTypeIdentifierBytes()
    {
        var first = CreatePhaseOneGrainManifest(
            new GrainType([0x80, 0x00, 0xFE]),
            ("key", "value"));
        var second = CreatePhaseOneGrainManifest(
            new GrainType([0x80, 0x00, 0xFF]),
            ("key", "value"));

        var firstHash = ManifestHashCalculator.ComputeHash(first);
        var secondHash = ManifestHashCalculator.ComputeHash(second);

        Assert.NotEqual(firstHash, secondHash);
        Assert.Equal(firstHash, ManifestHashCalculator.ComputeHash(first));
    }

    [Fact]
    public void ManifestHashUsesArbitraryRawInterfaceTypeIdentifierBytes()
    {
        var first = CreatePhaseOneInterfaceManifest(
            new GrainInterfaceType(new IdSpan([0x80, 0x00, 0xFE])),
            ("key", "value"));
        var second = CreatePhaseOneInterfaceManifest(
            new GrainInterfaceType(new IdSpan([0x80, 0x00, 0xFF])),
            ("key", "value"));

        var firstHash = ManifestHashCalculator.ComputeHash(first);
        var secondHash = ManifestHashCalculator.ComputeHash(second);

        Assert.NotEqual(firstHash, secondHash);
        Assert.Equal(secondHash, ManifestHashCalculator.ComputeHash(second));
    }

    [Fact]
    public void ManifestHashDistinguishesNullAndEmptyStrings()
    {
        var nullManifest = CreatePhaseOneGrainManifest(
            GrainType.Create("null-string"),
            ("value", null));
        var emptyManifest = CreatePhaseOneGrainManifest(
            GrainType.Create("null-string"),
            ("value", string.Empty));

        var nullHash = ManifestHashCalculator.ComputeHash(nullManifest);
        var emptyHash = ManifestHashCalculator.ComputeHash(emptyManifest);

        Assert.NotEqual(nullHash, emptyHash);
        Assert.Equal(nullHash, ManifestHashCalculator.ComputeHash(nullManifest));
        Assert.Equal(emptyHash, ManifestHashCalculator.ComputeHash(emptyManifest));
    }

    [Fact]
    public void ManifestHashDistinguishesDefaultAndEmptyGrainTypeIdentifiers()
    {
        var defaultManifest = CreatePhaseOneGrainManifest(default, ("key", "value"));
        var emptyManifest = CreatePhaseOneGrainManifest(new GrainType([]), ("key", "value"));

        var defaultHash = ManifestHashCalculator.ComputeHash(defaultManifest);
        var emptyHash = ManifestHashCalculator.ComputeHash(emptyManifest);

        Assert.NotEqual(defaultHash, emptyHash);
        Assert.Equal(defaultHash, ManifestHashCalculator.ComputeHash(defaultManifest));
        Assert.Equal(emptyHash, ManifestHashCalculator.ComputeHash(emptyManifest));
    }

    [Fact]
    public void ManifestHashDistinguishesDefaultAndEmptyInterfaceTypeIdentifiers()
    {
        var defaultManifest = CreatePhaseOneInterfaceManifest(default, ("key", "value"));
        var emptyManifest = CreatePhaseOneInterfaceManifest(
            new GrainInterfaceType(new IdSpan([])),
            ("key", "value"));

        var defaultHash = ManifestHashCalculator.ComputeHash(defaultManifest);
        var emptyHash = ManifestHashCalculator.ComputeHash(emptyManifest);

        Assert.NotEqual(defaultHash, emptyHash);
        Assert.Equal(defaultHash, ManifestHashCalculator.ComputeHash(defaultManifest));
        Assert.Equal(emptyHash, ManifestHashCalculator.ComputeHash(emptyManifest));
    }

    [Fact]
    public void ManifestHashIsStableAcrossEquivalentInsertionOrders()
    {
        var first = CreatePhaseOneOrderedManifest(reverse: false);
        var second = CreatePhaseOneOrderedManifest(reverse: true);

        var firstHash = ManifestHashCalculator.ComputeHash(first);
        var secondHash = ManifestHashCalculator.ComputeHash(second);

        Assert.Equal(firstHash, secondHash);
        Assert.Equal(firstHash, ManifestHashCalculator.ComputeHash(first));
        Assert.Equal(secondHash, ManifestHashCalculator.ComputeHash(second));
    }

    private static GrainManifest CreateManifest(params (string Grain, string Key, string Value)[] grains)
    {
        var grainBuilder = ImmutableDictionary.CreateBuilder<GrainType, GrainProperties>();
        foreach (var grain in grains)
        {
            var properties = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);
            properties[grain.Key] = grain.Value;
            grainBuilder[GrainType.Create(grain.Grain)] = new GrainProperties(
                properties.ToImmutable());
        }

        return new GrainManifest(
            grainBuilder.ToImmutable(),
            ImmutableDictionary<GrainInterfaceType, GrainInterfaceProperties>.Empty);
    }

    private static GrainManifest CreateManifestWithProperties(params (string Grain, (string Key, string Value)[] Properties)[] grains)
    {
        var grainBuilder = ImmutableDictionary.CreateBuilder<GrainType, GrainProperties>();
        foreach (var grain in grains)
        {
            var properties = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);
            foreach (var property in grain.Properties)
            {
                properties[property.Key] = property.Value;
            }

            grainBuilder[GrainType.Create(grain.Grain)] = new GrainProperties(properties.ToImmutable());
        }

        return new GrainManifest(
            grainBuilder.ToImmutable(),
            ImmutableDictionary<GrainInterfaceType, GrainInterfaceProperties>.Empty);
    }

    private static GrainManifest CreatePhaseOneGrainManifest(
        GrainType grainType,
        params (string Key, string? Value)[] properties) =>
        CreatePhaseOneGrainManifest((grainType, properties));

    private static GrainManifest CreatePhaseOneGrainManifest(
        params (GrainType GrainType, (string Key, string? Value)[] Properties)[] grains)
    {
        var builder = ImmutableDictionary.CreateBuilder<GrainType, GrainProperties>();
        foreach (var grain in grains)
        {
            builder.Add(grain.GrainType, new GrainProperties(CreatePhaseOneProperties(grain.Properties)));
        }

        return new GrainManifest(
            builder.ToImmutable(),
            ImmutableDictionary<GrainInterfaceType, GrainInterfaceProperties>.Empty);
    }

    private static GrainManifest CreatePhaseOneInterfaceManifest(
        GrainInterfaceType interfaceType,
        params (string Key, string? Value)[] properties)
    {
        var interfaces = ImmutableDictionary<GrainInterfaceType, GrainInterfaceProperties>.Empty
            .Add(interfaceType, new GrainInterfaceProperties(CreatePhaseOneProperties(properties)));
        return new GrainManifest(
            ImmutableDictionary<GrainType, GrainProperties>.Empty,
            interfaces);
    }

    private static ImmutableDictionary<string, string> CreatePhaseOneProperties(
        params (string Key, string? Value)[] properties)
    {
        var builder = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);
        foreach (var property in properties)
        {
            builder.Add(property.Key, property.Value!);
        }

        return builder.ToImmutable();
    }

    private static GrainManifest CreatePhaseOneOrderedManifest(bool reverse)
    {
        var grainEntries = new[]
        {
            (GrainType.Create("grain-a"), CreatePhaseOneProperties(("z", "last"), ("a", "first"))),
            (GrainType.Create("grain-b"), CreatePhaseOneProperties(("version", "2"))),
        };
        var interfaceEntries = new[]
        {
            (GrainInterfaceType.Create("interface-a"), CreatePhaseOneProperties(("z", "last"), ("a", "first"))),
            (GrainInterfaceType.Create("interface-b"), CreatePhaseOneProperties(("version", "2"))),
        };
        var grains = ImmutableDictionary.CreateBuilder<GrainType, GrainProperties>();
        var interfaces = ImmutableDictionary.CreateBuilder<GrainInterfaceType, GrainInterfaceProperties>();

        foreach (var (grainType, properties) in reverse ? grainEntries.AsEnumerable().Reverse() : grainEntries)
        {
            var entries = reverse ? properties.Reverse() : properties;
            grains.Add(grainType, new GrainProperties(entries.ToImmutableDictionary(StringComparer.Ordinal)));
        }

        foreach (var (interfaceType, properties) in reverse ? interfaceEntries.AsEnumerable().Reverse() : interfaceEntries)
        {
            var entries = reverse ? properties.Reverse() : properties;
            interfaces.Add(interfaceType, new GrainInterfaceProperties(entries.ToImmutableDictionary(StringComparer.Ordinal)));
        }

        return new GrainManifest(grains.ToImmutable(), interfaces.ToImmutable());
    }
}
