using System;
using Google.Protobuf;
using Google.Protobuf.Collections;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Serialization.Cloning;
using Orleans.Serialization.Serializers;

namespace Orleans.Serialization.UnitTests;

[Trait("Category", "BVT")]
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Serialization")]
public sealed class ProtobufNullAndCopierContractTests : IDisposable
{
    private readonly ServiceProvider _serviceProvider = new ServiceCollection()
        .AddSerializer(builder => builder.AddProtobufSerializer())
        .BuildServiceProvider();

    [Fact]
    public void MapFieldCodec_NullPayload_RoundTripsAsNull()
    {
        var serializer = _serviceProvider.GetRequiredService<Serializer<MapField<string, int>>>();

        var payload = serializer.SerializeToArray(null!);
        var result = serializer.Deserialize(payload);

        Assert.NotEmpty(payload);
        Assert.Null(result);
    }

    [Fact]
    public void MapFieldCodec_ValidPayload_RoundTripsEntriesIntoIndependentCollection()
    {
        var serializer = _serviceProvider.GetRequiredService<Serializer<MapField<string, int>>>();
        var input = new MapField<string, int>
        {
            ["first"] = 17,
            ["second"] = -23,
        };

        var payload = serializer.SerializeToArray(input);
        var result = serializer.Deserialize(payload);

        Assert.NotEmpty(payload);
        Assert.NotNull(result);
        Assert.NotSame(input, result);
        Assert.Equal(2, result.Count);
        Assert.Equal(17, result["first"]);
        Assert.Equal(-23, result["second"]);
    }

    [Fact]
    public void RepeatedFieldCodec_NullPayload_RoundTripsAsNull()
    {
        var serializer = _serviceProvider.GetRequiredService<Serializer<RepeatedField<int>>>();

        var payload = serializer.SerializeToArray(null!);
        var result = serializer.Deserialize(payload);

        Assert.NotEmpty(payload);
        Assert.Null(result);
    }

    [Fact]
    public void RepeatedFieldCodec_ValidPayload_RoundTripsItemsInOrderIntoIndependentCollection()
    {
        var serializer = _serviceProvider.GetRequiredService<Serializer<RepeatedField<int>>>();
        var input = new RepeatedField<int> { 17, -23, 41 };

        var payload = serializer.SerializeToArray(input);
        var result = serializer.Deserialize(payload);

        Assert.NotEmpty(payload);
        Assert.NotNull(result);
        Assert.NotSame(input, result);
        Assert.Equal([17, -23, 41], result);
    }

    [Fact]
    public void ByteStringCopier_NullInputWithValidContext_ReturnsNull()
    {
        var sut = new ByteStringCopier();
        using var context = GetCopyContext();

        var result = sut.DeepCopy(null!, context);

        Assert.Null(result);
    }

    [Fact]
    public void ByteStringCopier_NullContext_ThrowsWithExactParamNameBeforeCopyingInput()
    {
        var input = ByteString.CopyFrom(1, 2, 3, 4);
        var sut = new ByteStringCopier();

        var exception = Assert.Throws<ArgumentNullException>(() => sut.DeepCopy(input, null!));

        Assert.Equal("context", exception.ParamName);
        Assert.Equal(ByteString.CopyFrom(1, 2, 3, 4), input);
    }

    [Fact]
    public void ByteStringCopier_ValidInput_CopiesContentAndReusesContextCopy()
    {
        var input = ByteString.CopyFrom(1, 2, 3, 4);
        var sut = new ByteStringCopier();
        using var context = GetCopyContext();

        var result = sut.DeepCopy(input, context);
        var repeatedResult = sut.DeepCopy(input, context);

        Assert.Equal(input, result);
        Assert.NotSame(input, result);
        Assert.Same(result, repeatedResult);
    }

    [Fact]
    public void MapFieldCopier_NullInputWithValidContext_ReturnsNullWithoutInvokingElementCopiers()
    {
        var keyCopier = new TrackingStringCopier("key:");
        var valueCopier = new TrackingStringCopier("value:");
        var sut = new MapFieldCopier<string, string>(keyCopier, valueCopier);
        using var context = GetCopyContext();

        var result = sut.DeepCopy(null!, context);

        Assert.Null(result);
        Assert.Equal(0, keyCopier.InvocationCount);
        Assert.Equal(0, valueCopier.InvocationCount);
    }

