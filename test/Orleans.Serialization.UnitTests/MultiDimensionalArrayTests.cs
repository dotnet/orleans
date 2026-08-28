using System;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Serialization.Cloning;
using Orleans.Serialization.Codecs;
using Xunit;

namespace Orleans.Serialization.UnitTests;

[Trait("Category", "BVT")]
[Trait("Suite", "BVT")]
[Trait("Provider", "None")]
[Trait("Area", "Serialization")]
public sealed class MultiDimensionalArrayTests : IDisposable
{
    private readonly ServiceProvider _serviceProvider;
    private readonly DeepCopier _deepCopier;
    private readonly Serializer _serializer;

    public MultiDimensionalArrayTests()
    {
        _serviceProvider = new ServiceCollection().AddSerializer().BuildServiceProvider();
        _deepCopier = _serviceProvider.GetRequiredService<DeepCopier>();
        _serializer = _serviceProvider.GetRequiredService<Serializer>();
    }

    [Fact]
    public void DeepCopy_ShallowCopyableArray_ClonesStorage()
    {
        var original = new[,] { { 1, 2 }, { 3, 4 } };

        var result = _deepCopier.Copy(original)!;

        Assert.NotSame(original, result);
        Assert.Equal(2, result.GetLength(0));
        Assert.Equal(2, result.GetLength(1));
        Assert.Equal(1, result[0, 0]);
        Assert.Equal(2, result[0, 1]);
        Assert.Equal(3, result[1, 0]);
        Assert.Equal(4, result[1, 1]);

        result[0, 0] = 10;
        original[1, 1] = 40;
        Assert.Equal(1, original[0, 0]);
        Assert.Equal(4, result[1, 1]);
    }

    [Fact]
    public void DeepCopy_RankOneReferenceArray_PreservesAliasesAndCopiesElements()
    {
        var shared = new MyValue(10);
        var original = Array.CreateInstance(typeof(MyValue), [3], [1]);
        original.SetValue(shared, 1);
        original.SetValue(new MyValue(20), 2);
        original.SetValue(shared, 3);

        var result = Copy(original);

        Assert.NotSame(original, result);
        Assert.Equal(original.GetType(), result.GetType());
        Assert.Equal(1, result.GetLowerBound(0));
        Assert.Equal(3, result.GetUpperBound(0));
        var first = Assert.IsType<MyValue>(result.GetValue(1));
        Assert.Equal(10, first.Value);
        Assert.Equal(20, Assert.IsType<MyValue>(result.GetValue(2)).Value);
        Assert.Same(first, result.GetValue(3));
        Assert.NotSame(shared, first);

        first.Value = 100;
        Assert.Equal(10, shared.Value);
    }

    [Fact]
    public void DeepCopy_RankTwoReferenceArray_PreservesBoundsAliasesAndIndependence()
    {
        var shared = new MyValue(30);
        var original = Array.CreateInstance(typeof(MyValue), [2, 2], [2, -1]);
        original.SetValue(shared, 2, -1);
        original.SetValue(new MyValue(40), 2, 0);
        original.SetValue(new MyValue(50), 3, -1);
        original.SetValue(shared, 3, 0);

        var result = Copy(original);

        Assert.NotSame(original, result);
        Assert.Equal(original.GetType(), result.GetType());
        Assert.Equal(2, result.GetLowerBound(0));
        Assert.Equal(3, result.GetUpperBound(0));
        Assert.Equal(-1, result.GetLowerBound(1));
        Assert.Equal(0, result.GetUpperBound(1));
        var first = Assert.IsType<MyValue>(result.GetValue(2, -1));
        Assert.Equal(30, first.Value);
        Assert.Equal(40, Assert.IsType<MyValue>(result.GetValue(2, 0)).Value);
        Assert.Equal(50, Assert.IsType<MyValue>(result.GetValue(3, -1)).Value);
        Assert.Same(first, result.GetValue(3, 0));
        Assert.NotSame(shared, first);

        first.Value = 300;
        shared.Value = 31;
        Assert.Equal(31, Assert.IsType<MyValue>(original.GetValue(3, 0)).Value);
        Assert.Equal(300, Assert.IsType<MyValue>(result.GetValue(2, -1)).Value);
    }

    [Fact]
    public void DeepCopy_RankThreeReferenceArray_PreservesCyclesAndAliases()
    {
        var shared = new MyValue(60);
        var original = new object[1, 2, 2];
        original[0, 0, 0] = original;
        original[0, 0, 1] = shared;
        original[0, 1, 0] = "immutable";
        original[0, 1, 1] = shared;

        var result = Assert.IsType<object[,,]>(Copy(original));

        Assert.NotSame(original, result);
        Assert.Same(result, result[0, 0, 0]);
        var first = Assert.IsType<MyValue>(result[0, 0, 1]);
        Assert.Equal(60, first.Value);
        Assert.Same(first, result[0, 1, 1]);
        Assert.NotSame(shared, first);
        Assert.Same(original[0, 1, 0], result[0, 1, 0]);
    }

