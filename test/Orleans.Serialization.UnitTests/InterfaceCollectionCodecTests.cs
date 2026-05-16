using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
#if NET8_0_OR_GREATER
using System.Collections.Frozen;
#endif
using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using Orleans;
using Orleans.Serialization;
using Orleans.Serialization.Cloning;
using Orleans.Serialization.Codecs;
using Orleans.Serialization.Session;
using Orleans.Serialization.Serializers;
using Orleans.Serialization.TestKit;
using Orleans.Serialization.Utilities;
using Xunit;
using Xunit.Abstractions;
#if !NETCOREAPP3_1
using static VerifyXunit.Verifier;
#endif

namespace Orleans.Serialization.UnitTests;

public class EnumerableInterfaceCodecTests(ITestOutputHelper output, SerializationTesterFixture fixture) : FieldCodecTester<IEnumerable<int>, IFieldCodec<IEnumerable<int>>>(output, fixture), IClassFixture<SerializationTesterFixture>
{
    protected override IFieldCodec<IEnumerable<int>> CreateCodec() => ServiceProvider.GetRequiredService<ICodecProvider>().GetCodec<IEnumerable<int>>();
    protected override IEnumerable<int> CreateValue() => InterfaceCollectionTestHelpers.CreateList(Random);
    protected override IEnumerable<int>[] TestValues => [null, Array.Empty<int>(), new List<int> { 1, 2, 3 }, CreateValue()];
    protected override bool Equals(IEnumerable<int> left, IEnumerable<int> right) => InterfaceCollectionTestHelpers.SequenceEqual(left, right);
}

public class EnumerableInterfaceCopierTests(ITestOutputHelper output, SerializationTesterFixture fixture) : CopierTester<IEnumerable<int>, IDeepCopier<IEnumerable<int>>>(output, fixture), IClassFixture<SerializationTesterFixture>
{
    protected override IDeepCopier<IEnumerable<int>> CreateCopier() => ServiceProvider.GetRequiredService<IDeepCopierProvider>().GetDeepCopier<IEnumerable<int>>();
    protected override IEnumerable<int> CreateValue() => InterfaceCollectionTestHelpers.CreateList(Random);
    protected override IEnumerable<int>[] TestValues => [null, Array.Empty<int>(), new List<int> { 1, 2, 3 }, CreateValue()];
    protected override bool Equals(IEnumerable<int> left, IEnumerable<int> right) => InterfaceCollectionTestHelpers.SequenceEqual(left, right);
}

public class ReadOnlyCollectionInterfaceCodecTests(ITestOutputHelper output, SerializationTesterFixture fixture) : FieldCodecTester<IReadOnlyCollection<int>, IFieldCodec<IReadOnlyCollection<int>>>(output, fixture), IClassFixture<SerializationTesterFixture>
{
    protected override IFieldCodec<IReadOnlyCollection<int>> CreateCodec() => ServiceProvider.GetRequiredService<ICodecProvider>().GetCodec<IReadOnlyCollection<int>>();
    protected override IReadOnlyCollection<int> CreateValue() => InterfaceCollectionTestHelpers.CreateList(Random);
    protected override IReadOnlyCollection<int>[] TestValues => [null, Array.Empty<int>(), new List<int> { 1, 2, 3 }, CreateValue()];
    protected override bool Equals(IReadOnlyCollection<int> left, IReadOnlyCollection<int> right) => InterfaceCollectionTestHelpers.SequenceEqual(left, right);
}

public class ReadOnlyCollectionInterfaceCopierTests(ITestOutputHelper output, SerializationTesterFixture fixture) : CopierTester<IReadOnlyCollection<int>, IDeepCopier<IReadOnlyCollection<int>>>(output, fixture), IClassFixture<SerializationTesterFixture>
{
    protected override IDeepCopier<IReadOnlyCollection<int>> CreateCopier() => ServiceProvider.GetRequiredService<IDeepCopierProvider>().GetDeepCopier<IReadOnlyCollection<int>>();
    protected override IReadOnlyCollection<int> CreateValue() => InterfaceCollectionTestHelpers.CreateList(Random);
    protected override IReadOnlyCollection<int>[] TestValues => [null, Array.Empty<int>(), new List<int> { 1, 2, 3 }, CreateValue()];
    protected override bool Equals(IReadOnlyCollection<int> left, IReadOnlyCollection<int> right) => InterfaceCollectionTestHelpers.SequenceEqual(left, right);
}

