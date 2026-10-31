#nullable enable
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Orleans.Serialization.UnitTests.TaggedUnions;

[Trait("Category", "BVT")]
[Trait("Suite", "BVT")]
[Trait("Provider", "None")]
[Trait("Area", "Serialization")]
public sealed class TaggedUnionCustomAccessTests : IDisposable
{
    private readonly ServiceProvider _services = new ServiceCollection().AddSerializer().BuildServiceProvider();

    public void Dispose() => _services.Dispose();

    [Theory]
    [InlineData("number")]
    [InlineData("mutable")]
    [InlineData("empty")]
    public void CustomUnionRoundTripsUsingTypedAccessAndUserConversions(string shape)
    {
        var originalValues = new List<int> { 7, 11 };
        var original = CreateValue(shape, originalValues);
        var serializer = _services.GetRequiredService<Serializer>();

        var result = serializer.Deserialize<CustomAccessUnion>(serializer.SerializeToArray(original));

        AssertValue(shape, result, originalValues);
        Assert.Equal(shape != "empty", result.CreatedByConversion);
        Assert.Equal(0, original.ValueReads);
        Assert.Equal(0, result.ValueReads);
    }

    [Theory]
    [InlineData("number")]
    [InlineData("mutable")]
    [InlineData("empty")]
    public void CustomUnionDeepCopiesUsingTypedAccessAndUserConversions(string shape)
    {
        var originalValues = new List<int> { 7, 11 };
        var original = CreateValue(shape, originalValues);

        var result = _services.GetRequiredService<DeepCopier>().Copy(original);

        AssertValue(shape, result, originalValues);
        Assert.Equal(shape == "mutable", result.CreatedByConversion);
        Assert.Equal(0, original.ValueReads);
        Assert.Equal(0, result.ValueReads);
    }

    private static CustomAccessUnion CreateValue(string shape, List<int> values) => shape switch
    {
        "number" => new CustomAccessUnion(42),
        "mutable" => new CustomAccessUnion(values),
        "empty" => default,
        _ => throw new ArgumentOutOfRangeException(nameof(shape)),
    };

    private static void AssertValue(string shape, CustomAccessUnion result, List<int> originalValues)
    {
        Assert.Equal(shape != "empty", result.HasValue);
        Assert.Equal(shape == "number", result.TryGetValue(out int number));
        Assert.Equal(shape == "mutable", result.TryGetValue(out List<int>? values));
        if (shape == "number")
        {
            Assert.Equal(42, number);
        }
        else if (shape == "mutable")
        {
            Assert.Equal(new[] { 7, 11 }, values);
            Assert.NotSame(originalValues, values);
            values![0] = 101;
            Assert.Equal(new[] { 7, 11 }, originalValues);
        }
    }
}

[Union, GenerateSerializer]
public readonly struct CustomAccessUnion : IUnion
{
    [NonSerialized]
    private readonly int _number;
    [NonSerialized]
    private readonly List<int>? _values;
    [NonSerialized]
    private readonly AccessProbe? _probe;
    [NonSerialized]
    private readonly byte _tag;

    public CustomAccessUnion(int value) : this(value, false) { }
    public CustomAccessUnion(List<int> value) : this(value, false) { }

    private CustomAccessUnion(int value, bool createdByConversion)
    {
        _number = value;
        _tag = 1;
        _probe = new AccessProbe();
        CreatedByConversion = createdByConversion;
    }

    private CustomAccessUnion(List<int> value, bool createdByConversion)
    {
        _values = value;
        _tag = 2;
        _probe = new AccessProbe();
        CreatedByConversion = createdByConversion;
    }

    public bool HasValue => _tag != 0;
    [field: NonSerialized]
    public bool CreatedByConversion { get; }
    public int ValueReads => _probe?.Reads ?? 0;
    public object? Value
    {
        get
        {
            if (_probe is { } probe)
            {
                probe.Reads++;
            }

            return _tag switch { 1 => _number, 2 => _values, _ => null };
        }
    }

    public bool TryGetValue(out int value)
    {
        value = _number;
        return _tag == 1;
    }

    public bool TryGetValue(out List<int>? value)
    {
        value = _values;
        return _tag == 2;
    }

    public static implicit operator CustomAccessUnion(int value) => new(value, true);
    public static implicit operator CustomAccessUnion(List<int> value) => new(value, true);

    private sealed class AccessProbe
    {
        public int Reads;
    }
}
