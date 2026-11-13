using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Runtime.Internal;

namespace Orleans;

internal partial class InvokableObjectManager
{
    /// <summary>
    /// Observer-side support for returning <see cref="IAsyncEnumerable{T}"/> from observer methods.
    /// </summary>
    public sealed partial class AsyncEnumerableGrainObserverExtension : IAsyncEnumerableGrainExtension, IAsyncDisposable
    {
        private static readonly DiagnosticListener DiagnosticListener = new("Orleans.Runtime.AsyncEnumerableGrainObserverExtension");

#if NET10_0_OR_GREATER
        private readonly Lock _lock = new();
#else
        private readonly object _lock = new();
#endif

        private readonly TimeProvider _timeProvider;
        private readonly MessagingOptions _messagingOptions;
        private readonly Dictionary<Guid, EnumeratorState> _enumerators = [];

        private readonly ILogger<AsyncEnumerableGrainObserverExtension> _logger;

        private Task? _cleanupTask;
#pragma warning disable CA2213 // Disposable fields should be disposed
        // Will be auto-disposed by the cleanup timer loop breaking out
        private CancellationTokenSource? _cleanupCts;
#pragma warning restore CA2213 // Disposable fields should be disposed
        private readonly CancellationTokenSource _shutdownCts = new();

        /// <summary>
        /// This represents the disposal of the extension itself, not the dispoal of the individual enumerators.
        /// Although disposal of the extension implies disposal of the enumerators.
        /// This is important as disposal of the extension can race with the disposal of the enumerators.
        /// </summary>
        private Task? _disposeTask;

        // Internal for testing
        internal IGrainContext GrainContext { get; }

        public AsyncEnumerableGrainObserverExtension(
            IGrainContext grainContext,
            TimeProvider timeProvider,
            IOptions<MessagingOptions> messagingOptions,
            ILogger<AsyncEnumerableGrainObserverExtension> logger)
        {
            GrainContext = grainContext;

            _logger = logger;
            _timeProvider = timeProvider;
            _messagingOptions = messagingOptions.Value;

            OnAsyncEnumeratorGrainObserverExtensionCreated(this);
        }

        private static void OnAsyncEnumeratorGrainObserverExtensionCreated(AsyncEnumerableGrainObserverExtension extension)
        {
            if (DiagnosticListener.IsEnabled())
            {
                DiagnosticListener.Write(nameof(OnAsyncEnumeratorGrainObserverExtensionCreated), extension);
            }
        }

        private static void OnAsyncEnumerableGrainObserverExtensionDisposed(AsyncEnumerableGrainObserverExtension extension)
        {
            if (DiagnosticListener.IsEnabled())
            {
                DiagnosticListener.Write(nameof(OnAsyncEnumerableGrainObserverExtensionDisposed), extension);
            }
        }

        private static void OnObserverEnumeratorCleanupCompleted(AsyncEnumerableGrainObserverExtension extension)
        {
            if (DiagnosticListener.IsEnabled())
            {
                DiagnosticListener.Write(nameof(OnObserverEnumeratorCleanupCompleted), extension);
            }
        }

