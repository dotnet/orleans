using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace Orleans.DurableTasks;

/// <summary>Describes a running durable task and supplies its host's services.</summary>
/// <remarks>
/// Task code reads the running task's context from <see cref="DurableTask.Current"/>. Hosts derive from this type to
/// schedule and record the task's operations.
/// </remarks>
public abstract class DurableTaskContext
{
    private const char GeneratedSegmentPrefix = '$';
    private static readonly AsyncLocal<DurableTaskContext?> AmbientContext = new();
    private static readonly AsyncLocal<DurableTaskContext?> ShieldedContext = new();
    private readonly object _lock = new();

    // Canceled by task cancellation and by attempt cancellation: the source of CancellationToken.
    private readonly CancellationTokenSource _cancellationSource = new();

    // Canceled by attempt cancellation only, before _cancellationSource, so code which observes either token finds the
    // attempt canceled.
    private readonly CancellationTokenSource _attemptCancellationSource = new();
    private bool _taskCancellationRequested;
    private string? _taskCancellationReason;
    private DurableTaskAttemptCancellationReason? _attemptCancellationReason;
    private long _nextChildId;
    private long _nextOperationId;
    private Dictionary<string, int>? _nameOccurrences;

    /// <summary>Initializes an attempt of the durable task <paramref name="taskId"/>.</summary>
    /// <param name="taskId">The stable identifier of the task which this attempt runs.</param>
    /// <remarks>
    /// The host requests the task's durable cancellation with
    /// <see cref="DurableTaskRuntimeHelper.RequestTaskCancellation(DurableTaskContext, string?)"/>, and stops this attempt
    /// with <see cref="DurableTaskRuntimeHelper.CancelAttempt(DurableTaskContext, DurableTaskAttemptCancellationReason)"/>.
    /// A host drains a canceled attempt, discards its response, and runs the task again in a new context when the task
    /// still needs an outcome.
    /// </remarks>
    protected DurableTaskContext(TaskId taskId)
    {
        if (taskId.IsDefault)
        {
            throw new ArgumentException("A durable execution requires an explicit task identifier.", nameof(taskId));
        }

        Id = taskId;
    }

    /// <summary>Gets the context of the durable task which the calling code runs in, if any.</summary>
    /// <param name="context">The running task's context, or <see langword="null"/> outside a durable task.</param>
    /// <returns><see langword="true"/> when the calling code runs in a durable task.</returns>
    /// <remarks>Code which runs only in durable tasks reads <see cref="DurableTask.Current"/>.</remarks>
    public static bool TryGetCurrent([NotNullWhen(true)] out DurableTaskContext? context)
    {
        context = AmbientContext.Value;
        return context is not null;
    }

    /// <summary>Gets the context of the durable task which the calling code runs in, or <see langword="null"/>.</summary>
    internal static DurableTaskContext? Ambient => AmbientContext.Value;

    /// <summary>Gets this execution's identifier.</summary>
    public TaskId Id { get; }

    /// <summary>Gets the host-provided logical UTC time for this execution.</summary>
    public abstract DateTimeOffset UtcNow { get; }

    /// <summary>
    /// Gets a value indicating whether this run of the task's body is re-running operations which an earlier run
    /// recorded. Use it for logging and diagnostics only.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A host replays a task which didn't finish, for example after its grain was deactivated, by running its body again
    /// from the start. The value is <see langword="true"/> from the start of a run which finds operations that earlier
    /// runs recorded for this task, such as children, markers, and recorded effects, until the run has reached each of
    /// them or has started an operation which no earlier run recorded; it then stays <see langword="false"/>. Reaching
    /// an operation again doesn't end the replay. A first run reports <see langword="false"/> throughout.
    /// </para>
    /// <para>
    /// Code which reports events once, such as logging through
    /// <see cref="DurableTaskLoggerExtensions.ReplaySafe(Microsoft.Extensions.Logging.ILogger)"/>, skips them while the
    /// task replays. State changes and durable operations don't depend on the value: a run which ended before a durable
    /// write committed its work is replayed, so code which runs only while the value is <see langword="false"/> can run
    /// twice or never, and code which branches on it schedules different operations in different runs. A state change
    /// which runs once belongs in <see cref="DurableTask.Once{TResult}(string, Func{TResult})"/>, in a step, or in the
    /// body's synchronous tail.
    /// </para>
    /// <para>Hosts which don't track replay keep this default implementation, which returns <see langword="false"/>.</para>
    /// </remarks>
    public virtual bool IsReplaying => false;

