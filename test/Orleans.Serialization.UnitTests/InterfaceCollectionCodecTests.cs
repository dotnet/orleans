using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
#if NET8_0_OR_GREATER
using System.Collections.Frozen;
#endif
using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using Orleans;
using Orleans.Serialization;
using Orleans.Serialization.Cloning;
using Orleans.Serialization.Codecs;
using Orleans.Serialization.Serializers;
using Orleans.Serialization.TestKit;
using Xunit;
using Xunit.Abstractions;

namespace Orleans.Serialization.UnitTests;

public class EnumerableInterfaceCodecTests(ITestOutputHelper output) : FieldCodecTester<IEnumerable<int>, IFieldCodec<IEnumerable<int>>>(output)
{
    protected override IFieldCodec<IEnumerable<int>> CreateCodec() => ServiceProvider.GetRequiredService<ICodecProvider>().GetCodec<IEnumerable<int>>();
    protected override IEnumerable<int> CreateValue() => InterfaceCollectionTestHelpers.CreateList(Random);
    protected override IEnumerable<int>[] TestValues => [null, Array.Empty<int>(), new List<int> { 1, 2, 3 }, CreateValue()];
    protected override bool Equals(IEnumerable<int> left, IEnumerable<int> right) => InterfaceCollectionTestHelpers.SequenceEqual(left, right);
}

public class EnumerableInterfaceCopierTests(ITestOutputHelper output) : CopierTester<IEnumerable<int>, IDeepCopier<IEnumerable<int>>>(output)
{
    protected override IDeepCopier<IEnumerable<int>> CreateCopier() => ServiceProvider.GetRequiredService<IDeepCopierProvider>().GetDeepCopier<IEnumerable<int>>();
    protected override IEnumerable<int> CreateValue() => InterfaceCollectionTestHelpers.CreateList(Random);
    protected override IEnumerable<int>[] TestValues => [null, Array.Empty<int>(), new List<int> { 1, 2, 3 }, CreateValue()];
    protected override bool Equals(IEnumerable<int> left, IEnumerable<int> right) => InterfaceCollectionTestHelpers.SequenceEqual(left, right);
}

public class ReadOnlyCollectionInterfaceCodecTests(ITestOutputHelper output) : FieldCodecTester<IReadOnlyCollection<int>, IFieldCodec<IReadOnlyCollection<int>>>(output)
{
    protected override IFieldCodec<IReadOnlyCollection<int>> CreateCodec() => ServiceProvider.GetRequiredService<ICodecProvider>().GetCodec<IReadOnlyCollection<int>>();
    protected override IReadOnlyCollection<int> CreateValue() => InterfaceCollectionTestHelpers.CreateList(Random);
    protected override IReadOnlyCollection<int>[] TestValues => [null, Array.Empty<int>(), new List<int> { 1, 2, 3 }, CreateValue()];
    protected override bool Equals(IReadOnlyCollection<int> left, IReadOnlyCollection<int> right) => InterfaceCollectionTestHelpers.SequenceEqual(left, right);
}

public class ReadOnlyCollectionInterfaceCopierTests(ITestOutputHelper output) : CopierTester<IReadOnlyCollection<int>, IDeepCopier<IReadOnlyCollection<int>>>(output)
{
    protected override IDeepCopier<IReadOnlyCollection<int>> CreateCopier() => ServiceProvider.GetRequiredService<IDeepCopierProvider>().GetDeepCopier<IReadOnlyCollection<int>>();
    protected override IReadOnlyCollection<int> CreateValue() => InterfaceCollectionTestHelpers.CreateList(Random);
    protected override IReadOnlyCollection<int>[] TestValues => [null, Array.Empty<int>(), new List<int> { 1, 2, 3 }, CreateValue()];
    protected override bool Equals(IReadOnlyCollection<int> left, IReadOnlyCollection<int> right) => InterfaceCollectionTestHelpers.SequenceEqual(left, right);
}

public class ReadOnlyListInterfaceCodecTests(ITestOutputHelper output) : FieldCodecTester<IReadOnlyList<int>, IFieldCodec<IReadOnlyList<int>>>(output)
{
    protected override IFieldCodec<IReadOnlyList<int>> CreateCodec() => ServiceProvider.GetRequiredService<ICodecProvider>().GetCodec<IReadOnlyList<int>>();
    protected override IReadOnlyList<int> CreateValue() => InterfaceCollectionTestHelpers.CreateList(Random);
    protected override IReadOnlyList<int>[] TestValues => [null, Array.Empty<int>(), new List<int> { 1, 2, 3 }, CreateValue()];
    protected override bool Equals(IReadOnlyList<int> left, IReadOnlyList<int> right) => InterfaceCollectionTestHelpers.SequenceEqual(left, right);
}