        private async Task RunCleanupLoopAsync(CancellationTokenSource cancellationTokenSource)
        {
            using (cancellationTokenSource)
            {
                using var timer = new PeriodicTimer(_messagingOptions.ResponseTimeout, _timeProvider);

                try
                {
                    while (await timer.WaitForNextTickAsync(cancellationTokenSource.Token).ConfigureAwait(false))
                    {
                        try
                        {
                            List<EnumeratorState>? expiredStates = null;

                            lock (_lock)
                            {
                                if (_disposeTask is not null)
                                {
                                    return;
                                }

                                if (_enumerators.Count == 0)
                                {
                                    _cleanupTask = null;
                                    return;
                                }

                                foreach (var state in _enumerators.Values)
                                {
                                    if (state.IsDisposing || !state.TryEnterGate())
                                    {
                                        // We must not allow a stuck/slow producer to result in us holding the lock for an unbounded time.
                                        // So just skip those that currently have an in-flight MoveNextAsync call.
                                        continue;
                                    }

                                    try
                                    {
                                        if (state.Seen)
                                        {
                                            state.Seen = false;
                                        }
                                        else
                                        {
                                            expiredStates ??= [];
                                            expiredStates.Add(state);
                                        }
                                    }
                                    finally
                                    {
                                        state.ExitGate();
                                    }
                                }

                                // Note: we do not remove the state from _enumerators here.
                                // It remains until CompleteDisposalWorkAsync's finally block removes it, allowing
                                // any racing MoveNext to observe state.DisposalTask != null and bail out cleanly.
                            }

                            if (expiredStates is not null)
                            {
                                foreach (var state in expiredStates)
                                {
                                    // Sequential await is fine here as DisposalStartedTask completes as soon as the disposal work
                                    // is scheduled and CancelAsync is initiated (before user callbacks run or the gate is entered),
                                    // so this does NOT serialize actual teardown. This way we avoid also allocating a Task array!
                                    await StartEnumeratorDisposal(state).ConfigureAwait(false);
                                }
                            }

                            OnObserverEnumeratorCleanupCompleted(this); // Yes we report even if there are no disposal to do.
                        }
                        catch (Exception exception)
                        {
                            LogWarningErrorCleaningUpEnumerators(_logger, exception);
                        }
                    }
                }
                catch (OperationCanceledException) when (cancellationTokenSource.Token.IsCancellationRequested)
                {
                    // Ignore; we are shutting down.
                }
                catch (Exception exception)
                {
                    LogWarningErrorCleaningUpEnumerators(_logger, exception);
                }
            }
        }

        public async ValueTask<(EnumerationResult Status, object? Value)> StartEnumeration<T>(
            Guid requestId, IAsyncEnumerableRequest<T> request, CancellationToken cancellationToken)
        {
            if (request.MaxBatchSize <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(request), request.MaxBatchSize, "Max batch size must be positive.");
            }

            var cts = CancellationTokenSource.CreateLinkedTokenSource(request.GetCancellationToken());
            var state = new EnumeratorState
            {
                Seen = true,
                RequestId = requestId,
                ClientTokenSource = cts,
                MaxBatchSize = request.MaxBatchSize
            };

            // We enter the gate to prevent a concurrent DisposeAsync/MoveNextAsync from using a partially inited enumerator.
            await state.WaitToEnterGateAsync(CancellationToken.None).ConfigureAwait(false);

            Exception? validationException = null;

            lock (_lock)
            {
                if (_disposeTask is not null)
                {
                    // The extension was disposed while the caller was starting enumeration.
                    // This is not a caller error but rather a runtime race.
                    // The disposal of the extension means the disposal of the observer as well.
                    validationException = new ObserverNotReachableException($"The observer extension '{nameof(AsyncEnumerableGrainObserverExtension)}' has been disposed.");
                }
                else if (!_enumerators.TryAdd(requestId, state))
                {
                    // Otherwise its a caller-side programming error: duplicate request GUID so we throw.
                    validationException = new InvalidOperationException("An enumerator with the same id already exists.");
                }
                else if (_enumerators.Count == 1)
                {
                    _cleanupCts = CancellationTokenSource.CreateLinkedTokenSource(_shutdownCts.Token);

                    using (new ExecutionContextSuppressor())
                    {
                        _cleanupTask = RunCleanupLoopAsync(_cleanupCts);
                    }
                }
            }

            if (validationException is { } exception)
            {
                state.ExitGate();
                cts.Dispose();

                throw exception;
            }

            try
            {
                request.SetTarget(GrainContext);
                state.Enumerator = request.InvokeImplementation().GetAsyncEnumerator(cts.Token);
                state.ExitGate();
            }
            catch (Exception ex)
            {
                // Remove and claim cleanup while still holding the gate. The cleanup worker will
                // cancel the token and wait for this initialization path to release the gate.

                var disposalTask = StartEnumeratorDisposal(state);

                state.ExitGate();

                await WaitWithTimeoutAsync(disposalTask, "the enumerator's disposal to initiate").ConfigureAwait(false);

                return (EnumerationResult.Error, ex);
            }

            return await MoveNextCoreAsync<T>(requestId, state, cancellationToken).ConfigureAwait(false);
        }