    /// <summary>Gets a value indicating whether durable cancellation of the task has been requested.</summary>
    /// <remarks>
    /// Task cancellation is durable: the host records the request, and every attempt of the task, including a replay,
    /// observes it from the start. Code which decides a durable outcome, such as whether to compensate, checks this value,
    /// for example in <c>catch (OperationCanceledException) when (DurableTask.Current.IsTaskCancellationRequested)</c>.
    /// The cancellation of an attempt leaves it <see langword="false"/>.
    /// </remarks>
    public bool IsTaskCancellationRequested
    {
        get
        {
            lock (_lock)
            {
                return _taskCancellationRequested;
            }
        }
    }

    /// <summary>
    /// Gets the reason which the durable cancellation request carries, or <see langword="null"/> when task cancellation
    /// hasn't been requested or its request carries no reason.
    /// </summary>
    /// <remarks>
    /// The first cancellation request determines the reason, and later requests leave it unchanged. The host retains the
    /// reason with the task's cancellation, so a replay observes the same reason. The reason is set before
    /// <see cref="CancellationToken"/> is canceled, so code which observes the cancellation, such as a
    /// <see langword="catch"/> block, reads it.
    /// </remarks>
    public string? TaskCancellationReason
    {
        get
        {
            lock (_lock)
            {
                return _taskCancellationReason;
            }
        }
    }

    /// <summary>
    /// Gets whether the calling code runs within a <see cref="DurableTaskExtensions.Shielded(DurableTask)"/> start of this
    /// execution, so that a child which it schedules isn't reached by this execution's durable cancellation.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A shielded definition starts within the shield, and so do the inputs which a shielded combinator starts as part of
    /// this execution. The shield follows the asynchronous flow of that start, and applies only to this execution: a
    /// child's own children are reached by the child's cancellation as usual.
    /// </para>
    /// <para>
    /// Hosts read this when they schedule a child, before any asynchronous work, and record it with the child:
    /// a host creates and runs such a child even after this execution's cancellation was requested, and doesn't propagate
    /// this execution's cancellation to it. An administrative abort and deleting the execution's tree still reach it.
    /// </para>
    /// </remarks>
    protected internal bool SchedulesShieldedChildren => ReferenceEquals(ShieldedContext.Value, this);

    /// <summary>Starts shielding the children which the calling flow schedules, and returns the previous shield.</summary>
    internal DurableTaskContext? EnterShield()
    {
        var previous = ShieldedContext.Value;
        ShieldedContext.Value = this;
        return previous;
    }

    /// <summary>Restores the shield which <see cref="EnterShield"/> returned.</summary>
    internal static void RestoreShield(DurableTaskContext? previous) => ShieldedContext.Value = previous;

    /// <summary>
    /// Gets the token which is canceled when durable cancellation of the task is requested or the host cancels this
    /// attempt. Pass it to the calls which ordinary task code makes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The token ends the task's calls, such as an HTTP request or a wait for a semaphore, promptly in both cases.
    /// <see cref="IsTaskCancellationRequested"/> tells them apart: code which runs durable cleanup after task cancellation
    /// checks it, and passes <see cref="AttemptCancellationToken"/> to the calls which the cleanup makes, because this
    /// token is already canceled.
    /// </para>
    /// <para>
    /// A task started with <see cref="DurableTaskExtensions.Shielded(DurableTask)"/> isn't reached by its owner's task
    /// cancellation, so within a shielded task, this token is canceled only by the task's own cancellation and by the
    /// cancellation of its attempt.
    /// </para>
    /// <para>
    /// Registrations on this token are ordinary synchronous .NET observers, which run when the token is canceled and return
    /// promptly.
    /// </para>
    /// </remarks>
    public CancellationToken CancellationToken => _cancellationSource.Token;

    /// <summary>
    /// Gets the token which is canceled when the host cancels this attempt: its grain activation is stopping, or the task
    /// was aborted or deleted. Pass it to the calls which durable cleanup makes after task cancellation.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every durable operation of a canceled attempt throws <see cref="DurableTaskAttemptCanceledException"/>, and the host
    /// discards the attempt's response. A stopping activation's task replays in a new attempt, which runs the cleanup
    /// again; an aborted or deleted task already has its outcome. Code which observes this token releases in-process
    /// resources and returns. Changes which it makes to the grain's journaled state commit with the grain's next journal
    /// write.
    /// </para>
    /// <para>
    /// Task cancellation leaves this token uncanceled, so a call made with it during task-cancellation cleanup, such as a
    /// compensating HTTP request, runs to completion unless the attempt itself is canceled.
    /// </para>
    /// </remarks>
    public CancellationToken AttemptCancellationToken => _attemptCancellationSource.Token;

