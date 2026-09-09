using System.Collections;
using Orleans.Runtime.GrainDirectory;
using Orleans.Runtime.Utilities;
using Xunit;

namespace NonSilo.Tests.Utilities;

[TestCategory("BVT")]
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Runtime")]
public sealed class ConcurrentHashRangeDictionaryTests
{
    private static readonly uint[] Hashes =
    [
        0, 1, 2, 9, 10, 11, 19, 20, 21,
        0x07FFFFFE, 0x07FFFFFF, 0x08000000, 0x08000001,
        0x08000010, 0x08000011, 0x0800001F, 0x08000020, 0x08000021,
        0x0FFFFFFF, 0x10000000, 0x17FFFFFF, 0x18000000, 0x18000001,
        uint.MaxValue - 2, uint.MaxValue - 1, uint.MaxValue, 20, 0
    ];

    [Fact]
    public void ConstructorAndPointOperations_RejectInvalidArguments()
    {
        Assert.Throws<ArgumentNullException>("comparer", () => new ConcurrentHashRangeDictionary<string, int, TestHashComparer<string>>(null!));
        Assert.Throws<ArgumentOutOfRangeException>("capacity", () => new ConcurrentHashRangeDictionary<string, int, TestHashComparer<string>>(new(_ => 0), capacity: -1));
        Assert.Throws<ArgumentOutOfRangeException>("capacity", () => new ConcurrentHashRangeDictionary<string, int, TestHashComparer<string>>(new(_ => 0), capacity: int.MaxValue));
        Assert.Throws<ArgumentOutOfRangeException>("concurrencyLevel", () => new ConcurrentHashRangeDictionary<string, int, TestHashComparer<string>>(new(_ => 0), concurrencyLevel: 0));
        Assert.Throws<ArgumentOutOfRangeException>("concurrencyLevel", () => new ConcurrentHashRangeDictionary<string, int, TestHashComparer<string>>(new(_ => 0), concurrencyLevel: -2));
        Assert.Throws<ArgumentOutOfRangeException>("concurrencyLevel", () => new ConcurrentHashRangeDictionary<string, int, TestHashComparer<string>>(new(_ => 0), concurrencyLevel: int.MaxValue));

        var dictionary = new ConcurrentHashRangeDictionary<string, int, TestHashComparer<string>>(new(_ => 0), capacity: 0);
        Assert.Throws<ArgumentNullException>("key", () => dictionary.TryAdd(null!, 1));
        Assert.Throws<ArgumentNullException>("key", () => dictionary.TryGetValue(null!, out _));
        Assert.Throws<ArgumentNullException>("key", () => dictionary.TryGetValue(null!, 0, out _));
        Assert.Throws<ArgumentNullException>("key", () => dictionary.TryUpdate(null!, 2, 1));
        Assert.Throws<ArgumentNullException>("key", () => dictionary.TryRemove(null!, out _));
        Assert.Throws<ArgumentNullException>("key", () => dictionary.TryRemove(KeyValuePair.Create<string, int>(null!, 1)));
        Assert.Throws<ArgumentNullException>("key", () => dictionary[null!] = 1);
        Assert.Throws<ArgumentNullException>("visitor", () => dictionary.VisitRange<object>(RingRange.Full, new(), null!));
        Assert.Throws<ArgumentNullException>("removed", () => dictionary.RemoveRange(RingRange.Full, null!));
        Assert.Equal(0, dictionary.Count);
        Assert.Empty(dictionary);
        Assert.True(dictionary.TryAdd("valid", 17));
        Assert.Equal(17, dictionary["valid"]);
        Assert.Equal(1, dictionary.Count);
    }

    [Fact]
    public void TryAddAndIndexer_DistinguishInsertionDuplicateAndReplacement()
    {
        var dictionary = new ConcurrentHashRangeDictionary<int, string, TestHashComparer<int>>(new(key => (uint)key));
        Assert.False(dictionary.TryGetValue(1, out var missing));
        Assert.Null(missing);
        Assert.Throws<KeyNotFoundException>(() => dictionary[1]);
        Assert.True(dictionary.TryAdd(1, "original"));
        Assert.False(dictionary.TryAdd(1, "duplicate"));
        Assert.True(dictionary.TryGetValue(1, out var original));
        Assert.Equal("original", original);
        Assert.Equal(1, dictionary.Count);

        dictionary[1] = "replacement";
        Assert.Equal("replacement", dictionary[1]);
        Assert.Equal(1, dictionary.Count);
        dictionary[2] = "inserted";
        Assert.Equal(2, dictionary.Count);
        AssertEntries([KeyValuePair.Create(1, "replacement"), KeyValuePair.Create(2, "inserted")], dictionary);
    }

