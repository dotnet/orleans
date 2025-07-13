using FluentAssertions;
using System.Collections;
using Xunit;
using Xunit.Abstractions;
using Orleans.Caching;
using Orleans.Caching.Internal;

namespace NonSilo.Tests.Caching;

/// <summary>
/// Tests for the ConcurrentLruCache, which is a thread-safe LRU (Least Recently Used) cache implementation used in Orleans.
/// This cache is designed with a multi-generation architecture (Hot, Warm, Cold) to efficiently manage frequently accessed items.
/// It's used throughout Orleans for caching grain directory entries, grain references, and other frequently accessed data.
/// </summary>
[TestCategory("BVT")]
public class ConcurrentLruTests(ITestOutputHelper testOutputHelper)
{
    private readonly ITestOutputHelper _testOutputHelper = testOutputHelper;
    private const int Capacity = 100;
    private readonly CapacityPartition _capacityPartition = new(Capacity);
    private int HotCap => _capacityPartition.Hot;
    private int WarmCap => _capacityPartition.Warm;
    private int ColdCap => _capacityPartition.Cold;

    private readonly ConcurrentLruCache<int, string> _lru = new(Capacity);
    private readonly ValueFactory _valueFactory = new();

    private static ConcurrentLruCache<int, string>.ITestAccessor GetTestAccessor(ConcurrentLruCache<int, string> lru) => lru;

