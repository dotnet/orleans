using System.Collections.Concurrent;
using Polly;
using Polly.Timeout;

namespace Orleans.Messaging.Tests.Support;

/// <summary>
/// Injects scripted faults into the outbox's call to the receiver, as a strategy of
/// <see cref="Configuration.DurableMessagingOptions.DeliveryResiliencePipeline"/>.
/// </summary>
/// <remarks>
/// <para>
/// Each delivery try takes the next scripted <see cref="DeliveryFault"/>; once the script is exhausted, tries take a
/// fault from <see cref="Repeat"/> when one is set, or else call the receiver unchanged. Faults never wait for time to
/// pass: a timeout is reported at once, and holds wait for an explicit release or the delivery's cancellation.
/// </para>
/// <para>
/// The injector records the receiver's own answers, including answers which a fault hides from the sender, so tests
/// can assert what the receiver decided independently of what the sender observed.
/// </para>
/// </remarks>
public sealed class DeliveryFaultInjector : ResilienceStrategy
{
    private readonly ConcurrentQueue<DeliveryFault> _script = new();
    private readonly CountSignal _executions = new();
    private readonly CountSignal _receiverCalls = new();
    private readonly CountSignal _answerCount = new();
    private readonly List<DeliveryStatus> _answers = [];
    private Func<DeliveryFault>? _repeat;

    /// <summary>Gets the number of delivery tries which reached this strategy.</summary>
    public int Executions => _executions.Value;

    /// <summary>Gets the number of tries which called the receiver.</summary>
    public int ReceiverCalls => _receiverCalls.Value;

    /// <summary>Gets the statuses which the receiver answered, in completion order, whether or not the sender saw them.</summary>
    public IReadOnlyList<DeliveryStatus> Answers
    {
        get
        {
            lock (_answers) return [.. _answers];
        }
    }

    /// <summary>Creates a pipeline which consists of this strategy only.</summary>
    public ResiliencePipeline CreatePipeline() => AddTo(new ResiliencePipelineBuilder()).Build();

    /// <summary>Adds this strategy to <paramref name="builder"/>, innermost after the strategies added before it.</summary>
    public ResiliencePipelineBuilder AddTo(ResiliencePipelineBuilder builder) =>
        builder.AddStrategy(_ => this, new Options());

    /// <summary>Scripts <paramref name="faults"/> for the next delivery tries, in order.</summary>
    public void Enqueue(params DeliveryFault[] faults)
    {
        foreach (var fault in faults) _script.Enqueue(fault);
    }

    /// <summary>Applies a new fault from <paramref name="create"/> to every try once the script is exhausted.</summary>
    public void Repeat(Func<DeliveryFault> create) => _repeat = create;

    /// <summary>Lets tries call the receiver unchanged once the script is exhausted.</summary>
    public void StopRepeating() => _repeat = null;

    /// <summary>Waits until <paramref name="count"/> tries have reached this strategy.</summary>
    public Task WaitForExecutionsAsync(int count) => _executions.WaitAsync(count);

    /// <summary>Waits until <paramref name="count"/> tries have called the receiver.</summary>
    public Task WaitForReceiverCallsAsync(int count) => _receiverCalls.WaitAsync(count);

    /// <summary>Waits until the receiver has answered <paramref name="count"/> calls, and returns all answers so far.</summary>
    public async Task<IReadOnlyList<DeliveryStatus>> WaitForAnswersAsync(int count)
    {
        await _answerCount.WaitAsync(count);
        return Answers;
    }

    protected override ValueTask<Outcome<TResult>> ExecuteCore<TResult, TState>(
        Func<ResilienceContext, TState, ValueTask<Outcome<TResult>>> callback,
        ResilienceContext context,
        TState state)
    {
        _executions.Increment();
        var fault = _script.TryDequeue(out var scripted) ? scripted : _repeat?.Invoke();
        return fault is null ? CallReceiverAsync(callback, context, state, null) : PlayAsync(fault, callback, context, state);
    }