    [Fact]
    public void TryUpdate_UsesExpectedValueEqualityWithoutChangingCount()
    {
        var original = new Payload(17);
        var equalButDistinct = new Payload(17);
        var replacement = new Payload(29);
        var dictionary = new ConcurrentHashRangeDictionary<int, Payload, TestHashComparer<int>>(new(_ => 7));
        Assert.True(dictionary.TryAdd(1, original));
        Assert.False(dictionary.TryUpdate(2, replacement, original));
        Assert.False(dictionary.TryUpdate(1, replacement, new Payload(18)));
        Assert.Same(original, dictionary[1]);
        Assert.NotSame(original, equalButDistinct);
        Assert.True(dictionary.TryUpdate(1, replacement, equalButDistinct));
        Assert.Same(replacement, dictionary[1]);
        Assert.Equal(1, dictionary.Count);
        Assert.False(dictionary.TryUpdate(1, original, equalButDistinct));
        Assert.Same(replacement, Assert.Single(dictionary).Value);
    }

    [Fact]
    public void TryRemove_ReturnsActualValueAndConditionallyPreservesReplacement()
    {
        var dictionary = new ConcurrentHashRangeDictionary<int, Payload, TestHashComparer<int>>(new(_ => 7));
        var original = new Payload(17);
        var replacement = new Payload(29);
        dictionary[1] = original;
        dictionary[2] = new Payload(41);
        dictionary[1] = replacement;

        Assert.False(dictionary.TryRemove(KeyValuePair.Create(1, original)));
        Assert.Same(replacement, dictionary[1]);
        Assert.Equal(2, dictionary.Count);
        Assert.True(dictionary.TryRemove(KeyValuePair.Create(1, new Payload(29))));
        Assert.False(dictionary.TryRemove(KeyValuePair.Create(1, replacement)));
        Assert.False(dictionary.TryGetValue(1, out var missing));
        Assert.Null(missing);
        Assert.Equal(1, dictionary.Count);
        Assert.True(dictionary.TryRemove(2, out var removed));
        Assert.Equal(new Payload(41), removed);
        Assert.False(dictionary.TryRemove(2, out missing));
        Assert.Null(missing);
        Assert.Empty(dictionary);
        Assert.Equal(0, dictionary.Count);
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(false, 1)]
    [InlineData(false, 2)]
    [InlineData(true, 0)]
    [InlineData(true, 1)]
    [InlineData(true, 2)]
    public void TryRemove_CollisionChainHeadMiddleOrTailPreservesEveryNeighbor(bool conditional, int removedKey)
    {
        var dictionary = new ConcurrentHashRangeDictionary<int, int, TestHashComparer<int>>(new(_ => 17), capacity: 32, concurrencyLevel: 1);
        for (var key = 0; key < 3; key++)
        {
            Assert.True(dictionary.TryAdd(key, key + 41));
        }

        if (conditional)
        {
            Assert.True(dictionary.TryRemove(KeyValuePair.Create(removedKey, removedKey + 41)));
        }
        else
        {
            Assert.True(dictionary.TryRemove(removedKey, out var removedValue));
            Assert.Equal(removedKey + 41, removedValue);
        }

        Assert.False(dictionary.TryGetValue(removedKey, out var missing));
        Assert.Equal(0, missing);
        Assert.Equal(2, dictionary.Count);
        var expected = Enumerable.Range(0, 3).Where(key => key != removedKey).Select(key => KeyValuePair.Create(key, key + 41)).ToArray();
        AssertEntries(expected, dictionary);
        AssertEntries(expected, dictionary.EnumerateHash(17));
        foreach (var (key, value) in expected)
        {
            Assert.Equal(value, dictionary[key]);
        }
    }