public class ReadOnlyListInterfaceCodecTests(ITestOutputHelper output, SerializationTesterFixture fixture) : FieldCodecTester<IReadOnlyList<int>, IFieldCodec<IReadOnlyList<int>>>(output, fixture), IClassFixture<SerializationTesterFixture>
{
    protected override IFieldCodec<IReadOnlyList<int>> CreateCodec() => ServiceProvider.GetRequiredService<ICodecProvider>().GetCodec<IReadOnlyList<int>>();
    protected override IReadOnlyList<int> CreateValue() => InterfaceCollectionTestHelpers.CreateList(Random);
    protected override IReadOnlyList<int>[] TestValues => [null, Array.Empty<int>(), new List<int> { 1, 2, 3 }, CreateValue()];
    protected override bool Equals(IReadOnlyList<int> left, IReadOnlyList<int> right) => InterfaceCollectionTestHelpers.SequenceEqual(left, right);
}

public class ReadOnlyListInterfaceCopierTests(ITestOutputHelper output, SerializationTesterFixture fixture) : CopierTester<IReadOnlyList<int>, IDeepCopier<IReadOnlyList<int>>>(output, fixture), IClassFixture<SerializationTesterFixture>
{
    protected override IDeepCopier<IReadOnlyList<int>> CreateCopier() => ServiceProvider.GetRequiredService<IDeepCopierProvider>().GetDeepCopier<IReadOnlyList<int>>();
    protected override IReadOnlyList<int> CreateValue() => InterfaceCollectionTestHelpers.CreateList(Random);
    protected override IReadOnlyList<int>[] TestValues => [null, Array.Empty<int>(), new List<int> { 1, 2, 3 }, CreateValue()];
    protected override bool Equals(IReadOnlyList<int> left, IReadOnlyList<int> right) => InterfaceCollectionTestHelpers.SequenceEqual(left, right);
}

public class CollectionInterfaceCodecTests(ITestOutputHelper output, SerializationTesterFixture fixture) : FieldCodecTester<ICollection<int>, IFieldCodec<ICollection<int>>>(output, fixture), IClassFixture<SerializationTesterFixture>
{
    protected override IFieldCodec<ICollection<int>> CreateCodec() => ServiceProvider.GetRequiredService<ICodecProvider>().GetCodec<ICollection<int>>();
    protected override ICollection<int> CreateValue() => InterfaceCollectionTestHelpers.CreateList(Random);
    protected override ICollection<int>[] TestValues => [null, new List<int>(), new List<int> { 1, 2, 3 }, CreateValue()];
    protected override bool Equals(ICollection<int> left, ICollection<int> right) => InterfaceCollectionTestHelpers.SequenceEqual(left, right);
}

public class CollectionInterfaceCopierTests(ITestOutputHelper output, SerializationTesterFixture fixture) : CopierTester<ICollection<int>, IDeepCopier<ICollection<int>>>(output, fixture), IClassFixture<SerializationTesterFixture>
{
    protected override IDeepCopier<ICollection<int>> CreateCopier() => ServiceProvider.GetRequiredService<IDeepCopierProvider>().GetDeepCopier<ICollection<int>>();
    protected override ICollection<int> CreateValue() => InterfaceCollectionTestHelpers.CreateList(Random);
    protected override ICollection<int>[] TestValues => [null, new List<int>(), new List<int> { 1, 2, 3 }, CreateValue()];
    protected override bool Equals(ICollection<int> left, ICollection<int> right) => InterfaceCollectionTestHelpers.SequenceEqual(left, right);
}

public class ListInterfaceCodecTests(ITestOutputHelper output, SerializationTesterFixture fixture) : FieldCodecTester<IList<int>, IFieldCodec<IList<int>>>(output, fixture), IClassFixture<SerializationTesterFixture>
{
    protected override IFieldCodec<IList<int>> CreateCodec() => ServiceProvider.GetRequiredService<ICodecProvider>().GetCodec<IList<int>>();
    protected override IList<int> CreateValue() => InterfaceCollectionTestHelpers.CreateList(Random);
    protected override IList<int>[] TestValues => [null, new List<int>(), new List<int> { 1, 2, 3 }, CreateValue()];
    protected override bool Equals(IList<int> left, IList<int> right) => InterfaceCollectionTestHelpers.SequenceEqual(left, right);
}

public class ListInterfaceCopierTests(ITestOutputHelper output, SerializationTesterFixture fixture) : CopierTester<IList<int>, IDeepCopier<IList<int>>>(output, fixture), IClassFixture<SerializationTesterFixture>
{
    protected override IDeepCopier<IList<int>> CreateCopier() => ServiceProvider.GetRequiredService<IDeepCopierProvider>().GetDeepCopier<IList<int>>();
    protected override IList<int> CreateValue() => InterfaceCollectionTestHelpers.CreateList(Random);
    protected override IList<int>[] TestValues => [null, new List<int>(), new List<int> { 1, 2, 3 }, CreateValue()];
    protected override bool Equals(IList<int> left, IList<int> right) => InterfaceCollectionTestHelpers.SequenceEqual(left, right);
}