    private async ValueTask<Outcome<TResult>> PlayAsync<TResult, TState>(
        DeliveryFault fault,
        Func<ResilienceContext, TState, ValueTask<Outcome<TResult>>> callback,
        ResilienceContext context,
        TState state)
    {
        var continueOnCapturedContext = context.ContinueOnCapturedContext;
        var cancellationToken = context.CancellationToken;
        if (fault.Kind != DeliveryFaultKind.HoldAfterCall) fault.OnEntered();
        switch (fault.Kind)
        {
            case DeliveryFaultKind.Throw:
                return Outcome.FromException<TResult>(fault.Exception!);
            case DeliveryFaultKind.TimeOut:
                return Outcome.FromException<TResult>(new TimeoutRejectedException("Injected delivery timeout before the receiver was called."));
            case DeliveryFaultKind.Answer:
                return Outcome.FromResult((TResult)(object)fault.Result!.Value);
            case DeliveryFaultKind.TimeOutInFlight:
                // The abandoned call continues to the receiver; its answer is observable through ReceiverAnswer only.
                _ = CallReceiverAsync(callback, context, state, fault).AsTask();
                return Outcome.FromException<TResult>(new TimeoutRejectedException("Injected delivery timeout while the receiver call was in flight."));
            case DeliveryFaultKind.LoseResponse:
                await CallReceiverAsync(callback, context, state, fault).ConfigureAwait(continueOnCapturedContext);
                return Outcome.FromException<TResult>(new TimeoutRejectedException("Injected loss of the receiver's response."));
            case DeliveryFaultKind.HoldBeforeCall:
                try
                {
                    await fault.WaitForReleaseAsync(cancellationToken).ConfigureAwait(continueOnCapturedContext);
                }
                catch (Exception exception)
                {
                    return Outcome.FromException<TResult>(exception);
                }

                return await CallReceiverAsync(callback, context, state, fault).ConfigureAwait(continueOnCapturedContext);
            case DeliveryFaultKind.HoldAfterCall:
                var outcome = await CallReceiverAsync(callback, context, state, fault).ConfigureAwait(continueOnCapturedContext);
                fault.OnEntered();
                try
                {
                    await fault.WaitForReleaseAsync(cancellationToken).ConfigureAwait(continueOnCapturedContext);
                }
                catch (Exception exception)
                {
                    return Outcome.FromException<TResult>(exception);
                }

                return outcome;
            default:
                throw new InvalidOperationException($"Unknown delivery fault '{fault.Kind}'.");
        }
    }

    private async ValueTask<Outcome<TResult>> CallReceiverAsync<TResult, TState>(
        Func<ResilienceContext, TState, ValueTask<Outcome<TResult>>> callback,
        ResilienceContext context,
        TState state,
        DeliveryFault? fault)
    {
        var continueOnCapturedContext = context.ContinueOnCapturedContext;
        _receiverCalls.Increment();
        Outcome<TResult> outcome;
        try
        {
            outcome = await callback(context, state).ConfigureAwait(continueOnCapturedContext);
        }
        catch (Exception exception)
        {
            outcome = Outcome.FromException<TResult>(exception);
        }

        if (outcome.Exception is { } failure)
        {
            fault?.OnAnswered(failure);
        }
        else
        {
            var result = (DeliveryResult)(object)outcome.Result!;
            lock (_answers) _answers.Add(result.Status);
            _answerCount.Increment();
            fault?.OnAnswered(result);
        }

        return outcome;
    }

    private sealed class Options : ResilienceStrategyOptions;

    /// <summary>A counter whose waiters complete once it reaches their target.</summary>
    private sealed class CountSignal
    {
        private readonly List<(int Target, TaskCompletionSource Signal)> _waiters = [];
        private int _value;

        public int Value
        {
            get
            {
                lock (_waiters) return _value;
            }
        }

        public void Increment()
        {
            List<TaskCompletionSource>? ready = null;
            lock (_waiters)
            {
                _value++;
                for (var index = _waiters.Count - 1; index >= 0; index--)
                {
                    if (_waiters[index].Target > _value) continue;
                    (ready ??= []).Add(_waiters[index].Signal);
                    _waiters.RemoveAt(index);
                }
            }

            ready?.ForEach(static signal => signal.TrySetResult());
        }

        public Task WaitAsync(int target)
        {
            lock (_waiters)
            {
                if (_value >= target) return Task.CompletedTask;
                var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _waiters.Add((target, signal));
                return signal.Task.WaitAsync(TimeSpan.FromSeconds(30));
            }
        }
    }
}

/// <summary>The kinds of fault which <see cref="DeliveryFaultInjector"/> injects.</summary>
public enum DeliveryFaultKind
{
    /// <summary>The try fails with an exception, without calling the receiver.</summary>
    Throw,

    /// <summary>The try times out without calling the receiver.</summary>
    TimeOut,

