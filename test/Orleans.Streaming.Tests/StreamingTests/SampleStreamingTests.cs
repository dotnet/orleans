using Microsoft.Extensions.Logging;
using Orleans.TestingHost;
using Orleans.TestingHost.Utils;
using UnitTests.GrainInterfaces;
using Xunit;

namespace UnitTests.StreamingTests
{
    /// <summary>
    /// Sample streaming tests demonstrating basic producer-consumer patterns in Orleans Streaming.
    /// 
    /// These tests showcase fundamental streaming scenarios:
    /// - Producer and consumer grain communication via streams
    /// - Different subscription ordering (consumer-first vs producer-first)
    /// - Inline vs async consumer processing
    /// - Message delivery guarantees and counting
    /// 
    /// These patterns form the foundation for more complex streaming applications
    /// like real-time analytics, event processing, and pub-sub systems.
    /// </summary>
    public class SampleStreamingTests
    {
        private const string StreamNamespace = "SampleStreamNamespace";
        private static readonly TimeSpan _timeout = TimeSpan.FromSeconds(30);

        private readonly string streamProvider;
        private readonly ILogger logger;
        private readonly TestCluster cluster;

        public SampleStreamingTests(string streamProvider, ILogger logger, TestCluster cluster)
        {
            this.streamProvider = streamProvider;
            this.logger = logger;
            this.cluster = cluster;
        }