        public ValueTask<(EnumerationResult Status, object? Value)> MoveNext<T>(Guid requestId, CancellationToken cancellationToken)
        {
            EnumeratorState? state;

            lock (_lock)
            {
                if (!_enumerators.TryGetValue(requestId, out state) || state.IsDisposing)
                {
                    return ValueTask.FromResult<(EnumerationResult, object?)>((EnumerationResult.MissingEnumeratorError, default));
                }
            }

            return MoveNextCoreAsync<T>(requestId, state, cancellationToken);
        }

        private async ValueTask<(EnumerationResult Status, object? Value)> MoveNextCoreAsync<T>(
            Guid requestId, EnumeratorState state, CancellationToken cancellationToken)
        {
            // Note: this method is deliberately kept as a single flat (but fat) body rather than split into helper
            // methods or local functions: it is the hot path for every MoveNext, and each nested
            // async function would introduce its own state machine allocation and continuation dispatch.

            // First we try to acquire exclusive access to this enumerator. On the fast-path TryEnterGate avoids
            // allocating a linked CTS when uncontended. On the slow-path we link the caller's cancellation token with
            // the state's removal token so a stuck producer holding the gate during teardown can not keep us here,
            // because StartEnumeratorDisposal fires removal signal *before* teardown starts. A stale call that observes removal
            // returns MissingEnumeratorError without entering the gate, releasing its invocation admission so StopAsync can drain.
            // Note that we cant use two chained WaitAsync() calls here as that open a race window (see below)

            try
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (!state.TryEnterGate())
                {
                    using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, state.RemovalTokenSource.Token);
                    await state.WaitToEnterGateAsync(linkedCts.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (state.RemovalTokenSource.Token.IsCancellationRequested)
            {
                return (EnumerationResult.MissingEnumeratorError, default);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // This is a caller/client cancellation case.
                var disposalTask = StartEnumeratorDisposal(state);

                await WaitWithTimeoutAsync(disposalTask, "the enumerator's disposal to initiate").ConfigureAwait(false);

                return (EnumerationResult.Canceled, default);
            }

            Task<bool>? moveNextTask;
            (EnumerationResult Status, object? Value) result;

            try
            {
                // Then we validate that this enumerator is still live. It may have been removed by the
                // cleanup loop or an explicit dispose while the current thread was parked on the gate acquisition.

                lock (_lock)
                {
                    if (state.IsDisposing || !_enumerators.TryGetValue(requestId, out var existingState) || !ReferenceEquals(existingState, state))
                    {
                        // Cleanup or explicit disposal claimed this state while we waited on the gate.
                        // We report the enumerator as terminated; the owner of the disposal task cleans it up.
                        result = (EnumerationResult.MissingEnumeratorError, default);
                        goto Done;
                    }
                }

                if (state.Enumerator is not IAsyncEnumerator<T> typedEnumerator)
                {
                    result = (EnumerationResult.Error, new InvalidCastException("Attempted to access an enumerator of the wrong type."));
                    goto Done;
                }

                state.Seen = true;

                // Now we *synchronously* try to pull any items the producer has ready to build a batch up to
                // MaxBatchSize. If we produce anything here, we return immediately without awaiting.
                moveNextTask = state.MoveNextTask;

                if (moveNextTask is null)
                {
                    var currentBatchSize = 0;
                    object? batchResult = null;

                    do
                    {
                        // Check if the enumerator has a result ready synchronously.
                        var moveNext = typedEnumerator.MoveNextAsync();
                        if (moveNext.IsCompletedSuccessfully)
                        {
                            var hasValue = moveNext.Result;
                            if (hasValue)
                            {
                                // Account for the just-emitted element.
                                ++currentBatchSize;
                                var value = typedEnumerator.Current;
                                if (currentBatchSize == 1)
                                {
                                    batchResult = value;
                                }
                                else if (currentBatchSize == 2)
                                {
                                    // Grow from a single element to a list.
                                    batchResult = new List<T> { (T)batchResult!, value };
                                }
                                else
                                {
                                    ((List<T>)batchResult!).Add(value);
                                }
                            }
                            else
                            {
                                // Completed successfully, possibly with some final elements.
                                if (currentBatchSize == 0)
                                {
                                    result = (EnumerationResult.Completed, default);
                                    goto Done;
                                }
                                else if (currentBatchSize == 1)
                                {
                                    result = (EnumerationResult.CompletedWithElement, batchResult);
                                    goto Done;
                                }

                                result = (EnumerationResult.CompletedWithBatch, batchResult);
                                goto Done;
                            }
                        }
                        else
                        {
                            // The enumerator did not complete synchronously, so we need to await the result for subsequent elements.
                            state.MoveNextTask = moveNext.AsTask();
                            break;
                        }
                    } while (currentBatchSize < state.MaxBatchSize);

                    // If there are elements, return them now instead of waiting for the pending operation to complete.
                    if (currentBatchSize == 1)
                    {
                        result = (EnumerationResult.Element, batchResult!);
                        goto Done;
                    }
                    else if (currentBatchSize > 1)
                    {
                        result = (EnumerationResult.Batch, batchResult!);
                        goto Done;
                    }

                    // There are no elements, so wait for the pending operation to complete.
                    moveNextTask = state.MoveNextTask!;
                }

                // Otherwise we await the pending producer task with a long-poll timeout, after that we classify the outcome.
                var longPollTimeout = _messagingOptions.ConfiguredResponseTimeout / 2;

                // We use SuppressThrowing so that we do not rely on catching exceptions to determine the outcome.
                // If WaitAsync throws synchronously or the inner task faults/cancels, the await returns normally.
                // We classify the result directly below.

                // Contrary to the entry point, we chain two WaitAsync calls here instead of allocating a linked CTS.
                // That is safe because we already hold the gate: the outer wait acquires nothing, so cancelling it just
                // returns from this method, which releases the gate in the finally. The abandoned inner wrapper observes
                // `moveNextTask` in the background and completes unobserved once the producer finishes as state.MoveNextTask
                // still references the real producer task, so teardown waits for it as before.

                // At the entry point this is not possible because the inner wait is `_gate.WaitAsync(...)`, which acquires
                // the semaphore. If the outer wait cancelled and we returned, the inner wait could still take the gate with
                // nobody left to release it, hence the single linked CTS there.
                await ((Task)moveNextTask
                    .WaitAsync(longPollTimeout, _timeProvider, cancellationToken)
                    .WaitAsync(state.RemovalTokenSource.Token))
                    .ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);

                if (cancellationToken.IsCancellationRequested)
                {
                    // Regardless of the task's state, we surface cancellation as the caller is the one who cancelled it.
                    // If the caller cancelled *before* the producer finished, the producer is still running.
                    // By leaving MoveNextTask non-null, the teardown worker will correctly wait for the producer to finish before calling DisposeAsync.
                    result = (EnumerationResult.Canceled, default);
                    goto Done;
                }

                if (state.RemovalTokenSource.IsCancellationRequested)
                {
                    // Otherwise RemovalTokenSource fired while we were parked on the long-poll: StopAsync's observer
                    // disposal, an explicit DisposeAsync RPC, the extension's own DisposeAsync, or the cleanup loop
                    // evicting this state. In every case the enumerator is being (or has already been) torn down and
                    // no further elements may be delivered to the client. We surface MissingEnumeratorError rather
                    // than Canceled because the termination is runtime-initiated, not caller-initiated.
                    // We leave MoveNextTask set as teardown still needs to wait for the producer (which may still
                    // be running) before calling DisposeAsync. The terminated branch below calls StartEnumeratorDisposal(cancel: true),
                    // which joins the disposal task that removal already created, or creates one if it somehow does not exist.
                    result = (EnumerationResult.MissingEnumeratorError, default);
                    goto Done;
                }

                if (!moveNextTask.IsCompleted)
                {
                    // Long-poll elapsed; the producer task is still in flight. Again not nulling MoveNextTask
                    result = (EnumerationResult.Heartbeat, default);
                    goto Done;
                }

                //  We are about to consume the result (Element, Completed, Canceled, or Error).
                //  Since the task is finished, teardown no longer needs to wait for it, so we clear it.
                state.MoveNextTask = null;

                if (moveNextTask.IsCompletedSuccessfully)
                {
#pragma warning disable CA1849 // It won't be faulted if we reach this point!
                    result = moveNextTask.Result ? (EnumerationResult.Element, typedEnumerator.Current) : (EnumerationResult.Completed, default);
#pragma warning restore CA1849
                    goto Done;
                }

                if (moveNextTask.IsCanceled)
                {
                    // This looks redundant maybe but it catches cases where the producer's token was
                    // canceled (via ClientTokenSource), independent of the caller's token (cancellationToken).
                    result = (EnumerationResult.Canceled, default);
                    goto Done;
                }

                Debug.Assert(moveNextTask.IsFaulted);
                Debug.Assert(moveNextTask.Exception is not null);

                var moveNextException = moveNextTask.Exception!;
                result = (EnumerationResult.Error, moveNextException.InnerExceptions.Count == 1 ? moveNextException.InnerException! : moveNextException);
Done:;
            }
            catch (Exception exception)
            {
                result = (EnumerationResult.Error, exception);
            }
            finally
            {
                state.ExitGate();
            }

            // Finally if the status is a termination one, we dispose the enumerator BUT *outside* the gate,
            // since the disposal worker needs to acquire the same gate to guarantee that it does not
            // dispose an enumerator while its MoveNextAsync is running.
            if (result.Status.IsTerminated())
            {
                const int CancelMask = (int)(EnumerationResult.MissingEnumeratorError | EnumerationResult.Error | EnumerationResult.Canceled);

                // In case of abnormal terminations must cancel the producer because it may still have work outstanding.
                var disposalTask = StartEnumeratorDisposal(state, cancelClientToken: ((int)result.Status & CancelMask) != 0);

                await WaitWithTimeoutAsync(disposalTask, "the enumerator's disposal to initiate").ConfigureAwait(false);
            }

            return result;
        }