    /// <summary>Gets a value indicating whether the host canceled this attempt.</summary>
    internal bool IsAttemptCanceled => _attemptCancellationSource.IsCancellationRequested;

    /// <summary>
    /// Gets the token which ends an operation that runs as part of this execution, such as a signal wait: within a shield,
    /// only the cancellation of the attempt ends it, and otherwise the task's durable cancellation also does.
    /// </summary>
    /// <remarks>
    /// A shield keeps this execution's durable cancellation from reaching the operations which the shielded start covers,
    /// whether they run as children or as part of the execution, so a shielded wait completes after a cancellation.
    /// </remarks>
    internal CancellationToken InlineOperationCancellationToken => SchedulesShieldedChildren ? AttemptCancellationToken : CancellationToken;

    /// <summary>Throws <see cref="DurableTaskAttemptCanceledException"/> when the host canceled this attempt.</summary>
    internal void ThrowIfAttemptCanceled()
    {
        if (IsAttemptCanceled)
        {
            throw CreateAttemptCanceledException(innerException: null);
        }
    }

    /// <summary>Creates the exception which this canceled attempt's durable operations throw.</summary>
    /// <param name="innerException">The exception which the canceled operation threw, if any.</param>
    internal DurableTaskAttemptCanceledException CreateAttemptCanceledException(Exception? innerException)
    {
        DurableTaskAttemptCancellationReason? reason;
        lock (_lock)
        {
            reason = _attemptCancellationReason;
        }

        Debug.Assert(reason is not null, "Only a canceled attempt throws the attempt's cancellation, whose reason is recorded first.");
        return new(reason.GetValueOrDefault(), AttemptCancellationToken, innerException);
    }

    /// <summary>
    /// Returns <paramref name="operation"/>, converting a cancellation which ends it because this attempt was canceled to
    /// <see cref="DurableTaskAttemptCanceledException"/>.
    /// </summary>
    internal ValueTask<T> ObserveAttempt<T>(ValueTask<T> operation)
    {
        return operation.IsCompletedSuccessfully ? operation : ObserveAsync(operation);

        async ValueTask<T> ObserveAsync(ValueTask<T> operation)
        {
            try
            {
                return await operation.ConfigureAwait(false);
            }
            catch (OperationCanceledException exception) when (exception is not DurableTaskAttemptCanceledException && IsAttemptCanceled)
            {
                throw CreateAttemptCanceledException(exception);
            }
        }
    }

    /// <summary>
    /// Returns <paramref name="operation"/>'s response, converting a cancellation which ends it, or which it reports, because
    /// this attempt was canceled to <see cref="DurableTaskAttemptCanceledException"/>.
    /// </summary>
    internal ValueTask<DurableTaskResponse> ObserveAttempt(ValueTask<DurableTaskResponse> operation)
    {
        return operation.IsCompletedSuccessfully && operation.Result is not CanceledDurableTaskResponse ? operation : ObserveAsync(operation);

        async ValueTask<DurableTaskResponse> ObserveAsync(ValueTask<DurableTaskResponse> operation)
        {
            DurableTaskResponse response;
            try
            {
                response = await operation.ConfigureAwait(false);
            }
            catch (OperationCanceledException exception) when (exception is not DurableTaskAttemptCanceledException && IsAttemptCanceled)
            {
                throw CreateAttemptCanceledException(exception);
            }

            if (response is CanceledDurableTaskResponse { Exception: not DurableTaskAttemptCanceledException } canceled && IsAttemptCanceled)
            {
                throw CreateAttemptCanceledException(canceled.Exception);
            }

            return response;
        }
    }

    /// <inheritdoc cref="ObserveAttempt{T}(ValueTask{T})"/>
    internal ValueTask ObserveAttempt(ValueTask operation)
    {
        return operation.IsCompletedSuccessfully ? operation : ObserveAsync(operation);

        async ValueTask ObserveAsync(ValueTask operation)
        {
            try
            {
                await operation.ConfigureAwait(false);
            }
            catch (OperationCanceledException exception) when (exception is not DurableTaskAttemptCanceledException && IsAttemptCanceled)
            {
                throw CreateAttemptCanceledException(exception);
            }
        }
    }