        /// <summary>
        /// Tests the scenario where a consumer subscribes before the producer starts.
        /// This verifies that:
        /// - Consumers can subscribe to streams that don't yet have producers
        /// - Messages are delivered once the producer starts
        /// - No messages are lost in the handoff
        /// </summary>
        public async Task StreamingTests_Consumer_Producer(
            Guid streamId,
            CancellationToken cancellationToken = default)
        {
            // consumer joins first, producer later
            var consumer = this.cluster.GrainFactory!.GetGrain<ISampleStreaming_ConsumerGrain>(Guid.NewGuid());
            await consumer.BecomeConsumer(streamId, StreamNamespace, streamProvider, cancellationToken);

            var producer = this.cluster.GrainFactory!.GetGrain<ISampleStreaming_ProducerGrain>(Guid.NewGuid());
            await producer.BecomeProducer(streamId, StreamNamespace, streamProvider, cancellationToken);

            var producerStarted = false;
            try
            {
                producerStarted = true;
                await producer.StartPeriodicProducing(cancellationToken);
                await Task.Delay(TimeSpan.FromMilliseconds(1000), cancellationToken);
                await producer.StopPeriodicProducing(cancellationToken);
                producerStarted = false;

                await TestingUtils.WaitUntilAsync(
                    (lastTry, token) => CheckCounters(producer, consumer, lastTry, token),
                    _timeout,
                    cancellationToken: cancellationToken);
            }
            finally
            {
                using var cleanup = new CancellationTokenSource(_timeout);
                if (producerStarted)
                {
                    await producer.StopPeriodicProducing(cleanup.Token)
                        .ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing | ConfigureAwaitOptions.ContinueOnCapturedContext);
                }

                await consumer.StopConsuming(cleanup.Token)
                    .ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing | ConfigureAwaitOptions.ContinueOnCapturedContext);
            }
        }

        /// <summary>
        /// Tests the scenario where a producer starts before any consumers subscribe.
        /// This verifies that:
        /// - Producers can send to streams without active consumers
        /// - Late-joining consumers receive messages (depending on provider)
        /// - The system handles dynamic subscription scenarios
        /// </summary>
        public async Task StreamingTests_Producer_Consumer(
            Guid streamId,
            CancellationToken cancellationToken = default)
        {
            // producer joins first, consumer later
            var producer = this.cluster.GrainFactory!.GetGrain<ISampleStreaming_ProducerGrain>(Guid.NewGuid());
            await producer.BecomeProducer(streamId, StreamNamespace, streamProvider, cancellationToken);

            var consumer = this.cluster.GrainFactory!.GetGrain<ISampleStreaming_ConsumerGrain>(Guid.NewGuid());
            await consumer.BecomeConsumer(streamId, StreamNamespace, streamProvider, cancellationToken);

            var producerStarted = false;
            try
            {
                producerStarted = true;
                await producer.StartPeriodicProducing(cancellationToken);
                await Task.Delay(TimeSpan.FromMilliseconds(1000), cancellationToken);
                await producer.StopPeriodicProducing(cancellationToken);
                producerStarted = false;

                await TestingUtils.WaitUntilAsync(
                    (lastTry, token) => CheckCounters(producer, consumer, lastTry, token),
                    _timeout,
                    cancellationToken: cancellationToken);
            }
            finally
            {
                using var cleanup = new CancellationTokenSource(_timeout);
                if (producerStarted)
                {
                    await producer.StopPeriodicProducing(cleanup.Token)
                        .ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing | ConfigureAwaitOptions.ContinueOnCapturedContext);
                }

                await consumer.StopConsuming(cleanup.Token)
                    .ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing | ConfigureAwaitOptions.ContinueOnCapturedContext);
            }
        }

        /// <summary>
        /// Tests streaming with an inline consumer that processes messages synchronously.
        /// Inline consumers:
        /// - Process messages in the same task context as delivery
        /// - Can provide lower latency but may impact throughput
        /// - Are useful for simple, fast message processing
        /// </summary>
        public async Task StreamingTests_Producer_InlineConsumer(
            Guid streamId,
            CancellationToken cancellationToken = default)
        {
            // producer joins first, consumer later
            var producer = this.cluster.GrainFactory!.GetGrain<ISampleStreaming_ProducerGrain>(Guid.NewGuid());
            await producer.BecomeProducer(streamId, StreamNamespace, streamProvider, cancellationToken);

            var consumer = this.cluster.GrainFactory!.GetGrain<ISampleStreaming_InlineConsumerGrain>(Guid.NewGuid());
            await consumer.BecomeConsumer(streamId, StreamNamespace, streamProvider, cancellationToken);

            var producerStarted = false;
            try
            {
                producerStarted = true;
                await producer.StartPeriodicProducing(cancellationToken);
                await Task.Delay(TimeSpan.FromMilliseconds(1000), cancellationToken);
                await producer.StopPeriodicProducing(cancellationToken);
                producerStarted = false;

                await TestingUtils.WaitUntilAsync(
                    (lastTry, token) => CheckCounters(producer, consumer, lastTry, token),
                    _timeout,
                    cancellationToken: cancellationToken);
            }
            finally
            {
                using var cleanup = new CancellationTokenSource(_timeout);
                if (producerStarted)
                {
                    await producer.StopPeriodicProducing(cleanup.Token)
                        .ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing | ConfigureAwaitOptions.ContinueOnCapturedContext);
                }

                await consumer.StopConsuming(cleanup.Token)
                    .ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing | ConfigureAwaitOptions.ContinueOnCapturedContext);
            }
        }

        /// <summary>
        /// Verifies that all produced messages were consumed.
        /// This is a key validation for streaming tests to ensure:
        /// - No messages were lost
        /// - No duplicate deliveries occurred
        /// - Producer and consumer are properly synchronized
        /// </summary>
        private async Task<bool> CheckCounters(ISampleStreaming_ProducerGrain producer, ISampleStreaming_ConsumerGrain consumer, bool assertIsTrue, CancellationToken cancellationToken)
        {
            var numProduced = await producer.GetNumberProduced(cancellationToken);
            var numConsumed = await consumer.GetNumberConsumed(cancellationToken);
            this.logger.LogInformation("CheckCounters: numProduced = {ProducedCount}, numConsumed = {ConsumedCount}", numProduced, numConsumed);
            if (assertIsTrue)
            {
                Assert.Equal(numProduced, numConsumed);
                return true;
            }
            else
            {
                return numProduced == numConsumed;
            }
        }
    }
}
