using Orleans.Runtime;
using Orleans.Runtime.GrainDirectory;
using Orleans.Runtime.Utilities;
using Xunit;

namespace NonSilo.Tests.Utilities;

[TestCategory("BVT")]
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("GrainDirectory")]
public sealed class GrainIdUniformHashComparerTests
{
    [Fact]
    public void ComparerPreservesGrainIdentityAndUnsignedRingHash()
    {
        var comparer = GrainIdUniformHashComparer.Instance;
        var dictionary = new ConcurrentHashRangeDictionary<GrainId, int, GrainIdUniformHashComparer>(comparer);

        for (var i = 0; i < 128; i++)
        {
            var key = i.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var grainId = GrainId.Create("range-comparer", key);
            var equivalent = GrainId.Create("range-comparer", key);
            var differentType = GrainId.Create("other-type", key);
            var hash = comparer.GetHashCode(grainId);

            Assert.Equal(grainId.GetUniformHashCode(), hash);
            Assert.True(comparer.Equals(grainId, equivalent));
            Assert.False(comparer.Equals(grainId, differentType));
            Assert.Equal(comparer.GetHashCode(grainId), comparer.GetHashCode(equivalent));
            Assert.True(dictionary.TryAdd(grainId, i));
            Assert.True(dictionary.TryGetValue(equivalent, hash, out var value));
            Assert.Equal(i, value);
        }

        Assert.Equal(128, dictionary.Count);
    }

    [Fact]
    public void ConcreteComparerMapsSignedKeysToUnsignedCoordinates()
    {
        var dictionary = new ConcurrentHashRangeDictionary<int, int, IntComparer>(default);
        var keys = new[] { int.MinValue, -1, 0, 1, int.MaxValue };
        foreach (var key in keys)
        {
            Assert.True(dictionary.TryAdd(key, key));
            Assert.True(dictionary.TryGetValue(key, unchecked((uint)key), out var value));
            Assert.Equal(key, value);
            Assert.Equal(KeyValuePair.Create(key, key), Assert.Single(dictionary.EnumerateHash(unchecked((uint)key))));
        }

        Assert.Equal(
            new[] { -1, 0, 1 },
            dictionary.EnumerateRange(RingRange.Create(uint.MaxValue - 1, 1)).Select(static entry => entry.Key).Order());
        Assert.Empty(dictionary.EnumerateRange(RingRange.Empty));
        Assert.Equal(keys.Order(), dictionary.EnumerateRange(RingRange.Full).Select(static entry => entry.Key).Order());
    }

    private readonly struct IntComparer : IConsistentHashComparer<int>
    {
        public bool Equals(int x, int y) => x == y;
        public uint GetHashCode(int value) => unchecked((uint)value);
    }
}