    /// <summary>
    /// Returns whether <paramref name="exception"/> ends an operation because the caller canceled
    /// <paramref name="cancellationToken"/>, rather than because the host canceled the attempt.
    /// </summary>
    internal static bool IsCallerCancellation(OperationCanceledException exception, CancellationToken cancellationToken)
        => cancellationToken.IsCancellationRequested && exception is not DurableTaskAttemptCanceledException;

    /// <summary>
    /// Records durable cancellation of the task carrying <paramref name="reason"/>, and cancels
    /// <see cref="CancellationToken"/>. Later requests leave the first request's reason unchanged.
    /// </summary>
    internal void RequestTaskCancellation(string? reason)
    {
        lock (_lock)
        {
            if (_taskCancellationRequested)
            {
                return;
            }

            _taskCancellationReason = reason;
            _taskCancellationRequested = true;
        }

        _cancellationSource.Cancel();
    }

    /// <summary>
    /// Cancels this attempt for <paramref name="reason"/>: cancels <see cref="AttemptCancellationToken"/>, then
    /// <see cref="CancellationToken"/>. Later calls leave the first call's reason unchanged.
    /// </summary>
    internal void CancelAttempt(DurableTaskAttemptCancellationReason reason)
    {
        lock (_lock)
        {
            if (_attemptCancellationReason is not null)
            {
                return;
            }

            _attemptCancellationReason = reason;
        }

        try
        {
            _attemptCancellationSource.Cancel();
        }
        finally
        {
            _cancellationSource.Cancel();
        }
    }

    /// <summary>
    /// Starts, or reattaches to, <paramref name="taskDefinition"/> under <paramref name="taskId"/>, and records the start
    /// in this execution.
    /// </summary>
    /// <param name="taskId">
    /// A child of <see cref="Id"/> for an attached task, or the root identifier of a
    /// <see cref="DurableTaskStartOptions.Detached"/> task.
    /// </param>
    /// <param name="taskDefinition">The definition to start.</param>
    /// <param name="options">How the task relates to this execution, and when the start completes.</param>
    /// <param name="cancellationToken">A token which abandons the start. It doesn't cancel the task.</param>
    /// <returns>A handle through which this execution observes the task.</returns>
    /// <remarks>
    /// <para>
    /// A host records the start in this execution's tree, together with the task's start, so a replay which reaches the
    /// record observes the same task instead of starting another. An attached task is the child
    /// <paramref name="taskId"/>, which this execution owns: its cancellation reaches the child unless the start is
    /// shielded, as <see cref="SchedulesShieldedChildren"/> reports. A detached task is an independent root, which no
    /// cancellation, abort, or deletion of this execution reaches. The host names this execution in the detached task's
    /// start as a subscriber, so the returned handle receives the task's result; canceling the handle, or this
    /// execution, ends only that subscription.
    /// </para>
    /// <para>
    /// The returned operation completes once the start is durable. With
    /// <see cref="DurableTaskStartOptions.WaitForAcceptance"/>, it completes once the task's host has also accepted and
    /// durably stored the task, or the task's result is known, so the task's identifier can be handed to other parties
    /// which address it.
    /// </para>
    /// </remarks>
    protected internal abstract ValueTask<IScheduledTaskHandle> StartTaskAsync(
        TaskId taskId,
        DurableTask taskDefinition,
        DurableTaskStartOptions options,
        CancellationToken cancellationToken);

    /// <summary>
    /// Stages the start of <paramref name="taskDefinition"/> under <paramref name="taskId"/> and records it in this
    /// execution, synchronously.
    /// </summary>
    /// <param name="taskId">
    /// A child of <see cref="Id"/> for an attached task, or the root identifier of a
    /// <see cref="DurableTaskStartOptions.Detached"/> task.
    /// </param>
    /// <param name="taskDefinition">The definition to start.</param>
    /// <param name="options"><see cref="DurableTaskStartOptions.None"/> or <see cref="DurableTaskStartOptions.Detached"/>.</param>
    /// <returns>A handle through which this execution observes the task.</returns>
    /// <remarks>
    /// The host stages the start and its record in the calling code's turn, and both commit with the host's next durable
    /// write, together with the state changes which that turn made. A replay which reaches the record returns the same
    /// handle without starting the task again. Each option relates the task to this execution as
    /// <see cref="StartTaskAsync"/> describes. Hosts which can't stage a start synchronously keep this default
    /// implementation, which throws <see cref="NotSupportedException"/>.
    /// </remarks>
    protected internal virtual IScheduledTaskHandle PostTask(
        TaskId taskId,
        DurableTask taskDefinition,
        DurableTaskStartOptions options)
        => throw new NotSupportedException("The durable execution host does not support posting tasks.");