public class SetInterfaceCodecTests(ITestOutputHelper output, SerializationTesterFixture fixture) : FieldCodecTester<ISet<string>, IFieldCodec<ISet<string>>>(output, fixture), IClassFixture<SerializationTesterFixture>
{
    protected override IFieldCodec<ISet<string>> CreateCodec() => ServiceProvider.GetRequiredService<ICodecProvider>().GetCodec<ISet<string>>();
    protected override ISet<string> CreateValue() => InterfaceCollectionTestHelpers.CreateSet(Random);
    protected override ISet<string>[] TestValues => [null, new HashSet<string>(), new HashSet<string> { "a", "b" }, CreateValue()];
    protected override bool Equals(ISet<string> left, ISet<string> right) => InterfaceCollectionTestHelpers.SetEqual(left, right);
}

public class SetInterfaceCopierTests(ITestOutputHelper output, SerializationTesterFixture fixture) : CopierTester<ISet<string>, IDeepCopier<ISet<string>>>(output, fixture), IClassFixture<SerializationTesterFixture>
{
    protected override IDeepCopier<ISet<string>> CreateCopier() => ServiceProvider.GetRequiredService<IDeepCopierProvider>().GetDeepCopier<ISet<string>>();
    protected override ISet<string> CreateValue() => InterfaceCollectionTestHelpers.CreateSet(Random);
    protected override ISet<string>[] TestValues => [null, new HashSet<string>(), new HashSet<string> { "a", "b" }, CreateValue()];
    protected override bool Equals(ISet<string> left, ISet<string> right) => InterfaceCollectionTestHelpers.SetEqual(left, right);
}

#if NET5_0_OR_GREATER
public class ReadOnlySetInterfaceCodecTests(ITestOutputHelper output, SerializationTesterFixture fixture) : FieldCodecTester<IReadOnlySet<string>, IFieldCodec<IReadOnlySet<string>>>(output, fixture), IClassFixture<SerializationTesterFixture>
{
    protected override IFieldCodec<IReadOnlySet<string>> CreateCodec() => ServiceProvider.GetRequiredService<ICodecProvider>().GetCodec<IReadOnlySet<string>>();
    protected override IReadOnlySet<string> CreateValue() => InterfaceCollectionTestHelpers.CreateSet(Random);
    protected override IReadOnlySet<string>[] TestValues => [null, new HashSet<string>(), new HashSet<string> { "a", "b" }, CreateValue()];
    protected override bool Equals(IReadOnlySet<string> left, IReadOnlySet<string> right) => InterfaceCollectionTestHelpers.SetEqual(left, right);
}

public class ReadOnlySetInterfaceCopierTests(ITestOutputHelper output, SerializationTesterFixture fixture) : CopierTester<IReadOnlySet<string>, IDeepCopier<IReadOnlySet<string>>>(output, fixture), IClassFixture<SerializationTesterFixture>
{
    protected override IDeepCopier<IReadOnlySet<string>> CreateCopier() => ServiceProvider.GetRequiredService<IDeepCopierProvider>().GetDeepCopier<IReadOnlySet<string>>();
    protected override IReadOnlySet<string> CreateValue() => InterfaceCollectionTestHelpers.CreateSet(Random);
    protected override IReadOnlySet<string>[] TestValues => [null, new HashSet<string>(), new HashSet<string> { "a", "b" }, CreateValue()];
    protected override bool Equals(IReadOnlySet<string> left, IReadOnlySet<string> right) => InterfaceCollectionTestHelpers.SetEqual(left, right);
}
#endif

public class DictionaryInterfaceCodecTests(ITestOutputHelper output, SerializationTesterFixture fixture) : FieldCodecTester<IDictionary<string, int>, IFieldCodec<IDictionary<string, int>>>(output, fixture), IClassFixture<SerializationTesterFixture>
{
    protected override IFieldCodec<IDictionary<string, int>> CreateCodec() => ServiceProvider.GetRequiredService<ICodecProvider>().GetCodec<IDictionary<string, int>>();
    protected override IDictionary<string, int> CreateValue() => InterfaceCollectionTestHelpers.CreateDictionary(Random);
    protected override IDictionary<string, int>[] TestValues => [null, new Dictionary<string, int>(), new Dictionary<string, int> { ["a"] = 1, ["b"] = 2 }, CreateValue()];
    protected override bool Equals(IDictionary<string, int> left, IDictionary<string, int> right) => InterfaceCollectionTestHelpers.DictionaryEqual(left, right);
}

