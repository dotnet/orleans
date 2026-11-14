using System;
using System.Collections.Generic;
using System.Linq;
using Orleans.Serialization.Codecs;
using Orleans.Serialization.InterfaceCollectionSmoke;
using Xunit;

namespace Orleans.Serialization.UnitTests;

[Trait("Category", "BVT")]
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Serialization")]
public class InterfaceCollectionCodecResolverTests
{
    public static IEnumerable<object[]> SupportedTypes() =>
        InterfaceCollectionContracts.SupportedTypes.Select(pair => new object[] { pair.Codec, pair.Interface });

    public static IEnumerable<object?[]> UnsupportedTypes() =>
        InterfaceCollectionContracts.UnsupportedTypes.Select(type => new object?[] { type });

    [Theory]
    [MemberData(nameof(SupportedTypes))]
    public void ResolvesExistingClosedInterfaceMetadata(Type codecType, Type expectedInterface)
    {
        Assert.True(InterfaceCollectionCodecHelpers.TryGetInterfaceTypeForCodecType(codecType, out var interfaceType));
        Assert.Same(expectedInterface, interfaceType);
    }

    [Theory]
    [MemberData(nameof(UnsupportedTypes))]
    public void RejectsTypesOutsideRecognizedCodecDefinitions(Type? codecType)
    {
        Assert.False(InterfaceCollectionCodecHelpers.TryGetInterfaceTypeForCodecType(codecType, out var interfaceType));
        Assert.Null(interfaceType);
    }
}