    [Fact]
    public void NullValue_IsPresentAndParticipatesInConditionalUpdateAndRemoval()
    {
        var dictionary = new ConcurrentHashRangeDictionary<int, string?, TestHashComparer<int>>(new(_ => 17));
        Assert.True(dictionary.TryAdd(1, null));
        Assert.True(dictionary.TryGetValue(1, out var value));
        Assert.Null(value);
        Assert.False(dictionary.TryGetValue(2, out _));
        Assert.False(dictionary.TryAdd(1, "duplicate"));
        Assert.Equal(1, dictionary.Count);
        Assert.False(dictionary.TryRemove(KeyValuePair.Create<int, string?>(1, "not-null")));
        Assert.True(dictionary.TryUpdate(1, "replacement", null));
        Assert.Equal("replacement", dictionary[1]);
        Assert.False(dictionary.TryUpdate(1, "wrong", null));
        Assert.True(dictionary.TryUpdate(1, null, "replacement"));
        Assert.True(dictionary.TryRemove(KeyValuePair.Create<int, string?>(1, null)));
        Assert.False(dictionary.TryGetValue(1, out _));
        Assert.Empty(dictionary);
        Assert.Equal(0, dictionary.Count);
    }

    [Fact]
    public void KeyEquality_IsIndependentOfRangeCoordinateAndOrdinaryHash()
    {
        var comparer = new KeyComparer();
        var dictionary = new ConcurrentHashRangeDictionary<Key, string, TestHashComparer<Key>>(new(key => key.Hash, comparer));
        var key = new Key(1, 0x80000000);
        var equalKey = new Key(1, 0x80000000);
        var collision = new Key(2, 0x80000000);
        var outsider = new Key(3, 0x10000000);
        Assert.True(dictionary.TryAdd(key, "first"));
        Assert.False(dictionary.TryAdd(equalKey, "duplicate"));
        Assert.True(dictionary.TryAdd(collision, "collision"));
        Assert.True(dictionary.TryAdd(outsider, "outside"));
        Assert.Equal("first", dictionary[equalKey]);
        Assert.True(dictionary.TryUpdate(equalKey, "updated", "first"));
        Assert.Equal("updated", dictionary[key]);
        var range = dictionary.EnumerateHash(key.Hash).OrderBy(pair => pair.Key.Id).ToArray();
        Assert.Equal(new[] { "updated", "collision" }, range.Select(pair => pair.Value));
        Assert.Same(key, range[0].Key);
        Assert.Same(collision, range[1].Key);
        Assert.True(dictionary.TryRemove(equalKey, out var removed));
        Assert.Equal("updated", removed);
        Assert.Equal("collision", dictionary[collision]);
        Assert.Equal("outside", dictionary[outsider]);
        Assert.Equal(2, dictionary.Count);
        // KeyComparer.GetHashCode throws: every point operation above must use the stable coordinate.
    }

    [Fact]
    public void PrehashedLookup_DoesNotRecomputeHashAndDistinguishesCollisions()
    {
        var calls = 0;
        var dictionary = new ConcurrentHashRangeDictionary<int, string, TestHashComparer<int>>(new(_ =>
        {
            calls++;
            return 17;
        }));
        dictionary[1] = "one";
        dictionary[2] = "two";
        calls = 0;
        Assert.True(dictionary.TryGetValue(2, 17, out var value));
        Assert.Equal("two", value);
        Assert.False(dictionary.TryGetValue(3, 17, out var missing));
        Assert.Null(missing);
        Assert.Equal(0, calls);
        Assert.True(dictionary.TryGetValue(1, out value));
        Assert.Equal("one", value);
        Assert.Equal(1, calls);
        Assert.Equal(2, dictionary.Count);
    }