public class DictionaryInterfaceCopierTests(ITestOutputHelper output, SerializationTesterFixture fixture) : CopierTester<IDictionary<string, int>, IDeepCopier<IDictionary<string, int>>>(output, fixture), IClassFixture<SerializationTesterFixture>
{
    protected override IDeepCopier<IDictionary<string, int>> CreateCopier() => ServiceProvider.GetRequiredService<IDeepCopierProvider>().GetDeepCopier<IDictionary<string, int>>();
    protected override IDictionary<string, int> CreateValue() => InterfaceCollectionTestHelpers.CreateDictionary(Random);
    protected override IDictionary<string, int>[] TestValues => [null, new Dictionary<string, int>(), new Dictionary<string, int> { ["a"] = 1, ["b"] = 2 }, CreateValue()];
    protected override bool Equals(IDictionary<string, int> left, IDictionary<string, int> right) => InterfaceCollectionTestHelpers.DictionaryEqual(left, right);
}

public class ReadOnlyDictionaryInterfaceCodecTests(ITestOutputHelper output, SerializationTesterFixture fixture) : FieldCodecTester<IReadOnlyDictionary<string, int>, IFieldCodec<IReadOnlyDictionary<string, int>>>(output, fixture), IClassFixture<SerializationTesterFixture>
{
    protected override IFieldCodec<IReadOnlyDictionary<string, int>> CreateCodec() => ServiceProvider.GetRequiredService<ICodecProvider>().GetCodec<IReadOnlyDictionary<string, int>>();
    protected override IReadOnlyDictionary<string, int> CreateValue() => InterfaceCollectionTestHelpers.CreateDictionary(Random);
    protected override IReadOnlyDictionary<string, int>[] TestValues => [null, new Dictionary<string, int>(), new Dictionary<string, int> { ["a"] = 1, ["b"] = 2 }, CreateValue()];
    protected override bool Equals(IReadOnlyDictionary<string, int> left, IReadOnlyDictionary<string, int> right) => InterfaceCollectionTestHelpers.DictionaryEqual(left, right);
}

public class ReadOnlyDictionaryInterfaceCopierTests(ITestOutputHelper output, SerializationTesterFixture fixture) : CopierTester<IReadOnlyDictionary<string, int>, IDeepCopier<IReadOnlyDictionary<string, int>>>(output, fixture), IClassFixture<SerializationTesterFixture>
{
    protected override IDeepCopier<IReadOnlyDictionary<string, int>> CreateCopier() => ServiceProvider.GetRequiredService<IDeepCopierProvider>().GetDeepCopier<IReadOnlyDictionary<string, int>>();
    protected override IReadOnlyDictionary<string, int> CreateValue() => InterfaceCollectionTestHelpers.CreateDictionary(Random);
    protected override IReadOnlyDictionary<string, int>[] TestValues => [null, new Dictionary<string, int>(), new Dictionary<string, int> { ["a"] = 1, ["b"] = 2 }, CreateValue()];
    protected override bool Equals(IReadOnlyDictionary<string, int> left, IReadOnlyDictionary<string, int> right) => InterfaceCollectionTestHelpers.DictionaryEqual(left, right);
}

[Trait("Category", "BVT")]
public class InterfaceCollectionRegressionTests
{
    private const int MaxInterfaceCollectionCapacityHint = 16 * 1024;
    private const string EnumerableCodecAlias = "EnumerableCodec`1";
    private const string ReadOnlyCollectionCodecAlias = "ReadOnlyCollectionInterfaceCodec`1";
    private const string ReadOnlyListCodecAlias = "ReadOnlyListInterfaceCodec`1";
    private const string CollectionCodecAlias = "CollectionInterfaceCodec`1";
    private const string ListCodecAlias = "ListInterfaceCodec`1";
    private const string SetCodecAlias = "SetInterfaceCodec`1";
    private const string ReadOnlySetCodecAlias = "ReadOnlySetInterfaceCodec`1";
    private const string DictionaryCodecAlias = "DictionaryInterfaceCodec`2";
    private const string ReadOnlyDictionaryCodecAlias = "ReadOnlyDictionaryInterfaceCodec`2";
    private static readonly int[] ExpectedArray = [1, 2, 3];

    [Fact]
    public void CanSerializeCollectionExpressionReadOnlyList()
    {
        var original = new ReadOnlyListContainer { Values = ["a", "b"] };

        var result = RoundTrip(original);

        Assert.Collection(result.Values, value => Assert.Equal("a", value), value => Assert.Equal("b", value));
    }

    [Fact]
    public void CanCopyCollectionExpressionReadOnlyList()
    {
        var original = new ReadOnlyListContainer { Values = ["a", "b"] };

        var result = Copy(original);

        Assert.NotSame(original, result);
        Assert.Collection(result.Values, value => Assert.Equal("a", value), value => Assert.Equal("b", value));
    }

    [Fact]
    public void CanSerializeLinqEnumerableAsDeclaredInterface()
    {
        IEnumerable<int> original = Enumerable.Range(0, 10).Where(value => value % 2 == 0);

        var result = RoundTrip(original);

        Assert.Equal([0, 2, 4, 6, 8], result);
    }