    /// <summary>Schedules a durable delay at <paramref name="dueTime"/>.</summary>
    protected internal abstract ValueTask<DurableTaskResponse> ScheduleDelayAsync(
        TaskId taskId,
        DateTimeOffset dueTime,
        CancellationToken cancellationToken);

    /// <summary>Gets the host handle for an existing child identifier.</summary>
    protected internal abstract IScheduledTaskHandle GetChildTaskHandle(TaskId taskId);

    /// <summary>
    /// Receives this execution's terminal response when its body completes, synchronously with the body's final code.
    /// </summary>
    /// <param name="response">The body's terminal response.</param>
    /// <remarks>
    /// <para>
    /// The definitions which run bodies call this before any continuation of the body runs: an <see langword="async"/>
    /// durable task method calls it from the step which completes its state machine, and a delegate definition calls it
    /// from the work which completes its delegate, or from a continuation which runs synchronously with that work.
    /// </para>
    /// <para>
    /// A host which commits the state changes a body makes together with the body's result, such as journaled grain
    /// state, stages the result here. Work which runs after the body's final code, such as a commit which another
    /// operation requests, then commits the result together with the changes that code made. The host still receives the
    /// response from <see cref="DurableTask.RunAsync(DurableTaskContext)"/>. Implementations return without
    /// throwing. The default implementation does nothing.
    /// </para>
    /// </remarks>
    protected internal virtual void OnBodyCompleted(DurableTaskResponse response)
    {
    }

    /// <summary>
    /// Waits for the signal named <paramref name="name"/> of the invoked task whose execution contains this execution,
    /// and returns a successful response holding the signal's recorded value.
    /// </summary>
    /// <remarks>
    /// The host records the first value it receives for each name of an invoked task, together with that task's state,
    /// and returns it to every wait for that name, including waits during replay. A signal which arrived before the wait
    /// completes it immediately. Canceling <paramref name="cancellationToken"/> abandons the wait. Hosts which don't
    /// support signals keep this default implementation, which throws <see cref="NotSupportedException"/>.
    /// </remarks>
    protected internal virtual ValueTask<DurableTaskResponse> WaitForSignalAsync(
        string name,
        CancellationToken cancellationToken)
        => throw new NotSupportedException("The durable execution host does not support signals.");

    /// <summary>
    /// Returns a completed candidate, and returns the same candidate for every call with the same
    /// <paramref name="decisionId"/>, including calls during replay.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The candidate is the one which completed first: a host persists the selection under <paramref name="decisionId"/>,
    /// or derives it from a durable completion order, such as a sequence which stamps each terminal result when the host
    /// stages it. A durable order needs no record: the completed candidate with the lowest sequence is the same in every
    /// replay, because candidates which complete later receive higher sequences.
    /// </para>
    /// <para>
    /// <see cref="DurableTask.WhenAny(IReadOnlyList{DurableTask})"/> selects its winner with one call, and
    /// <see cref="ScheduledTask.WhenEach(IReadOnlyList{ScheduledTask})"/> selects each next task with one call over the
    /// remaining candidates, under a distinct decision identifier. The host reads <paramref name="candidates"/> only during
    /// the call.
    /// </para>
    /// </remarks>
    protected internal abstract ValueTask<TaskId> SelectCompletionAsync(
        TaskId decisionId,
        IReadOnlyList<TaskId> candidates,
        CancellationToken cancellationToken);

