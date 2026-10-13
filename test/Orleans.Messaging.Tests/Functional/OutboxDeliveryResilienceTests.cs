using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Messaging.Configuration;
using Orleans.Messaging.Tests.Support;
using Orleans.Runtime;
using Polly;
using Polly.CircuitBreaker;
using Polly.Retry;
using Xunit;

namespace Orleans.Messaging.Tests.Functional;

[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Messaging")]
public sealed class OutboxDeliveryResilienceTests
{
    private const int InPipelineRetries = 2;

    [Fact]
    public async Task FailedTry_IsRetriedWithinTheDeliveryAttempt()
    {
        await using var fixture = await StartAsync(static strategy => new ResiliencePipelineBuilder()
            .AddRetry(new RetryStrategyOptions
            {
                ShouldHandle = new PredicateBuilder().Handle<TimeoutException>(),
                MaxRetryAttempts = InPipelineRetries,
                Delay = TimeSpan.Zero,
            })
            .AddStrategy(_ => strategy, new FaultStrategyOptions())
            .Build());
        fixture.Strategy.FailNext(InPipelineRetries);

        var (sender, receiver) = await SendAsync(fixture, "retried-in-attempt");

        var delivered = await fixture.WaitForEffectCountAsync(receiver, 1);
        Assert.Equal("retried-in-attempt", Assert.Single(delivered.Effects).Value);
        Assert.Equal(InPipelineRetries + 1, fixture.Strategy.Executions);
        var drained = await fixture.WaitForOutboxCountAsync(sender, 0);
        Assert.Empty(drained.OutboxDeadLetters);
    }

    [Fact]
    public async Task FailuresLeavingThePipeline_RetryDurablyWithoutConsumingDeliveryAttempts()
    {
        await using var fixture = await StartAsync(static strategy => new ResiliencePipelineBuilder()
            .AddStrategy(_ => strategy, new FaultStrategyOptions())
            .Build());
        const int FailedAttempts = 3;
        fixture.Strategy.FailNext(FailedAttempts);

        var (sender, receiver) = await SendAsync(fixture, "retried-durably");

        var delivered = await fixture.WaitForEffectCountAsync(receiver, 1);
        Assert.Equal("retried-durably", Assert.Single(delivered.Effects).Value);
        Assert.Equal(FailedAttempts + 1, fixture.Strategy.Executions);
        var drained = await fixture.WaitForOutboxCountAsync(sender, 0);
        Assert.Empty(drained.OutboxDeadLetters);
    }

    [Fact]
    public async Task EmptyPipeline_DeliversDirectly()
    {
        await using var fixture = await StartAsync(static _ => ResiliencePipeline.Empty);

        var (sender, receiver) = await SendAsync(fixture, "direct");

        var delivered = await fixture.WaitForEffectCountAsync(receiver, 1);
        Assert.Equal("direct", Assert.Single(delivered.Effects).Value);
        Assert.Equal(0, fixture.Strategy.Executions);
        Assert.Empty((await fixture.WaitForOutboxCountAsync(sender, 0)).OutboxDeadLetters);
    }

    [Fact]
    public async Task DefaultPipeline_RetriesFailedCallsAndPropagatesOpenCircuit()
    {
        Assert.Null(new DurableMessagingOptions().DeliveryResiliencePipeline);
        var pipeline = new DefaultDeliveryResiliencePipeline(
            Options.Create(new SiloMessagingOptions { ResponseTimeout = TimeSpan.FromMilliseconds(100) }),
            TimeProvider.System).Pipeline;
        var cancellationToken = TestContext.Current.CancellationToken;

        var calls = 0;
        var result = await pipeline.ExecuteAsync(
            _ => ++calls == 1 ? throw new IOException("Injected outage.") : ValueTask.FromResult(DeliveryResult.Accepted()),
            cancellationToken);
        Assert.Equal(DeliveryStatus.Accepted, result.Status);
        Assert.Equal(2, calls);

        calls = 0;
        await Assert.ThrowsAsync<BrokenCircuitException>(() => pipeline.ExecuteAsync<DeliveryResult>(
            _ =>
            {
                calls++;
                throw new BrokenCircuitException();
            },
            cancellationToken).AsTask());
        Assert.Equal(1, calls);
    }

    private static async Task<DeliveryResilienceClusterFixture> StartAsync(Func<FaultStrategy, ResiliencePipeline> createPipeline)
    {
        var fixture = new DeliveryResilienceClusterFixture(createPipeline);
        await fixture.InitializeAsync();
        return fixture;
    }

    private static async Task<(IMessagingTestGrain Sender, IMessagingTestGrain Receiver)> SendAsync(MessagingClusterFixture fixture, string value)
    {
        var sender = fixture.Client.GetGrain<IMessagingTestGrain>(Guid.NewGuid());
        var receiver = fixture.Client.GetGrain<IMessagingTestGrain>(Guid.NewGuid());
        await sender.SendAsync(
            receiver.GetGrainId(),
            "messages/resilience",
            new TestMessage(TestApplicationProtocol.NewMessageId(), 1, value));
        return (sender, receiver);
    }

    private sealed class DeliveryResilienceClusterFixture(Func<FaultStrategy, ResiliencePipeline> createPipeline)
        : MessagingClusterFixture(initialSilos: 1)
    {
        public FaultStrategy Strategy { get; } = new();

        protected override void ConfigureOptions(DurableMessagingOptions options)
        {
            base.ConfigureOptions(options);
            options.MaxDeliveryAttempts = 1;
            options.BackpressureRetryDelay = TimeSpan.FromMilliseconds(5);
            options.DeliveryResiliencePipeline = createPipeline(Strategy);
        }
    }

    /// <summary>Fails a configured number of tries before calling the receiver.</summary>
    private sealed class FaultStrategy : ResilienceStrategy
    {
        private int _failures;
        private int _executions;

        public int Executions => Volatile.Read(ref _executions);

        public void FailNext(int count) => Volatile.Write(ref _failures, count);

        protected override ValueTask<Outcome<TResult>> ExecuteCore<TResult, TState>(
            Func<ResilienceContext, TState, ValueTask<Outcome<TResult>>> callback,
            ResilienceContext context,
            TState state)
        {
            Interlocked.Increment(ref _executions);
            return Interlocked.Decrement(ref _failures) >= 0
                ? ValueTask.FromResult(Outcome.FromException<TResult>(new TimeoutException("Injected delivery failure.")))
                : callback(context, state);
        }
    }

    private sealed class FaultStrategyOptions : ResilienceStrategyOptions;
}