    [Fact]
    public void MapFieldCopier_NullContext_ThrowsWithExactParamNameBeforeInvokingElementCopiers()
    {
        var keyCopier = new TrackingStringCopier("key:");
        var valueCopier = new TrackingStringCopier("value:");
        var sut = new MapFieldCopier<string, string>(keyCopier, valueCopier);
        var input = new MapField<string, string> { ["alpha"] = "one" };

        var exception = Assert.Throws<ArgumentNullException>(() => sut.DeepCopy(input, null!));

        Assert.Equal("context", exception.ParamName);
        Assert.Equal("one", input["alpha"]);
        Assert.Equal(0, keyCopier.InvocationCount);
        Assert.Equal(0, valueCopier.InvocationCount);
    }

    [Fact]
    public void MapFieldCopier_CopyIntoNullInput_ThrowsWithExactParamNameBeforeMutatingOutput()
    {
        var keyCopier = new TrackingStringCopier("key:");
        var valueCopier = new TrackingStringCopier("value:");
        var sut = new MapFieldCopier<string, string>(keyCopier, valueCopier);
        var output = new MapField<string, string> { ["existing"] = "unchanged" };
        using var context = GetCopyContext();

        var exception = Assert.Throws<ArgumentNullException>(() => sut.DeepCopy(null!, output, context));

        Assert.Equal("input", exception.ParamName);
        Assert.Single(output);
        Assert.Equal("unchanged", output["existing"]);
        Assert.Equal(0, keyCopier.InvocationCount);
        Assert.Equal(0, valueCopier.InvocationCount);
    }

    [Fact]
    public void MapFieldCopier_CopyIntoNullOutput_ThrowsWithExactParamNameBeforeInvokingElementCopiers()
    {
        var keyCopier = new TrackingStringCopier("key:");
        var valueCopier = new TrackingStringCopier("value:");
        var sut = new MapFieldCopier<string, string>(keyCopier, valueCopier);
        var input = new MapField<string, string> { ["alpha"] = "one" };
        using var context = GetCopyContext();

        var exception = Assert.Throws<ArgumentNullException>(() => sut.DeepCopy(input, null!, context));

        Assert.Equal("output", exception.ParamName);
        Assert.Equal("one", input["alpha"]);
        Assert.Equal(0, keyCopier.InvocationCount);
        Assert.Equal(0, valueCopier.InvocationCount);
    }

    [Fact]
    public void MapFieldCopier_CopyIntoNullContext_ThrowsWithExactParamNameBeforeMutatingOutput()
    {
        var keyCopier = new TrackingStringCopier("key:");
        var valueCopier = new TrackingStringCopier("value:");
        var sut = new MapFieldCopier<string, string>(keyCopier, valueCopier);
        var input = new MapField<string, string> { ["alpha"] = "one" };
        var output = new MapField<string, string> { ["existing"] = "unchanged" };

        var exception = Assert.Throws<ArgumentNullException>(() => sut.DeepCopy(input, output, null!));

        Assert.Equal("context", exception.ParamName);
        Assert.Single(output);
        Assert.Equal("unchanged", output["existing"]);
        Assert.Equal(0, keyCopier.InvocationCount);
        Assert.Equal(0, valueCopier.InvocationCount);
    }

    [Fact]
    public void MapFieldCopier_ValidInput_DeepCopiesEntriesAndReusesContextCopy()
    {
        var keyCopier = new TrackingStringCopier("key:");
        var valueCopier = new TrackingStringCopier("value:");
        var sut = new MapFieldCopier<string, string>(keyCopier, valueCopier);
        var input = new MapField<string, string>
        {
            ["alpha"] = "one",
            ["beta"] = "two",
        };
        using var context = GetCopyContext();

        var result = sut.DeepCopy(input, context);
        var repeatedResult = sut.DeepCopy(input, context);

        Assert.NotSame(input, result);
        Assert.Equal(2, result.Count);
        Assert.Equal("value:one", result["key:alpha"]);
        Assert.Equal("value:two", result["key:beta"]);
        Assert.Equal("one", input["alpha"]);
        Assert.Equal(2, keyCopier.InvocationCount);
        Assert.Equal(2, valueCopier.InvocationCount);
        Assert.Same(result, repeatedResult);
    }