public class ReadOnlyListInterfaceCopierTests(ITestOutputHelper output) : CopierTester<IReadOnlyList<int>, IDeepCopier<IReadOnlyList<int>>>(output)
{
    protected override IDeepCopier<IReadOnlyList<int>> CreateCopier() => ServiceProvider.GetRequiredService<IDeepCopierProvider>().GetDeepCopier<IReadOnlyList<int>>();
    protected override IReadOnlyList<int> CreateValue() => InterfaceCollectionTestHelpers.CreateList(Random);
    protected override IReadOnlyList<int>[] TestValues => [null, Array.Empty<int>(), new List<int> { 1, 2, 3 }, CreateValue()];
    protected override bool Equals(IReadOnlyList<int> left, IReadOnlyList<int> right) => InterfaceCollectionTestHelpers.SequenceEqual(left, right);
}

public class CollectionInterfaceCodecTests(ITestOutputHelper output) : FieldCodecTester<ICollection<int>, IFieldCodec<ICollection<int>>>(output)
{
    protected override IFieldCodec<ICollection<int>> CreateCodec() => ServiceProvider.GetRequiredService<ICodecProvider>().GetCodec<ICollection<int>>();
    protected override ICollection<int> CreateValue() => InterfaceCollectionTestHelpers.CreateList(Random);
    protected override ICollection<int>[] TestValues => [null, new List<int>(), new List<int> { 1, 2, 3 }, CreateValue()];
    protected override bool Equals(ICollection<int> left, ICollection<int> right) => InterfaceCollectionTestHelpers.SequenceEqual(left, right);
}

public class CollectionInterfaceCopierTests(ITestOutputHelper output) : CopierTester<ICollection<int>, IDeepCopier<ICollection<int>>>(output)
{
    protected override IDeepCopier<ICollection<int>> CreateCopier() => ServiceProvider.GetRequiredService<IDeepCopierProvider>().GetDeepCopier<ICollection<int>>();
    protected override ICollection<int> CreateValue() => InterfaceCollectionTestHelpers.CreateList(Random);
    protected override ICollection<int>[] TestValues => [null, new List<int>(), new List<int> { 1, 2, 3 }, CreateValue()];
    protected override bool Equals(ICollection<int> left, ICollection<int> right) => InterfaceCollectionTestHelpers.SequenceEqual(left, right);
}

public class ListInterfaceCodecTests(ITestOutputHelper output) : FieldCodecTester<IList<int>, IFieldCodec<IList<int>>>(output)
{
    protected override IFieldCodec<IList<int>> CreateCodec() => ServiceProvider.GetRequiredService<ICodecProvider>().GetCodec<IList<int>>();
    protected override IList<int> CreateValue() => InterfaceCollectionTestHelpers.CreateList(Random);
    protected override IList<int>[] TestValues => [null, new List<int>(), new List<int> { 1, 2, 3 }, CreateValue()];
    protected override bool Equals(IList<int> left, IList<int> right) => InterfaceCollectionTestHelpers.SequenceEqual(left, right);
}

public class ListInterfaceCopierTests(ITestOutputHelper output) : CopierTester<IList<int>, IDeepCopier<IList<int>>>(output)
{
    protected override IDeepCopier<IList<int>> CreateCopier() => ServiceProvider.GetRequiredService<IDeepCopierProvider>().GetDeepCopier<IList<int>>();
    protected override IList<int> CreateValue() => InterfaceCollectionTestHelpers.CreateList(Random);
    protected override IList<int>[] TestValues => [null, new List<int>(), new List<int> { 1, 2, 3 }, CreateValue()];
    protected override bool Equals(IList<int> left, IList<int> right) => InterfaceCollectionTestHelpers.SequenceEqual(left, right);
}

public class SetInterfaceCodecTests(ITestOutputHelper output) : FieldCodecTester<ISet<string>, IFieldCodec<ISet<string>>>(output)
{
    protected override IFieldCodec<ISet<string>> CreateCodec() => ServiceProvider.GetRequiredService<ICodecProvider>().GetCodec<ISet<string>>();
    protected override ISet<string> CreateValue() => InterfaceCollectionTestHelpers.CreateSet(Random);
    protected override ISet<string>[] TestValues => [null, new HashSet<string>(), new HashSet<string> { "a", "b" }, CreateValue()];
    protected override bool Equals(ISet<string> left, ISet<string> right) => InterfaceCollectionTestHelpers.SetEqual(left, right);
}

public class SetInterfaceCopierTests(ITestOutputHelper output) : CopierTester<ISet<string>, IDeepCopier<ISet<string>>>(output)
{
    protected override IDeepCopier<ISet<string>> CreateCopier() => ServiceProvider.GetRequiredService<IDeepCopierProvider>().GetDeepCopier<ISet<string>>();
    protected override ISet<string> CreateValue() => InterfaceCollectionTestHelpers.CreateSet(Random);
    protected override ISet<string>[] TestValues => [null, new HashSet<string>(), new HashSet<string> { "a", "b" }, CreateValue()];
    protected override bool Equals(ISet<string> left, ISet<string> right) => InterfaceCollectionTestHelpers.SetEqual(left, right);
}