    /// <summary>
    /// Creates a child identifier, using <paramref name="name"/> when supplied.
    /// </summary>
    /// <remarks>
    /// A name repeated in one run identifies its later occurrences by their order: the first is <paramref name="name"/>,
    /// and later ones append their ordinal, such as <c>reserve$2</c>, so children named in a loop keep their identifiers
    /// on replay. Named children, named combinators, and <see cref="DurableTask.Once{TResult}(string, Func{TResult})"/>
    /// effects share one count per name. Names don't contain <c>$</c>, which generated identifiers use. Unnamed children
    /// and unnamed <see cref="DurableTask.Once{TResult}(Func{TResult})"/> effects share one count of generated
    /// identifiers, <c>$child-1</c>, <c>$child-2</c>, and so on. Generated identifiers are replay-stable when unnamed
    /// operations are created in the same deterministic order. A concurrent
    /// batch can use
    /// <see cref="DurableTask.WhenAll(IReadOnlyList{DurableTask})"/> and
    /// <see cref="DurableTask.WhenAny(IReadOnlyList{DurableTask})"/>, whose input indexes provide
    /// stable logical slots. Concurrent independent scheduling operations, including concurrent
    /// combinator invocations, must use explicit names or otherwise begin in deterministic order.
    /// </remarks>
    /// <exception cref="ArgumentException"><paramref name="name"/> is empty or contains <c>$</c>.</exception>
    protected internal virtual TaskId CreateChildTaskId(string? name)
    {
        if (name is not null)
        {
            ValidateName(name, nameof(name));
            return CreateNamedChildId(name);
        }

        return Id.Child(
            $"$child-{Interlocked.Increment(ref _nextChildId).ToString(CultureInfo.InvariantCulture)}");
    }

    /// <summary>
    /// Gets the identity of the host which runs this execution, such as its grain, which qualifies the root identifiers of
    /// the detached tasks which the execution starts without a name, or <see langword="null"/> for none.
    /// </summary>
    /// <remarks>
    /// A task identifier is unique within its host, so executions on two hosts can share one. A host whose executions
    /// start detached tasks on targets which other hosts also start tasks on supplies its identity, so the executions'
    /// derived root identifiers differ.
    /// </remarks>
    protected internal virtual string? HostId => null;

    /// <summary>
    /// Creates the root identifier of a detached task which this execution starts without a name:
    /// <c>{HostId}/{Id}:{segment}</c>, or <c>{Id}:{segment}</c> for a host without an identity, where <c>{segment}</c> is
    /// the identifier which <see cref="CreateChildTaskId(string)"/> generates for the next unnamed operation.
    /// </summary>
    /// <remarks>
    /// The segment comes from the count which unnamed children share, so a replay which starts unnamed operations in the
    /// same order derives the same root identifier, and other parties derive it from the host's identity and this
    /// execution's identifier. A named detached task's root identifier is its name.
    /// </remarks>
    internal TaskId CreateDetachedRootId()
    {
        var root = $"{Id}:{CreateChildTaskId(name: null).LastSegment}";
        return TaskId.CreateRoot(HostId is { } hostId ? $"{hostId}/{root}" : root);
    }

    internal TaskId CreateOperationId(string kind)
        => Id.Child($"${kind}-{Interlocked.Increment(ref _nextOperationId).ToString(CultureInfo.InvariantCulture)}");

    /// <summary>Validates a caller-supplied name, which can't contain the <c>$</c> which generated identifiers use.</summary>
    internal static void ValidateName(string name, string paramName)
    {
        ArgumentException.ThrowIfNullOrEmpty(name, paramName);
        if (name.Contains(GeneratedSegmentPrefix, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"Names can't contain '{GeneratedSegmentPrefix}', which generated identifiers use.",
                paramName);
        }
    }

    /// <summary>
    /// Creates the identifier of a named operation of this execution: the first occurrence of <paramref name="name"/> in
    /// this attempt is <c>{prefix}{name}</c>, and each later occurrence appends its ordinal, such as <c>reserve$2</c>.
    /// </summary>
    /// <param name="name">The caller-supplied name.</param>
    /// <param name="kindPrefix">
    /// The prefix which distinguishes the kind of operation, such as <c>$once:</c> for a recorded effect, or empty for a
    /// child or a combinator.
    /// </param>
    /// <remarks>
    /// Occurrences are numbered in the order in which the attempt creates them, so a replay which creates the same named
    /// operations in the same order assigns the same identifiers, and adding a later occurrence doesn't rename earlier
    /// ones. Every kind of named operation shares one count per name, so the same occurrence of a name has the same
    /// ordinal whichever kind of operation uses it: <c>x$2</c> and <c>$once:x$2</c> identify the same occurrence. A host
    /// which finds the other kind recorded for an occurrence reports the replay's divergence rather than running the
    /// operation again.
    /// </remarks>
    internal TaskId CreateNamedChildId(string name, string kindPrefix = "")
    {
        int occurrence;
        lock (_lock)
        {
            _nameOccurrences ??= new(StringComparer.Ordinal);
            _nameOccurrences.TryGetValue(name, out occurrence);
            _nameOccurrences[name] = ++occurrence;
        }

        return Id.Child(occurrence == 1
            ? kindPrefix + name
            : $"{kindPrefix}{name}{GeneratedSegmentPrefix}{occurrence.ToString(CultureInfo.InvariantCulture)}");
    }