        public ValueTask DisposeAsync(Guid requestId) => DisposeAsync(requestId, CancellationToken.None);

        public async ValueTask DisposeAsync(Guid requestId, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            EnumeratorState? enumerator;

            lock (_lock)
            {
                _enumerators.TryGetValue(requestId, out enumerator);
            }

            if (enumerator is not null)
            {
                // This is an RPC disposal call which may be the first to initiate teardown (or it joins an existing teardown).
                // StartEnumeratorDisposal returns the fast-path completion signal, which we discard here
                // as we want to await the actual teardown, not just cancellation acknowledgement.
                _ = StartEnumeratorDisposal(enumerator);

                Debug.Assert(enumerator.TeardownTask is not null, "Expected TeardownTask to be set when enumerator disposal has been initiated.");

                if (!enumerator.TeardownTask.IsCompletedSuccessfully)
                {
                    await WaitWithTimeoutAsync(enumerator.TeardownTask, "the enumerator's teardown to complete").ConfigureAwait(false);
                }
            }
            else
            {
                // Enumerator was never started or is already fully torn down, nothing to do.
            }
        }

        private Task StartEnumeratorDisposal(EnumeratorState enumerator, bool cancelClientToken = true)
        {
            Task disposalStartedTask;

            lock (_lock)
            {
                if (enumerator.DisposalStartedTask is null)
                {
                    var startedCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    var teardownCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

                    enumerator.DisposalStartedTask = startedCompletion.Task;
                    enumerator.TeardownTask = teardownCompletion.Task;
                    enumerator.DisposalWork = new DisposalWork(enumerator, cancelClientToken, startedCompletion, teardownCompletion);
                }

                disposalStartedTask = enumerator.DisposalStartedTask;
            }

            // Outside the lock, see comment in DisposeAsync on why.
            // Cancellation is idempontant in case multiple sources request disposal of this enumerator.
            enumerator.SignalRemoval();

            // We atomically claim the work! If another thread got here first,
            // the Exchange result is null, which means another thread (maybe from DisposeAsync) grabbed the work!
            if (Interlocked.Exchange(ref enumerator.DisposalWork, null) is { } disposalWork)
            {
                CompleteDisposalWorkAsync(disposalWork).Ignore();
            }

            return disposalStartedTask;
        }

