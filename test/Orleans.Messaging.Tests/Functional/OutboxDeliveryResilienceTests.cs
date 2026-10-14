using System.Collections.Concurrent;
using System.Diagnostics;
using Orleans.Messaging.Configuration;
using Orleans.Messaging.Tests.Support;
using Orleans.Runtime;
using Polly;
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
            .AddStrategy(_ => strategy, new ScriptedStrategyOptions())
            .Build());
        fixture.Strategy.Script(Step.Unanswered(), Step.Unanswered());

        var (sender, receiver) = await SendAsync(fixture, "retried-in-attempt");

        var delivered = await fixture.WaitForEffectCountAsync(receiver, 1);
        Assert.Equal("retried-in-attempt", Assert.Single(delivered.Effects).Value);
        Assert.Equal(InPipelineRetries + 1, fixture.Strategy.Executions);
        var drained = await fixture.WaitForOutboxCountAsync(sender, 0);
        Assert.Empty(drained.OutboxDeadLetters);
    }

    [Fact]
    public async Task FailuresLeavingThePipeline_RetryInLaterBatches()
    {
        await using var fixture = await StartAsync(Scripted);
        const int FailedAttempts = 3;
        fixture.Strategy.Script(Step.Unanswered(), Step.Unanswered(), Step.Unanswered());

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
    public async Task Backpressured_RetriesBeyondTheRetryAgeUntilTheReceiverTakesTheMessage()
    {
        var retryAge = TimeSpan.FromMilliseconds(100);
        await using var fixture = await StartAsync(Scripted, options => options.MaxOutboxRetryAge = retryAge);
        const int BackpressuredAttempts = 8;
        fixture.Strategy.Script(Enumerable.Repeat(Step.Answer(DeliveryResult.Backpressured()), BackpressuredAttempts).ToArray());
        var elapsed = Stopwatch.StartNew();

        var (sender, receiver) = await SendAsync(fixture, "backpressured");

        var delivered = await fixture.WaitForEffectCountAsync(receiver, 1);
        Assert.True(elapsed.Elapsed > retryAge);
        Assert.Equal("backpressured", Assert.Single(delivered.Effects).Value);
        Assert.Equal(BackpressuredAttempts + 1, fixture.Strategy.Executions);
        Assert.Empty((await fixture.WaitForOutboxCountAsync(sender, 0)).OutboxDeadLetters);
    }

    [Fact]
    public async Task UnansweredAttempts_DeadLetterAfterMaxOutboxRetryAge()
    {
        await using var fixture = await StartAsync(Scripted, static options => options.MaxOutboxRetryAge = TimeSpan.FromMilliseconds(100));
        fixture.Strategy.Repeat(Step.Unanswered());

        var (sender, receiver) = await SendAsync(fixture, "unanswered");

        var deadLetter = Assert.Single((await fixture.WaitForDeadLetterCountAsync(sender, 1)).OutboxDeadLetters);
        Assert.True(deadLetter.AttemptCount > 1);
        Assert.Equal(fixture.Strategy.Executions, deadLetter.AttemptCount);
        Assert.Empty((await receiver.GetSnapshotAsync()).Effects);
    }

    [Fact]
    public async Task Backpressured_RestartsTheRetryAge()
    {
        // Each failing span stays within the retry age, while both spans together exceed it.
        var timeout = TimeSpan.FromMilliseconds(1600);
        await using var fixture = await StartAsync(Scripted, static options =>
        {
            options.MaxOutboxRetryAge = TimeSpan.FromSeconds(3);
            options.BackpressureRetryDelay = TimeSpan.FromMilliseconds(1);
        });
        fixture.Strategy.Script(Step.Unanswered(timeout), Step.Answer(DeliveryResult.Backpressured()), Step.Answer(DeliveryResult.HandlerNotFound(), timeout));

        var (sender, receiver) = await SendAsync(fixture, "answered-between-outages");

        var delivered = await fixture.WaitForEffectCountAsync(receiver, 1);
        Assert.Equal("answered-between-outages", Assert.Single(delivered.Effects).Value);
        Assert.Equal(4, fixture.Strategy.Executions);
        Assert.Empty((await fixture.WaitForOutboxCountAsync(sender, 0)).OutboxDeadLetters);
    }

    [Fact]
    public async Task HandlerNotFound_DeadLettersAfterMaxOutboxRetryAge()
    {
        await using var fixture = await StartAsync(Scripted, static options => options.MaxOutboxRetryAge = TimeSpan.FromSeconds(3));
        fixture.Strategy.Repeat(Step.Answer(DeliveryResult.HandlerNotFound()));

        var (sender, receiver) = await SendAsync(fixture, "unhandled");

        var deadLetter = Assert.Single((await fixture.WaitForDeadLetterCountAsync(sender, 1)).OutboxDeadLetters);
        Assert.True(deadLetter.AttemptCount > 1);
        Assert.Equal(DeliveryResult.HandlerNotFound().Message, deadLetter.Reason);
        Assert.Empty((await receiver.GetSnapshotAsync()).Effects);
    }

    private static ResiliencePipeline Scripted(ScriptedStrategy strategy) => new ResiliencePipelineBuilder()
        .AddStrategy(_ => strategy, new ScriptedStrategyOptions())
        .Build();

    private static async Task<DeliveryResilienceClusterFixture> StartAsync(
        Func<ScriptedStrategy, ResiliencePipeline> createPipeline,
        Action<DurableMessagingOptions>? configure = null)
    {
        var fixture = new DeliveryResilienceClusterFixture(createPipeline, configure);
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

    private sealed class DeliveryResilienceClusterFixture(
        Func<ScriptedStrategy, ResiliencePipeline> createPipeline,
        Action<DurableMessagingOptions>? configure)
        : MessagingClusterFixture(initialSilos: 1)
    {
        public ScriptedStrategy Strategy { get; } = new();

        protected override void ConfigureOptions(DurableMessagingOptions options)
        {
            base.ConfigureOptions(options);
            options.BackpressureRetryDelay = TimeSpan.FromMilliseconds(5);
            options.DeliveryResiliencePipeline = createPipeline(Strategy);
            configure?.Invoke(options);
        }
    }

    /// <summary>A scripted outcome of one try: a receiver answer, or a failure without an answer.</summary>
    private readonly record struct Step(DeliveryResult? Result, TimeSpan Delay)
    {
        public static Step Answer(DeliveryResult result, TimeSpan delay = default) => new(result, delay);

        public static Step Unanswered(TimeSpan delay = default) => new(null, delay);
    }

    /// <summary>Plays scripted steps in place of calling the receiver, then repeats a step or calls the receiver.</summary>
    private sealed class ScriptedStrategy : ResilienceStrategy
    {
        private readonly ConcurrentQueue<Step> _steps = new();
        private Step? _then;
        private int _executions;

        public int Executions => Volatile.Read(ref _executions);

        public void Script(params Step[] steps)
        {
            foreach (var step in steps) _steps.Enqueue(step);
        }

        public void Repeat(Step step) => _then = step;

        protected override ValueTask<Outcome<TResult>> ExecuteCore<TResult, TState>(
            Func<ResilienceContext, TState, ValueTask<Outcome<TResult>>> callback,
            ResilienceContext context,
            TState state)
        {
            Interlocked.Increment(ref _executions);
            if (!_steps.TryDequeue(out var step))
            {
                if (_then is not { } then)
                {
                    return callback(context, state);
                }

                step = then;
            }

            return PlayAsync<TResult>(step, context.CancellationToken);
        }

        private static async ValueTask<Outcome<TResult>> PlayAsync<TResult>(Step step, CancellationToken cancellationToken)
        {
            if (step.Delay > TimeSpan.Zero)
            {
                await Task.Delay(step.Delay, cancellationToken);
            }

            return step.Result is { } result
                ? Outcome.FromResult((TResult)(object)result)
                : Outcome.FromException<TResult>(new TimeoutException("Injected delivery failure."));
        }
    }

    private sealed class ScriptedStrategyOptions : ResilienceStrategyOptions;
}