    /// <summary>
    /// Returns the outcome recorded for the effect <paramref name="effectId"/>, or runs the effect and records its outcome.
    /// </summary>
    /// <typeparam name="TState">The type of the state which <paramref name="effect"/> receives.</typeparam>
    /// <param name="effectId">The effect's identifier, a child of this execution.</param>
    /// <param name="state">The state which <paramref name="effect"/> receives.</param>
    /// <param name="effect">
    /// Runs the effect and returns its outcome: its result, or the exception which it threw. It doesn't throw.
    /// </param>
    /// <returns>The effect's outcome, which the host recorded.</returns>
    /// <remarks>
    /// <para>
    /// <see cref="DurableTask.Once{TResult}(string, Func{TResult})"/> calls this synchronously from the task's body. A host
    /// runs the effect and stages its record in the same synchronous work, so the record commits with the next durable
    /// write together with the state changes which the effect made. Every later run of the body, including a replay,
    /// receives the recorded outcome without running the effect.
    /// </para>
    /// <para>
    /// A host which can't store the outcome, for example because its result doesn't serialize, records and returns that
    /// failure instead, so every run observes the same failure. A host throws <see cref="InvalidOperationException"/>
    /// without running the effect when it can't stage the record with the effect's state changes, for example when it is
    /// called off the host's scheduler, or when an earlier run recorded another kind of operation under the effect's name
    /// and occurrence, which is how a replay which diverges from its recorded execution surfaces. Hosts which don't
    /// support recorded effects keep this default implementation, which throws <see cref="NotSupportedException"/>.
    /// </para>
    /// </remarks>
    protected internal virtual DurableTaskResponse RunEffect<TState>(
        TaskId effectId,
        TState state,
        Func<TState, DurableTaskResponse> effect)
        => throw new NotSupportedException("The durable task host does not support DurableTask.Once.");

    /// <summary>
    /// Returns the version of the change <paramref name="name"/> which this execution follows, deciding and recording it
    /// at the execution's first call for the name.
    /// </summary>
    /// <param name="name">The change's validated name.</param>
    /// <param name="minSupported">The oldest version which the calling code supports, at least <c>0</c>.</param>
    /// <param name="maxSupported">The newest version, at least <paramref name="minSupported"/>.</param>
    /// <returns>The execution's version, from <paramref name="minSupported"/> to <paramref name="maxSupported"/>.</returns>
    /// <remarks>
    /// <para>
    /// <see cref="DurableTask.GetVersion(string, int, int)"/> calls this synchronously from the task's body. A host
    /// returns the version which the execution recorded, or which an enclosing execution recorded; otherwise it decides
    /// <c>0</c> when earlier attempts recorded operations which this attempt hasn't reached yet, and
    /// <paramref name="maxSupported"/> when they didn't, and records the decision with the execution.
    /// </para>
    /// <para>
    /// A host cancels the attempt for <see cref="DurableTaskAttemptCancellationReason.UnsupportedVersion"/> when the
    /// execution's version lies outside the supported range, and throws
    /// <see cref="DurableTaskAttemptCanceledException"/> whose inner exception is a
    /// <see cref="DurableTaskVersionException"/>; the task stays pending. Hosts which don't support versions keep this
    /// default implementation, which throws <see cref="NotSupportedException"/>.
    /// </para>
    /// </remarks>
    protected internal virtual int GetVersion(string name, int minSupported, int maxSupported)
        => throw new NotSupportedException("The durable task host does not support DurableTask.GetVersion.");

    internal static ContextScope Enter(DurableTaskContext context)
    {
        var previous = AmbientContext.Value;
        AmbientContext.Value = context;
        return new(previous);
    }

    internal readonly struct ContextScope(DurableTaskContext? previous) : IDisposable
    {
        public void Dispose() => AmbientContext.Value = previous;
    }
}