    [Theory]
    [InlineData("Empty")]
    [InlineData("Full")]
    [InlineData("EqualEndpointsFull")]
    [InlineData("Zero")]
    [InlineData("Max")]
    [InlineData("Ordinary")]
    [InlineData("Wrapped")]
    [InlineData("SamePrefixWrapped")]
    [InlineData("InteriorBuckets")]
    [InlineData("PrefixBoundary")]
    public void RangeApis_RespectExclusiveStartInclusiveEndAndRetainCollisions(string scenario)
    {
        var (range, contains) = GetRangeCase(scenario);
        var dictionary = new ConcurrentHashRangeDictionary<int, int, TestHashComparer<int>>(new(key => Hashes[key]), capacity: 32, concurrencyLevel: 1);
        var all = Hashes.Select((_, key) => KeyValuePair.Create(key, key * 13 + 7)).ToArray();
        foreach (var (key, value) in all)
        {
            Assert.True(dictionary.TryAdd(key, value));
        }

        var expected = all.Where(pair => contains(Hashes[pair.Key])).ToArray();
        AssertEntries(expected, dictionary.EnumerateRange(range));
        var visited = new List<KeyValuePair<int, int>>();
        dictionary.VisitRange(range, visited, static (state, key, value) => state.Add(KeyValuePair.Create(key, value)));
        AssertEntries(expected, visited);
        AssertEntries(all, dictionary);
        Assert.Equal(all.Length, dictionary.Count);

        var sentinel = KeyValuePair.Create(-1, -71);
        var removed = new List<KeyValuePair<int, int>> { sentinel };
        // Writers are quiescent: this is a complete extraction, not a concurrent snapshot promise.
        dictionary.RemoveRange(range, removed);
        Assert.Equal(sentinel, removed[0]);
        AssertEntries(expected, removed.Skip(1));
        AssertEntries(all.Where(pair => !contains(Hashes[pair.Key])), dictionary);
        Assert.Equal(all.Length - expected.Length, dictionary.Count);
        dictionary.RemoveRange(range, removed);
        AssertEntries(expected, removed.Skip(1));
    }

    [Fact]
    public void RangeOperations_HashOnlyIntersectingBoundaryBucketsAndSkipFullyCoveredInteriors()
    {
        var calls = new List<int>();
        var armed = false;
        var dictionary = new ConcurrentHashRangeDictionary<int, int, TestHashComparer<int>>(new(key =>
        {
            if (armed)
            {
                calls.Add(key);
            }

            return Hashes[key];
        }), capacity: 24, concurrencyLevel: 1);
        for (var i = 0; i < Hashes.Length; i++)
        {
            dictionary[i] = i + 17;
        }

        var range = RingRange.Create(10, 20);
        var expected = Enumerable.Range(0, Hashes.Length)
            .Where(key => Hashes[key] > 10 && Hashes[key] <= 20)
            .Select(key => KeyValuePair.Create(key, key + 17)).ToArray();
        armed = true;
        AssertEntries(expected, dictionary.EnumerateRange(range));
        var visited = new List<KeyValuePair<int, int>>();
        dictionary.VisitRange(range, visited, static (state, key, value) => state.Add(KeyValuePair.Create(key, value)));
        AssertEntries(expected, visited);
        AssertEntries(expected.Where(pair => Hashes[pair.Key] == 20), dictionary.EnumerateHash(20));
        // Capacity 24 yields 32 buckets with initial occupancy headroom.
        Assert.All(calls, key => Assert.Equal(0u, Hashes[key] >> 27));
        calls.Clear();

        var interiorRange = RingRange.Create(0x07FFFFFE, 0x18000001);
        var interiorExpected = Enumerable.Range(0, Hashes.Length)
            .Where(key => Hashes[key] > 0x07FFFFFE && Hashes[key] <= 0x18000001)
            .Select(key => KeyValuePair.Create(key, key + 17)).ToArray();
        AssertEntries(interiorExpected, dictionary.EnumerateRange(interiorRange));
        Assert.All(calls, key => Assert.True((Hashes[key] >> 27) is 0 or 3,
            $"Key {key} in prefix {Hashes[key] >> 27} was hashed even though it is outside the range or in a fully covered interior bucket."));
        calls.Clear();
        AssertEntries(Enumerable.Range(0, Hashes.Length).Select(key => KeyValuePair.Create(key, key + 17)), dictionary);
        Assert.Empty(calls);

        var removed = new List<KeyValuePair<int, int>>();
        dictionary.RemoveRange(range, removed);
        armed = false;
        AssertEntries(expected, removed);
        Assert.All(calls, key => Assert.Equal(0u, Hashes[key] >> 27));
        foreach (var candidate in expected)
        {
            Assert.Contains(candidate.Key, calls);
        }

        Assert.Equal(Hashes.Length - expected.Length, dictionary.Count);
        AssertEntries(Enumerable.Range(0, Hashes.Length).Where(key => Hashes[key] <= 10 || Hashes[key] > 20)
            .Select(key => KeyValuePair.Create(key, key + 17)), dictionary);
    }