    [Fact]
    public void CanCopyUnsupportedReadOnlyListAsDeclaredInterface()
    {
        IReadOnlyList<int> original = [1, 2, 3];

        var result = Copy(original);

        Assert.IsType<List<int>>(result);
        Assert.Equal(original, result);
    }

    [Fact]
    public void FallbackSerializationCapsCapacityHint()
    {
        IReadOnlyCollection<int> original = new MisreportedReadOnlyCollection<int>(
            [1, 2, 3],
            MaxInterfaceCollectionCapacityHint + 1);

        var result = RoundTrip(original);

        var list = Assert.IsType<List<int>>(result);
        Assert.Equal(ExpectedArray, list);
        Assert.Equal(MaxInterfaceCollectionCapacityHint, list.Capacity);
    }

    [Fact]
    public void FallbackCopyCapsCapacityHint()
    {
        IReadOnlyCollection<int> original = new MisreportedReadOnlyCollection<int>(
            [1, 2, 3],
            MaxInterfaceCollectionCapacityHint + 1);

        var result = Copy(original);

        var list = Assert.IsType<List<int>>(result);
        Assert.Equal(ExpectedArray, list);
        Assert.Equal(MaxInterfaceCollectionCapacityHint, list.Capacity);
    }

    [Fact]
    public void RuntimeConcreteCodecIsPreservedWhenAvailable()
    {
        var original = new IntReadOnlyListContainer
        {
            Values = new GeneratedReadOnlyList { Values = [1, 2, 3], Marker = "preserved" }
        };

        var result = RoundTrip(original);

        var concrete = Assert.IsType<GeneratedReadOnlyList>(result.Values);
        Assert.Equal("preserved", concrete.Marker);
        Assert.Equal([1, 2, 3], concrete);
    }

    [Fact]
    public void RuntimeConcreteSetComparerIsPreservedWhenAvailable()
    {
        var original = new SetContainer
        {
            Values = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "key" }
        };

        var result = RoundTrip(original);

        var set = Assert.IsType<HashSet<string>>(result.Values);
        Assert.Contains("KEY", set);
    }

#if NET8_0_OR_GREATER
    [Fact]
    public void CanSerializeFrozenCollectionsAsDeclaredInterfaces()
    {
        var original = new FrozenCollectionContainer
        {
            Set = new HashSet<string> { "a", "b" }.ToFrozenSet(),
            Dictionary = new Dictionary<string, int> { ["a"] = 1, ["b"] = 2 }.ToFrozenDictionary()
        };

        var result = RoundTrip(original);

        Assert.True(result.Set.SetEquals(["a", "b"]));
        Assert.Equal(1, result.Dictionary["a"]);
        Assert.Equal(2, result.Dictionary["b"]);
    }
#endif

    [Fact]
    public void CanSerializeGenericInterfaceFieldsUsingFallbackCodecTypes()
    {
        var original = new GenericInterfaceCollectionContainer
        {
            Enumerable = new UnknownList<int>(ExpectedArray),
            ReadOnlyCollection = new MisreportedReadOnlyCollection<int>(ExpectedArray, ExpectedArray.Length),
            ReadOnlyList = new UnknownList<int>(ExpectedArray),
            Collection = new UnknownList<int>(ExpectedArray),
            List = new UnknownList<int>(ExpectedArray),
            Set = new UnknownSet<string>(["a", "b"]),
#if NET5_0_OR_GREATER
            ReadOnlySet = new UnknownSet<string>(["c", "d"]),
#endif
            Dictionary = new UnknownDictionary<string, int>(new Dictionary<string, int> { ["a"] = 1, ["b"] = 2 }),
            ReadOnlyDictionary = new UnknownDictionary<string, int>(new Dictionary<string, int> { ["c"] = 3, ["d"] = 4 }),
        };

        var result = RoundTrip(original);

        var enumerable = Assert.IsType<List<int>>(result.Enumerable);
        Assert.Equal(ExpectedArray, enumerable);
        var readOnlyCollection = Assert.IsType<List<int>>(result.ReadOnlyCollection);
        Assert.Equal(ExpectedArray, readOnlyCollection);
        var readOnlyList = Assert.IsType<List<int>>(result.ReadOnlyList);
        Assert.Equal(ExpectedArray, readOnlyList);
        var collection = Assert.IsType<List<int>>(result.Collection);
        Assert.Equal(ExpectedArray, collection);
        var list = Assert.IsType<List<int>>(result.List);
        Assert.Equal(ExpectedArray, list);
        var set = Assert.IsType<HashSet<string>>(result.Set);
        Assert.True(set.SetEquals(["a", "b"]));
#if NET5_0_OR_GREATER
        var readOnlySet = Assert.IsType<HashSet<string>>(result.ReadOnlySet);
        Assert.True(readOnlySet.SetEquals(["c", "d"]));
#endif
        var dictionary = Assert.IsType<Dictionary<string, int>>(result.Dictionary);
        Assert.Equal(1, dictionary["a"]);
        Assert.Equal(2, dictionary["b"]);
        var readOnlyDictionary = Assert.IsType<Dictionary<string, int>>(result.ReadOnlyDictionary);
        Assert.Equal(3, readOnlyDictionary["c"]);
        Assert.Equal(4, readOnlyDictionary["d"]);
    }

