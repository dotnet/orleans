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
    private readonly CancellationTokenSource _cancellationSource = new();
    private readonly CancellationTokenSource? _executionCancellationSource;
    private readonly CancellationToken _executionAbortToken;
    private List<CancellationRegistration>? _registrations;
    private CancellationOperation? _cancellationOperation;
    private bool _cancellationRequested;
    private string? _cancellationReason;
    private long _nextChildId;
    private long _nextOperationId;
    private Dictionary<string, int>? _nameOccurrences;

    /// <summary>Initializes an execution with durable cancellation.</summary>
    protected DurableTaskContext(TaskId taskId) : this(taskId, CancellationToken.None)
    {
    }

    /// <summary>Initializes an execution with durable cancellation and a host execution-abort token.</summary>
    /// <param name="taskId">The stable identifier of this execution.</param>
    /// <param name="executionAbortToken">The host-owned token for aborting this execution attempt.</param>
    /// <remarks>
    /// <see cref="CancellationToken"/>, and the delegate and delay operations which receive it, observe both durable
    /// cancellation and host execution abort. <see cref="IsCancellationRequested"/> and durable cancellation callbacks
    /// observe the durable request only. The host owns the abort source for this execution's lifetime, drains
    /// the aborted attempt, discards its response, and replays it in a new context.
    /// </remarks>
    protected DurableTaskContext(TaskId taskId, CancellationToken executionAbortToken)
    {
        if (taskId.IsDefault)
        {
            throw new ArgumentException("A durable execution requires an explicit task identifier.", nameof(taskId));
        }

        Id = taskId;
        _executionAbortToken = executionAbortToken;
        if (executionAbortToken.CanBeCanceled)
        {
            _executionCancellationSource = CancellationTokenSource.CreateLinkedTokenSource(
                _cancellationSource.Token,
                executionAbortToken);
        }
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

    /// <summary>Gets a value indicating whether durable cancellation has been requested.</summary>
    public bool IsCancellationRequested
    {
        get
        {
            lock (_lock)
            {
                return _cancellationRequested;
            }
        }
    }

    /// <summary>
    /// Gets the reason which the durable cancellation request carries, or <see langword="null"/> when cancellation hasn't
    /// been requested or its request carries no reason.
    /// </summary>
    /// <remarks>
    /// The first cancellation request determines the reason, and later requests leave it unchanged. The host retains the
    /// reason with the task's cancellation, so a replay observes the same reason. The reason is set before the durable
    /// token is canceled, so code which observes the cancellation, such as a <see langword="catch"/> block, reads it.
    /// </remarks>
    public string? CancellationReason
    {
        get
        {
            lock (_lock)
            {
                return _cancellationReason;
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
    /// Gets the token which is canceled when durable cancellation is requested or the host aborts this execution attempt.
    /// </summary>
    /// <remarks>
    /// <see cref="IsCancellationRequested"/> reports durable cancellation only, so code which decides a durable outcome,
    /// such as whether to compensate, checks it rather than this token. An aborted attempt's response is discarded and the
    /// task replays in a new context.
    ///
    /// <para>
    /// Registrations on this token are ordinary synchronous .NET cleanup observers only. They follow
    /// standard <see cref="CancellationToken"/> behavior, including registration-time
    /// <see cref="ExecutionContext"/> capture, optional <see cref="SynchronizationContext"/> dispatch,
    /// and current execution-context behavior for unsafe registrations.
    /// </para>
    ///
    /// <para>
    /// These observers must return promptly. They must not call, block on, await, or otherwise
    /// orchestrate <see cref="DurableTaskRuntimeHelper.RequestCancellationAsync(DurableTaskContext, CancellationToken)"/> or durable
    /// cancellation of any context. Doing so is outside this contract and has the usual synchronous
    /// reentrancy and sync-over-async risks. Use <see cref="RegisterCancellationCallbackAsync"/> for
    /// all cancellation work which requests another durable context, awaits asynchronous work, needs
    /// cycle handling, or participates in durable failure aggregation.
    /// </para>
    /// </remarks>
    public CancellationToken CancellationToken => _executionCancellationSource?.Token ?? _cancellationSource.Token;

    /// <summary>Gets the host-owned token for aborting this execution attempt.</summary>
    internal CancellationToken ExecutionAbortToken => _executionAbortToken;

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
    /// Registers an asynchronous callback which observes the durable cancellation request.
    /// </summary>
    /// <remarks>
    /// This API establishes cancellation-operation causality. The callback executes with this durable
    /// context as <see cref="DurableTask.Current"/> and participates in durable cancellation dependency tracking
    /// and failure aggregation. Its cancellation operation flows with <see cref="ExecutionContext"/>
    /// across ordinary awaits and safe thread-pool dispatch, including <see cref="Task.Run(Action)"/>.
    /// Suppressing execution-context flow or using unsafe dispatch detaches subsequent asynchronous
    /// work according to standard .NET behavior, so cancellation requests made by that work are
    /// external rather than durable dependencies. Dependency edges are based only on this explicitly
    /// flowed operation; no thread or global activity is inferred.
    ///
    /// <para>
    /// Disposing the returned registration prevents an invocation which has not started, or
    /// asynchronously waits for an active invocation to finish. A callback can dispose its own
    /// registration without blocking. A registration accepted before cancellation completion is
    /// enlisted in that cancellation operation: its callback, completion, and failure contribute to
    /// the shared cancellation result. Once cancellation completion has closed registration, the
    /// callback starts immediately in a new callback causality scope. It cannot change the completed
    /// shared cancellation result; disposing its returned registration waits for that invocation and
    /// propagates its failure.
    /// </para>
    /// </remarks>
    [SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "Ownership of the registration is returned to the caller while the context controls invocation.")]
    public ValueTask<IAsyncDisposable> RegisterCancellationCallbackAsync(
        Func<CancellationToken, ValueTask> callback,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(callback);
        cancellationToken.ThrowIfCancellationRequested();

        CancellationRegistration registration;
        CancellationOperation? callbackOperation = null;
        lock (_lock)
        {
            if (!_cancellationRequested || !_cancellationOperation!.Task.IsCompleted)
            {
                registration = new(this, callback);
                (_registrations ??= []).Add(registration);
                return new(registration);
            }

            registration = new(this, callback, propagateInvocationExceptionOnDispose: true);
            callbackOperation = new(this);
        }

        // Record nested callback causality before the callback can request its caller's cancellation.
        _ = CancellationOperation.TryAddDependency(CancellationOperation.Current, callbackOperation!);
        _ = InvokePostCompletionCallbackAsync(callbackOperation!, registration);
        return new(registration);
    }

    internal Task RequestCancellationAsync(string? reason, CancellationToken cancellationToken)
    {
        CancellationOperation operation;
        lock (_lock)
        {
            if (!_cancellationRequested)
            {
                _cancellationReason = reason;
                _cancellationRequested = true;
            }

            operation = _cancellationOperation ??= new(this);
        }

        var waitForCompletion = CancellationOperation.TryAddDependency(
            CancellationOperation.Current,
            operation);
        operation.Start();
        if (!waitForCompletion)
        {
            return Task.CompletedTask;
        }

        return cancellationToken.CanBeCanceled
            ? operation.Task.WaitAsync(cancellationToken)
            : operation.Task;
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Every cancellation observer failure is aggregated into the durable cancellation result.")]
    [SuppressMessage("Performance", "CA1849:Call async methods when in an async method", Justification = "CancellationTokenSource.Cancel is intentionally synchronous so ordinary token observers run before durable callbacks.")]
    [SuppressMessage("Maintainability", "CA1508:Avoid dead conditional code", Justification = "CancellationTokenSource.Cancel can populate the exception list by throwing AggregateException.")]
    private async Task CompleteCancellationAsync(CancellationOperation operation)
    {
        List<Exception>? exceptions = null;
        using (operation.Enter())
        {
            try
            {
                using (operation.Detach())
                {
                    _cancellationSource.Cancel();
                }
            }
            catch (AggregateException exception)
            {
                (exceptions ??= []).AddRange(exception.InnerExceptions);
            }
            catch (Exception exception)
            {
                (exceptions ??= []).Add(exception);
            }

            while (true)
            {
                List<CancellationRegistration> callbacks;
                lock (_lock)
                {
                    if (_registrations is not { Count: > 0 })
                    {
                        CompleteCancellation(operation, exceptions);
                        return;
                    }

                    callbacks = _registrations;
                    _registrations = null;
                }

                foreach (var callback in callbacks)
                {
                    try
                    {
                        using (Enter(this))
                        {
                            await callback.InvokeAsync(CancellationToken).ConfigureAwait(false);
                        }
                    }
                    catch (Exception exception)
                    {
                        (exceptions ??= []).Add(exception);
                    }
                }
            }
        }
    }

    private static void CompleteCancellation(
        CancellationOperation operation,
        List<Exception>? exceptions)
    {
        if (exceptions is not null)
        {
            operation.SetException(new AggregateException(
                "One or more cancellation observers failed.",
                exceptions));
        }
        else
        {
            operation.SetResult();
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "The registration records post-completion callback failures for observation during disposal.")]
    private async Task InvokePostCompletionCallbackAsync(
        CancellationOperation operation,
        CancellationRegistration registration)
    {
        using (operation.Enter())
        using (Enter(this))
        {
            try
            {
                await registration.InvokeAsync(CancellationToken).ConfigureAwait(false);
            }
            catch
            {
                // The registration owns post-completion callback observation.
            }
            finally
            {
                operation.CompleteGraph();
            }
        }
    }

    private void Unregister(CancellationRegistration registration)
    {
        lock (_lock)
        {
            _registrations?.Remove(registration);
        }
    }

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

    private sealed class CancellationOperation(DurableTaskContext context)
    {
        private static readonly object GraphLock = new();
        private static readonly AsyncLocal<CancellationOperation?> AmbientOperation = new();
        private readonly TaskCompletionSource _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private HashSet<CancellationOperation>? _dependencies;
        private bool _graphCompleted;
        private int _started;

        public static CancellationOperation? Current => AmbientOperation.Value;

        public Task Task => _completion.Task;

        public void Start()
        {
            if (Interlocked.Exchange(ref _started, 1) == 0)
            {
                _ = context.CompleteCancellationAsync(this);
            }
        }

        public static bool TryAddDependency(
            CancellationOperation? source,
            CancellationOperation target)
        {
            if (source is null)
            {
                return true;
            }

            lock (GraphLock)
            {
                if (source._graphCompleted || target._graphCompleted)
                {
                    return true;
                }

                if (ReferenceEquals(source, target) || HasPath(target, source))
                {
                    return false;
                }

                (source._dependencies ??= []).Add(target);
                return true;
            }
        }

        public OperationScope Enter()
        {
            var previous = AmbientOperation.Value;
            AmbientOperation.Value = this;
            return new(previous);
        }

        public OperationScope Detach()
        {
            var previous = AmbientOperation.Value;
            AmbientOperation.Value = null;
            return new(previous);
        }

        public void SetException(Exception exception)
        {
            CompleteGraph();
            _completion.SetException(exception);
        }

        public void SetResult()
        {
            CompleteGraph();
            _completion.SetResult();
        }

        private static bool HasPath(
            CancellationOperation start,
            CancellationOperation destination)
        {
            var pending = new Stack<CancellationOperation>();
            var visited = new HashSet<CancellationOperation>();
            pending.Push(start);
            while (pending.TryPop(out var current))
            {
                if (!visited.Add(current))
                {
                    continue;
                }

                if (ReferenceEquals(current, destination))
                {
                    return true;
                }

                if (current._dependencies is { } dependencies)
                {
                    foreach (var dependency in dependencies)
                    {
                        pending.Push(dependency);
                    }
                }
            }

            return false;
        }

        public void CompleteGraph()
        {
            lock (GraphLock)
            {
                _graphCompleted = true;
                _dependencies = null;
            }
        }

        public readonly struct OperationScope(CancellationOperation? previous) : IDisposable
        {
            public void Dispose() => AmbientOperation.Value = previous;
        }
    }

    private sealed class CancellationRegistration : IAsyncDisposable
    {
        private static readonly AsyncLocal<CancellationRegistration?> Current = new();
        private const int Pending = 0;
        private const int Invoking = 1;
        private const int Completed = 2;
        private const int DisposedState = 3;
        private readonly DurableTaskContext? _context;
        private readonly Func<CancellationToken, ValueTask>? _callback;
        private readonly bool _propagateInvocationExceptionOnDispose;
        private TaskCompletionSource? _completion;
        private Exception? _invocationException;
        private CancellationRegistration? _previous;
        private int _state;

        public CancellationRegistration(
            DurableTaskContext context,
            Func<CancellationToken, ValueTask> callback,
            bool propagateInvocationExceptionOnDispose = false)
        {
            _context = context;
            _callback = callback;
            _propagateInvocationExceptionOnDispose = propagateInvocationExceptionOnDispose;
        }

        public async ValueTask InvokeAsync(CancellationToken cancellationToken)
        {
            if (Interlocked.CompareExchange(ref _state, Invoking, Pending) != Pending)
            {
                return;
            }

            var previous = Current.Value;
            _previous = previous;
            Current.Value = this;
            try
            {
                await _callback!(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                _invocationException = exception;
                throw;
            }
            finally
            {
                Current.Value = previous;
                _previous = null;
                Interlocked.Exchange(ref _state, Completed);
                CompleteInvocation(Volatile.Read(ref _completion));
            }
        }

        public ValueTask DisposeAsync()
        {
            _context?.Unregister(this);
            while (true)
            {
                switch (Volatile.Read(ref _state))
                {
                    case Pending:
                        if (Interlocked.CompareExchange(ref _state, DisposedState, Pending) == Pending)
                        {
                            return ValueTask.CompletedTask;
                        }

                        break;
                    case Invoking:
                        if (IsCurrentCallback(this))
                        {
                            return ValueTask.CompletedTask;
                        }

                        return new(GetInvocationCompletionTask());
                    case Completed when _propagateInvocationExceptionOnDispose
                        && _invocationException is not null:
                        return new(GetInvocationCompletionTask());
                    case Completed:
                    case DisposedState:
                        return ValueTask.CompletedTask;
                }
            }
        }

        private Task GetInvocationCompletionTask()
        {
            var completion = Volatile.Read(ref _completion);
            if (completion is null)
            {
                var candidate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                completion = Interlocked.CompareExchange(ref _completion, candidate, null) ?? candidate;
            }

            if (Volatile.Read(ref _state) == Completed)
            {
                CompleteInvocation(completion);
            }

            return completion.Task;
        }

        private void CompleteInvocation(TaskCompletionSource? completion)
        {
            if (completion is null)
            {
                return;
            }

            if (_propagateInvocationExceptionOnDispose && _invocationException is { } exception)
            {
                completion.TrySetException(exception);
            }
            else
            {
                completion.TrySetResult();
            }
        }

        private static bool IsCurrentCallback(CancellationRegistration registration)
        {
            for (var current = Current.Value; current is not null; current = current._previous)
            {
                if (ReferenceEquals(current, registration))
                {
                    return true;
                }
            }

            return false;
        }
    }
}