    [Fact]
    public async Task GrowthHashFailurePreservesPublishedTableAndCommittedInsertion()
    {
        var fail = false;
        var failure = new InvalidOperationException("controlled growth hash fault");
        var dictionary = new ConcurrentHashRangeDictionary<int, int, TestHashComparer<int>>(new(key =>
        {
            if (fail && key == 1)
            {
                throw failure;
            }

            return unchecked((uint)key << 30);
        }), capacity: 1, concurrencyLevel: 1);
        dictionary[1] = 17;
        dictionary[2] = 29;
        var enumerator = dictionary.GetEnumerator();
        Assert.True(enumerator.MoveNext());
        var observed = new List<KeyValuePair<int, int>> { enumerator.Current };

        fail = true;
        Assert.Same(failure, Assert.Throws<InvalidOperationException>(() => dictionary.TryAdd(3, 41)));
        fail = false;
        AssertEntries([KeyValuePair.Create(1, 17), KeyValuePair.Create(2, 29), KeyValuePair.Create(3, 41)], dictionary);
        Assert.Equal(3, dictionary.Count);
        while (enumerator.MoveNext())
        {
            observed.Add(enumerator.Current);
        }

        Assert.Contains(KeyValuePair.Create(1, 17), observed);
        Assert.Contains(KeyValuePair.Create(2, 29), observed);
        Assert.Equal(observed.Count, observed.Select(static entry => entry.Key).Distinct().Count());
        await Task.Run(() =>
        {
            Assert.True(dictionary.TryUpdate(3, 43, 41));
            Assert.True(dictionary.TryAdd(4, 53));
        }, TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        AssertEntries(
            [KeyValuePair.Create(1, 17), KeyValuePair.Create(2, 29), KeyValuePair.Create(3, 43), KeyValuePair.Create(4, 53)],
            dictionary);
        Assert.Equal(4, dictionary.Count);
    }

    [Fact]
    public void PresizingAccommodatesUniformPopulationWithoutRehashing()
    {
        const int count = 16_384;
        var hashes = 0;
        var dictionary = new ConcurrentHashRangeDictionary<int, int, TestHashComparer<int>>(new(key =>
        {
            hashes++;
            return unchecked((uint)key * 2654435761);
        }), capacity: count, concurrencyLevel: 32);
        for (var i = 0; i < count; i++)
        {
            Assert.True(dictionary.TryAdd(i, i));
        }

        Assert.Equal(count, hashes);
        Assert.Equal(count, dictionary.Count);
        Assert.Equal(Enumerable.Range(0, count), dictionary.Select(static entry => entry.Key).Order());
    }

    [Fact]
    public void Enumeration_CapturesAtFirstMoveAndSupportsResetAndAdapters()
    {
        var dictionary = new ConcurrentHashRangeDictionary<int, int, TestHashComparer<int>>(new(key => (uint)key));
        var enumerator = dictionary.GetEnumerator();
        dictionary[1] = 17;
        Assert.True(enumerator.MoveNext());
        Assert.Equal(KeyValuePair.Create(1, 17), enumerator.Current);
        Assert.False(enumerator.MoveNext());
        dictionary[2] = 29;
        enumerator.Reset();
        var results = new List<KeyValuePair<int, int>>();
        while (enumerator.MoveNext())
        {
            results.Add(enumerator.Current);
        }

        enumerator.Dispose();
        var expected = new[] { KeyValuePair.Create(1, 17), KeyValuePair.Create(2, 29) };
        AssertEntries(expected, results);
        AssertEntries(expected, (IEnumerable<KeyValuePair<int, int>>)dictionary);
        AssertEntries(expected, ((IEnumerable)dictionary).Cast<KeyValuePair<int, int>>());
        AssertEntries(expected, ((IEnumerable)dictionary.EnumerateRange(RingRange.Full)).Cast<KeyValuePair<int, int>>());
        Assert.Equal(2, dictionary.Count);
    }

    [Fact]
    public void RemoveRange_MaterializesAllCandidatesAndReservesCapacityBeforeFirstRemoval()
    {
        Action? onHash = null;
        var dictionary = new ConcurrentHashRangeDictionary<int, Payload, TestHashComparer<int>>(new(key =>
        {
            onHash?.Invoke();
            return (uint)key << 28;
        }), capacity: 32, concurrencyLevel: 1);
        for (var key = 1; key <= 4; key++)
        {
            dictionary[key] = new Payload(key * 17);
        }

        // Use observed order only to place a fault/mutation, not as an ordering contract.
        var candidates = dictionary.EnumerateRange(RingRange.Full).ToArray();
        var sentinel = KeyValuePair.Create(-1, new Payload(-1));
        var removed = new List<KeyValuePair<int, Payload>>(1) { sentinel };
        var replacements = candidates.Skip(1).ToDictionary(pair => pair.Key, pair => new Payload(pair.Value.Number + 1000));
        var checkpoints = 0;
        onHash = () =>
        {
            onHash = null;
            checkpoints++;
            Assert.Equal(sentinel, Assert.Single(removed));
            Assert.True(removed.Capacity >= candidates.Length + 1);
            AssertEntries(candidates, dictionary);
            Assert.Equal(candidates.Length, dictionary.Count);
            // If enumeration were lazy, these later nodes would yield their NEW values and be removed.
            foreach (var (key, replacement) in replacements)
            {
                dictionary[key] = replacement;
            }
        };

        dictionary.RemoveRange(RingRange.Full, removed);
        Assert.Equal(1, checkpoints);
        Assert.Equal(sentinel, removed[0]);
        Assert.Equal(candidates[0], Assert.Single(removed.Skip(1)));
        Assert.False(dictionary.TryGetValue(candidates[0].Key, out _));
        Assert.Equal(3, dictionary.Count);
        foreach (var (key, replacement) in replacements)
        {
            Assert.Same(replacement, dictionary[key]);
        }

        AssertEntries(replacements, dictionary);
    }

    [Fact]
    public void RemoveRange_HashFaultDuringCandidateEnumerationRemovesNothing()
    {
        var armed = false;
        var calls = 0;
        var failure = new InvalidOperationException("controlled candidate enumeration fault");
        var dictionary = new ConcurrentHashRangeDictionary<int, int, TestHashComparer<int>>(new(key =>
        {
            if (armed && ++calls == 3)
            {
                throw failure;
            }

            return (uint)key;
        }), capacity: 32, concurrencyLevel: 1);
        var expected = Enumerable.Range(1, 4).Select(key => KeyValuePair.Create(key, key * 17)).ToArray();
        foreach (var (key, value) in expected)
        {
            dictionary[key] = value;
        }

        var sentinel = KeyValuePair.Create(-1, -71);
        var removed = new List<KeyValuePair<int, int>> { sentinel };
        armed = true;
        Assert.Same(failure, Assert.Throws<InvalidOperationException>(
            () => dictionary.RemoveRange(RingRange.Create(0, 4), removed)));
        armed = false;
        Assert.Equal(3, calls);
        Assert.Equal(sentinel, Assert.Single(removed));
        Assert.Equal(4, dictionary.Count);
        AssertEntries(expected, dictionary);
        dictionary.RemoveRange(RingRange.Create(0, 4), removed);
        Assert.Equal(sentinel, removed[0]);
        AssertEntries(expected, removed.Skip(1));
        Assert.Empty(dictionary);
        Assert.Equal(0, dictionary.Count);
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(false, 2)]
    [InlineData(true, 0)]
    [InlineData(true, 2)]
    public async Task RemoveRange_HashOrComparerFaultRetainsEveryCompletedRemoval(bool comparerFault, int failureIndex)
    {
        Action? checkpoint = null;
        var comparer = new CallbackComparer(() =>
        {
            if (comparerFault)
            {
                checkpoint?.Invoke();
            }
        });
        var dictionary = new ConcurrentHashRangeDictionary<int, int, TestHashComparer<int>>(new(key =>
        {
            if (!comparerFault)
            {
                checkpoint?.Invoke();
            }

            return (uint)key << 28;
        }, comparer), capacity: 32, concurrencyLevel: 1);
        for (var key = 1; key <= 4; key++)
        {
            dictionary[key] = key * 17;
        }

        var candidates = dictionary.EnumerateRange(RingRange.Full).ToArray();
        var sentinel = KeyValuePair.Create(-1, -71);
        var removed = new List<KeyValuePair<int, int>>(1) { sentinel };
        var failure = new InvalidOperationException("controlled removal fault");
        var attempts = 0;
        checkpoint = () =>
        {
            Assert.Equal(sentinel, removed[0]);
            Assert.Equal(candidates.Take(attempts), removed.Skip(1));
            Assert.True(removed.Capacity >= candidates.Length + 1);
            if (attempts++ == failureIndex)
            {
                throw failure;
            }
        };

        Assert.Same(failure, Assert.Throws<InvalidOperationException>(() => dictionary.RemoveRange(RingRange.Full, removed)));
        checkpoint = null;
        Assert.Equal(failureIndex + 1, attempts);
        Assert.Equal(sentinel, removed[0]);
        AssertEntries(candidates.Take(failureIndex), removed.Skip(1));
        AssertEntries(candidates.Skip(failureIndex), dictionary);
        Assert.Equal(4 - failureIndex, dictionary.Count);

        // A different thread proves a throwing comparer did not leave a writer lock held.
        await Task.Run(() =>
        {
            Assert.True(dictionary.TryAdd(9, 153));
            Assert.True(dictionary.TryUpdate(9, 154, 153));
            Assert.True(dictionary.TryRemove(9, out var value));
            Assert.Equal(154, value);
        }, TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        dictionary.RemoveRange(RingRange.Full, removed);
        AssertEntries(candidates, removed.Skip(1));
        Assert.Empty(dictionary);
        Assert.Equal(0, dictionary.Count);
    }

    [Fact]
    public async Task VisitRange_PropagatesCallbackExceptionAndPreservesCompletedMutation()
    {
        var dictionary = new ConcurrentHashRangeDictionary<int, int, TestHashComparer<int>>(new(key => (uint)key));
        dictionary[1] = 17;
        dictionary[2] = 29;
        var failure = new InvalidOperationException("controlled visitor fault");
        var visited = new List<KeyValuePair<int, int>>();
        Assert.Same(failure, Assert.Throws<InvalidOperationException>(() =>
            dictionary.VisitRange(RingRange.Full, visited, (state, key, value) =>
            {
                state.Add(KeyValuePair.Create(key, value));
                dictionary[key] = value + 100;
                throw failure;
            })));

        var first = Assert.Single(visited);
        var originals = new Dictionary<int, int> { [1] = 17, [2] = 29 };
        Assert.Equal(originals[first.Key], first.Value);
        Assert.Equal(first.Value + 100, dictionary[first.Key]);
        var neighbor = first.Key == 1 ? 2 : 1;
        Assert.Equal(originals[neighbor], dictionary[neighbor]);
        Assert.Equal(2, dictionary.Count);
        await Task.Run(() => Assert.True(dictionary.TryUpdate(first.Key, first.Value + 200, first.Value + 100)), TestContext.Current.CancellationToken)
            .WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        AssertEntries([KeyValuePair.Create(first.Key, first.Value + 200), KeyValuePair.Create(neighbor, originals[neighbor])], dictionary);
    }

    [Theory]
    [InlineData(0x51A7)]
    [InlineData(0xC0FFEE)]
    public void SeededOperations_MatchIndependentDictionaryAndRingModel(int seed)
    {
        var random = new Random(seed);
        static uint Hash(int key) => key % 17 == 0 ? 0 : key % 19 == 0 ? uint.MaxValue : key % 7 == 0 ? 0x08000011 : unchecked((uint)key * 2654435761);
        var dictionary = new ConcurrentHashRangeDictionary<int, int, TestHashComparer<int>>(new(Hash), capacity: 2, concurrencyLevel: 1);
        var model = new Dictionary<int, int>();
        for (var operation = 0; operation < 1000; operation++)
        {
            var key = random.Next(128);
            var value = random.Next(1, 10000);
            var oldValue = model.GetValueOrDefault(key);
            var comparison = random.Next(2) == 0 ? oldValue : -1;
            var start = (uint)random.NextInt64(1L << 32);
            var end = (uint)random.NextInt64(1L << 32);
            var range = RingRange.Create(start, end);
            bool Contains(uint hash) => start == end ? start != 0 : start < end ? hash > start && hash <= end : hash > start || hash <= end;
            switch (random.Next(9))
            {
                case 0:
                    Assert.Equal(model.TryAdd(key, value), dictionary.TryAdd(key, value));
                    break;
                case 1:
                    model[key] = value;
                    dictionary[key] = value;
                    break;
                case 2:
                    Assert.Equal(model.TryGetValue(key, out var expected), dictionary.TryGetValue(key, out var actual));
                    Assert.Equal(expected, actual);
                    break;
                case 3:
                    var updated = model.ContainsKey(key) && oldValue == comparison;
                    Assert.Equal(updated, dictionary.TryUpdate(key, value, comparison));
                    if (updated)
                    {
                        model[key] = value;
                    }

                    break;
                case 4:
                    Assert.Equal(model.Remove(key, out expected), dictionary.TryRemove(key, out actual));
                    Assert.Equal(expected, actual);
                    break;
                case 5:
                    var matched = model.ContainsKey(key) && oldValue == comparison;
                    Assert.Equal(matched, dictionary.TryRemove(KeyValuePair.Create(key, comparison)));
                    if (matched)
                    {
                        model.Remove(key);
                    }

                    break;
                case 6:
                    var selected = model.Where(pair => Contains(Hash(pair.Key))).ToArray();
                    AssertEntries(selected, dictionary.EnumerateRange(range));
                    var visited = new List<KeyValuePair<int, int>>();
                    dictionary.VisitRange(range, visited, static (state, k, v) => state.Add(KeyValuePair.Create(k, v)));
                    AssertEntries(selected, visited);
                    break;
                case 7:
                    selected = model.Where(pair => Contains(Hash(pair.Key))).ToArray();
                    var removed = new List<KeyValuePair<int, int>>();
                    dictionary.RemoveRange(range, removed);
                    AssertEntries(selected, removed);
                    foreach (var entry in selected)
                    {
                        model.Remove(entry.Key);
                    }

                    break;
                default:
                    AssertEntries(model.Where(pair => Hash(pair.Key) == Hash(key)), dictionary.EnumerateHash(Hash(key)));
                    break;
            }

            Assert.True(model.Count == dictionary.Count, $"Seed {seed}, operation {operation}, key {key}: expected count {model.Count}, actual {dictionary.Count}.");
            if (operation % 16 == 0)
            {
                AssertEntries(model, dictionary);
            }
        }

        AssertEntries(model, dictionary);
    }

    private static (RingRange Range, Func<uint, bool> Contains) GetRangeCase(string scenario) => scenario switch
    {
        "Empty" => (RingRange.Empty, _ => false),
        "Full" => (RingRange.Full, _ => true),
        "EqualEndpointsFull" => (RingRange.Create(7, 7), _ => true),
        "Zero" => (RingRange.FromPoint(0), hash => hash == 0),
        "Max" => (RingRange.FromPoint(uint.MaxValue), hash => hash == uint.MaxValue),
        "Ordinary" => (RingRange.Create(10, 20), hash => hash > 10 && hash <= 20),
        "Wrapped" => (RingRange.Create(uint.MaxValue - 2, 2), hash => hash > uint.MaxValue - 2 || hash <= 2),
        "SamePrefixWrapped" => (RingRange.Create(0x08000020, 0x08000010), hash => hash > 0x08000020 || hash <= 0x08000010),
        "InteriorBuckets" => (RingRange.Create(0x07FFFFFE, 0x18000001), hash => hash > 0x07FFFFFE && hash <= 0x18000001),
        "PrefixBoundary" => (RingRange.Create(0x07FFFFFF, 0x08000000), hash => hash == 0x08000000),
        _ => throw new ArgumentOutOfRangeException(nameof(scenario))
    };

    private static void AssertEntries<T>(IEnumerable<KeyValuePair<int, T>> expected, IEnumerable<KeyValuePair<int, T>> actual)
        => Assert.Equal(expected.OrderBy(pair => pair.Key).ToArray(), actual.OrderBy(pair => pair.Key).ToArray());

    private sealed record Payload(int Number);
    private sealed record Key(int Id, uint Hash);

    private sealed class KeyComparer : IEqualityComparer<Key>
    {
        public bool Equals(Key? x, Key? y) => x?.Id == y?.Id;
        public int GetHashCode(Key obj) => throw new InvalidOperationException("Ordinary equality hashing is not the range coordinate.");
    }

    private sealed class CallbackComparer(Action checkpoint) : IEqualityComparer<int>
    {
        public bool Equals(int x, int y)
        {
            checkpoint();
            return x == y;
        }

        public int GetHashCode(int obj) => obj;
    }
}

internal sealed class TestHashComparer<TKey>(Func<TKey, uint> hash, IEqualityComparer<TKey>? equality = null) : IConsistentHashComparer<TKey>
{
    public uint GetHashCode(TKey value) => hash(value);
    public bool Equals(TKey? x, TKey? y) => (equality ?? EqualityComparer<TKey>.Default).Equals(x, y);
}