    [Fact]
    public void MapFieldCopier_CopyIntoValidCollections_PreservesExistingEntriesAndCopiesNewEntries()
    {
        var keyCopier = new TrackingStringCopier("key:");
        var valueCopier = new TrackingStringCopier("value:");
        var sut = new MapFieldCopier<string, string>(keyCopier, valueCopier);
        var input = new MapField<string, string>
        {
            ["alpha"] = "one",
            ["beta"] = "two",
        };
        var output = new MapField<string, string> { ["existing"] = "unchanged" };
        using var context = GetCopyContext();

        sut.DeepCopy(input, output, context);

        Assert.Equal(3, output.Count);
        Assert.Equal("unchanged", output["existing"]);
        Assert.Equal("value:one", output["key:alpha"]);
        Assert.Equal("value:two", output["key:beta"]);
        Assert.Equal(2, keyCopier.InvocationCount);
        Assert.Equal(2, valueCopier.InvocationCount);
    }

    [Fact]
    public void RepeatedFieldCopier_NullInputWithValidContext_ReturnsNullWithoutInvokingElementCopier()
    {
        var elementCopier = new TrackingStringCopier("copy:");
        var sut = new RepeatedFieldCopier<string>(elementCopier);
        using var context = GetCopyContext();

        var result = sut.DeepCopy(null!, context);

        Assert.Null(result);
        Assert.Equal(0, elementCopier.InvocationCount);
    }

    [Fact]
    public void RepeatedFieldCopier_NullContext_ThrowsWithExactParamNameBeforeInvokingElementCopier()
    {
        var elementCopier = new TrackingStringCopier("copy:");
        var sut = new RepeatedFieldCopier<string>(elementCopier);
        var input = new RepeatedField<string> { "alpha", "beta" };

        var exception = Assert.Throws<ArgumentNullException>(() => sut.DeepCopy(input, null!));

        Assert.Equal("context", exception.ParamName);
        Assert.Equal(["alpha", "beta"], input);
        Assert.Equal(0, elementCopier.InvocationCount);
    }

    [Fact]
    public void RepeatedFieldCopier_CopyIntoNullInput_ThrowsWithExactParamNameBeforeMutatingOutput()
    {
        var elementCopier = new TrackingStringCopier("copy:");
        var sut = new RepeatedFieldCopier<string>(elementCopier);
        var output = new RepeatedField<string> { "unchanged" };
        using var context = GetCopyContext();

        var exception = Assert.Throws<ArgumentNullException>(() => sut.DeepCopy(null!, output, context));

        Assert.Equal("input", exception.ParamName);
        Assert.Equal(["unchanged"], output);
        Assert.Equal(0, elementCopier.InvocationCount);
    }

    [Fact]
    public void RepeatedFieldCopier_CopyIntoNullOutput_ThrowsWithExactParamNameBeforeInvokingElementCopier()
    {
        var elementCopier = new TrackingStringCopier("copy:");
        var sut = new RepeatedFieldCopier<string>(elementCopier);
        var input = new RepeatedField<string> { "alpha", "beta" };
        using var context = GetCopyContext();

        var exception = Assert.Throws<ArgumentNullException>(() => sut.DeepCopy(input, null!, context));

        Assert.Equal("output", exception.ParamName);
        Assert.Equal(["alpha", "beta"], input);
        Assert.Equal(0, elementCopier.InvocationCount);
    }

    [Fact]
    public void RepeatedFieldCopier_CopyIntoNullContext_ThrowsWithExactParamNameBeforeMutatingOutput()
    {
        var elementCopier = new TrackingStringCopier("copy:");
        var sut = new RepeatedFieldCopier<string>(elementCopier);
        var input = new RepeatedField<string> { "alpha", "beta" };
        var output = new RepeatedField<string> { "unchanged" };

        var exception = Assert.Throws<ArgumentNullException>(() => sut.DeepCopy(input, output, null!));

        Assert.Equal("context", exception.ParamName);
        Assert.Equal(["unchanged"], output);
        Assert.Equal(0, elementCopier.InvocationCount);
    }

