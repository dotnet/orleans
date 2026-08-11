#if NET6_0_OR_GREATER
using System;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Providers.Streams.Common;
using Orleans.Serialization;
using Orleans.Serialization.Cloning;
using Orleans.Serialization.Codecs;
using Orleans.Serialization.Serializers;
using Orleans.Streams;
using UnitTests.SerializerExternalModels;
using Xunit;

namespace Orleans.Serialization.UnitTests;

[Trait("Category", "BVT")]
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Serialization")]
public sealed class SequentialItemSerializationTests : IDisposable
{
    private readonly ServiceProvider _serviceProvider;
    private readonly CodecProvider _codecProvider;
    private readonly Serializer _serializer;
    private readonly DeepCopier _deepCopier;

    public SequentialItemSerializationTests()
    {
        _serviceProvider = new ServiceCollection()
            .AddSerializer()
            .BuildServiceProvider();
        _codecProvider = _serviceProvider.GetRequiredService<CodecProvider>();
        _serializer = _serviceProvider.GetRequiredService<Serializer>();
        _deepCopier = _serviceProvider.GetRequiredService<DeepCopier>();
    }

    [Fact]
    public void GeneratedOpenGenericCodecAndCopierResolveForConstructedSequentialItem()
    {
        var codec = _codecProvider.GetCodec<SequentialItem<Person2External>>();
        var copier = _codecProvider.GetDeepCopier<SequentialItem<Person2External>>();

        AssertGeneratedOpenGeneric(
            codec.GetType(),
            "Codec_SequentialItem`1",
            typeof(IFieldCodec<>));
        AssertGeneratedOpenGeneric(
            copier.GetType(),
            "Copier_SequentialItem`1",
            typeof(IDeepCopier<>));
    }

    [Fact]
    public void ConstructedSequentialItemSerializesAndDeserializes()
    {
        var original = CreateSequentialItem();

        var serialized = _serializer.SerializeToArray(original);
        var result = _serializer.Deserialize<SequentialItem<Person2External>>(serialized);

        Assert.NotEmpty(serialized);
        Assert.NotNull(result);
        Assert.NotSame(original, result);
        AssertSequentialItem(original, result);
        Assert.NotSame(original.Item, result.Item);
        Assert.NotSame(original.Token, result.Token);
    }

    [Fact]
    public void ConstructedSequentialItemIsDeepCopied()
    {
        var original = CreateSequentialItem();

        var result = _deepCopier.Copy(original);

        Assert.NotNull(result);
        Assert.NotSame(original, result);
        AssertSequentialItem(original, result);
        Assert.NotSame(original.Item, result.Item);
        Assert.NotSame(original.Token, result.Token);
    }

    public void Dispose() => _serviceProvider.Dispose();

    private static SequentialItem<Person2External> CreateSequentialItem() =>
        new(
            new Person2External(42, "Douglas")
            {
                FavouriteColor = "blue",
                StarSign = "Betelgeuse",
            },
            new EventSequenceTokenV2(123, 7));

    private static void AssertGeneratedOpenGeneric(Type resolvedType, string expectedName, Type serviceType)
    {
        Assert.True(resolvedType.IsConstructedGenericType);
        var genericType = resolvedType.GetGenericTypeDefinition();
        Assert.Equal(expectedName, genericType.Name);
        Assert.Equal("OrleansCodeGen.Orleans.Streams", genericType.Namespace);
        Assert.Equal(typeof(Person2External), Assert.Single(resolvedType.GenericTypeArguments));

        var implementedService = Assert.Single(
            resolvedType.GetInterfaces(),
            type => type.IsGenericType && type.GetGenericTypeDefinition() == serviceType);
        Assert.Equal(typeof(SequentialItem<Person2External>), implementedService.GenericTypeArguments[0]);
    }

    private static void AssertSequentialItem(
        SequentialItem<Person2External> expected,
        SequentialItem<Person2External> actual)
    {
        Assert.Equal(42, actual.Item.Age);
        Assert.Equal("Douglas", actual.Item.Name);
        Assert.Equal("blue", actual.Item.FavouriteColor);
        Assert.Equal("Betelgeuse", actual.Item.StarSign);
        Assert.Equal(expected.Item, actual.Item);

        var token = Assert.IsType<EventSequenceTokenV2>(actual.Token);
        Assert.Equal(123, token.SequenceNumber);
        Assert.Equal(7, token.EventIndex);
        Assert.Equal(expected.Token, token);
    }
}
#endif