        private async Task CompleteDisposalWorkAsync(DisposalWork work)
        {
            // We want to yield control to the caller and NOT capture the context.
            await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);

            var state = work.State;
            var cancellationTask = Task.CompletedTask;

            // We signal cancellation *before* waiting for the gate. An in-flight MoveNextAsync may be
            // waiting for the producer, and cancellation is what gives it a chance to finish, because
            // it will notice the cancellation and fault thereby releasing the gate itself.
            if (work.CancelClientToken)
            {
                try
                {
                    // Contrary to the grain extension's cancellation semantics, which require cancellation
                    // to happen synchronously with the justification that callbacks need to run synchronously
                    // on the grain scheduler before awaiting move completion, here we dont have a grain scheduler
                    // so we can invoke cancellation asynchronously, and the worker can track completion of the callbacks separately.
                    cancellationTask = state.ClientTokenSource.CancelAsync();
                }
                catch (Exception exception)
                {
                    LogWarningErrorCancelingEnumerator(_logger, exception);
                }
            }

            // Then we complete the response-facing task. In-flight MoveNextAsync / StartEnumeration invocations
            // await this *before* returning their response, and they must NOT be gated on the teardown below.
            work.DisposalStartedCompletion.TrySetResult();

            // Now we start the teardown, this is serialized against in-flight invocations by the gate.
            var gateEntered = false;

