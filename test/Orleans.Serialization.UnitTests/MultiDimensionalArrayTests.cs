using System;
using System.Buffers;
using System.IO.Pipelines;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Serialization.Buffers;
using Orleans.Serialization.Cloning;
using Orleans.Serialization.Codecs;
using Orleans.Serialization.Serializers;
using Orleans.Serialization.Session;
using Orleans.Serialization.WireProtocol;
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
    private readonly ObjectSerializer _objectSerializer;

    public MultiDimensionalArrayTests()
    {
        _serviceProvider = new ServiceCollection().AddSerializer().BuildServiceProvider();
        _deepCopier = _serviceProvider.GetRequiredService<DeepCopier>();
        _serializer = _serviceProvider.GetRequiredService<Serializer>();
        _objectSerializer = _serviceProvider.GetRequiredService<ObjectSerializer>();
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

    [Theory]
    [InlineData(0, 2)]
    [InlineData(2, 0)]
    [InlineData(0, 0)]
    [InlineData(0, 2, 3)]
    [InlineData(2, 0, 3)]
    [InlineData(2, 3, 0)]
    [InlineData(2, 0, 0)]
    [InlineData(0, 2, 3, 4)]
    [InlineData(2, 0, 3, 4)]
    [InlineData(2, 3, 0, 4)]
    [InlineData(2, 3, 4, 0)]
    [InlineData(1024, 1024, 0)]
    public void RoundTrip_EmptyArrays_PreservesDimensionsAndAliases(params int[] lengths)
    {
        var array = Array.CreateInstance(typeof(int), lengths);
        var original = new object[] { array, array, 42 };

        var result = _serializer.Deserialize<object[]>(_serializer.SerializeToArray(original))!;

        Assert.Equal(3, result.Length);
        var restored = Assert.IsAssignableFrom<Array>(result[0]);
        Assert.Equal(array.GetType(), restored.GetType());
        Assert.NotSame(array, restored);
        Assert.Empty(restored);
        Assert.Equal(lengths.Length, restored.Rank);
        for (var dimension = 0; dimension < lengths.Length; dimension++)
        {
            Assert.Equal(lengths[dimension], restored.GetLength(dimension));
            Assert.Equal(0, restored.GetLowerBound(dimension));
        }

        Assert.Same(restored, result[1]);
        Assert.Equal(42, Assert.IsType<int>(result[2]));
    }

    // Captured from the unmodified writer on main c7056b3ab620e5d3144ea72b12d572dc398c988d.
    [Theory]
    [InlineData(2, 0, "2020000501090001E0E0")]
    [InlineData(0, 2, "2020000501010009E0E0")]
    [InlineData(1, 2, "2020000501050009E0012D0057E0")]
    public void Deserialize_LegacyRankTwoPayload_PreservesShapeElementsAndWriterBytes(int rows, int columns, string hex)
    {
        var original = new int[rows, columns];
        if (original.Length > 0)
        {
            original[0, 0] = 11;
            original[0, 1] = -22;
        }

        var payload = Convert.FromHexString(hex);
        var result = _serializer.Deserialize<int[,]>(payload)!;

        Assert.Equal(rows, result.GetLength(0));
        Assert.Equal(columns, result.GetLength(1));
        Assert.Equal(original, result);
        Assert.Equal(payload, _serializer.SerializeToArray(original));
    }

    [Theory]
    [InlineData(2, 2)]
    [InlineData(int.MaxValue, int.MaxValue)]
    [InlineData(65536, 65536)]
    [InlineData(int.MaxValue, int.MaxValue, int.MaxValue)]
    public void Deserialize_NonEmptyDimensionsExceedInput_ThrowsBeforeAllocation(params int[] lengths)
    {
        var payload = CreateArrayPayload(lengths);
        var arrayType = typeof(int).MakeArrayType(lengths.Length);

        var exception = Assert.Throws<IndexOutOfRangeException>(() => _objectSerializer.Deserialize(payload, arrayType));

        Assert.Equal(
            $"Declared dimensions [{string.Join(", ", lengths)}] require more elements than the remaining length of the input, 1.",
            exception.Message);
    }

    [Fact]
    public async Task Deserialize_DimensionProductOverflow_ThrowsBeforeAllocation()
    {
        var lengths = new[] { int.MaxValue, int.MaxValue, int.MaxValue };
        var pipe = new Pipe();
        await pipe.Writer.WriteAsync(CreateArrayPayload(lengths), TestContext.Current.CancellationToken);
        await pipe.Writer.CompleteAsync();
        using var stream = pipe.Reader.AsStream();

        var exception = Assert.Throws<IndexOutOfRangeException>(() => _serializer.Deserialize<int[,,]>(stream));

        Assert.Equal(
            $"Declared dimensions [{string.Join(", ", lengths)}] require more elements than the remaining length of the input, {long.MaxValue}.",
            exception.Message);
    }

    [Theory]
    [InlineData(-1, 2)]
    [InlineData(-1, 0)]
    [InlineData(0, -1)]
    [InlineData(2, -1, 0)]
    [InlineData(2, 0, -1)]
    public void Deserialize_EmptyArrayWithNegativeDimension_Throws(params int[] lengths)
    {
        var payload = CreateArrayPayload(lengths);
        var arrayType = typeof(int).MakeArrayType(lengths.Length);

        Assert.Throws<ArgumentOutOfRangeException>(() => _objectSerializer.Deserialize(payload, arrayType));
    }

    [Theory]
    [InlineData(0, 2)]
    [InlineData(2, 0)]
    [InlineData(2, 3, 0)]
    public void Deserialize_EmptyArrayWithElement_Throws(params int[] lengths)
    {
        var payload = CreateArrayPayload(lengths, [42]);
        var arrayType = typeof(int).MakeArrayType(lengths.Length);

        Assert.Throws<IndexOutOfRangeException>(() => _objectSerializer.Deserialize(payload, arrayType));
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
    public void RoundTrip_TypedArraysWithDifferentRanksPreservesShapesAndAliases()
    {
        var shared = new MyValue(83);
        var rankTwo = new MyValue[,] { { shared, shared } };
        var rankThree = new MyValue[,,] { { { shared, shared } } };

        var restoredTwo = _serializer.Deserialize<MyValue[,]>(_serializer.SerializeToArray(rankTwo))!;
        var restoredThree = _serializer.Deserialize<MyValue[,,]>(_serializer.SerializeToArray(rankThree))!;
        var secondTwo = _serializer.Deserialize<MyValue[,]>(_serializer.SerializeToArray(rankTwo))!;

        Assert.Equal(rankTwo.GetType(), restoredTwo.GetType());
        Assert.Equal(1, restoredTwo.GetLength(0));
        Assert.Equal(2, restoredTwo.GetLength(1));
        Assert.Equal(83, restoredTwo[0, 0].Value);
        Assert.Same(restoredTwo[0, 0], restoredTwo[0, 1]);
        Assert.NotSame(shared, restoredTwo[0, 0]);
        Assert.Equal(rankThree.GetType(), restoredThree.GetType());
        Assert.Equal(1, restoredThree.GetLength(0));
        Assert.Equal(1, restoredThree.GetLength(1));
        Assert.Equal(2, restoredThree.GetLength(2));
        Assert.Equal(83, restoredThree[0, 0, 0].Value);
        Assert.Same(restoredThree[0, 0, 0], restoredThree[0, 0, 1]);
        Assert.NotSame(shared, restoredThree[0, 0, 0]);
        Assert.Equal(rankTwo.GetType(), secondTwo.GetType());
        Assert.Equal(83, secondTwo[0, 0].Value);
        Assert.Same(secondTwo[0, 0], secondTwo[0, 1]);
    }

    [Fact]
    public void RoundTrip_RecursiveModelWithDifferentArrayRanksPreservesShape()
    {
        var root = new MixedRankValue();
        root.RankTwo = new[,] { { root } };
        root.RankThree = new[, ,] { { { root } } };

        var restored = _serializer.Deserialize<MixedRankValue>(_serializer.SerializeToArray(root))!;

        Assert.Same(restored, restored.RankTwo[0, 0]);
        Assert.Same(restored, restored.RankThree[0, 0, 0]);
        Assert.Equal(typeof(MixedRankValue[,]), restored.RankTwo.GetType());
        Assert.Equal(typeof(MixedRankValue[,,]), restored.RankThree.GetType());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RoundTrip_ArrayRootWithRecursiveMixedRanksPreservesShapesAliasesAndCopy(bool rankThreeRoot)
    {
        var model = new MixedRankValue { Value = 42 };
        model.RankTwo = new[,] { { model, model } };
        model.RankThree = new[, ,] { { { model, model } } };
        model.RankTwoAlias = model.RankTwo;
        model.RankThreeAlias = model.RankThree;
        Array original;
        Array restored;
        if (rankThreeRoot)
        {
            var root = new[, ,] { { { model, model } } };
            original = root;
            restored = _serializer.Deserialize<MixedRankValue[,,]>(_serializer.SerializeToArray(root))!;
        }
        else
        {
            var root = new[,] { { model, model } };
            original = root;
            restored = _serializer.Deserialize<MixedRankValue[,]>(_serializer.SerializeToArray(root))!;
        }
        var copied = Copy(original);
        var first = new int[original.Rank];
        var last = new int[original.Rank];
        last[^1] = 1;
        foreach (var result in new[] { restored, copied })
        {
            Assert.Equal(original.GetType(), result.GetType());
            Assert.NotSame(original, result);
            var value = Assert.IsType<MixedRankValue>(result.GetValue(first));
            Assert.Equal(42, value.Value);
            Assert.NotSame(model, value);
            Assert.Same(value, result.GetValue(last));
            Assert.Equal(typeof(MixedRankValue[,]), value.RankTwo.GetType());
            Assert.Equal(typeof(MixedRankValue[,,]), value.RankThree.GetType());
            Assert.Same(value.RankTwo, value.RankTwoAlias);
            Assert.Same(value.RankThree, value.RankThreeAlias);
            Assert.Same(value, value.RankTwo[0, 0]);
            Assert.Same(value, value.RankTwo[0, 1]);
            Assert.Same(value, value.RankThree[0, 0, 0]);
            Assert.Same(value, value.RankThree[0, 0, 1]);
            Assert.NotSame(model.RankTwo, value.RankTwo);
            Assert.NotSame(model.RankThree, value.RankThree);
        }
        var copy = Assert.IsType<MixedRankValue>(copied.GetValue(first));
        copy.Value = 99;
        copy.RankTwo[0, 0] = new MixedRankValue();
        Assert.Equal(42, model.Value);
        Assert.Same(model, model.RankTwo[0, 0]);
        Assert.Same(copy.RankTwo[0, 0], copy.RankTwoAlias[0, 0]);
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

    private byte[] CreateArrayPayload(int[] lengths, int[]? elements = null)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using var session = _serviceProvider.GetRequiredService<SerializerSessionPool>().GetSession();
        var writer = Writer.Create(buffer, session);
        var arrayType = typeof(int).MakeArrayType(lengths.Length);
        ReferenceCodec.MarkValueField(session);
        writer.WriteFieldHeader(0, arrayType, arrayType, WireType.TagDelimited);
        _serviceProvider.GetRequiredService<CodecProvider>().GetCodec<int[]>().WriteField(ref writer, 0, typeof(int[]), lengths);
        uint fieldIdDelta = 1;
        foreach (var element in elements ?? [])
        {
            Int32Codec.WriteField(ref writer, fieldIdDelta, element);
            fieldIdDelta = 0;
        }

        writer.WriteEndObject();
        writer.Commit();
        return buffer.WrittenSpan.ToArray();
    }

    [GenerateSerializer]
    public sealed class MixedRankValue
    {
        [Id(0)]
        public MixedRankValue[,] RankTwo { get; set; } = new MixedRankValue[0, 0];

        [Id(1)]
        public MixedRankValue[,,] RankThree { get; set; } = new MixedRankValue[0, 0, 0];

        [Id(2)]
        public MixedRankValue[,] RankTwoAlias { get; set; } = new MixedRankValue[0, 0];

        [Id(3)]
        public MixedRankValue[,,] RankThreeAlias { get; set; } = new MixedRankValue[0, 0, 0];

        [Id(4)]
        public int Value { get; set; }
    }
}
