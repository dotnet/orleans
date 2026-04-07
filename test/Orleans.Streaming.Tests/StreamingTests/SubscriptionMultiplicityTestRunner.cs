#nullable enable

using Microsoft.Extensions.DependencyInjection;
using Orleans.Runtime;
using Orleans.Streams;
using Orleans.TestingHost;
using TestExtensions;
using UnitTests.GrainInterfaces;
using Xunit;

namespace UnitTests.StreamingTests;

public class SubscriptionMultiplicityTestRunner
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);
    private const int EventCountPerPhase = 10;
    private readonly string streamProviderName;
    private readonly TestCluster testCluster;

    private sealed class ConsumptionObserver : IMultipleSubscriptionConsumerObserver
    {
        private readonly CancellationToken cancellationToken;
        private readonly Dictionary<Guid, int> currentCounts = new();
        private readonly Dictionary<Guid, int> targetCounts = new();
        private readonly Dictionary<Guid, TaskCompletionSource<int>> completions = new();
        private readonly object sync = new();

        public ConsumptionObserver(CancellationToken cancellationToken)
        {
            this.cancellationToken = cancellationToken;
        }

        public Task WaitForAsync(Guid handleId, int expectedCount)
        {
            lock (sync)
            {
                if (!completions.TryGetValue(handleId, out var completion))
                {
                    completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
                    if (cancellationToken.CanBeCanceled)
                    {
                        cancellationToken.Register(static state => ((TaskCompletionSource<int>)state!).TrySetCanceled(), completion);
                    }

                    completions[handleId] = completion;
                }

                targetCounts[handleId] = expectedCount;
                if (currentCounts.TryGetValue(handleId, out var currentCount) && currentCount >= expectedCount)
                {
                    completion.TrySetResult(currentCount);
                }

                return completion.Task;
            }
        }

        public void ConsumedCountReached(Guid handleId, int count)
        {
            TaskCompletionSource<int>? completion = null;
            lock (sync)
            {
                currentCounts[handleId] = count;
                if (completions.TryGetValue(handleId, out completion)
                    && targetCounts.TryGetValue(handleId, out var targetCount)
                    && count >= targetCount)
                {
                    // completed outside lock
                }
                else
                {
                    completion = null;
                }
            }

            completion?.TrySetResult(count);
        }

        public void ConsumptionFailed(Guid handleId, int errorCount)
        {
            TaskCompletionSource<int>? completion = null;
            lock (sync)
            {
                if (!completions.TryGetValue(handleId, out completion))
                {
                    completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
                    completions[handleId] = completion;
                }
            }

            completion!.TrySetException(new InvalidOperationException($"Consumption failed for handle {handleId} with {errorCount} errors."));
        }
    }

    private sealed class LocalCountObserver
    {
        private readonly CancellationToken cancellationToken;
        private readonly object sync = new();
        private TaskCompletionSource<bool>? completion;
        private int targetCount;
        private int currentCount;

        public LocalCountObserver(CancellationToken cancellationToken)
        {
            this.cancellationToken = cancellationToken;
        }

        public int CurrentCount => currentCount;

        public void Reset()
        {
            lock (sync)
            {
                completion = null;
                targetCount = 0;
            }

            Interlocked.Exchange(ref currentCount, 0);
        }

        public void Increment()
        {
            var count = Interlocked.Increment(ref currentCount);
            TaskCompletionSource<bool>? currentCompletion = null;
            lock (sync)
            {
                if (completion is not null && count >= targetCount)
                {
                    currentCompletion = completion;
                }
            }

            currentCompletion?.TrySetResult(true);
        }

        public Task WaitForCountAsync(int count)
        {
            var currentCompletion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (cancellationToken.CanBeCanceled)
            {
                cancellationToken.Register(static state => ((TaskCompletionSource<bool>)state!).TrySetCanceled(), currentCompletion);
            }

            lock (sync)
            {
                targetCount = count;
                completion = currentCompletion;
                if (currentCount >= targetCount)
                {
                    currentCompletion.TrySetResult(true);
                }
            }

            return currentCompletion.Task;
        }
    }

    public SubscriptionMultiplicityTestRunner(string streamProviderName, TestCluster testCluster)
    {
        if (string.IsNullOrWhiteSpace(streamProviderName))
        {
            throw new ArgumentNullException(nameof(streamProviderName));
        }
        this.streamProviderName = streamProviderName;
        this.testCluster = testCluster;
    }

    public async Task MultipleParallelSubscriptionTest(Guid streamGuid, string streamNamespace)
    {
        using var cts = new CancellationTokenSource(Timeout);

        // get producer and consumer
        var producer = this.testCluster.GrainFactory.GetGrain<ISampleStreaming_ProducerGrain>(Guid.NewGuid());
        var consumer = this.testCluster.GrainFactory.GetGrain<IMultipleSubscriptionConsumerGrain>(Guid.NewGuid());
        var streamId = StreamId.Create(streamNamespace, streamGuid);

        // setup two subscriptions
        StreamSubscriptionHandle<int> firstSubscriptionHandle = null!;
        await WaitForSubscriptionRegisteredAsync(streamId, async () => firstSubscriptionHandle = await consumer.BecomeConsumer(streamGuid, streamNamespace, streamProviderName), cts.Token);
        StreamSubscriptionHandle<int> secondSubscriptionHandle = null!;
        await WaitForSubscriptionRegisteredAsync(streamId, async () => secondSubscriptionHandle = await consumer.BecomeConsumer(streamGuid, streamNamespace, streamProviderName), cts.Token);
        // produce some messages
        await producer.BecomeProducer(streamGuid, streamNamespace, streamProviderName);
        await WarmUpColdStreamAsync(
            producer,
            this.testCluster.Client,
            cts.Token,
            (consumer, firstSubscriptionHandle.HandleId),
            (consumer, secondSubscriptionHandle.HandleId));
        var produced = await ProduceUntilConsumedAsync(
            producer,
            this.testCluster.Client,
            cts.Token,
            EventCountPerPhase,
            (consumer, firstSubscriptionHandle.HandleId),
            (consumer, secondSubscriptionHandle.HandleId));
        await AssertCountersAsync(producer, consumer, produced, 2);

        // unsubscribe
        await consumer.StopConsuming(firstSubscriptionHandle);
        await consumer.StopConsuming(secondSubscriptionHandle);
    }

    public async Task MultipleLinearSubscriptionTest(Guid streamGuid, string streamNamespace)
    {
        using var cts = new CancellationTokenSource(Timeout);

        // get producer and consumer
        var producer = this.testCluster.GrainFactory.GetGrain<ISampleStreaming_ProducerGrain>(Guid.NewGuid());
        var consumer = this.testCluster.GrainFactory.GetGrain<IMultipleSubscriptionConsumerGrain>(Guid.NewGuid());
        var streamId = StreamId.Create(streamNamespace, streamGuid);

        await producer.BecomeProducer(streamGuid, streamNamespace, streamProviderName);

        // setup one subscription and send messsages
        StreamSubscriptionHandle<int> firstSubscriptionHandle = null!;
        await WaitForSubscriptionRegisteredAsync(streamId, async () => firstSubscriptionHandle = await consumer.BecomeConsumer(streamGuid, streamNamespace, streamProviderName), cts.Token);
        await WarmUpColdStreamAsync(producer, this.testCluster.Client, cts.Token, (consumer, firstSubscriptionHandle.HandleId));
        var produced = await ProduceUntilConsumedAsync(producer, this.testCluster.Client, cts.Token, EventCountPerPhase, (consumer, firstSubscriptionHandle.HandleId));
        await AssertCountersAsync(producer, consumer, produced, 1);

        // clear counts
        await consumer.ClearNumberConsumed();
        await producer.ClearNumberProduced();
        // remove first subscription and send messages
        await WaitForSubscriptionDetachedAsync(streamId, async () => await consumer.StopConsuming(firstSubscriptionHandle), cts.Token);

        // setup second subscription and send messages
        StreamSubscriptionHandle<int> secondSubscriptionHandle = null!;
        await WaitForSubscriptionRegisteredAsync(streamId, async () => secondSubscriptionHandle = await consumer.BecomeConsumer(streamGuid, streamNamespace, streamProviderName), cts.Token);
        await WarmUpColdStreamAsync(producer, this.testCluster.Client, cts.Token, (consumer, secondSubscriptionHandle.HandleId));
        produced = await ProduceUntilConsumedAsync(producer, this.testCluster.Client, cts.Token, EventCountPerPhase, (consumer, secondSubscriptionHandle.HandleId));
        await AssertCountersAsync(producer, consumer, produced, 1);

        // remove second subscription
        await consumer.StopConsuming(secondSubscriptionHandle);
    }

    public async Task MultipleSubscriptionTest_AddRemove(Guid streamGuid, string streamNamespace)
    {
        using var cts = new CancellationTokenSource(Timeout);

        // get producer and consumer
        var producer = this.testCluster.GrainFactory.GetGrain<ISampleStreaming_ProducerGrain>(Guid.NewGuid());
        var consumer = this.testCluster.GrainFactory.GetGrain<IMultipleSubscriptionConsumerGrain>(Guid.NewGuid());
        var streamId = StreamId.Create(streamNamespace, streamGuid);

        await producer.BecomeProducer(streamGuid, streamNamespace, streamProviderName);

        // setup one subscription and send messsages
        StreamSubscriptionHandle<int> firstSubscriptionHandle = null!;
        await WaitForSubscriptionRegisteredAsync(streamId, async () => firstSubscriptionHandle = await consumer.BecomeConsumer(streamGuid, streamNamespace, streamProviderName), cts.Token);
        await WarmUpColdStreamAsync(producer, this.testCluster.Client, cts.Token, (consumer, firstSubscriptionHandle.HandleId));
        var produced = await ProduceUntilConsumedAsync(producer, this.testCluster.Client, cts.Token, EventCountPerPhase, (consumer, firstSubscriptionHandle.HandleId));
        await AssertCountersAsync(producer, consumer, produced, 1);

        // clear counts
        await consumer.ClearNumberConsumed();
        await producer.ClearNumberProduced();

        // setup second subscription and send messages
        StreamSubscriptionHandle<int> secondSubscriptionHandle = null!;
        await WaitForSubscriptionRegisteredAsync(streamId, async () => secondSubscriptionHandle = await consumer.BecomeConsumer(streamGuid, streamNamespace, streamProviderName), cts.Token);
        await WarmUpColdStreamAsync(
            producer,
            this.testCluster.Client,
            cts.Token,
            (consumer, firstSubscriptionHandle.HandleId),
            (consumer, secondSubscriptionHandle.HandleId));
        produced = await ProduceUntilConsumedAsync(
            producer,
            this.testCluster.Client,
            cts.Token,
            EventCountPerPhase,
            (consumer, firstSubscriptionHandle.HandleId),
            (consumer, secondSubscriptionHandle.HandleId));
        await AssertCountersAsync(producer, consumer, produced, 2);

        // clear counts
        await consumer.ClearNumberConsumed();
        await producer.ClearNumberProduced();

        // remove first subscription and send messages
        await WaitForSubscriptionDetachedAsync(streamId, async () => await consumer.StopConsuming(firstSubscriptionHandle), cts.Token);

        produced = await ProduceUntilConsumedAsync(producer, this.testCluster.Client, cts.Token, EventCountPerPhase, (consumer, secondSubscriptionHandle.HandleId));
        await AssertCountersAsync(producer, consumer, produced, 1);

        // remove second subscription
        await consumer.StopConsuming(secondSubscriptionHandle);
    }

    public async Task ResubscriptionTest(Guid streamGuid, string streamNamespace)
    {
        using var cts = new CancellationTokenSource(Timeout);

        // get producer and consumer
        var producer = this.testCluster.GrainFactory.GetGrain<ISampleStreaming_ProducerGrain>(Guid.NewGuid());
        var consumer = this.testCluster.GrainFactory.GetGrain<IMultipleSubscriptionConsumerGrain>(Guid.NewGuid());
        var streamId = StreamId.Create(streamNamespace, streamGuid);

        await producer.BecomeProducer(streamGuid, streamNamespace, streamProviderName);

        // setup one subscription and send messsages
        StreamSubscriptionHandle<int> firstSubscriptionHandle = null!;
        await WaitForSubscriptionRegisteredAsync(streamId, async () => firstSubscriptionHandle = await consumer.BecomeConsumer(streamGuid, streamNamespace, streamProviderName), cts.Token);
        await WarmUpColdStreamAsync(producer, this.testCluster.Client, cts.Token, (consumer, firstSubscriptionHandle.HandleId));
        var produced = await ProduceUntilConsumedAsync(producer, this.testCluster.Client, cts.Token, EventCountPerPhase, (consumer, firstSubscriptionHandle.HandleId));
        await AssertCountersAsync(producer, consumer, produced, 1);

        // Resume
        StreamSubscriptionHandle<int> resumeHandle = null!;
        resumeHandle = await consumer.Resume(firstSubscriptionHandle);

        Assert.Equal(firstSubscriptionHandle, resumeHandle);

        produced = await ProduceUntilConsumedAsync(producer, this.testCluster.Client, cts.Token, EventCountPerPhase, false, (consumer, resumeHandle.HandleId));
        await AssertCountersAsync(producer, consumer, produced, 1);

        // remove subscription
        await consumer.StopConsuming(resumeHandle);
    }

    public async Task ResubscriptionAfterDeactivationTest(Guid streamGuid, string streamNamespace)
    {
        using var grainObserver = GrainDiagnosticObserver.Create();
        using var cts = new CancellationTokenSource(Timeout);

        // get producer and consumer
        var producer = this.testCluster.GrainFactory.GetGrain<ISampleStreaming_ProducerGrain>(Guid.NewGuid());
        var consumer = this.testCluster.GrainFactory.GetGrain<IMultipleSubscriptionConsumerGrain>(Guid.NewGuid());
        var streamId = StreamId.Create(streamNamespace, streamGuid);

        await producer.BecomeProducer(streamGuid, streamNamespace, streamProviderName);

        // setup one subscription and send messsages
        StreamSubscriptionHandle<int> firstSubscriptionHandle = null!;
        await WaitForSubscriptionRegisteredAsync(streamId, async () => firstSubscriptionHandle = await consumer.BecomeConsumer(streamGuid, streamNamespace, streamProviderName), cts.Token);
        await WarmUpColdStreamAsync(producer, this.testCluster.Client, cts.Token, (consumer, firstSubscriptionHandle.HandleId));
        var produced = await ProduceUntilConsumedAsync(producer, this.testCluster.Client, cts.Token, EventCountPerPhase, (consumer, firstSubscriptionHandle.HandleId));
        await AssertCountersAsync(producer, consumer, produced, 1);

        // Deactivate grain
        await consumer.Deactivate();
        await grainObserver.WaitForDeactivatedAsync(consumer, Timeout);

        // clear producer counts
        await producer.ClearNumberProduced();

        // Resume
        StreamSubscriptionHandle<int> resumeHandle = null!;
        resumeHandle = await consumer.Resume(firstSubscriptionHandle);

        Assert.Equal(firstSubscriptionHandle, resumeHandle);

        produced = await ProduceUntilConsumedAsync(producer, this.testCluster.Client, cts.Token, EventCountPerPhase, (consumer, resumeHandle.HandleId));
        await AssertCountersAsync(producer, consumer, produced, 1);

        // remove subscription
        await consumer.StopConsuming(resumeHandle);
    }

    public async Task ActiveSubscriptionTest(Guid streamGuid, string streamNamespace)
    {
        const int subscriptionCount = 10;

        // get producer and consumer
        var consumer = this.testCluster.GrainFactory.GetGrain<IMultipleSubscriptionConsumerGrain>(Guid.NewGuid());

        // create expected subscriptions
        IEnumerable<Task<StreamSubscriptionHandle<int>>> subscriptionTasks =
            Enumerable.Range(0, subscriptionCount)
                .Select(async i => await consumer.BecomeConsumer(streamGuid, streamNamespace, streamProviderName));
        List<StreamSubscriptionHandle<int>> expectedSubscriptions = (await Task.WhenAll(subscriptionTasks)).ToList();

        // query actuall subscriptions
        IList<StreamSubscriptionHandle<int>> actualSubscriptions = await consumer.GetAllSubscriptions(streamGuid, streamNamespace, streamProviderName);

        // validate
        Assert.Equal(subscriptionCount, actualSubscriptions.Count);
        Assert.Equal(subscriptionCount, expectedSubscriptions.Count);
        foreach (StreamSubscriptionHandle<int> subscription in actualSubscriptions)
        {
            Assert.True(expectedSubscriptions.Contains(subscription), "Subscription Match");
        }

        // unsubscribe from one of the subscriptions
        StreamSubscriptionHandle<int> firstHandle = expectedSubscriptions.First();
        await consumer.StopConsuming(firstHandle);
        expectedSubscriptions.Remove(firstHandle);

        // query actuall subscriptions again
        actualSubscriptions = await consumer.GetAllSubscriptions(streamGuid, streamNamespace, streamProviderName);

        // validate
        Assert.Equal(subscriptionCount-1, actualSubscriptions.Count);
        Assert.Equal(subscriptionCount-1, expectedSubscriptions.Count);
        foreach (StreamSubscriptionHandle<int> subscription in actualSubscriptions)
        {
            Assert.True(expectedSubscriptions.Contains(subscription), "Subscription Match");
        }

        // unsubscribe from the rest of the subscriptions
        await Task.WhenAll(expectedSubscriptions.Select(h => consumer.StopConsuming(h)));

        // query actuall subscriptions again
        actualSubscriptions = await consumer.GetAllSubscriptions(streamGuid, streamNamespace, streamProviderName);

        // validate
        Assert.Empty(actualSubscriptions);
    }

    public async Task TwoIntermitentStreamTest(Guid streamGuid)
    {
        const string streamNamespace1 = "streamNamespace1";
        const string streamNamespace2 = "streamNamespace2";
        using var cts = new CancellationTokenSource(Timeout);

        // send events on first stream /////////////////////////////
        var producer = this.testCluster.GrainFactory.GetGrain<ISampleStreaming_ProducerGrain>(Guid.NewGuid());
        var consumer = this.testCluster.GrainFactory.GetGrain<IMultipleSubscriptionConsumerGrain>(Guid.NewGuid());
        var streamId1 = StreamId.Create(streamNamespace1, streamGuid);

        await producer.BecomeProducer(streamGuid, streamNamespace1, streamProviderName);

        StreamSubscriptionHandle<int> handle = null!;
        await WaitForSubscriptionRegisteredAsync(streamId1, async () => handle = await consumer.BecomeConsumer(streamGuid, streamNamespace1, streamProviderName), cts.Token);
        await WarmUpColdStreamAsync(producer, this.testCluster.Client, cts.Token, (consumer, handle.HandleId));
        var produced = await ProduceUntilConsumedAsync(producer, this.testCluster.Client, cts.Token, EventCountPerPhase, (consumer, handle.HandleId));
        await AssertCountersAsync(producer, consumer, produced, 1);

        // send some events on second stream /////////////////////////////
        var producer2 = this.testCluster.GrainFactory.GetGrain<ISampleStreaming_ProducerGrain>(Guid.NewGuid());
        var consumer2 = this.testCluster.GrainFactory.GetGrain<IMultipleSubscriptionConsumerGrain>(Guid.NewGuid());
        var streamId2 = StreamId.Create(streamNamespace2, streamGuid);

        await producer2.BecomeProducer(streamGuid, streamNamespace2, streamProviderName);

        StreamSubscriptionHandle<int> handle2 = null!;
        await WaitForSubscriptionRegisteredAsync(streamId2, async () => handle2 = await consumer2.BecomeConsumer(streamGuid, streamNamespace2, streamProviderName), cts.Token);
        await WarmUpColdStreamAsync(producer2, this.testCluster.Client, cts.Token, (consumer2, handle2.HandleId));
        var produced2 = await ProduceUntilConsumedAsync(producer2, this.testCluster.Client, cts.Token, EventCountPerPhase, (consumer2, handle2.HandleId));
        await AssertCountersAsync(producer2, consumer2, produced2, 1);

        // send some events on first stream again /////////////////////////////
        produced = await ProduceUntilConsumedAsync(producer, this.testCluster.Client, cts.Token, EventCountPerPhase, false, (consumer, handle.HandleId));
        await AssertCountersAsync(producer, consumer, produced, 1);

        await consumer.StopConsuming(handle);
        await consumer2.StopConsuming(handle2);
    }

    public async Task SubscribeFromClientTest(Guid streamGuid, string streamNamespace)
    {
        using var cts = new CancellationTokenSource(Timeout);

        // get producer and consumer
        var producer = this.testCluster.GrainFactory.GetGrain<ISampleStreaming_ProducerGrain>(Guid.NewGuid());
        var eventCount = new LocalCountObserver(cts.Token);
        var streamId = StreamId.Create(streamNamespace, streamGuid);

        var provider = this.testCluster.Client.ServiceProvider.GetKeyedService<IStreamProvider>(streamProviderName);
        var stream = provider.GetStream<int>(streamNamespace, streamGuid);
        StreamSubscriptionHandle<int> handle = null!;
        await WaitForSubscriptionRegisteredAsync(
            streamId,
            async () =>
            {
                handle = await stream.SubscribeAsync((e, t) =>
                {
                    eventCount.Increment();
                    return Task.CompletedTask;
                });
            },
            cts.Token);

        // produce some messages
        await producer.BecomeProducer(streamGuid, streamNamespace, streamProviderName);
        await WarmUpColdClientStreamAsync(producer, cts.Token, eventCount);
        var produced = await ProduceUntilClientConsumedAsync(producer, cts.Token, eventCount, EventCountPerPhase);
        await AssertCountersAsync(producer, () => eventCount.CurrentCount, produced);

        // unsubscribe
        await handle.UnsubscribeAsync();
    }

    private static async Task ProduceExactCountAsync(ISampleStreaming_ProducerGrain producer, int count)
    {
        for (var i = 0; i < count; i++)
        {
            await producer.Produce();
        }
    }

    private static Task<int> ProduceUntilConsumedAsync(
        ISampleStreaming_ProducerGrain producer,
        IGrainFactory grainFactory,
        CancellationToken cancellationToken,
        int target,
        params (IMultipleSubscriptionConsumerGrain Consumer, Guid HandleId)[] waits)
        => ProduceUntilConsumedAsync(producer, grainFactory, cancellationToken, target, true, waits);

    private static async Task WaitForConsumerCountsAsync(
        IGrainFactory grainFactory,
        CancellationToken cancellationToken,
        int expectedCount,
        params (IMultipleSubscriptionConsumerGrain Consumer, Guid HandleId)[] waits)
    {
        var observer = new ConsumptionObserver(cancellationToken);
        var observerReference = grainFactory.CreateObjectReference<IMultipleSubscriptionConsumerObserver>(observer);
        try
        {
            var waitTasks = waits.Select(wait => observer.WaitForAsync(wait.HandleId, expectedCount)).ToArray();
            foreach (var wait in waits)
            {
                await wait.Consumer.RegisterCountObserver(wait.HandleId, expectedCount, observerReference);
            }

            try
            {
                await Task.WhenAll(waitTasks);
            }
            catch (OperationCanceledException exception) when (cancellationToken.IsCancellationRequested)
            {
                var snapshots = new List<string>(waits.Length);
                foreach (var wait in waits)
                {
                    var counts = await wait.Consumer.GetNumberConsumed();
                    var match = counts.FirstOrDefault(entry => entry.Key.HandleId == wait.HandleId);
                    if (match.Key is not null)
                    {
                        snapshots.Add($"{wait.HandleId}: consumed={match.Value.Item1}, errors={match.Value.Item2}");
                    }
                    else
                    {
                        snapshots.Add($"{wait.HandleId}: missing");
                    }
                }

                throw new TimeoutException(
                    $"Timed out waiting for consumers to reach count {expectedCount}. {string.Join("; ", snapshots)}",
                    exception);
            }
        }
        finally
        {
            grainFactory.DeleteObjectReference<IMultipleSubscriptionConsumerObserver>(observerReference);
        }
    }

    private static async Task<int> ProduceUntilConsumedAsync(
        ISampleStreaming_ProducerGrain producer,
        IGrainFactory grainFactory,
        CancellationToken cancellationToken,
        int target,
        bool resetCounts = true,
        params (IMultipleSubscriptionConsumerGrain Consumer, Guid HandleId)[] waits)
    {
        if (resetCounts)
        {
            foreach (var consumer in waits.Select(wait => wait.Consumer).Distinct())
            {
                await consumer.ClearNumberConsumed();
            }

            await producer.ClearNumberProduced();
        }

        await ProduceExactCountAsync(producer, target);
        var produced = await producer.GetNumberProduced();

        try
        {
            await WaitForConsumerCountsAsync(grainFactory, cancellationToken, produced, waits);
        }
        catch (TimeoutException exception)
        {
            var currentProduced = await producer.GetNumberProduced();
            throw new TimeoutException(
                $"Timed out waiting for consumers to reach produced count {produced}. Current produced={currentProduced}. {exception.Message}",
                exception);
        }

        return produced;
    }

    private static async Task WaitForClientCountAsync(
        ISampleStreaming_ProducerGrain producer,
        CancellationToken cancellationToken,
        LocalCountObserver countObserver,
        int expectedCount)
    {
        try
        {
            await countObserver.WaitForCountAsync(expectedCount);
        }
        catch (OperationCanceledException exception) when (cancellationToken.IsCancellationRequested)
        {
            var currentProduced = await producer.GetNumberProduced();
            throw new TimeoutException(
                $"Timed out waiting for client consumer count to reach {expectedCount}. Current produced={currentProduced}, current client count={countObserver.CurrentCount}.",
                exception);
        }
    }

    private static async Task<int> ProduceUntilClientConsumedAsync(
        ISampleStreaming_ProducerGrain producer,
        CancellationToken cancellationToken,
        LocalCountObserver countObserver,
        int target)
    {
        countObserver.Reset();
        await producer.ClearNumberProduced();
        await ProduceExactCountAsync(producer, target);
        var produced = await producer.GetNumberProduced();
        await WaitForClientCountAsync(producer, cancellationToken, countObserver, produced);

        return produced;
    }

    private static async Task WaitForConsumersToCatchUpAsync(
        ISampleStreaming_ProducerGrain producer,
        IGrainFactory grainFactory,
        CancellationToken cancellationToken,
        params (IMultipleSubscriptionConsumerGrain Consumer, Guid HandleId)[] waits)
    {
        while (true)
        {
            var produced = await producer.GetNumberProduced();
            await WaitForConsumerCountsAsync(grainFactory, cancellationToken, produced, waits);

            var confirmedProduced = await producer.GetNumberProduced();
            if (confirmedProduced == produced)
            {
                return;
            }
        }
    }

    private static async Task WaitForClientToCatchUpAsync(
        ISampleStreaming_ProducerGrain producer,
        CancellationToken cancellationToken,
        LocalCountObserver countObserver)
    {
        while (true)
        {
            var produced = await producer.GetNumberProduced();
            await WaitForClientCountAsync(producer, cancellationToken, countObserver, produced);

            var confirmedProduced = await producer.GetNumberProduced();
            if (confirmedProduced == produced)
            {
                return;
            }
        }
    }

    private static async Task WarmUpColdStreamAsync(
        ISampleStreaming_ProducerGrain producer,
        IGrainFactory grainFactory,
        CancellationToken cancellationToken,
        params (IMultipleSubscriptionConsumerGrain Consumer, Guid HandleId)[] waits)
    {
        foreach (var consumer in waits.Select(wait => wait.Consumer).Distinct())
        {
            await consumer.ClearNumberConsumed();
        }

        await producer.ClearNumberProduced();
        await producer.StartPeriodicProducing();
        try
        {
            await WaitForConsumerCountsAsync(grainFactory, cancellationToken, 1, waits);
        }
        finally
        {
            await producer.StopPeriodicProducing().ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing | ConfigureAwaitOptions.ContinueOnCapturedContext);
        }

        await WaitForConsumersToCatchUpAsync(producer, grainFactory, cancellationToken, waits);

        foreach (var consumer in waits.Select(wait => wait.Consumer).Distinct())
        {
            await consumer.ClearNumberConsumed();
        }

        await producer.ClearNumberProduced();
    }

    private static async Task WarmUpColdClientStreamAsync(
        ISampleStreaming_ProducerGrain producer,
        CancellationToken cancellationToken,
        LocalCountObserver countObserver)
    {
        countObserver.Reset();
        await producer.ClearNumberProduced();
        await producer.StartPeriodicProducing();
        try
        {
            await WaitForClientCountAsync(producer, cancellationToken, countObserver, 1);
        }
        finally
        {
            await producer.StopPeriodicProducing().ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing | ConfigureAwaitOptions.ContinueOnCapturedContext);
        }

        await WaitForClientToCatchUpAsync(producer, cancellationToken, countObserver);
        countObserver.Reset();
        await producer.ClearNumberProduced();
    }

    private async Task WaitForSubscriptionRegisteredAsync(StreamId streamId, Func<Task> action, CancellationToken cancellationToken)
    {
        using var observer = StreamingDiagnosticObserver.Create();
        await action();
        await observer.WaitForSubscriptionRegisteredAsync(streamId, streamProviderName, cancellationToken);
    }

    private async Task WaitForSubscriptionDetachedAsync(StreamId streamId, Func<Task> action, CancellationToken cancellationToken)
    {
        using var observer = StreamingDiagnosticObserver.Create();
        await action();
        await observer.WaitForSubscriptionDetachedAsync(streamId, streamProviderName, cancellationToken);
    }

    private async Task AssertCountersAsync(ISampleStreaming_ProducerGrain producer, IMultipleSubscriptionConsumerGrain consumer, int expectedProduced, int expectedConsumerCount)
    {
        var numProduced = await producer.GetNumberProduced();
        var numConsumed = await consumer.GetNumberConsumed();

        Assert.Equal(expectedProduced, numProduced);
        Assert.Equal(expectedConsumerCount, numConsumed.Count);
        Assert.True(numConsumed.Values.All(v => v.Item2 == 0), "Errors");

        foreach (var consumed in numConsumed)
        {
            Assert.Equal(expectedProduced, consumed.Value.Item1);
        }
    }

    private static async Task AssertCountersAsync(ISampleStreaming_ProducerGrain producer, Func<int> eventCount, int expectedProduced)
    {
        var numProduced = await producer.GetNumberProduced();

        Assert.Equal(expectedProduced, numProduced);
        Assert.Equal(expectedProduced, eventCount());
    }
}