    /// <summary>
    /// Verifies that the cache requires a minimum capacity of 3 to support its multi-generation architecture.
    /// </summary>
    [Fact]
    public void WhenCapacityIsLessThan3CtorThrows()
    {
        Action constructor = () => { var x = new ConcurrentLruCache<int, string>(2, EqualityComparer<int>.Default); };

        constructor.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void WhenCapacityIs4HotHasCapacity1AndColdHasCapacity1()
    {
        var lru = new ConcurrentLruCache<int, int>(4, EqualityComparer<int>.Default);

        for (var i = 0; i < 5; i++)
        {
            lru.GetOrAdd(i, x => x);
        }

        lru.HotCount.Should().Be(1);
        lru.ColdCount.Should().Be(1);
        lru.Capacity.Should().Be(4);
    }

    [Fact]
    public void WhenCapacityIs10HotHasCapacity1AndWarmHasCapacity8AndColdHasCapacity1()
    {
        var lru = new ConcurrentLruCache<int, int>(10, EqualityComparer<int>.Default);

        for (var i = 0; i < lru.Capacity; i++)
        {
            lru.GetOrAdd(i, x => x);
        }

        lru.HotCount.Should().Be(1);
        lru.WarmCount.Should().Be(8);
        lru.ColdCount.Should().Be(1);
        lru.Capacity.Should().Be(10);
    }

    [Fact]
    public void ConstructAddAndRetrieveWithDefaultCtorReturnsValue()
    {
        var x = new ConcurrentLruCache<int, int>(3);

        x.GetOrAdd(1, k => k).Should().Be(1);
    }

    /// <summary>
    /// Tests that the cache correctly tracks the count of items when new entries are added.
    /// </summary>
    [Fact]
    public void WhenItemIsAddedCountIsCorrect()
    {
        _lru.Count.Should().Be(0);
        _lru.GetOrAdd(1, _valueFactory.Create);
        _lru.Count.Should().Be(1);
    }

    [Fact]
    public void WhenItemsAddedKeysContainsTheKeys()
    {
        _lru.Count.Should().Be(0);
        _lru.GetOrAdd(1, _valueFactory.Create);
        _lru.GetOrAdd(2, _valueFactory.Create);
        _lru.Keys.Should().BeEquivalentTo(new[] { 1, 2 });
    }

    [Fact]
    public void WhenItemsAddedGenericEnumerateContainsKvps()
    {
        _lru.Count.Should().Be(0);
        _lru.GetOrAdd(1, _valueFactory.Create);
        _lru.GetOrAdd(2, _valueFactory.Create);
        _lru.Should().BeEquivalentTo(new[] { new KeyValuePair<int, string>(1, "1"), new KeyValuePair<int, string>(2, "2") });
    }

    [Fact]
    public void WhenItemsAddedEnumerateContainsKvps()
    {
        _lru.Count.Should().Be(0);
        _lru.GetOrAdd(1, _valueFactory.Create);
        _lru.GetOrAdd(2, _valueFactory.Create);

        var enumerable = (IEnumerable)_lru;
        enumerable.Should().BeEquivalentTo(new[] { new KeyValuePair<int, string>(1, "1"), new KeyValuePair<int, string>(2, "2") });
    }

    [Fact]
    public void FromColdWarmupFillsWarmQueue()
    {
        FillCache();

        _lru.Count.Should().Be(Capacity);
    }

    [Fact]
    public void WhenItemExistsTryGetReturnsValueAndTrue()
    {
        _lru.GetOrAdd(1, _valueFactory.Create);
        var result = _lru.TryGet(1, out var value);

        result.Should().Be(true);
        value.Should().Be("1");
    }

    [Fact]
    public void WhenItemDoesNotExistTryGetReturnsNullAndFalse()
    {
        _lru.GetOrAdd(1, _valueFactory.Create);
        var result = _lru.TryGet(2, out var value);

        result.Should().Be(false);
        value.Should().BeNull();
    }

    [Fact]
    public void WhenItemIsAddedThenRetrievedMetricHitRatioIsHalf()
    {
        _lru.GetOrAdd(1, _valueFactory.Create);
        var result = _lru.TryGet(1, out var value);

        _lru.Metrics.HitRatio.Should().Be(0.5);
    }

    [Fact]
    public void WhenItemIsAddedThenRetrievedTotalIs2()
    {
        _lru.GetOrAdd(1, _valueFactory.Create);
        var result = _lru.TryGet(1, out var value);

        _lru.Metrics.Total.Should().Be(2);
    }

    [Fact]
    public void WhenRefToMetricsIsCapturedResultIsCorrect()
    {
        // this detects the case where the struct is copied. If the internal Data class
        // doesn't work, this test fails.
        var m = _lru.Metrics;

        _lru.GetOrAdd(1, _valueFactory.Create);
        var result = _lru.TryGet(1, out var value);

        m.HitRatio.Should().Be(0.5);
    }

    [Fact]
    public void WhenKeyIsRequestedItIsCreatedAndCached()
    {
        var result1 = _lru.GetOrAdd(1, _valueFactory.Create);
        var result2 = _lru.GetOrAdd(1, _valueFactory.Create);

        _valueFactory.TimesCalled.Should().Be(1);
        result1.Should().Be(result2);
    }

    [Fact]
    public void WhenKeyIsRequestedWithArgItIsCreatedAndCached()
    {
        var result1 = _lru.GetOrAdd(1, _valueFactory.Create, "x");
        var result2 = _lru.GetOrAdd(1, _valueFactory.Create, "y");

        _valueFactory.TimesCalled.Should().Be(1);
        result1.Should().Be(result2);
    }

    [Fact]
    public void WhenDifferentKeysAreRequestedValueIsCreatedForEach()
    {
        var result1 = _lru.GetOrAdd(1, _valueFactory.Create);
        var result2 = _lru.GetOrAdd(2, _valueFactory.Create);

        _valueFactory.TimesCalled.Should().Be(2);

        result1.Should().Be("1");
        result2.Should().Be("2");
    }

    [Fact]
    public void WhenValuesAreNotReadAndMoreKeysRequestedThanCapacityCountDoesNotIncrease()
    {
        FillCache();

        var result = _lru.GetOrAdd(1, _valueFactory.Create);

        _lru.Count.Should().Be(Capacity);
        _valueFactory.TimesCalled.Should().Be(Capacity + 1);
    }

    [Fact]
    public void WhenValuesAreReadAndMoreKeysRequestedThanCapacityCountIsBounded()
    {
        for (var i = 0; i < Capacity + 1; i++)
        {
            _lru.GetOrAdd(i, _valueFactory.Create);

            // touch items already cached when they are still in hot
            if (i > 0)
            {
                _lru.GetOrAdd(i - 1, _valueFactory.Create);
            }
        }

        _lru.Count.Should().Be(Capacity);
        _valueFactory.TimesCalled.Should().Be(Capacity + 1);
    }

    [Fact]
    public void WhenKeysAreContinuouslyRequestedInTheOrderTheyAreAddedCountIsBounded()
    {
        for (var i = 0; i < Capacity + 10; i++)
        {
            _lru.GetOrAdd(i, _valueFactory.Create);

            // Touch all items already cached in hot, warm and cold.
            // This is worst case scenario, since we touch them in the exact order they
            // were added.
            for (var j = 0; j < i; j++)
            {
                _lru.GetOrAdd(j, _valueFactory.Create);
            }

            _testOutputHelper.WriteLine($"Total: {_lru.Count} Hot: {_lru.HotCount} Warm: {_lru.WarmCount} Cold: {_lru.ColdCount}");
            _lru.Count.Should().BeLessThanOrEqualTo(Capacity + 1);
        }
    }

    [Fact]
    public void WhenKeysAreContinuouslyRequestedInTheOrderTheyAreAddedCountIsBounded2()
    {
        var lru = new ConcurrentLruCache<int, string>(128, EqualityComparer<int>.Default);

        for (var i = 0; i < 128 + 10; i++)
        {
            lru.GetOrAdd(i, _valueFactory.Create);

            // Touch all items already cached in hot, warm and cold.
            // This is worst case scenario, since we touch them in the exact order they
            // were added.
            for (var j = 0; j < i; j++)
            {
                lru.GetOrAdd(j, _valueFactory.Create);
            }

            lru.Count.Should().BeLessThanOrEqualTo(128 + 1, $"Total: {lru.Count} Hot: {lru.HotCount} Warm: {lru.WarmCount} Cold: {lru.ColdCount}");
        }
    }

    /// <summary>
    /// Tests the multi-generation eviction policy: items not accessed in the Hot generation are demoted to Cold.
    /// This tests the cache's ability to distinguish between frequently and infrequently accessed items.
    /// </summary>
    [Fact]
    public void WhenValueIsNotTouchedAndExpiresFromHotValueIsBumpedToCold()
    {
        FillCache();

        // Insert a value, making it hot
        Touch(0);

        // Insert more values, demoting it to cold
        GetOrAddRangeInclusive(1, Capacity - 1);

        IsInCache(0); // Value should be cold

        GetOrAddRangeInclusive(Capacity, Capacity + 1);

        // Value should have been evicted
        _lru.TryGet(0, out var value).Should().BeFalse();
    }

    private bool IsInCache(int key) => _lru.Keys.Contains(key);

    private void Touch(int key)
    {
        _lru.GetOrAdd(key, _valueFactory.Create);
    }

    private void GetOrAddRangeInclusive(int start, int end)
    {
        if (start <= end)
        {
            for (var i = start; i <= end; i++)
            {
                Touch(i);
            }
        }
        else
        {
            for (var i = start; i >= end; i--)
            {
                Touch(i);
            }
        }
    }

    private void AddOrUpdateRangeInclusive(int start, int end)
    {
        if (start <= end)
        {
            for (var i = start; i <= end; i++)
            {
                _lru.AddOrUpdate(i, _valueFactory.Create(i));
            }
        }
        else
        {
            for (var i = start; i >= end; i--)
            {
                _lru.AddOrUpdate(i, _valueFactory.Create(i));
            }
        }
    }

    [Fact]
    public void WhenValueIsTouchedAndExpiresFromHotValueIsBumpedToWarm()
    {
        FillCache();

        // Promote to hot
        Touch(0);
        Touch(0);

        GetOrAddRangeInclusive(1, 9);

        _lru.TryGet(0, out var value).Should().BeTrue();
    }

    [Fact]
    public void WhenValueIsTouchedAndExpiresFromColdItIsBumpedToWarm()
    {
        FillCache();

        _lru.GetOrAdd(0, _valueFactory.Create);

        IsInCache(0).Should().BeTrue();


        _lru.GetOrAdd(1, _valueFactory.Create);
        _lru.GetOrAdd(2, _valueFactory.Create);
        _lru.GetOrAdd(3, _valueFactory.Create); // push 0 to cold (not touched in hot)

        IsInCache(0).Should().BeTrue();

        _lru.GetOrAdd(0, _valueFactory.Create); // Touch 0 in cold

        IsInCache(0).Should().BeTrue();

        GetOrAddRangeInclusive(4, 9);
        _lru.GetOrAdd(4, _valueFactory.Create); // fully cycle cold, this will evict 0 if it is not moved to warm
        _lru.GetOrAdd(5, _valueFactory.Create);
        _lru.GetOrAdd(6, _valueFactory.Create);
        _lru.GetOrAdd(7, _valueFactory.Create);
        _lru.GetOrAdd(8, _valueFactory.Create);
        _lru.GetOrAdd(9, _valueFactory.Create);

        _lru.TryGet(0, out var value).Should().BeTrue();
    }

    [Fact]
    public void WhenValueIsNotTouchedAndExpiresFromColdItIsRemoved()
    {
        FillCache();

        _lru.GetOrAdd(0, _valueFactory.Create);

        AddOrUpdateRangeInclusive(1, Capacity);

        _lru.TryGet(0, out var value).Should().BeFalse();
    }

    [Fact]
    public void WhenValueIsNotTouchedAndExpiresFromWarmValueIsBumpedToCold()
    {
        FillCache();

        // Insert hot
        Touch(0);

        // Touch it again so that it will be promoted to 'warm'
        Touch(0);

        GetOrAddRangeInclusive(1, Capacity - 1);

        IsInCache(0).Should().BeTrue();

        // Touch more values to have it evicted.
        for (var i = 1; i < 1 + Capacity; i++)
        {
            Touch(i);
            Touch(i);
        }

        _lru.TryGet(0, out var value).Should().BeFalse();
    }

    [Fact]
    public void WhenValueIsTouchedAndExpiresFromWarmValueIsBumpedBackIntoWarm()
    {
        FillCache();

        _lru.GetOrAdd(0, _valueFactory.Create);
        _lru.GetOrAdd(0, _valueFactory.Create); // Touch 0 in hot, it will promote to warm

        _lru.GetOrAdd(1, _valueFactory.Create);
        _lru.GetOrAdd(2, _valueFactory.Create);
        _lru.GetOrAdd(3, _valueFactory.Create); // push 0 to warm

        // touch next 3 values, so they will promote to warm
        _lru.GetOrAdd(4, _valueFactory.Create); _lru.GetOrAdd(4, _valueFactory.Create);
        _lru.GetOrAdd(5, _valueFactory.Create); _lru.GetOrAdd(5, _valueFactory.Create);
        _lru.GetOrAdd(6, _valueFactory.Create); _lru.GetOrAdd(6, _valueFactory.Create);

        // push 4,5,6 to warm, 0 to cold
        _lru.GetOrAdd(7, _valueFactory.Create);
        _lru.GetOrAdd(8, _valueFactory.Create);
        _lru.GetOrAdd(9, _valueFactory.Create);

        // Touch 0
        _lru.TryGet(0, out var value).Should().BeTrue();

        // push 7,8,9 to cold, cycle 0 back to warm
        _lru.GetOrAdd(10, _valueFactory.Create);
        _lru.GetOrAdd(11, _valueFactory.Create);
        _lru.GetOrAdd(12, _valueFactory.Create);

        _lru.TryGet(0, out value).Should().BeTrue();
    }

    [Fact]
    public void WhenValueExpiresItIsDisposed()
    {
        var lruOfDisposable = new ConcurrentLruCache<int, DisposableItem>(6, EqualityComparer<int>.Default);
        var disposableValueFactory = new DisposableValueFactory();

        for (var i = 0; i < 7; i++)
        {
            lruOfDisposable.GetOrAdd(i, disposableValueFactory.Create);
        }

        disposableValueFactory.Items[0].IsDisposed.Should().BeTrue();

        disposableValueFactory.Items[1].IsDisposed.Should().BeFalse();
        disposableValueFactory.Items[2].IsDisposed.Should().BeFalse();
        disposableValueFactory.Items[3].IsDisposed.Should().BeFalse();
        disposableValueFactory.Items[4].IsDisposed.Should().BeFalse();
        disposableValueFactory.Items[5].IsDisposed.Should().BeFalse();
        disposableValueFactory.Items[6].IsDisposed.Should().BeFalse();
    }

    [Fact]
    public void WhenAddingNullValueCanBeAddedAndRemoved()
    {
        _lru.GetOrAdd(1, _ => null).Should().BeNull();
        _lru.AddOrUpdate(1, null);
        _lru.TryRemove(1).Should().BeTrue();
    }

    [Fact]
    public void WhenValuesAreEvictedEvictionMetricCountsEvicted()
    {
        FillCache();

        _lru.GetOrAdd(1, _valueFactory.Create);

        _lru.Metrics.Evicted.Should().Be(1);
    }

    [Fact]
    public void WhenKeyExistsTryRemoveRemovesItemAndReturnsTrue()
    {
        _lru.GetOrAdd(1, _valueFactory.Create);

        _lru.TryRemove(1).Should().BeTrue();
        _lru.TryGet(1, out var value).Should().BeFalse();
    }

    [Fact]
    public void WhenKeyExistsTryRemoveReturnsValue()
    {
        _lru.GetOrAdd(1, _valueFactory.Create);

        _lru.TryRemove(1, out var value).Should().BeTrue();
        value.Should().Be("1");
    }

    [Fact]
    public void WhenItemIsRemovedItIsDisposed()
    {
        var lruOfDisposable = new ConcurrentLruCache<int, DisposableItem>(6, EqualityComparer<int>.Default);
        var disposableValueFactory = new DisposableValueFactory();

        lruOfDisposable.GetOrAdd(1, disposableValueFactory.Create);
        lruOfDisposable.TryRemove(1);

        disposableValueFactory.Items[1].IsDisposed.Should().BeTrue();
    }

    [Fact]
    public void WhenItemRemovedFromHotDuringWarmupItIsEagerlyCycledOut()
    {
        _lru.GetOrAdd(1, _valueFactory.Create);

        _lru.TryRemove(1);
        Print();                                    // Hot [1] Warm [] Cold []

        _lru.GetOrAdd(1, _valueFactory.Create);
        _lru.GetOrAdd(2, _valueFactory.Create);
        _lru.GetOrAdd(3, _valueFactory.Create);
        Print();                                    // Hot [1,2,3] Warm [] Cold []

        _lru.WarmCount.Should().Be(0);
        _lru.ColdCount.Should().Be(0);
    }

    [Fact]
    public void WhenItemRemovedFromHotAfterWarmupItIsEagerlyCycledOut()
    {
        for (var i = 0; i < _lru.Capacity; i++)
        {
            _lru.GetOrAdd(i, _valueFactory.Create);
        }

        _lru.Metrics.Evicted.Should().Be(0);

        _lru.GetOrAdd(-1, _valueFactory.Create);

        _lru.TryRemove(-1);

        // fully cycle hot, which is 3 items
        foreach (var item in Enumerable.Range(1000, HotCap))
        {
            _lru.GetOrAdd(item, _valueFactory.Create);
        }

        // without eager eviction as -1 is purged from hot, a 4th item will pushed out since hot queue is full
        _lru.Metrics.Evicted.Should().Be(HotCap);
    }

    [Fact]
    public void WhenItemRemovedFromWarmDuringWarmupItIsEagerlyCycledOut()
    {
        foreach (var item in Enumerable.Range(1, HotCap))
        {
            _lru.GetOrAdd(item, _valueFactory.Create);
        }

        _lru.TryRemove(1);

        foreach (var item in Enumerable.Range(1000, HotCap))
        {
            _lru.GetOrAdd(item, _valueFactory.Create);
        }

        // Items are cycled from Hot to Warm, since Warm is not yet filled.
        // The previously removed item is skipped.
        _lru.WarmCount.Should().Be(HotCap - 1);
        _lru.ColdCount.Should().Be(0);
    }


    [Fact]
    public void WhenItemRemovedFromWarmAfterWarmupItIsEagerlyCycledOut()
    {
        for (var i = 0; i < _lru.Capacity; i++)
        {
            _lru.GetOrAdd(i, _valueFactory.Create);
        }

        Print();                                    // Hot [6,7,8] Warm [1,2,3] Cold [0,4,5]
        _lru.Metrics.Evicted.Should().Be(0);

        _lru.TryRemove(1);

        _lru.GetOrAdd(6, _valueFactory.Create); // 6 -> W
        _lru.GetOrAdd(9, _valueFactory.Create);

        Print();                                    // Hot [7,8,9] Warm [2,3,6] Cold [0,4,5]

        _lru.Metrics.Evicted.Should().Be(0);
    }

    [Fact]
    public void WhenItemRemovedFromColdAfterWarmupItIsEagerlyCycledOut()
    {
        for (var i = 0; i < _lru.Capacity; i++)
        {
            _lru.GetOrAdd(i, _valueFactory.Create);
        }

        Print();                                    // Hot [6,7,8] Warm [1,2,3] Cold [0,4,5]
        _lru.Metrics.Evicted.Should().Be(0);

        _lru.GetOrAdd(0, _valueFactory.Create);
        _lru.TryRemove(0);

        _lru.GetOrAdd(9, _valueFactory.Create);

        Print();                                    // Hot [7,8,9] Warm [1,2,3] Cold [4,5,6]

        _lru.Metrics.Evicted.Should().Be(0);
    }

    [Fact]
    public void WhenKeyDoesNotExistTryRemoveReturnsFalse()
    {
        _lru.GetOrAdd(1, _valueFactory.Create);

        _lru.TryRemove(2).Should().BeFalse();
    }

    [Fact]
    public void WhenItemsAreRemovedTrimRemovesDeletedItemsFromQueues()
    {
        for (var i = 0; i < _lru.Capacity; i++)
        {
            _lru.GetOrAdd(i, _valueFactory.Create);
        }

        _lru.TryRemove(0);
        _lru.TryRemove(1);
        _lru.TryRemove(6);

        _lru.Trim(1);

        _lru.HotCount.Should().Be(HotCap);
        _lru.WarmCount.Should().Be(WarmCap);
        _lru.ColdCount.Should().Be(ColdCap - 1);
    }

    [Fact]
    public void WhenRepeatedlyAddingAndRemovingSameValueLruRemainsInConsistentState()
    {
        for (var i = 0; i < Capacity; i++)
        {
            // Because TryRemove leaves the item in the queue, when it is eventually removed
            // from the cold queue, it should not remove the newly created value.
            _lru.GetOrAdd(1, _valueFactory.Create);
            _lru.TryGet(1, out var value).Should().BeTrue();
            _lru.TryRemove(1);
        }
    }

    [Fact]
    public void WhenKeyExistsTryUpdateUpdatesValueAndReturnsTrue()
    {
        _lru.GetOrAdd(1, _valueFactory.Create);

        _lru.TryUpdate(1, "2").Should().BeTrue();

        _lru.TryGet(1, out var value);
        value.Should().Be("2");
    }

    [Fact]
    public void WhenKeyExistsTryUpdateDisposesOldValue()
    {
        var lruOfDisposable = new ConcurrentLruCache<int, DisposableItem>(6, EqualityComparer<int>.Default);
        var disposableValueFactory = new DisposableValueFactory();
        var newValue = new DisposableItem();

        lruOfDisposable.GetOrAdd(1, disposableValueFactory.Create);
        lruOfDisposable.TryUpdate(1, newValue);

        disposableValueFactory.Items[1].IsDisposed.Should().BeTrue();
    }

    [Fact]
    public void WhenKeyDoesNotExistTryUpdateReturnsFalse()
    {
        _lru.GetOrAdd(1, _valueFactory.Create);

        _lru.TryUpdate(2, "3").Should().BeFalse();
    }

    // backcompat: remove conditional compile
#if NETCOREAPP3_0_OR_GREATER
    [Fact]
    public void WhenKeyExistsTryUpdateIncrementsUpdateCount()
    {
        _lru.GetOrAdd(1, _valueFactory.Create);

        _lru.TryUpdate(1, "2").Should().BeTrue();

        _lru.Metrics.Updated.Should().Be(1);
    }

    [Fact]
    public void WhenKeyDoesNotExistTryUpdateDoesNotIncrementCounter()
    {
        _lru.GetOrAdd(1, _valueFactory.Create);

        _lru.TryUpdate(2, "3").Should().BeFalse();

        _lru.Metrics.Updated.Should().Be(0);
    }
#endif
    [Fact]
    public void WhenKeyDoesNotExistAddOrUpdateAddsNewItem()
    {
        _lru.AddOrUpdate(1, "1");

        _lru.TryGet(1, out var value).Should().BeTrue();
        value.Should().Be("1");
    }

    [Fact]
    public void WhenKeyExistsAddOrUpdateUpdatesExistingItem()
    {
        _lru.AddOrUpdate(1, "1");
        _lru.AddOrUpdate(1, "2");

        _lru.TryGet(1, out var value).Should().BeTrue();
        value.Should().Be("2");
    }

    [Fact]
    public void WhenKeyExistsAddOrUpdateGuidUpdatesExistingItem()
    {
        var lru2 = new ConcurrentLruCache<int, Guid>(Capacity, EqualityComparer<int>.Default);

        var b = new byte[8];
        lru2.AddOrUpdate(1, new Guid(1, 0, 0, b));
        lru2.AddOrUpdate(1, new Guid(2, 0, 0, b));

        lru2.TryGet(1, out var value).Should().BeTrue();
        value.Should().Be(new Guid(2, 0, 0, b));
    }

    [Fact]
    public void WhenKeyExistsAddOrUpdateDisposesOldValue()
    {
        var lruOfDisposable = new ConcurrentLruCache<int, DisposableItem>(6, EqualityComparer<int>.Default);
        var disposableValueFactory = new DisposableValueFactory();
        var newValue = new DisposableItem();

        lruOfDisposable.GetOrAdd(1, disposableValueFactory.Create);
        lruOfDisposable.AddOrUpdate(1, newValue);

        disposableValueFactory.Items[1].IsDisposed.Should().BeTrue();
    }

    [Fact]
    public void WhenKeyDoesNotExistAddOrUpdateMaintainsLruOrder()
    {
        AddOrUpdateRangeInclusive(1, Capacity + 1);

        _lru.HotCount.Should().Be(HotCap);
        _lru.WarmCount.Should().Be(WarmCap);
        _lru.TryGet(0, out _).Should().BeFalse();
    }

    [Fact]
    public void WhenCacheIsEmptyClearIsNoOp()
    {
        _lru.Clear();
        _lru.Count.Should().Be(0);
    }

    [Fact]
    public void WhenItemsExistClearRemovesAllItems()
    {
        _lru.AddOrUpdate(1, "1");
        _lru.AddOrUpdate(2, "2");

        _lru.Clear();

        _lru.Count.Should().Be(0);

        // verify queues are purged
        _lru.HotCount.Should().Be(0);
        _lru.WarmCount.Should().Be(0);
        _lru.ColdCount.Should().Be(0);
    }

    // This is a special case:
    // Cycle 1: hot => warm
    // Cycle 2: warm => warm
    // Cycle 3: warm => cold
    // Cycle 4: cold => remove
    // Cycle 5: cold => remove
    [Fact]
    public void WhenCacheIsSize3ItemsExistAndItemsAccessedClearRemovesAllItems()
    {
        var lru = new ConcurrentLruCache<int, string>(3);

        lru.AddOrUpdate(1, "1");
        lru.AddOrUpdate(2, "1");

        lru.TryGet(1, out _);
        lru.TryGet(2, out _);

        lru.Clear();

        lru.Count.Should().Be(0);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(10)]
    public void WhenItemsExistAndItemsAccessedClearRemovesAllItems(int itemCount)
    {
        // By default capacity is 9. Test all possible states of touched items
        // in the cache.

        for (var i = 0; i < itemCount; i++)
        {
            _lru.AddOrUpdate(i, "1");
        }

        // touch n items
        for (var i = 0; i < itemCount; i++)
        {
            _lru.TryGet(i, out _);
        }

        _lru.Clear();

        _testOutputHelper.WriteLine("LRU " + string.Join(" ", _lru.Keys));

        _lru.Count.Should().Be(0);

        // verify queues are purged
        _lru.HotCount.Should().Be(0);
        _lru.WarmCount.Should().Be(0);
        _lru.ColdCount.Should().Be(0);
    }

    [Fact]
    public void WhenWarmThenClearedIsWarmIsReset()
    {
        for (var i = 0; i < Capacity; i++)
        {
            Touch(i);
        }

        var testAccessor = GetTestAccessor(_lru);
        testAccessor.IsWarm.Should().BeTrue();

        _lru.Clear();
        _lru.Count.Should().Be(0);
        testAccessor.IsWarm.Should().BeFalse();

        for (var i = 0; i < Capacity; i++)
        {
            Touch(i);
        }

        testAccessor.IsWarm.Should().BeTrue();
        _lru.Count.Should().Be(_lru.Capacity);
    }

    [Fact]
    public void WhenWarmThenTrimIsWarmIsReset()
    {
        GetOrAddRangeInclusive(1, Capacity);

        var testAccessor = GetTestAccessor(_lru);
        testAccessor.IsWarm.Should().BeTrue();
        _lru.Trim(Capacity / 2);

        testAccessor.IsWarm.Should().BeFalse();
        _lru.Count.Should().Be(Capacity / 2);

        for (var i = 0; i < Capacity; i++)
        {
            Touch(i);
        }

        testAccessor.IsWarm.Should().BeTrue();
        _lru.Count.Should().Be(_lru.Capacity);
    }

    /// <summary>
    /// Verifies that the cache properly disposes IDisposable items when they are removed via Clear().
    /// This is important for preventing resource leaks when caching disposable objects.
    /// </summary>
    [Fact]
    public void WhenItemsAreDisposableClearDisposesItemsOnRemove()
    {
        var lruOfDisposable = new ConcurrentLruCache<int, DisposableItem>(6, EqualityComparer<int>.Default);

        var items = Enumerable.Range(1, 4).Select(i => new DisposableItem()).ToList();

        for (var i = 0; i < 4; i++)
        {
            lruOfDisposable.AddOrUpdate(i, items[i]);
        }

        lruOfDisposable.Clear();

        items.All(i => i.IsDisposed == true).Should().BeTrue();
    }

    [Fact]
    public void WhenTrimCountIsZeroThrows()
    {
        _lru.Invoking(l => _lru.Trim(0)).Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void WhenTrimCountIsMoreThanCapacityThrows()
    {
        _lru.Invoking(l => _lru.Trim(HotCap + WarmCap + ColdCap + 1)).Should().Throw<ArgumentOutOfRangeException>();
    }

    [Theory]
    [InlineData(10)]
    [InlineData(20)]
    [InlineData(30)]
    [InlineData(40)]
    [InlineData(50)]
    [InlineData(60)]
    [InlineData(70)]
    [InlineData(80)]
    [InlineData(90)]
    public void WhenColdItemsExistTrimRemovesExpectedItemCount(int trimCount)
    {
        FillCache();

        // Warm items
        var warmItems = Enumerable.Range(1, WarmCap).ToArray();
        foreach (var item in warmItems)
        {
            _lru.AddOrUpdate(item, item.ToString());
            _lru.GetOrAdd(item, _valueFactory.Create);
        }

        // Cold items (added but untouched)
        var coldItems = Enumerable.Range(1000, ColdCap).ToArray();
        foreach (var item in coldItems)
        {
            _lru.AddOrUpdate(item, item.ToString());
        }

        // Hot Items (evict the previous hot items to cold)
        var hotItems = Enumerable.Range(2000, HotCap).ToArray();
        foreach (var item in hotItems)
        {
            _lru.AddOrUpdate(item, item.ToString());
        }

        _lru.Trim(trimCount);

        int[] expected = [
            .. warmItems.Skip(Math.Max(0, trimCount - coldItems.Length)),
            .. hotItems,
            .. coldItems.Skip(trimCount)];
        _lru.Keys.Order().Should().BeEquivalentTo(expected.Order());
    }

    [Theory]
    [InlineData(1, new[] { 6, 5, 4, 3, 2 })]
    [InlineData(2, new[] { 6, 5, 4, 3 })]
    [InlineData(3, new[] { 6, 5, 4 })]
    [InlineData(4, new[] { 6, 5 })]
    [InlineData(5, new[] { 6 })]
    [InlineData(6, new int[] { })]
    [InlineData(7, new int[] { })]
    [InlineData(8, new int[] { })]
    [InlineData(9, new int[] { })]
    public void WhenHotAndWarmItemsExistTrimRemovesExpectedItemCount(int itemCount, int[] expected)
    {
        // initial state:
        // Hot = 6, 5, 4
        // Warm = 3, 2, 1
        // Cold = -
        _lru.AddOrUpdate(1, "1");
        _lru.AddOrUpdate(2, "2");
        _lru.AddOrUpdate(3, "3");
        _lru.GetOrAdd(1, i => i.ToString());
        _lru.GetOrAdd(2, i => i.ToString());
        _lru.GetOrAdd(3, i => i.ToString());

        _lru.AddOrUpdate(4, "4");
        _lru.AddOrUpdate(5, "5");
        _lru.AddOrUpdate(6, "6");

        _lru.Trim(itemCount);

        _lru.Keys.Should().BeEquivalentTo(expected);
    }

    [Theory]
    [InlineData(1, new[] { 3, 2 })]
    [InlineData(2, new[] { 3 })]
    [InlineData(3, new int[] { })]
    [InlineData(4, new int[] { })]
    [InlineData(5, new int[] { })]
    [InlineData(6, new int[] { })]
    [InlineData(7, new int[] { })]
    [InlineData(8, new int[] { })]
    [InlineData(9, new int[] { })]
    public void WhenHotItemsExistTrimRemovesExpectedItemCount(int itemCount, int[] expected)
    {
        // initial state:
        // Hot = 3, 2, 1
        // Warm = -
        // Cold = -
        _lru.AddOrUpdate(1, "1");
        _lru.AddOrUpdate(2, "2");
        _lru.AddOrUpdate(3, "3");

        _lru.Trim(itemCount);

        _lru.Keys.Should().BeEquivalentTo(expected);
    }

    [Theory]
    [InlineData(10)]
    [InlineData(20)]
    [InlineData(30)]
    [InlineData(40)]
    [InlineData(50)]
    [InlineData(60)]
    [InlineData(70)]
    [InlineData(80)]
    public void WhenColdItemsAreTouchedTrimRemovesExpectedItemCount(int trimCount)
    {
        FillCache();

        // Warm items
        var warmItems = Enumerable.Range(1, WarmCap).ToArray();
        foreach (var item in warmItems)
        {
            _lru.AddOrUpdate(item, item.ToString());
            _lru.GetOrAdd(item, _valueFactory.Create);
        }

        // Cold items (added but untouched)
        var coldItems = Enumerable.Range(1000, ColdCap).ToArray();
        foreach (var item in coldItems)
        {
            _lru.AddOrUpdate(item, item.ToString());
        }

        // Hot Items (evict the previous hot items to cold)
        var hotItems = Enumerable.Range(2000, HotCap).ToArray();
        foreach (var item in hotItems)
        {
            _lru.AddOrUpdate(item, item.ToString());
        }

        // Touch cold items to promote them to warm
        foreach (var item in coldItems)
        {
            _lru.GetOrAdd(item, _valueFactory.Create);
        }

        _lru.Trim(trimCount);

        int[] expected = [
            .. warmItems.Skip(Math.Max(0, trimCount)),
            .. hotItems,
            .. coldItems];
        _lru.Keys.Order().Should().BeEquivalentTo(expected.Order());
        _testOutputHelper.WriteLine("LRU " + string.Join(" ", _lru.Keys));
        _testOutputHelper.WriteLine("exp " + string.Join(" ", expected));

        _lru.Keys.Should().BeEquivalentTo(expected);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(10)]
    public void WhenItemsExistAndItemsAccessedTrimRemovesAllItems(int itemCount)
    {
        // By default capacity is 9. Test all possible states of touched items
        // in the cache.

        for (var i = 0; i < itemCount; i++)
        {
            _lru.AddOrUpdate(i, "1");
        }

        // touch n items
        for (var i = 0; i < itemCount; i++)
        {
            _lru.TryGet(i, out _);
        }

        _lru.Trim(Math.Min(itemCount, _lru.Capacity));

        _testOutputHelper.WriteLine("LRU " + string.Join(" ", _lru.Keys));

        _lru.Count.Should().Be(0);

        // verify queues are purged
        _lru.HotCount.Should().Be(0);
        _lru.WarmCount.Should().Be(0);
        _lru.ColdCount.Should().Be(0);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(10)]
    public void WhenItemsRemovedClearRemovesAllItems(int itemCount)
    {
        for (var i = 0; i < itemCount; i++)
        {
            _lru.AddOrUpdate(i, "1");
        }

        // this leaves an item in the queue but not the dictionary
        _lru.TryRemove(0, out _);

        _lru.Clear();

        _testOutputHelper.WriteLine("LRU " + string.Join(" ", _lru.Keys));

        _lru.Count.Should().Be(0);

        // verify queues are purged
        _lru.HotCount.Should().Be(0);
        _lru.WarmCount.Should().Be(0);
        _lru.ColdCount.Should().Be(0);
    }

    [Fact]
    public void WhenItemsAreDisposableTrimDisposesItems()
    {
        var lruOfDisposable = new ConcurrentLruCache<int, DisposableItem>(6, EqualityComparer<int>.Default);

        var items = Enumerable.Range(1, 4).Select(i => new DisposableItem()).ToList();

        for (var i = 0; i < 4; i++)
        {
            lruOfDisposable.AddOrUpdate(i, items[i]);
        }

        lruOfDisposable.Trim(2);

        items[0].IsDisposed.Should().BeTrue();
        items[1].IsDisposed.Should().BeTrue();
        items[2].IsDisposed.Should().BeFalse();
        items[3].IsDisposed.Should().BeFalse();
    }

    private void FillCache() => GetOrAddRangeInclusive(-1, -Capacity);

    private void Print()
    {
#if DEBUG
        _testOutputHelper.WriteLine(_lru.FormatLruString());
#endif
    }

    private class ValueFactory
    {
        public int TimesCalled;

        public string Create(int key)
        {
            TimesCalled++;
            return key.ToString();
        }

        public string Create<TArg>(int key, TArg arg)
        {
            TimesCalled++;
            return $"{key}{arg}";
        }

        public Task<string> CreateAsync(int key)
        {
            TimesCalled++;
            return Task.FromResult(key.ToString());
        }

        public Task<string> CreateAsync<TArg>(int key, TArg arg)
        {
            TimesCalled++;
            return Task.FromResult($"{key}{arg}");
        }
    }

    private class DisposableItem : IDisposable
    {
        public bool IsDisposed { get; private set; }

        public void Dispose()
        {
            IsDisposed = true;
        }
    }

    private class DisposableValueFactory
    {
        public Dictionary<int, DisposableItem> Items { get; } = [];

        public DisposableItem Create(int key)
        {
            var item = new DisposableItem();
            Items.Add(key, item);
            return item;
        }

        public Task<DisposableItem> CreateAsync(int key)
        {
            var item = new DisposableItem();
            Items.Add(key, item);
            return Task.FromResult(item);
        }
    }
}