    [Fact]
    public void DeepCopy_EmptyHigherRankArray_PreservesDimensions()
    {
        var original = new MyValue[2, 0, 3, 4];

        var result = Assert.IsType<MyValue[,,,]>(Copy(original));

        Assert.NotSame(original, result);
        Assert.Empty(result);
        Assert.Equal(2, result.GetLength(0));
        Assert.Equal(0, result.GetLength(1));
        Assert.Equal(3, result.GetLength(2));
        Assert.Equal(4, result.GetLength(3));
    }

    [Fact]
    public void DeepCopy_ExtremeBounds_PreservesBoundsWithoutOverflow()
    {
        var rankOne = Array.CreateInstance(typeof(MyValue), [1], [int.MaxValue]);
        rankOne.SetValue(new MyValue(65), int.MaxValue);
        var rankTwo = Array.CreateInstance(typeof(MyValue), [1, 1], [int.MaxValue, int.MinValue]);
        rankTwo.SetValue(new MyValue(66), int.MaxValue, int.MinValue);

        var rankOneResult = Copy(rankOne);
        var rankTwoResult = Copy(rankTwo);

        Assert.Equal(int.MaxValue, rankOneResult.GetLowerBound(0));
        Assert.Equal(int.MaxValue, rankOneResult.GetUpperBound(0));
        Assert.Equal(65, Assert.IsType<MyValue>(rankOneResult.GetValue(int.MaxValue)).Value);
        Assert.Equal(int.MaxValue, rankTwoResult.GetLowerBound(0));
        Assert.Equal(int.MaxValue, rankTwoResult.GetUpperBound(0));
        Assert.Equal(int.MinValue, rankTwoResult.GetLowerBound(1));
        Assert.Equal(int.MinValue, rankTwoResult.GetUpperBound(1));
        Assert.Equal(66, Assert.IsType<MyValue>(rankTwoResult.GetValue(int.MaxValue, int.MinValue)).Value);
    }

    [Fact]
    public void DeepCopy_RepeatedArrayInSameContext_ReturnsRecordedCopy()
    {
        var original = new MyValue[,] { { new(70) } };
        var copier = new MultiDimensionalArrayCopier<MyValue>();
        using var context = _serviceProvider.GetRequiredService<CopyContextPool>().GetContext();

        var first = copier.DeepCopy(original, context);
        var second = copier.DeepCopy(original, context);

        Assert.Same(first, second);
    }

    [Fact]
    public void DeepCopy_RepeatedShallowArrayInSameContext_ReturnsRecordedCopy()
    {
        var original = new[,] { { 71 } };
        var copier = new MultiDimensionalArrayCopier<int>();
        using var context = _serviceProvider.GetRequiredService<CopyContextPool>().GetContext();

        var first = copier.DeepCopy(original, context);
        var second = copier.DeepCopy(original, context);

        Assert.NotSame(original, first);
        Assert.Same(first, second);
    }

    [Fact]
    public void RoundTrip_RankThreeReferenceArray_PreservesAliasesAndCopyIndependence()
    {
        var shared = new MyValue(80);
        var original = new MyValue[2, 1, 2];
        original[0, 0, 0] = shared;
        original[0, 0, 1] = new MyValue(90);
        original[1, 0, 0] = new MyValue(100);
        original[1, 0, 1] = shared;

        var payload = _serializer.SerializeToArray(original);
        var result = _serializer.Deserialize<MyValue[,,]>(payload)!;

        Assert.NotEmpty(payload);
        Assert.NotSame(original, result);
        Assert.Equal(2, result.GetLength(0));
        Assert.Equal(1, result.GetLength(1));
        Assert.Equal(2, result.GetLength(2));
        var first = result[0, 0, 0];
        Assert.Equal(80, first.Value);
        Assert.Equal(90, result[0, 0, 1].Value);
        Assert.Equal(100, result[1, 0, 0].Value);
        Assert.Same(first, result[1, 0, 1]);
        Assert.NotSame(shared, first);

        first.Value = 800;
        shared.Value = 81;
        Assert.Equal(81, original[1, 0, 1].Value);
        Assert.Equal(800, result[0, 0, 0].Value);
    }

    [Fact]
    public void Serialize_NonZeroLowerBoundArray_ThrowsNotSupportedException()
    {
        var original = Array.CreateInstance(typeof(int), [2, 2], [1, -1]);

        var exception = Assert.Throws<NotSupportedException>(() => _serializer.SerializeToArray((object)original));

        Assert.Equal(
            "Serialization of multi-dimensional arrays with non-zero lower bounds is not supported.",
            exception.Message);
    }

    [Fact]
    public void IsSupportedType_RequiresNonVectorArray()
    {
        var copier = new MultiDimensionalArrayCopier<int>();

        Assert.True(copier.IsSupportedType(typeof(int[,])));
        Assert.True(copier.IsSupportedType(typeof(int).MakeArrayType(1)));
        Assert.False(copier.IsSupportedType(typeof(int[])));
        Assert.False(copier.IsSupportedType(typeof(int)));
    }

    public void Dispose() => _serviceProvider.Dispose();

    private Array Copy(Array original) => Assert.IsAssignableFrom<Array>(_deepCopier.Copy((object)original));
}