#if NET5_0_OR_GREATER
public class ReadOnlySetInterfaceCodecTests(ITestOutputHelper output) : FieldCodecTester<IReadOnlySet<string>, IFieldCodec<IReadOnlySet<string>>>(output)
{
    protected override IFieldCodec<IReadOnlySet<string>> CreateCodec() => ServiceProvider.GetRequiredService<ICodecProvider>().GetCodec<IReadOnlySet<string>>();
    protected override IReadOnlySet<string> CreateValue() => InterfaceCollectionTestHelpers.CreateSet(Random);
    protected override IReadOnlySet<string>[] TestValues => [null, new HashSet<string>(), new HashSet<string> { "a", "b" }, CreateValue()];
    protected override bool Equals(IReadOnlySet<string> left, IReadOnlySet<string> right) => InterfaceCollectionTestHelpers.SetEqual(left, right);
}

public class ReadOnlySetInterfaceCopierTests(ITestOutputHelper output) : CopierTester<IReadOnlySet<string>, IDeepCopier<IReadOnlySet<string>>>(output)
{
    protected override IDeepCopier<IReadOnlySet<string>> CreateCopier() => ServiceProvider.GetRequiredService<IDeepCopierProvider>().GetDeepCopier<IReadOnlySet<string>>();
    protected override IReadOnlySet<string> CreateValue() => InterfaceCollectionTestHelpers.CreateSet(Random);
    protected override IReadOnlySet<string>[] TestValues => [null, new HashSet<string>(), new HashSet<string> { "a", "b" }, CreateValue()];
    protected override bool Equals(IReadOnlySet<string> left, IReadOnlySet<string> right) => InterfaceCollectionTestHelpers.SetEqual(left, right);
}
#endif

public class DictionaryInterfaceCodecTests(ITestOutputHelper output) : FieldCodecTester<IDictionary<string, int>, IFieldCodec<IDictionary<string, int>>>(output)
{
    protected override IFieldCodec<IDictionary<string, int>> CreateCodec() => ServiceProvider.GetRequiredService<ICodecProvider>().GetCodec<IDictionary<string, int>>();
    protected override IDictionary<string, int> CreateValue() => InterfaceCollectionTestHelpers.CreateDictionary(Random);
    protected override IDictionary<string, int>[] TestValues => [null, new Dictionary<string, int>(), new Dictionary<string, int> { ["a"] = 1, ["b"] = 2 }, CreateValue()];
    protected override bool Equals(IDictionary<string, int> left, IDictionary<string, int> right) => InterfaceCollectionTestHelpers.DictionaryEqual(left, right);
}

public class DictionaryInterfaceCopierTests(ITestOutputHelper output) : CopierTester<IDictionary<string, int>, IDeepCopier<IDictionary<string, int>>>(output)
{
    protected override IDeepCopier<IDictionary<string, int>> CreateCopier() => ServiceProvider.GetRequiredService<IDeepCopierProvider>().GetDeepCopier<IDictionary<string, int>>();
    protected override IDictionary<string, int> CreateValue() => InterfaceCollectionTestHelpers.CreateDictionary(Random);
    protected override IDictionary<string, int>[] TestValues => [null, new Dictionary<string, int>(), new Dictionary<string, int> { ["a"] = 1, ["b"] = 2 }, CreateValue()];
    protected override bool Equals(IDictionary<string, int> left, IDictionary<string, int> right) => InterfaceCollectionTestHelpers.DictionaryEqual(left, right);
}

public class ReadOnlyDictionaryInterfaceCodecTests(ITestOutputHelper output) : FieldCodecTester<IReadOnlyDictionary<string, int>, IFieldCodec<IReadOnlyDictionary<string, int>>>(output)
{
    protected override IFieldCodec<IReadOnlyDictionary<string, int>> CreateCodec() => ServiceProvider.GetRequiredService<ICodecProvider>().GetCodec<IReadOnlyDictionary<string, int>>();
    protected override IReadOnlyDictionary<string, int> CreateValue() => InterfaceCollectionTestHelpers.CreateDictionary(Random);
    protected override IReadOnlyDictionary<string, int>[] TestValues => [null, new Dictionary<string, int>(), new Dictionary<string, int> { ["a"] = 1, ["b"] = 2 }, CreateValue()];
    protected override bool Equals(IReadOnlyDictionary<string, int> left, IReadOnlyDictionary<string, int> right) => InterfaceCollectionTestHelpers.DictionaryEqual(left, right);
}

public class ReadOnlyDictionaryInterfaceCopierTests(ITestOutputHelper output) : CopierTester<IReadOnlyDictionary<string, int>, IDeepCopier<IReadOnlyDictionary<string, int>>>(output)
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
    private readonly List<T> _values = [with(values)];

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

internal sealed class MisreportedReadOnlyCollection<T>(IEnumerable<T> values, int count) : IReadOnlyCollection<T>
{
    private readonly IReadOnlyList<T> _values = [.. values];

    public int Count { get; } = count;

    public IEnumerator<T> GetEnumerator() => _values.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