            try
            {
                // The cancellation signalling above ensures that we wont get stuck
                // waiting here for the full ResponseTimeout duration.
                await state.WaitToEnterGateAsync(CancellationToken.None).ConfigureAwait(false);

                gateEntered = true;

                // DisposeAsync must not overlap a running MoveNextAsync, so we wait until a running MoveNextAsync completes fully,
                // but only up to ResponseTimeout. If the producer ignores cancellation and the wait times out, the
                // "MoveNextTask is null or { IsCompleted: true }" guard below ensures we abandon instead of calling
                // DisposeAsync concurrently with the still-pending MoveNextAsync.

                var pendingMoveNextTask = state.MoveNextTask;
                if (pendingMoveNextTask is not null)
                {
                    await WaitWithTimeoutAsync(pendingMoveNextTask, "the producer's pending MoveNextAsync").ConfigureAwait(false);
                }

                // Then we finalize cancellation (bounded by ResponseTimeout).
                await WaitWithTimeoutAsync(cancellationTask, "cancellation callbacks to complete").ConfigureAwait(false);

                // Finally we dispose the enumerator, but only if the producer is no longer running a MoveNextAsync.
                if (pendingMoveNextTask is null or { IsCompleted: true } && state.Enumerator is { } enumerator)
                {
                    try
                    {
                        await WaitWithTimeoutAsync(enumerator.DisposeAsync().AsTask(), "the enumerator's disposal").ConfigureAwait(false);
                    }
                    catch (Exception exception)
                    {
                        LogWarningErrorDisposingEnumerator(_logger, exception);
                    }
                }

                state.ClientTokenSource.Dispose();
            }
            catch (Exception exception)
            {
                // Teardown is best-effort since the per-enumerator gate is never disposed,
                // since a stale request may already be waiting to acquire it.
                LogWarningErrorDisposingEnumerator(_logger, exception);
            }
            finally
            {
                if (gateEntered)
                {
                    state.ExitGate();
                }

                // Its generally safer to mark completation *before* we remove the teardown task from the outstanding disposals.
                // Since continuations are configured to run asynchronously, this should NOT result in synchronous execution of continuation code.

                work.TeardownCompletion.TrySetResult();

                lock (_lock)
                {
                    // Now that the teardown is fully complete, we can finally remove the state
                    // from the dictionary. Any subsequent MoveNext will return MissingEnumeratorError.
                    if (_enumerators.TryGetValue(state.RequestId, out var current) && ReferenceEquals(current, state))
                    {
                        _enumerators.Remove(state.RequestId);
                    }
                }
            }
        }
        private async Task WaitWithTimeoutAsync(Task task, string operationName)
        {
            try
            {
                // We also pass the shutdown token so that once extension disposal begins (which cancels _shutdownCts),
                // any in-flight WaitAsync bails out immediately rather than waiting the full ResponseTimeout.
                await task.WaitAsync(_messagingOptions.ResponseTimeout, _timeProvider, _shutdownCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (task.IsCanceled)
            {
                // The awaited operation observed the cancellation! This is expected during normal teardown of a
                // cancelled enumeration, so no log is justified as this is NOT a failure!
            }
            catch (OperationCanceledException) when (_shutdownCts.IsCancellationRequested)
            {
                // Shutdown interrupted the wait!
                LogWarningErrorDisposingEnumerator(_logger, new OperationCanceledException($"Shutdown interrupted waiting for {operationName}."));
            }
            catch (TimeoutException)
            {
                // If this is the disposal task, the worker remains alive and retains responsibility for cleanup.
                // Request cancellation and disposal responses are therefore bounded even when the underlying operation is not.
                LogWarningErrorDisposingEnumerator(_logger, new TimeoutException($"Timed out waiting for {operationName}."));
            }
            catch (Exception exception)
            {
                LogWarningErrorDisposingEnumerator(_logger, exception);
            }
        }

        public ValueTask DisposeAsync()
        {
            List<EnumeratorState> states = [];
            TaskCompletionSource? disposalCompletion;

            lock (_lock)
            {
                if (_disposeTask is not null)
                {
                    return new ValueTask(_disposeTask);
                }

                foreach (var state in _enumerators.Values)
                {
                    states.Add(state);
                }

                // We do NOT clear _enumerators here! Instead that happens as teardown completes, preserving the
                // reference checks in MoveNextCoreAsync. And we create and hand off the promise to dispose this
                // extension (and all enumerators) to any (potentially concurrent) callers.

                disposalCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

                _disposeTask = disposalCompletion.Task;
            }

            // We signal removal outside the lock so pending MoveNextCoreAsync call can safely acquire _lock in StartEnumeratorDisposal.
            // This is because the thread cancelling these will execute the parked MoveNext logic. And we need cancellation
            // to happen synchrounsly so we are sure that we do NOT start the actual disposing before we have singnalled cancellation to them.
            foreach (var state in states)
            {
                state.SignalRemoval();
            }

            CompleteExtensionDisposalAsync(states, disposalCompletion).Ignore();

            return new ValueTask(_disposeTask);
        }

        private async Task CompleteExtensionDisposalAsync(List<EnumeratorState> states, TaskCompletionSource disposalCompletion)
        {
            // We want to yield control to the caller and NOT capture the context.
            // This is not so important really as the caller thread will just execute the
            // CompleteDisposalWorkAsync(work) loop, and each CompleteDisposalWorkAsync will yield immedialty,
            // but its better to have it than not.
            await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);

            foreach (var state in states)
            {
                _ = StartEnumeratorDisposal(state);
            }

            // We cancel the cleanup loop directly via _cleanupCts, NOT via _shutdownCts.
            // _shutdownCts.Token is used by WaitWithTimeoutAsync inside CompleteDisposalWorkAsync
            // to wait for the producer's pending MoveNextAsync. If we would cancel here it could result
            // in the iterator's finally block to never run.
            CancellationTokenSource? cleanupCts;

            lock (_lock)
            {
                cleanupCts = _cleanupCts;
                _cleanupCts = null;
            }

            if (cleanupCts is not null)
            {
                await cleanupCts.CancelAsync().ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
                // Disposal of the cleanup cts is done implicitly by RunCleanupLoopAsync's using statement.
            }

            Task? cleanupTask;

            lock (_lock)
            {
                cleanupTask = _cleanupTask;
            }

            if (cleanupTask is not null)
            {
                await cleanupTask.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            }

            try
            {
                await Task.WhenAll(states.Select(s => s.TeardownTask!))
                    .WaitAsync(_messagingOptions.ResponseTimeout, _timeProvider)
                    .ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                LogWarningErrorDisposingEnumerator(_logger, new TimeoutException("Timed out waiting for all observer async enumerators to dispose."));
            }
            catch (Exception exception)
            {
                LogWarningErrorDisposingEnumerator(_logger, exception);
            }
            finally
            {
                try
                {
                    // Now that the teardown is complete we cancel _shutdownCts.
                    await _shutdownCts.CancelAsync().ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);

                    _shutdownCts.Dispose();

                    OnAsyncEnumerableGrainObserverExtensionDisposed(this);
                }
                finally
                {
                    disposalCompletion.TrySetResult();
                }
            }
        }

        private sealed record DisposalWork(
            EnumeratorState State,
            bool CancelClientToken, // Whether to cancel the client token in order to request that its MoveNextAsync completes.
            TaskCompletionSource DisposalStartedCompletion, // This should be awaited by in-flight invocations.          
            TaskCompletionSource TeardownCompletion); // This should be awaited by the extension's own DisposeAsync.

        // Contrary to the grain extension the enumerator state here is a class instead of a mutable struct.
        // The grain extension can mutate the dictionary's stored state under its (much safer) execution assumptions.
        // The state here though must retain a reference while asynchronous operations are waiting,
        // while the dictionary entry may be removed by another (concurrent) operation.
        // A request must never start on E1 state and continue on another E2 state.
        // Thats why instead of just checking the id we check also for reference equality!
        [SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable", Justification =
            "EnumeratorState owns a SemaphoreSlim and a CancellationTokenSource which are intentionally never disposed. " +
            "EnumeratorState can still be referenced by stale or concurrent MoveNextCoreAsync operations after teardown completes. " +
            "Disposing these resources could race with gate operations or access to RemovalTokenSource.Token, causing ObjectDisposedException. " +
            "These resources are left for garbage collection once all outstanding references to the state are released.")]
        private sealed class EnumeratorState
        {
            private readonly SemaphoreSlim _gate = new(1, 1);

            public required Guid RequestId { get; init; }
            public required int MaxBatchSize { get; init; }

            public required CancellationTokenSource ClientTokenSource { get; init; }
            public CancellationTokenSource RemovalTokenSource { get; } = new();
            public DisposalWork? DisposalWork;

            // These props should be accessed while the gate is held.
            public bool Seen { get; set; }
            public IAsyncDisposable? Enumerator { get; set; }
            public Task<bool>? MoveNextTask { get; set; }

            // These props should be accessed under the _lock of the extension.
            public bool IsDisposing => DisposalStartedTask is not null;

            /// <summary>
            /// Represents the initiation of the disposal of this enumerator.
            /// </summary>
            /// <remarks>
            /// Fires immediately after cancellation is initiated, unblocking in-flight MoveNext.
            /// This completes before the producer's DisposeAsync runs; see <see cref="TeardownTask"/> for that.
            /// </remarks>
            public Task? DisposalStartedTask { get; set; }

            /// <summary>
            /// Represents the full destruction of the enumerator include pending MoveNext calls.
            /// </summary>
            /// <remarks>Fires when the producer's DisposeAsync completes, unblocking explicit dispose RPCs.</remarks>
            public Task? TeardownTask { get; set; }

            public bool TryEnterGate() => _gate.Wait(0);
            public Task WaitToEnterGateAsync(CancellationToken cancellationToken) => _gate.WaitAsync(cancellationToken);
            public void ExitGate() => _gate.Release();

            /// <summary>
            /// Signals that this enumerator is being removed, unblocking pending MoveNext operations.
            /// </summary>
            /// <remarks>
            /// Cancellation is synchronous because this token is used exclusively for internal coordination.
            /// Its cancellation registrations execute inline, ensuring removal is signalled before the disposal worker starts.
            /// Do NOT call this under _lock!
            /// </remarks>
            public void SignalRemoval() => RemovalTokenSource.Cancel();
        }

        [LoggerMessage(Level = LogLevel.Warning, Message = "Error canceling observer async enumerator.")]
        private static partial void LogWarningErrorCancelingEnumerator(ILogger logger, Exception exception);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Error disposing observer async enumerator.")]
        private static partial void LogWarningErrorDisposingEnumerator(ILogger logger, Exception exception);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Error cleaning up observer async enumerators.")]
        private static partial void LogWarningErrorCleaningUpEnumerators(ILogger logger, Exception exception);
    }
}
