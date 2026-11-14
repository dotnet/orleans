using System;
using System.Collections.Generic;
using System.Collections.Frozen;
using System.Collections.Immutable;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Serialization.Cloning;
using Orleans.Serialization.Codecs;
using Xunit;

namespace Orleans.Serialization.UnitTests;

[Trait("Category", "BVT")]
[Trait("Suite", "BVT")]
[Trait("Provider", "None")]
[Trait("Area", "Serialization")]
public sealed class MixedDictionaryCopierTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("immutable")]
    [InlineData("immutable sorted")]
    [InlineData("frozen")]
    public void MixedDependenciesQueryTraitsOncePerCopyAndPreserveValuesAliasesAndComparers(string kind)
    {
        const int count = 1000;
        var keys = new CountingCopier<string>(new ShallowCopier<string>(), shallow: true);
        var values = new CountingCopier<List<int>>(new ListCopier<int>(new ShallowCopier<int>()), shallow: false);
        var shared = new List<int> { 13, 17 };
        var entries = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
        var originalKeys = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < count; index++)
        {
            var key = $"key{index}";
            entries.Add(key, shared);
            originalKeys.Add(key, key);
        }
        var (copier, input) = Create(kind, keys, values, entries);
        Assert.Equal(0, keys.Queries);
        Assert.Equal(0, values.Queries);
        keys.Ready = values.Ready = true;
        Assert.False(((IOptionalDeepCopier)copier).IsShallowCopyable());
        Assert.Equal(1, keys.Queries);
        Assert.Equal(1, values.Queries);
        using var services = new ServiceCollection().AddSerializer().BuildServiceProvider();
        List<int>? previousCopy = null;
        var queryCounts = new List<(int Keys, int Values)>();
        for (var iteration = 0; iteration < 3; iteration++)
        {
            keys.Queries = values.Queries = 0;
            values.Copies = 0;
            using var context = services.GetRequiredService<CopyContextPool>().GetContext();
            var result = Assert.IsAssignableFrom<IReadOnlyDictionary<string, List<int>>>(copier.DeepCopy(input, context));
            var keyQueries = keys.Queries;
            var valueQueries = values.Queries;
            queryCounts.Add((keyQueries, valueQueries));
            output.WriteLine($"{kind}: {count} entries; copy {iteration + 1}: key trait queries {keyQueries}, value trait queries {valueQueries}.");
            Assert.NotSame(input, result);
            Assert.Equal(count, result.Count);
            var copied = result["KEY0"];
            Assert.Equal(new[] { 13, 17 }, copied);
            Assert.NotSame(shared, copied);
            Assert.NotSame(previousCopy, copied);
            foreach (var entry in result)
            {
                Assert.Same(copied, entry.Value);
                Assert.Same(entry.Key, originalKeys[entry.Key]);
            }
            switch (result)
            {
                case ImmutableDictionary<string, List<int>> dictionary:
                    Assert.Same(((ImmutableDictionary<string, List<int>>)input).KeyComparer, dictionary.KeyComparer);
                    Assert.Same(((ImmutableDictionary<string, List<int>>)input).ValueComparer, dictionary.ValueComparer);
                    break;
                case ImmutableSortedDictionary<string, List<int>> dictionary:
                    Assert.Same(((ImmutableSortedDictionary<string, List<int>>)input).KeyComparer, dictionary.KeyComparer);
                    Assert.Same(((ImmutableSortedDictionary<string, List<int>>)input).ValueComparer, dictionary.ValueComparer);
                    break;
                case FrozenDictionary<string, List<int>> dictionary:
                    Assert.Same(((FrozenDictionary<string, List<int>>)input).Comparer, dictionary.Comparer);
                    break;
            }
            Assert.Same(result, copier.DeepCopy(input, context));
            Assert.Equal(keyQueries, keys.Queries);
            Assert.Equal(valueQueries, values.Queries);
            Assert.Equal(count, values.Copies);
            Assert.Equal(0, keys.Copies);
            copied[0] = 23;
            Assert.Equal(13, shared[0]);
            Assert.Equal(23, result["KEY999"][0]);
            previousCopy = copied;
        }
        Assert.All(queryCounts, counts =>
        {
            Assert.Equal(1, counts.Keys);
            Assert.Equal(1, counts.Values);
        });
    }

    private static (IDeepCopier Copier, object Input) Create(string kind, IDeepCopier<string> keys,
        IDeepCopier<List<int>> values, Dictionary<string, List<int>> entries)
        => kind switch
        {
            "immutable" => (new ImmutableDictionaryCopier<string, List<int>>(keys, values),
                entries.ToImmutableDictionary(StringComparer.OrdinalIgnoreCase, new ListValueComparer())),
            "immutable sorted" => (new ImmutableSortedDictionaryCopier<string, List<int>>(keys, values),
                entries.ToImmutableSortedDictionary(StringComparer.OrdinalIgnoreCase, new ListValueComparer())),
            "frozen" => (new FrozenDictionaryCopier<string, List<int>>(keys, values),
                entries.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase)),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
        };

    private sealed class ListValueComparer : IEqualityComparer<List<int>>
    {
        public bool Equals(List<int>? first, List<int>? second) => ReferenceEquals(first, second);
        public int GetHashCode(List<int> value) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(value);
    }

    private sealed class CountingCopier<T>(IDeepCopier<T> inner, bool shallow) : IDeepCopier<T>, IOptionalDeepCopier
    {
        public bool Ready { get; set; }
        public int Queries { get; set; }
        public int Copies { get; set; }

        public bool IsShallowCopyable()
        {
            Assert.True(Ready, "A consuming constructor queried an incomplete copier.");
            Queries++;
            return shallow;
        }

        public T? DeepCopy(T? input, CopyContext context)
        {
            Assert.True(Ready);
            Assert.False(shallow, "A shallow dependency must not be invoked.");
            Copies++;
            return inner.DeepCopy(input, context);
        }
    }
}
