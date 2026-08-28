using System.Text;
using Orleans.Runtime;
using Orleans.Storage;

namespace UnitTests.StorageTests.Relational;

[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Persistence")]
public sealed class Orleans3CompatibleHasherTests
{
    [Theory]
    [InlineData(0, "BD49D10D")]
    [InlineData(1, "6DDFB8C9")]
    [InlineData(2, "D1AF6F8A")]
    [InlineData(3, "C643A2B0")]
    [InlineData(4, "821CC2DB")]
    [InlineData(5, "641B59C9")]
    [InlineData(6, "E44BDCB2")]
    [InlineData(7, "D074CE9C")]
    [InlineData(8, "A491F494")]
    [InlineData(9, "9CAC434C")]
    [InlineData(10, "AD3B7804")]
    [InlineData(11, "F189C885")]
    [InlineData(12, "99BDD9EF")]
    [InlineData(13, "ECAD9B0D")]
    public void ComputeHash_ProducesOrleans3CompatibleVector(int length, string expectedHex)
    {
        var input = Enumerable.Range(0, length).Select(static value => (byte)value).ToArray();

        var actual = JenkinsHash.ComputeHash(input);

        Assert.Equal(expectedHex, actual.ToString("X8"));
    }

    [Fact]
    public void ComputeHash_ProducesIndependentMultiBlockVector()
    {
        var input = Enumerable.Range(0, 32).Select(static value => (byte)value).ToArray();

        var actual = JenkinsHash.ComputeHash(input);

        Assert.Equal(0x9E867842u, actual);
    }

    [Fact]
    public void Hash_ByteArrayAndSpanProduceSameValue()
    {
        var sut = new Orleans3CompatibleHasher();
        var input = Enumerable.Range(0, 13).Select(static value => (byte)value).ToArray();

        var arrayHash = sut.Hash(input);
        var spanHash = sut.Hash(input.AsSpan());

        Assert.Equal(unchecked((int)0xECAD9B0D), arrayHash);
        Assert.Equal(arrayHash, spanHash);
    }

    [Fact]
    public void Description_IdentifiesOrleans3Compatibility()
    {
        var sut = new Orleans3CompatibleHasher();

        Assert.Equal("Orleans v3 hash function (JenkinsHash).", sut.Description);
    }

    [Fact]
    public void HashProviders_ContainsSharedCompatibleHasher()
    {
        var sut = new Orleans3CompatibleStorageHashPicker();

        var hasher = Assert.Single(sut.HashProviders);

        Assert.IsType<Orleans3CompatibleHasher>(hasher);
        Assert.Equal("Orleans v3 hash function (JenkinsHash).", hasher.Description);
    }

    [Fact]
    public void PickHasher_ReturnsSharedHasher_ForIntegerAndGuidKeys()
    {
        var sut = new Orleans3CompatibleStorageHashPicker();
        var state = new GrainState<object> { State = new() };
        var integerId = GrainId.Create(
            GrainType.Create("integer-grain"),
            GrainIdKeyExtensions.CreateIntegerKey(0x1234_5678));
        var guidId = GrainId.Create(
            GrainType.Create("guid-grain"),
            GrainIdKeyExtensions.CreateGuidKey(Guid.Parse("751D8030-9C84-4A91-816E-E95F64CE7588")));

        var integerHasher = sut.PickHasher("service", "provider", "integer-grain", integerId, state);
        var guidHasher = sut.PickHasher("service", "provider", "guid-grain", guidId, state);

        Assert.Same(Assert.Single(sut.HashProviders), integerHasher);
        Assert.Same(integerHasher, guidHasher);
    }

    [Fact]
    public void PickHasher_ReturnsContentAwareHasher_ForStringKey()
    {
        var sut = new Orleans3CompatibleStorageHashPicker();
        var state = new GrainState<object> { State = new() };
        var grainId = GrainId.Create("customer-grain", "customer/key");

        var first = sut.PickHasher("service", "provider", "customer-grain", grainId, state);
        var second = sut.PickHasher("service", "provider", "customer-grain", grainId, state);

        Assert.IsType<Orleans3CompatibleStringKeyHasher>(first);
        Assert.NotSame(Assert.Single(sut.HashProviders), first);
        Assert.NotSame(first, second);
        Assert.Equal(unchecked((int)0xE82B5CEF), first.Hash(Encoding.UTF8.GetBytes("customer/key")));
    }

    [Fact]
    public void StringHasher_ExtendsKeyWithEightZeroBytes()
    {
        var sut = CreateStringHasher("Contoso.CustomerGrain");
        var key = Encoding.UTF8.GetBytes("customer-42");

        var actual = sut.Hash(key);

        Assert.Equal(unchecked((int)0xB49371FE), actual);
        Assert.NotEqual(unchecked((int)0x6F645A72), actual);
    }

    [Fact]
    public void StringHasher_DoesNotExtendGrainType()
    {
        const string GrainType = "Contoso.CustomerGrain";
        var sut = CreateStringHasher(GrainType);

        var actual = sut.Hash(Encoding.UTF8.GetBytes(GrainType));

        Assert.Equal(unchecked((int)0xFA04865B), actual);
        Assert.NotEqual(0x42D641BA, actual);
    }

    [Fact]
    public void StringHasher_UsesUtf8ForNonAsciiContent()
    {
        const string GrainType = "Gräin.東京";
        var sut = CreateStringHasher(GrainType);
        var utf8GrainType = Encoding.UTF8.GetBytes(GrainType);

        var actual = sut.Hash(utf8GrainType);

        Assert.Equal(13, utf8GrainType.Length);
        Assert.Equal(unchecked((int)0xE9A22DAF), actual);
        Assert.NotEqual(unchecked((int)0xC0B7345C), actual);
    }

    [Theory]
    [InlineData(248, 0x2DE3D3DD)]
    [InlineData(249, 0x113CC853)]
    public void StringHasher_HandlesStackAndPooledBufferBoundary(int keyLength, int expected)
    {
        var sut = CreateStringHasher("T");
        var key = Enumerable.Range(0, keyLength)
            .Select(static value => (byte)((value * 37 + 11) % 256))
            .ToArray();

        var actual = sut.Hash(key);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void StringHasher_PreservesDocumentedEqualContentAmbiguity()
    {
        var sut = CreateStringHasher("orders");
        var contentSharedByKeyAndType = Encoding.UTF8.GetBytes("orders");

        var actual = sut.Hash(contentSharedByKeyAndType);

        Assert.Equal(0x01F833FC, actual);
        Assert.NotEqual(0x27342C9B, actual);
    }

    private static Orleans3CompatibleStringKeyHasher CreateStringHasher(string grainType) =>
        new(new Orleans3CompatibleHasher(), grainType);
}