#if !NETCOREAPP3_1
    [Fact]
    public Task EnumerableInterfaceFallback_Formatted_MatchesSnapshot()
        => VerifyFallbackBitStream<IEnumerable<int>>(new UnknownList<int>(ExpectedArray), EnumerableCodecAlias);

    [Fact]
    public Task ReadOnlyCollectionInterfaceFallback_Formatted_MatchesSnapshot()
        => VerifyFallbackBitStream<IReadOnlyCollection<int>>(new MisreportedReadOnlyCollection<int>(ExpectedArray, ExpectedArray.Length), ReadOnlyCollectionCodecAlias);

    [Fact]
    public Task ReadOnlyListInterfaceFallback_Formatted_MatchesSnapshot()
        => VerifyFallbackBitStream<IReadOnlyList<int>>(new UnknownList<int>(ExpectedArray), ReadOnlyListCodecAlias);

    [Fact]
    public Task CollectionInterfaceFallback_Formatted_MatchesSnapshot()
        => VerifyFallbackBitStream<ICollection<int>>(new UnknownList<int>(ExpectedArray), CollectionCodecAlias);

    [Fact]
    public Task ListInterfaceFallback_Formatted_MatchesSnapshot()
        => VerifyFallbackBitStream<IList<int>>(new UnknownList<int>(ExpectedArray), ListCodecAlias);

    [Fact]
    public Task SetInterfaceFallback_Formatted_MatchesSnapshot()
        => VerifyFallbackBitStream<ISet<string>>(new UnknownSet<string>(["a", "b"]), SetCodecAlias);

#if NET5_0_OR_GREATER
    [Fact]
    public Task ReadOnlySetInterfaceFallback_Formatted_MatchesSnapshot()
        => VerifyFallbackBitStream<IReadOnlySet<string>>(new UnknownSet<string>(["a", "b"]), ReadOnlySetCodecAlias);
#endif

    [Fact]
    public Task DictionaryInterfaceFallback_Formatted_MatchesSnapshot()
        => VerifyFallbackBitStream<IDictionary<string, int>>(new UnknownDictionary<string, int>(new Dictionary<string, int> { ["a"] = 1, ["b"] = 2 }), DictionaryCodecAlias);

    [Fact]
    public Task ReadOnlyDictionaryInterfaceFallback_Formatted_MatchesSnapshot()
        => VerifyFallbackBitStream<IReadOnlyDictionary<string, int>>(new UnknownDictionary<string, int>(new Dictionary<string, int> { ["a"] = 1, ["b"] = 2 }), ReadOnlyDictionaryCodecAlias);
#endif

    private static T RoundTrip<T>(T value)
    {
        using var serviceProvider = new ServiceCollection().AddSerializer().BuildServiceProvider();
        var serializer = serviceProvider.GetRequiredService<Serializer>();
        return serializer.Deserialize<T>(serializer.SerializeToArray(value));
    }

    private static T Copy<T>(T value)
    {
        using var serviceProvider = new ServiceCollection().AddSerializer().BuildServiceProvider();
        return serviceProvider.GetRequiredService<DeepCopier<T>>().Copy(value);
    }

#if !NETCOREAPP3_1
    private static Task VerifyFallbackBitStream<T>(T value, string codecAlias)
    {
        var formatted = FormatSerializedPayload(value);
        Assert.Contains($"TypeName \"{codecAlias}", formatted);
        return Verify(formatted, extension: "txt").UseDirectory("snapshots");
    }

    private static string FormatSerializedPayload<T>(T value)
    {
        using var serviceProvider = new ServiceCollection().AddSerializer().BuildServiceProvider();
        var serializer = serviceProvider.GetRequiredService<Serializer<T>>();
        var payload = serializer.SerializeToArray(value);
        using var session = serviceProvider.GetRequiredService<SerializerSessionPool>().GetSession();
        return BitStreamFormatter.Format(payload, session);
    }
