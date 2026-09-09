using Orleans.Runtime.GrainDirectory;
using Orleans.Runtime.Utilities;
using Xunit;

namespace NonSilo.Tests.Utilities;

[TestCategory("BVT")]
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Runtime")]
public sealed class ConcurrentHashRangeDictionaryConcurrencyTests
{
    private static readonly TimeSpan PhaseTimeout = TimeSpan.FromSeconds(30);

    [Theory]
    [InlineData(1)]
    [InlineData(-1)]
    public async Task ConcurrentFirstInsertionAndGrowth_PreserveLongLivedKeysAndExactQuiescentCounts(int concurrencyLevel)
    {
        const int writers = 4;
        const int batchSize = 128;
        const int rounds = 4;
        static uint Hash(int key) => key < writers || key % 31 == 0 ? 0x08000011 : unchecked((uint)key * 2654435761);
        var dictionary = new ConcurrentHashRangeDictionary<int, int, TestHashComparer<int>>(new(Hash), capacity: 2, concurrencyLevel: concurrencyLevel);
        var expected = new Dictionary<int, int>();
        static int Value(int key) => key * 17 + 1;
        static int Key(int round, int worker, int offset) => writers + (round * writers + worker) * batchSize + offset;

        await RunWorkers(writers + 1, (worker, phases, cancellation) =>
        {
            Meet(phases, cancellation, "first insertion armed");
            if (worker < writers)
            {
                Assert.True(dictionary.TryAdd(worker, Value(worker)));
            }

            Meet(phases, cancellation, "long-lived collision keys published");
            for (var round = 0; round < rounds; round++)
            {
                if (worker < writers)
                {
                    for (var offset = 0; offset < batchSize; offset++)
                    {
                        var key = Key(round, worker, offset);
                        Assert.True(dictionary.TryAdd(key, Value(key)));
                    }
                }
                else
                {
                    ReadLongLivedKeys();
                }

                Meet(phases, cancellation, $"round {round} insertions complete");
                if (worker < writers)
                {
                    for (var offset = 1; offset < batchSize; offset += 2)
                    {
                        var key = Key(round, worker, offset);
                        Assert.True(dictionary.TryRemove(key, out var value));
                        Assert.Equal(Value(key), value);
                    }
                }
                else
                {
                    ReadLongLivedKeys();
                }

                Meet(phases, cancellation, $"round {round} removals complete");
            }
        }, phases =>
        {
            var phase = (int)phases.CurrentPhaseNumber;
            if (phase == 1)
            {
                for (var key = 0; key < writers; key++)
                {
                    expected.Add(key, Value(key));
                }
            }
            else if (phase >= 2)
            {
                var round = (phase - 2) / 2;
                for (var worker = 0; worker < writers; worker++)
                {
                    for (var offset = 0; offset < batchSize; offset++)
                    {
                        var key = Key(round, worker, offset);
                        if (phase % 2 == 0)
                        {
                            expected.Add(key, Value(key));
                        }
                        else if (offset % 2 == 1)
                        {
                            Assert.True(expected.Remove(key));
                        }
                    }
                }
            }

            // The barrier's post-phase action executes while every worker is quiescent.
            Assert.Equal(expected.Count, dictionary.Count);
            Assert.Equal(expected.OrderBy(pair => pair.Key), dictionary.OrderBy(pair => pair.Key));
        });

        Assert.Equal(writers + rounds * writers * batchSize / 2, dictionary.Count);
        ReadLongLivedKeys();
        foreach (var (key, value) in expected)
        {
            Assert.True(dictionary.TryGetValue(key, out var actual));
            Assert.Equal(value, actual);
        }

        void ReadLongLivedKeys()
        {
            for (var iteration = 0; iteration < 256; iteration++)
            {
                for (var key = 0; key < writers; key++)
                {
                    Assert.True(dictionary.TryGetValue(key, out var value), $"Long-lived key {key} disappeared during growth/removal.");
                    Assert.Equal(Value(key), value);
                }
            }
        }
    }

