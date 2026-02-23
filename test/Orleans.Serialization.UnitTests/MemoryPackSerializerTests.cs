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
/// Tests for Orleans' MemoryPack serialization support.
/// 
/// MemoryPack is a binary serialization format that provides:
/// - More compact representation than JSON
/// - Faster serialization/deserialization than text formats
/// - Cross-platform and cross-language support
/// 
/// Orleans' MemoryPack integration:
/// - Leverages the MemoryPack-CSharp library
/// - Supports union types (discriminated unions)
/// - Provides both serialization and deep copy functionality
/// - Can be used for specific types while using Orleans' native format for others
/// 
/// This is useful when:
/// - Interoperating with systems that use MemoryPack
/// - Requiring a compact binary format with broad language support
/// - Needing better performance than JSON but more portability than Orleans' native format
/// </summary>
[Trait("Category", "BVT")]
public class MemoryPackCodecTests : FieldCodecTester<MyMemoryPackClass?, IFieldCodec<MyMemoryPackClass?>>
{
    public MemoryPackCodecTests(ITestOutputHelper output) : base(output)
    {
    }

    protected override void Configure(ISerializerBuilder builder)
    {
        builder.AddMemoryPackSerializer();
    }

    protected override MyMemoryPackClass? CreateValue() => new() { IntProperty = 30, StringProperty = "hello", SubClass = new() { Id = Guid.NewGuid() } };

    protected override MyMemoryPackClass?[] TestValues => new MyMemoryPackClass?[]
    {
        null,
        new() { SubClass = new() { Id = Guid.NewGuid() } },
        new() { IntProperty = 150, StringProperty = new string('c', 20), SubClass = new() { Id = Guid.NewGuid() } },
        new() { IntProperty = 150_000, StringProperty = new string('c', 6_000), SubClass = new() { Id = Guid.NewGuid() } },
        new() { Union = new MyMemoryPackUnionVariant1 { IntProperty = 1 } },
        new() { Union = new MyMemoryPackUnionVariant2 { StringProperty = "String" } },
    };

    [Fact]
    public void MemoryPackSerializerDeepCopyTyped()
    {
        var original = new MyMemoryPackClass { IntProperty = 30, StringProperty = "hi", SubClass = new() { Id = Guid.NewGuid() } };
        var copier = ServiceProvider.GetRequiredService<DeepCopier<MyMemoryPackClass>>();
        var result = copier.Copy(original);

        Assert.Equal(original.IntProperty, result.IntProperty);
        Assert.Equal(original.StringProperty, result.StringProperty);
        Assert.Equal(original.SubClass.Id, result.SubClass.Id);
    }

    [Fact]
    public void MemoryPackSerializerDeepCopyUntyped()
    {
        var original = new MyMemoryPackClass { IntProperty = 30, StringProperty = "hi", SubClass = new() { Id = Guid.NewGuid() } };
        var copier = ServiceProvider.GetRequiredService<DeepCopier>();
        var result = (MyMemoryPackClass)copier.Copy((object)original);

        Assert.Equal(original.IntProperty, result.IntProperty);
        Assert.Equal(original.StringProperty, result.StringProperty);
        Assert.Equal(original.SubClass.Id, result.SubClass.Id);
    }

    [Fact]
    public void MemoryPackSerializerRoundTripThroughCodec()
    {
        var original = new MyMemoryPackClass { IntProperty = 30, StringProperty = "hi", SubClass = new() { Id = Guid.NewGuid() } };
        var result = RoundTripThroughCodec(original);

        Assert.Equal(original.IntProperty, result.IntProperty);
        Assert.Equal(original.StringProperty, result.StringProperty);
    }

    [Fact]
    public void MemoryPackSerializerRoundTripThroughUntypedSerializer()
    {
        var original = new MyMemoryPackClass { IntProperty = 30, StringProperty = "hi", SubClass = new() { Id = Guid.NewGuid() } };
        var untypedResult = RoundTripThroughUntypedSerializer(original, out _);

        var result = Assert.IsType<MyMemoryPackClass>(untypedResult);
        Assert.Equal(original.IntProperty, result.IntProperty);
        Assert.Equal(original.StringProperty, result.StringProperty);
    }
}


[Trait("Category", "BVT")]
public class MemoryPackUnionCodecTests : FieldCodecTester<IMyMemoryPackUnion?, IFieldCodec<IMyMemoryPackUnion?>>
{
    public MemoryPackUnionCodecTests(ITestOutputHelper output) : base(output)
    {
    }

    protected override void Configure(ISerializerBuilder builder)
    {
        builder.AddMemoryPackSerializer();
    }

    protected override IMyMemoryPackUnion? CreateValue() => new MyMemoryPackUnionVariant1() { IntProperty = 30 };

    protected override IMyMemoryPackUnion?[] TestValues => new IMyMemoryPackUnion?[]
    {
        null,
        new MyMemoryPackUnionVariant1 { IntProperty = 1 },
        new MyMemoryPackUnionVariant2 { StringProperty = "String" },
    };
}


[Trait("Category", "BVT")]
public class MemoryPackCodecCopierTests : CopierTester<MyMemoryPackClass?, IDeepCopier<MyMemoryPackClass?>>
{
    public MemoryPackCodecCopierTests(ITestOutputHelper output) : base(output)
    {
    }

    protected override void Configure(ISerializerBuilder builder)
    {
        builder.AddMemoryPackSerializer();
    }
    protected override IDeepCopier<MyMemoryPackClass?> CreateCopier() => ServiceProvider.GetRequiredService<ICodecProvider>().GetDeepCopier<MyMemoryPackClass?>();

    protected override MyMemoryPackClass? CreateValue() => new() { IntProperty = 30, StringProperty = "hello", SubClass = new() { Id = Guid.NewGuid() } };

    protected override MyMemoryPackClass?[] TestValues => new MyMemoryPackClass?[]
    {
        null,
        new() { SubClass = new() { Id = Guid.NewGuid() } },
        new() { IntProperty = 150, StringProperty = new string('c', 20), SubClass = new() { Id = Guid.NewGuid() } },
        new() { IntProperty = 150_000, StringProperty = new string('c', 6_000), SubClass = new() { Id = Guid.NewGuid() } },
    };
}