#endif

    [GenerateSerializer]
    [Alias("Orleans.Serialization.UnitTests.InterfaceCollectionRegressionTests.ReadOnlyListContainer")]
    public sealed class ReadOnlyListContainer
    {
        [Id(0)]
        public IReadOnlyList<string> Values { get; set; }
    }

    [GenerateSerializer]
    [Alias("Orleans.Serialization.UnitTests.InterfaceCollectionRegressionTests.IntReadOnlyListContainer")]
    public sealed class IntReadOnlyListContainer
    {
        [Id(0)]
        public IReadOnlyList<int> Values { get; set; }
    }

    [GenerateSerializer]
    [Alias("Orleans.Serialization.UnitTests.InterfaceCollectionRegressionTests.SetContainer")]
    public sealed class SetContainer
    {
        [Id(0)]
        public ISet<string> Values { get; set; }
    }

    [GenerateSerializer]
    [Alias("Orleans.Serialization.UnitTests.InterfaceCollectionRegressionTests.GeneratedReadOnlyList")]
    public sealed class GeneratedReadOnlyList : IReadOnlyList<int>
    {
        [Id(0)]
        public List<int> Values { get; set; } = [];

        [Id(1)]
        public string Marker { get; set; }

        public int Count => Values.Count;

        public int this[int index] => Values[index];

        public IEnumerator<int> GetEnumerator() => Values.GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

#if NET8_0_OR_GREATER
    [GenerateSerializer]
    [Alias("Orleans.Serialization.UnitTests.InterfaceCollectionRegressionTests.FrozenCollectionContainer")]
    public sealed class FrozenCollectionContainer
    {
        [Id(0)]
        public IReadOnlySet<string> Set { get; set; }

        [Id(1)]
        public IReadOnlyDictionary<string, int> Dictionary { get; set; }
    }
#endif

    [GenerateSerializer]
    [Alias("Orleans.Serialization.UnitTests.InterfaceCollectionRegressionTests.GenericInterfaceCollectionContainer")]
    public sealed class GenericInterfaceCollectionContainer
    {
        [Id(0)]
        public IEnumerable<int> Enumerable { get; set; }

        [Id(1)]
        public IReadOnlyCollection<int> ReadOnlyCollection { get; set; }

        [Id(2)]
        public IReadOnlyList<int> ReadOnlyList { get; set; }

        [Id(3)]
        public ICollection<int> Collection { get; set; }

        [Id(4)]
        public IList<int> List { get; set; }

        [Id(5)]
        public ISet<string> Set { get; set; }

#if NET5_0_OR_GREATER
        [Id(6)]
        public IReadOnlySet<string> ReadOnlySet { get; set; }
#endif

        [Id(7)]
        public IDictionary<string, int> Dictionary { get; set; }

        [Id(8)]
        public IReadOnlyDictionary<string, int> ReadOnlyDictionary { get; set; }
    }
}

internal static class InterfaceCollectionTestHelpers
{
    public static List<int> CreateList(Random random)
    {
        var result = new List<int>();
        var length = random.Next(17) + 5;
        for (var i = 0; i < length; i++)
        {
            result.Add(random.Next());
        }

        return result;
    }

    public static HashSet<string> CreateSet(Random random)
    {
        var result = new HashSet<string>();
        var length = random.Next(17) + 5;
        for (var i = 0; i < length; i++)
        {
            result.Add(random.Next().ToString(CultureInfo.InvariantCulture));
        }

        return result;
    }

    public static Dictionary<string, int> CreateDictionary(Random random)
    {
        var result = new Dictionary<string, int>();
        var length = random.Next(17) + 5;
        for (var i = 0; i < length; i++)
        {
            result[random.Next().ToString(CultureInfo.InvariantCulture)] = random.Next();
        }

        return result;
    }

    public static bool SequenceEqual<T>(IEnumerable<T> left, IEnumerable<T> right) =>
        ReferenceEquals(left, right) || left is not null && right is not null && left.SequenceEqual(right);

    public static bool SetEqual<T>(IEnumerable<T> left, IEnumerable<T> right) =>
        ReferenceEquals(left, right) || left is not null && right is not null && left.ToHashSet().SetEquals(right);

    public static bool DictionaryEqual<TKey, TValue>(IEnumerable<KeyValuePair<TKey, TValue>> left, IEnumerable<KeyValuePair<TKey, TValue>> right) where TKey : notnull
    {
        if (ReferenceEquals(left, right))
        {
            return true;
        }

        if (left is null || right is null)
        {
            return false;
        }

        var rightDictionary = new Dictionary<TKey, TValue>();
        foreach (var (key, value) in right)
        {
            rightDictionary[key] = value;
        }

        var count = 0;
        foreach (var (key, value) in left)
        {
            count++;
            if (!rightDictionary.TryGetValue(key, out var result) || !EqualityComparer<TValue>.Default.Equals(value, result))
            {
                return false;
            }
        }

        return count == rightDictionary.Count;
    }
}