    [Fact]
    public void RepeatedFieldCopier_ValidInput_DeepCopiesItemsAndReusesContextCopy()
    {
        var elementCopier = new TrackingStringCopier("copy:");
        var sut = new RepeatedFieldCopier<string>(elementCopier);
        var input = new RepeatedField<string> { "alpha", "beta" };
        using var context = GetCopyContext();

        var result = sut.DeepCopy(input, context);
        var repeatedResult = sut.DeepCopy(input, context);

        Assert.NotSame(input, result);
        Assert.Equal(["copy:alpha", "copy:beta"], result);
        Assert.Equal(["alpha", "beta"], input);
        Assert.Equal(2, elementCopier.InvocationCount);
        Assert.Same(result, repeatedResult);
    }

    [Fact]
    public void RepeatedFieldCopier_CopyIntoValidCollections_PreservesExistingItemsAndAppendsCopies()
    {
        var elementCopier = new TrackingStringCopier("copy:");
        var sut = new RepeatedFieldCopier<string>(elementCopier);
        var input = new RepeatedField<string> { "alpha", "beta" };
        var output = new RepeatedField<string> { "existing" };
        using var context = GetCopyContext();

        sut.DeepCopy(input, output, context);

        Assert.Equal(["existing", "copy:alpha", "copy:beta"], output);
        Assert.Equal(2, elementCopier.InvocationCount);
    }

    [Fact]
    public void ProtobufCodec_NullInputWithValidContext_ReturnsNull()
    {
        var sut = _serviceProvider.GetRequiredService<ProtobufCodec>();
        using var context = GetCopyContext();

        var result = sut.DeepCopy(null, context);

        Assert.Null(result);
    }

    [Fact]
    public void ProtobufCodec_NullContext_ThrowsWithExactParamNameBeforeInspectingInput()
    {
        var sut = _serviceProvider.GetRequiredService<ProtobufCodec>();
        var input = new MyProtobufClass
        {
            IntProperty = 17,
            StringProperty = "unchanged",
        };

        var exception = Assert.Throws<ArgumentNullException>(() => sut.DeepCopy(input, null!));

        Assert.Equal("context", exception.ParamName);
        Assert.Equal(17, input.IntProperty);
        Assert.Equal("unchanged", input.StringProperty);
    }

    [Fact]
    public void ProtobufCodec_ValidInput_DeepCopiesMessageAndReusesContextCopy()
    {
        var sut = _serviceProvider.GetRequiredService<ProtobufCodec>();
        var id = Guid.NewGuid().ToByteString();
        var input = new MyProtobufClass
        {
            IntProperty = 17,
            StringProperty = "payload",
            SubClass = new MyProtobufClass.Types.SubClass { Id = id },
        };
        using var context = GetCopyContext();

        var result = Assert.IsType<MyProtobufClass>(sut.DeepCopy(input, context));
        var repeatedResult = sut.DeepCopy(input, context);

        Assert.NotSame(input, result);
        Assert.Equal(17, result.IntProperty);
        Assert.Equal("payload", result.StringProperty);
        Assert.NotSame(input.SubClass, result.SubClass);
        Assert.Equal(id, result.SubClass.Id);
        Assert.Same(result, repeatedResult);
    }

    public void Dispose() => _serviceProvider.Dispose();

    private CopyContext GetCopyContext()
        => _serviceProvider.GetRequiredService<CopyContextPool>().GetContext();

    private sealed class TrackingStringCopier(string prefix) : IDeepCopier<string>
    {
        public int InvocationCount { get; private set; }

        [return: System.Diagnostics.CodeAnalysis.NotNullIfNotNull(nameof(input))]
        public string? DeepCopy(string? input, CopyContext context)
        {
            InvocationCount++;
            return input is null ? null : prefix + input;
        }
    }
}