    /// <summary>The try returns an answer without calling the receiver.</summary>
    Answer,

    /// <summary>The try calls the receiver and times out at once, while the call continues.</summary>
    TimeOutInFlight,

    /// <summary>The try calls the receiver, then hides its answer behind a timeout.</summary>
    LoseResponse,

    /// <summary>The try waits for a release before it calls the receiver.</summary>
    HoldBeforeCall,

    /// <summary>The try calls the receiver, then waits for a release before it returns the answer.</summary>
    HoldAfterCall
}

/// <summary>
/// One single-use fault for a delivery try, and the handle through which a test observes and controls it.
/// </summary>
public sealed class DeliveryFault
{
    private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<DeliveryResult> _answer = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _used;

    private DeliveryFault(DeliveryFaultKind kind, Exception? exception = null, DeliveryResult? result = null)
    {
        Kind = kind;
        Exception = exception;
        Result = result;
    }

    /// <summary>Gets the kind of this fault.</summary>
    public DeliveryFaultKind Kind { get; }

    internal Exception? Exception { get; }
    internal DeliveryResult? Result { get; }

    /// <summary>
    /// Gets the receiver's answer to the call which this fault made, or its failure. It completes only for faults which
    /// call the receiver.
    /// </summary>
    public Task<DeliveryResult> ReceiverAnswer => _answer.Task.WaitAsync(TimeSpan.FromSeconds(30));

    /// <summary>Fails the try with <paramref name="exception"/>, or an <see cref="IOException"/> by default.</summary>
    public static DeliveryFault Throw(Exception? exception = null) =>
        new(DeliveryFaultKind.Throw, exception ?? new IOException("Injected transient delivery failure."));

    /// <summary>Times the try out with a <see cref="TimeoutRejectedException"/>, without calling the receiver.</summary>
    public static DeliveryFault TimeOut() => new(DeliveryFaultKind.TimeOut);

    /// <summary>Answers the try with <paramref name="result"/>, without calling the receiver.</summary>
    public static DeliveryFault Answer(DeliveryResult result) => new(DeliveryFaultKind.Answer, result: result);

    /// <summary>
    /// Calls the receiver and times the try out at once, while the call continues; <see cref="ReceiverAnswer"/> reports
    /// the abandoned call's answer.
    /// </summary>
    public static DeliveryFault TimeOutInFlight() => new(DeliveryFaultKind.TimeOutInFlight);

    /// <summary>
    /// Calls the receiver and lets it decide, then fails the try with a <see cref="TimeoutRejectedException"/>, as when
    /// the response is lost after the receiver committed.
    /// </summary>
    public static DeliveryFault LoseResponse() => new(DeliveryFaultKind.LoseResponse);

    /// <summary>
    /// Holds the try before it calls the receiver until <see cref="Release"/> or <see cref="Fail"/>, or until the delivery
    /// is canceled.
    /// </summary>
    public static DeliveryFault HoldBeforeCall() => new(DeliveryFaultKind.HoldBeforeCall);

    /// <summary>
    /// Calls the receiver, then holds its answer until <see cref="Release"/> or <see cref="Fail"/>, or until the delivery
    /// is canceled, which models a sender that stops after the receiver committed.
    /// </summary>
    public static DeliveryFault HoldAfterCall() => new(DeliveryFaultKind.HoldAfterCall);

    /// <summary>
    /// Waits until a try takes this fault; for <see cref="DeliveryFaultKind.HoldAfterCall"/>, until the receiver answered.
    /// </summary>
    public Task WaitUntilEnteredAsync() => _entered.Task.WaitAsync(TimeSpan.FromSeconds(30));

    /// <summary>Releases a held try to continue.</summary>
    public void Release() => _released.TrySetResult();

    /// <summary>Releases a held try by failing it with <paramref name="exception"/>, without the receiver's answer.</summary>
    public void Fail(Exception exception) => _released.TrySetException(exception);

    internal void OnEntered()
    {
        if (Interlocked.Exchange(ref _used, 1) != 0)
        {
            throw new InvalidOperationException("A delivery fault applies to one try only.");
        }

        _entered.TrySetResult();
    }

    internal Task WaitForReleaseAsync(CancellationToken cancellationToken) => _released.Task.WaitAsync(cancellationToken);

    internal void OnAnswered(DeliveryResult result) => _answer.TrySetResult(result);

    internal void OnAnswered(Exception exception) => _answer.TrySetException(exception);
}
