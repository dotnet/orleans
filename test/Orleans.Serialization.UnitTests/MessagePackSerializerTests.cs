#nullable enable
using System;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Serialization.Cloning;
using Orleans.Serialization.Codecs;
using Orleans.Serialization.Serializers;
using Orleans.Serialization.TestKit;
using Xunit;
using Xunit.Abstractions;

namespace Orleans.Serialization.UnitTests;

/// <summary>
/// Tests for Orleans' MessagePack serialization support.
/// 
/// MessagePack is a binary serialization format that provides:
/// - More compact representation than JSON
/// - Faster serialization/deserialization than text formats
/// - Cross-platform and cross-language support
/// 
/// Orleans' MessagePack integration:
/// - Leverages the MessagePack-CSharp library
/// - Supports union types (discriminated unions)
/// - Provides both serialization and deep copy functionality
/// - Can be used for specific types while using Orleans' native format for others
/// 
/// This is useful when:
/// - Interoperating with systems that use MessagePack
/// - Requiring a compact binary format with broad language support
/// - Needing better performance than JSON but more portability than Orleans' native format
/// </summary>
[Trait("Category", "BVT")]
public class MessagePackCodecTests : FieldCodecTester<MyMessagePackClass?, IFieldCodec<MyMessagePackClass?>>
{
    public MessagePackCodecTests(ITestOutputHelper output) : base(output)
    {
    }

    protected override void Configure(ISerializerBuilder builder)
    {
        builder.AddMessagePackSerializer();
    }

    protected override MyMessagePackClass? CreateValue() => new() { IntProperty = 30, StringProperty = "hello", SubClass = new() { Id = Guid.NewGuid() } };

    protected override MyMessagePackClass?[] TestValues => new MyMessagePackClass?[]
    {
        null,
        new() { SubClass = new() { Id = Guid.NewGuid() } },
        new() { IntProperty = 150, StringProperty = new string('c', 20), SubClass = new() { Id = Guid.NewGuid() } },
        new() { IntProperty = 150_000, StringProperty = new string('c', 6_000), SubClass = new() { Id = Guid.NewGuid() } },
        new() { Union = new MyMessagePackUnionVariant1 { IntProperty = 1 } },
        new() { Union = new MyMessagePackUnionVariant2 { StringProperty = "String" } },
    };

    [Fact]
    public void MessagePackSerializerDeepCopyTyped()
    {
        var original = new MyMessagePackClass { IntProperty = 30, StringProperty = "hi", SubClass = new() { Id = Guid.NewGuid() } };
        var copier = ServiceProvider.GetRequiredService<DeepCopier<MyMessagePackClass>>();
        var result = copier.Copy(original);

        Assert.Equal(original.IntProperty, result.IntProperty);
        Assert.Equal(original.StringProperty, result.StringProperty);
        Assert.Equal(original.SubClass.Id, result.SubClass.Id);
    }

    [Fact]
    public void MessagePackSerializerDeepCopyUntyped()
    {
        var original = new MyMessagePackClass { IntProperty = 30, StringProperty = "hi", SubClass = new() { Id = Guid.NewGuid() } };
        var copier = ServiceProvider.GetRequiredService<DeepCopier>();
        var result = (MyMessagePackClass)copier.Copy((object)original);

        Assert.Equal(original.IntProperty, result.IntProperty);
        Assert.Equal(original.StringProperty, result.StringProperty);
        Assert.Equal(original.SubClass.Id, result.SubClass.Id);
    }

    [Fact]
    public void MessagePackSerializerRoundTripThroughCodec()
    {
        var original = new MyMessagePackClass { IntProperty = 30, StringProperty = "hi", SubClass = new() { Id = Guid.NewGuid() } };
        var result = RoundTripThroughCodec(original);

        Assert.Equal(original.IntProperty, result.IntProperty);
        Assert.Equal(original.StringProperty, result.StringProperty);
    }

    [Fact]
    public void MessagePackSerializerRoundTripThroughUntypedSerializer()
    {
        var original = new MyMessagePackClass { IntProperty = 30, StringProperty = "hi", SubClass = new() { Id = Guid.NewGuid() } };
        var untypedResult = RoundTripThroughUntypedSerializer(original, out _);

        var result = Assert.IsType<MyMessagePackClass>(untypedResult);
        Assert.Equal(original.IntProperty, result.IntProperty);
        Assert.Equal(original.StringProperty, result.StringProperty);
    }
}


[Trait("Category", "BVT")]
public class MessagePackUnionCodecTests : FieldCodecTester<IMyMessagePackUnion?, IFieldCodec<IMyMessagePackUnion?>>
{
    public MessagePackUnionCodecTests(ITestOutputHelper output) : base(output)
    {
    }

    protected override void Configure(ISerializerBuilder builder)
    {
        builder.AddMessagePackSerializer();
    }

    protected override IMyMessagePackUnion? CreateValue() => new MyMessagePackUnionVariant1() { IntProperty = 30 };

    protected override IMyMessagePackUnion?[] TestValues => new IMyMessagePackUnion?[]
    {
        null,
        new MyMessagePackUnionVariant1 { IntProperty = 1 },
        new MyMessagePackUnionVariant2 { StringProperty = "String" },
    };
}


[Trait("Category", "BVT")]
public class MessagePackCodecCopierTests : CopierTester<MyMessagePackClass?, IDeepCopier<MyMessagePackClass?>>
{
    public MessagePackCodecCopierTests(ITestOutputHelper output) : base(output)
    {
    }

    protected override void Configure(ISerializerBuilder builder)
    {
        builder.AddMessagePackSerializer();
    }
    protected override IDeepCopier<MyMessagePackClass?> CreateCopier() => ServiceProvider.GetRequiredService<ICodecProvider>().GetDeepCopier<MyMessagePackClass?>();

    protected override MyMessagePackClass? CreateValue() => new() { IntProperty = 30, StringProperty = "hello", SubClass = new() { Id = Guid.NewGuid() } };

    protected override MyMessagePackClass?[] TestValues => new MyMessagePackClass?[]
    {
        null,
        new() { SubClass = new() { Id = Guid.NewGuid() } },
        new() { IntProperty = 150, StringProperty = new string('c', 20), SubClass = new() { Id = Guid.NewGuid() } },
        new() { IntProperty = 150_000, StringProperty = new string('c', 6_000), SubClass = new() { Id = Guid.NewGuid() } },
    };
}
