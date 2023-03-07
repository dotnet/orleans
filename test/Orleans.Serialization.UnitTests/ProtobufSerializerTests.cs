#nullable enable
using System;
using Google.Protobuf;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Serialization.Cloning;
using Orleans.Serialization.Codecs;
using Orleans.Serialization.Serializers;
using Orleans.Serialization.TestKit;
using Xunit;
using Xunit.Abstractions;

namespace Orleans.Serialization.UnitTests;

[Trait("Category", "BVT")]
public class ProtobufSerializerTests : FieldCodecTester<MyProtobufClass?, IFieldCodec<MyProtobufClass?>>
{
    public ProtobufSerializerTests(ITestOutputHelper output) : base(output)
    {
    }

    protected override void Configure(ISerializerBuilder builder)
    {
        builder.AddProtobufSerializer();
    }

    protected override MyProtobufClass? CreateValue() => new() { IntProperty = 30, StringProperty = "hello", SubClass = new MyProtobufClass.Types.SubClass { Id = Guid.NewGuid().ToByteString() } };

    protected override MyProtobufClass?[] TestValues => new MyProtobufClass?[]
    {
        null,
        new () { SubClass = new MyProtobufClass.Types.SubClass { Id = Guid.NewGuid().ToByteString() } },
        new () { IntProperty = 150, StringProperty = new string('c', 20), SubClass = new MyProtobufClass.Types.SubClass { Id = Guid.NewGuid().ToByteString() } },
        new () { IntProperty = -150_000, StringProperty = new string('c', 6_000), SubClass = new MyProtobufClass.Types.SubClass { Id = Guid.NewGuid().ToByteString() } },
    };

    [Fact]
    public void ProtobufSerializerDeepCopyTyped()
    {
        var original = new MyProtobufClass { IntProperty = 30, StringProperty = "hi", SubClass = new MyProtobufClass.Types.SubClass { Id = Guid.NewGuid().ToByteString() } };
        var copier = ServiceProvider.GetRequiredService<DeepCopier<MyProtobufClass>>();
        var result = copier.Copy(original);

        Assert.Equal(original.IntProperty, result.IntProperty);
        Assert.Equal(original.StringProperty, result.StringProperty);
    }

    [Fact]
    public void ProtobufSerializerDeepCopyUntyped()
    {
        var original = new MyProtobufClass { IntProperty = 30, StringProperty = "hi", SubClass = new MyProtobufClass.Types.SubClass { Id = Guid.NewGuid().ToByteString() } };
        var copier = ServiceProvider.GetRequiredService<DeepCopier>();
        var result = (MyProtobufClass)copier.Copy((object)original);

        Assert.Equal(original.IntProperty, result.IntProperty);
        Assert.Equal(original.StringProperty, result.StringProperty);
    }

    [Fact]
    public void ProtobufSerializerRoundTripThroughCodec()
    {
        var original = new MyProtobufClass { IntProperty = 30, StringProperty = "hi", SubClass = new MyProtobufClass.Types.SubClass { Id = Guid.NewGuid().ToByteString() } };
        var result = RoundTripThroughCodec(original);

        Assert.Equal(original.IntProperty, result.IntProperty);
        Assert.Equal(original.StringProperty, result.StringProperty);
    }

    [Fact]
    public void ProtobufSerializerRoundTripThroughUntypedSerializer()
    {
        var original = new MyProtobufClass { IntProperty = 30, StringProperty = "hi", SubClass = new MyProtobufClass.Types.SubClass { Id = Guid.NewGuid().ToByteString() } };
        var untypedResult = RoundTripThroughUntypedSerializer(original, out _);

        var result = Assert.IsType<MyProtobufClass>(untypedResult);
        Assert.Equal(original.IntProperty, result.IntProperty);
        Assert.Equal(original.StringProperty, result.StringProperty);
    }
}

[Trait("Category", "BVT")]
public class ProtobufCodecCopierTests : CopierTester<MyProtobufClass?, IDeepCopier<MyProtobufClass?>>
{
    public ProtobufCodecCopierTests(ITestOutputHelper output) : base(output)
    {
    }

    protected override void Configure(ISerializerBuilder builder)
    {
        builder.AddProtobufSerializer();
    }
    protected override IDeepCopier<MyProtobufClass?> CreateCopier() => ServiceProvider.GetRequiredService<ICodecProvider>().GetDeepCopier<MyProtobufClass?>();

    protected override MyProtobufClass? CreateValue() => new MyProtobufClass { IntProperty = 30, StringProperty = "hello", SubClass = new MyProtobufClass.Types.SubClass { Id = Guid.NewGuid().ToByteString() } };

    protected override MyProtobufClass?[] TestValues => new MyProtobufClass?[]
    {
        null,
        new () { SubClass = new MyProtobufClass.Types.SubClass { Id = Guid.NewGuid().ToByteString() } },
        new () { IntProperty = 150, StringProperty = new string('c', 20), SubClass = new MyProtobufClass.Types.SubClass { Id = Guid.NewGuid().ToByteString() } },
        new () { IntProperty = -150_000, StringProperty = new string('c', 6_000), SubClass = new MyProtobufClass.Types.SubClass { Id = Guid.NewGuid().ToByteString() } },
    };
}

public static class ProtobufGuidExtensions
{
    public static ByteString ToByteString(this Guid guid)
    {
        Span<byte> span = stackalloc byte[16];
        guid.TryWriteBytes(span);
        return ByteString.CopyFrom(span);
    }
}