internal sealed class UnknownList<T>(IEnumerable<T> values) : IList<T>, IReadOnlyList<T>
{
    private readonly List<T> _values = [.. values];

    public int Count => _values.Count;

    public bool IsReadOnly => false;

    public T this[int index]
    {
        get => _values[index];
        set => _values[index] = value;
    }

    public void Add(T item) => _values.Add(item);

    public void Clear() => _values.Clear();

    public bool Contains(T item) => _values.Contains(item);

    public void CopyTo(T[] array, int arrayIndex) => _values.CopyTo(array, arrayIndex);

    public IEnumerator<T> GetEnumerator() => _values.GetEnumerator();

    public int IndexOf(T item) => _values.IndexOf(item);

    public void Insert(int index, T item) => _values.Insert(index, item);

    public bool Remove(T item) => _values.Remove(item);

    public void RemoveAt(int index) => _values.RemoveAt(index);

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

internal sealed class UnknownSet<T>(IEnumerable<T> values) : ISet<T>
#if NET5_0_OR_GREATER
    , IReadOnlySet<T>
#endif
{
    private readonly HashSet<T> _values = [.. values];

    public int Count => _values.Count;

    public bool IsReadOnly => false;

    public bool Add(T item) => _values.Add(item);

    void ICollection<T>.Add(T item) => Add(item);

    public void ExceptWith(IEnumerable<T> other) => _values.ExceptWith(other);

    public IEnumerator<T> GetEnumerator() => _values.GetEnumerator();

    public void IntersectWith(IEnumerable<T> other) => _values.IntersectWith(other);

    public bool IsProperSubsetOf(IEnumerable<T> other) => _values.IsProperSubsetOf(other);

    public bool IsProperSupersetOf(IEnumerable<T> other) => _values.IsProperSupersetOf(other);

    public bool IsSubsetOf(IEnumerable<T> other) => _values.IsSubsetOf(other);

    public bool IsSupersetOf(IEnumerable<T> other) => _values.IsSupersetOf(other);

    public bool Overlaps(IEnumerable<T> other) => _values.Overlaps(other);

    public bool SetEquals(IEnumerable<T> other) => _values.SetEquals(other);

    public void SymmetricExceptWith(IEnumerable<T> other) => _values.SymmetricExceptWith(other);

    public void UnionWith(IEnumerable<T> other) => _values.UnionWith(other);

    public void Clear() => _values.Clear();

    public bool Contains(T item) => _values.Contains(item);

    public void CopyTo(T[] array, int arrayIndex) => _values.CopyTo(array, arrayIndex);

    public bool Remove(T item) => _values.Remove(item);

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

internal sealed class UnknownDictionary<TKey, TValue>(IDictionary<TKey, TValue> values) : IDictionary<TKey, TValue>, IReadOnlyDictionary<TKey, TValue>
{
    private readonly Dictionary<TKey, TValue> _values = new(values);

    public int Count => _values.Count;

    public bool IsReadOnly => false;

    public ICollection<TKey> Keys => _values.Keys;

    public ICollection<TValue> Values => _values.Values;

    IEnumerable<TKey> IReadOnlyDictionary<TKey, TValue>.Keys => _values.Keys;

    IEnumerable<TValue> IReadOnlyDictionary<TKey, TValue>.Values => _values.Values;

    public TValue this[TKey key]
    {
        get => _values[key];
        set => _values[key] = value;
    }

    public void Add(TKey key, TValue value) => _values.Add(key, value);

    public void Add(KeyValuePair<TKey, TValue> item) => ((ICollection<KeyValuePair<TKey, TValue>>)_values).Add(item);

    public void Clear() => _values.Clear();

    public bool Contains(KeyValuePair<TKey, TValue> item) => ((ICollection<KeyValuePair<TKey, TValue>>)_values).Contains(item);

    public bool ContainsKey(TKey key) => _values.ContainsKey(key);

    public void CopyTo(KeyValuePair<TKey, TValue>[] array, int arrayIndex) => ((ICollection<KeyValuePair<TKey, TValue>>)_values).CopyTo(array, arrayIndex);

    public IEnumerator<KeyValuePair<TKey, TValue>> GetEnumerator() => _values.GetEnumerator();

    public bool Remove(TKey key) => _values.Remove(key);

    public bool Remove(KeyValuePair<TKey, TValue> item) => ((ICollection<KeyValuePair<TKey, TValue>>)_values).Remove(item);

    public bool TryGetValue(TKey key, out TValue value) => _values.TryGetValue(key, out value);

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

internal sealed class MisreportedReadOnlyCollection<T>(IEnumerable<T> values, int count) : IReadOnlyCollection<T>
{
    private readonly IReadOnlyList<T> _values = [.. values];

    public int Count { get; } = count;

    public IEnumerator<T> GetEnumerator() => _values.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