    [Fact]
    public void Enumerator_RetainsOriginalKeysAcrossTableGrowth()
    {
        var dictionary = new ConcurrentHashRangeDictionary<int, int, TestHashComparer<int>>(new(key => unchecked((uint)key * 2654435761)), capacity: 2, concurrencyLevel: 1);
        for (var key = 0; key < 4; key++)
        {
            dictionary[key] = key + 17;
        }

        var enumerator = dictionary.GetEnumerator();
        Assert.True(enumerator.MoveNext());
        var captured = new List<KeyValuePair<int, int>> { enumerator.Current };
        for (var key = 4; key < 1024; key++)
        {
            dictionary[key] = key + 17;
        }

        while (enumerator.MoveNext())
        {
            captured.Add(enumerator.Current);
        }

        Assert.Equal(captured.Count, captured.Select(pair => pair.Key).Distinct().Count());
        Assert.Equal(Enumerable.Range(0, 4).Select(key => KeyValuePair.Create(key, key + 17)),
            captured.Where(pair => pair.Key < 4).OrderBy(pair => pair.Key));
        Assert.All(captured, pair => Assert.Equal(pair.Key + 17, pair.Value));
        Assert.Equal(1024, dictionary.Count);
        enumerator.Reset();
        var afterReset = new List<KeyValuePair<int, int>>();
        while (enumerator.MoveNext())
        {
            afterReset.Add(enumerator.Current);
        }

        enumerator.Dispose();
        Assert.Equal(Enumerable.Range(0, 1024).Select(key => KeyValuePair.Create(key, key + 17)),
            afterReset.OrderBy(pair => pair.Key));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task MultiwordValues_RemainWholeDuringSetterOrConditionalUpdatesAndGrowth(bool useTryUpdate, bool updateHead)
    {
        const int rounds = 8;
        const int writesPerHalf = 128;
        const int readsPerHalf = 256;
        const int growthPerHalf = 64;
        const int totalWrites = rounds * 2 * writesPerHalf;
        const uint collisionHash = 0x40000000;
        static uint Hash(int key) => key < 2 ? collisionHash : unchecked((uint)key * 2654435761);
        var dictionary = new ConcurrentHashRangeDictionary<int, WideValue, TestHashComparer<int>>(new(Hash), capacity: 32, concurrencyLevel: 1);
        dictionary[0] = WideValue.Create(0);
        dictionary[1] = WideValue.Create(0);
        // Before growth, key 1 is the chain head and key 0 is an interior node.
        var target = updateHead ? 1 : 0;
        var neighbor = 1 - target;
        var observations = new int[2];
        var writes = 0;

        await RunWorkers(4, (worker, phases, cancellation) =>
        {
            var current = WideValue.Create(0);
            for (var round = 0; round < rounds; round++)
            {
                Meet(phases, cancellation, $"struct round {round} ready");
                for (var half = 0; half < 2; half++)
                {
                    switch (worker)
                    {
                        case 0:
                            for (var i = 0; i < writesPerHalf; i++)
                            {
                                var next = WideValue.Create(current.Version + 1);
                                if (useTryUpdate)
                                {
                                    Assert.True(dictionary.TryUpdate(target, next, current));
                                }
                                else
                                {
                                    dictionary[target] = next;
                                }

                                current = next;
                                writes++;
                            }

                            break;
                        case 1:
                        case 2:
                            for (var i = 0; i < readsPerHalf; i++)
                            {
                                Assert.True(dictionary.TryGetValue(target, out var value));
                                AssertWhole(value);
                                var values = dictionary.EnumerateHash(collisionHash).ToArray();
                                Assert.Equal(2, values.Length);
                                AssertWhole(Assert.Single(values, pair => pair.Key == target).Value);
                                Assert.Equal(WideValue.Create(0), Assert.Single(values, pair => pair.Key == neighbor).Value);
                                observations[worker - 1]++;
                            }

                            break;
                        case 3 when round >= 2:
                            // The first two rounds exercise both original chain positions without resizing.
                            for (var i = 0; i < growthPerHalf; i++)
                            {
                                var key = 2 + ((round - 2) * 2 + half) * growthPerHalf + i;
                                Assert.True(dictionary.TryAdd(key, WideValue.Create(key)));
                            }

                            break;
                    }

                    Meet(phases, cancellation, $"struct round {round}, half {half} complete");
                }
            }
        }, phases =>
        {
            var phase = (int)phases.CurrentPhaseNumber;
            var round = phase / 3;
            var completedHalves = round * 2 + phase % 3;
            Assert.Equal(completedHalves * writesPerHalf, writes);
            Assert.All(observations, count => Assert.Equal(completedHalves * readsPerHalf, count));
        });

        Assert.Equal(totalWrites, writes);
        Assert.Equal(WideValue.Create(totalWrites), dictionary[target]);
        Assert.Equal(WideValue.Create(0), dictionary[neighbor]);
        var growthCount = (rounds - 2) * 2 * growthPerHalf;
        Assert.Equal(2 + growthCount, dictionary.Count);
        Assert.Equal(Enumerable.Range(0, 2 + growthCount), dictionary.Select(pair => pair.Key).Order());
        for (var key = 2; key < 2 + growthCount; key++)
        {
            Assert.Equal(WideValue.Create(key), dictionary[key]);
        }

        static void AssertWhole(WideValue value)
        {
            Assert.InRange(value.Version, 0, totalWrites);
            Assert.Equal(WideValue.Create(value.Version), value);
        }
    }

    [Fact]
    public async Task ConditionalRemove_PreservesReplacementPublishedBeforeRemoval()
    {
        using var removeMayContinue = new ManualResetEventSlim();
        var removalReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var armed = 0;
        var cancellation = TestContext.Current.CancellationToken;
        var dictionary = new ConcurrentHashRangeDictionary<int, string, TestHashComparer<int>>(new(_ =>
        {
            if (Interlocked.Exchange(ref armed, 0) == 1)
            {
                removalReady.SetResult();
                Assert.True(removeMayContinue.Wait(PhaseTimeout, cancellation), "Conditional removal was not released after replacement publication.");
            }

            return 17;
        }));
        dictionary[1] = "original";
        armed = 1;
        var removal = Task.Run(() => dictionary.TryRemove(KeyValuePair.Create(1, "original")), cancellation);
        var removed = true;
        try
        {
            await removalReady.Task.WaitAsync(PhaseTimeout, cancellation);
            dictionary[1] = "replacement";
        }
        finally
        {
            removeMayContinue.Set();
            removed = await removal.WaitAsync(PhaseTimeout, cancellation);
        }

        Assert.False(removed);
        Assert.Equal("replacement", dictionary[1]);
        Assert.Equal(1, dictionary.Count);
        Assert.True(dictionary.TryRemove(KeyValuePair.Create(1, "replacement")));
        Assert.Empty(dictionary);
        Assert.Equal(0, dictionary.Count);
    }

    [Fact]
    public async Task VisitRange_AllowsAnotherThreadToMutateTheVisitedBucket()
    {
        var dictionary = new ConcurrentHashRangeDictionary<int, int, TestHashComparer<int>>(new(key => (uint)key), capacity: 2, concurrencyLevel: 1);
        dictionary[1] = 17;
        using var mutationComplete = new ManualResetEventSlim();
        var cancellation = TestContext.Current.CancellationToken;
        Task mutation = Task.CompletedTask;
        var visited = new List<KeyValuePair<int, int>>();
        try
        {
            dictionary.VisitRange(RingRange.FromPoint(1), visited, (state, key, value) =>
            {
                state.Add(KeyValuePair.Create(key, value));
                mutation = Task.Run(() =>
                {
                    try
                    {
                        Assert.True(dictionary.TryUpdate(key, 29, value));
                        Assert.True(dictionary.TryRemove(key, out var removed));
                        Assert.Equal(29, removed);
                        Assert.True(dictionary.TryAdd(2, 41));
                    }
                    finally
                    {
                        mutationComplete.Set();
                    }
                }, cancellation);
                Assert.True(mutationComplete.Wait(PhaseTimeout, cancellation),
                    "Visitor retained a writer lock: the worker could not update/remove the visited key and insert its neighbor.");
            });
        }
        finally
        {
            await mutation.WaitAsync(PhaseTimeout, cancellation);
        }

        Assert.Equal(KeyValuePair.Create(1, 17), Assert.Single(visited));
        Assert.Equal(KeyValuePair.Create(2, 41), Assert.Single(dictionary));
        Assert.Equal(1, dictionary.Count);
        Assert.False(dictionary.TryGetValue(1, out _));
    }

    private static async Task RunWorkers(int count, Action<int, Barrier, CancellationToken> work, Action<Barrier>? checkpoint = null)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        using var phases = new Barrier(count, checkpoint);
        var workers = Enumerable.Range(0, count).Select(worker => Task.Factory.StartNew(() =>
        {
            try
            {
                work(worker, phases, cancellation.Token);
            }
            catch
            {
                cancellation.Cancel();
                throw;
            }
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default)).ToArray();
        await Task.WhenAll(workers);
    }

    private static void Meet(Barrier phases, CancellationToken cancellation, string description)
        => Assert.True(phases.SignalAndWait(PhaseTimeout, cancellation),
            $"Timed out at phase {phases.CurrentPhaseNumber}: {description}; remaining participants {phases.ParticipantsRemaining}.");

    private readonly record struct WideValue(long Version, long Complement, long Product, long Checksum, long Negative, long Offset)
    {
        public static WideValue Create(long version)
            => new(version, ~version, unchecked(version * 0x123456789ABCDEF), version ^ 0x5A5A5A5A5A5A5A5A, -version, version + 0x1234567800000000);
    }
}
