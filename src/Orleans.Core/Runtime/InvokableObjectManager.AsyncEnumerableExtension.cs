using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Microsoft.CodeAnalysis;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Configuration;

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

        private readonly Task _cleanupTask;
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
            _cleanupTask = RunCleanupLoopAsync(_shutdownCts.Token);

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

        private async Task RunCleanupLoopAsync(CancellationToken cancellationToken)
        {
            using var timer = new PeriodicTimer(_messagingOptions.ResponseTimeout, _timeProvider);

            try
            {
                while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
                {
                    try
                    {
                        List<(Guid RequestId, EnumeratorState State)>? expired = null;
                        List<DisposalWork>? workItems = null;
                        List<Task>? disposalTasks = null;

                        lock (_lock)
                        {
                            if (_disposeTask is not null)
                            {
                                return;
                            }

                            foreach (var (requestId, state) in _enumerators)
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
                                        expired ??= [];
                                        expired.Add((requestId, state));
                                    }
                                }
                                finally
                                {
                                    state.ExitGate();
                                }
                            }

                            if (expired is not null)
                            {
                                foreach (var (requestId, state) in expired)
                                {
                                    // Note: we do not remove the state from _enumerators here.
                                    // It remains until StartDisposal's finally block removes it, allowing
                                    // any racing MoveNext to observe state.DisposalTask != null and bail out cleanly.
                                    GetOrCreateDisposalTask(state, cancel: true, out var disposalTask, out var disposalWork);

                                    (disposalTasks ??= []).Add(disposalTask);

                                    if (disposalWork is { } work)
                                    {
                                        (workItems ??= []).Add(work);
                                    }
                                }
                            }
                        }

                        if (workItems is not null)
                        {
                            foreach (var work in workItems)
                            {
                                StartDisposal(work);
                            }
                        }

                        if (disposalTasks is { Count: > 0 })
                        {
                            await Task.WhenAll(disposalTasks).ConfigureAwait(false);
                        }

                        OnObserverEnumeratorCleanupCompleted(this); // Yes we report even if there are no disposal to do.
                    }
                    catch (Exception exception)
                    {
                        LogWarningErrorCleaningUpEnumerators(_logger, exception);
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Ignore; we are shutting down.
            }
            catch (Exception exception)
            {
                LogWarningErrorCleaningUpEnumerators(_logger, exception);
            }
        }

        public async ValueTask<(EnumerationResult Status, object? Value)> StartEnumeration<T>(
            Guid requestId, IAsyncEnumerableRequest<T> request, CancellationToken cancellationToken)
        {
            Debug.Assert(request.MaxBatchSize > 0, "Max batch size must be positive.");

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

            Exception? exception = null;

            lock (_lock)
            {
                if (_disposeTask is not null)
                {
                    exception = new ObjectDisposedException(nameof(AsyncEnumerableGrainObserverExtension));
                }
                else if (!_enumerators.TryAdd(requestId, state))
                {
                    exception = new InvalidOperationException("An enumerator with the same id already exists.");
                }
            }

            if (exception is not null)
            {
                state.ExitGate();
                cts.Dispose();

                return (EnumerationResult.Error, exception);
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

                var disposalTask = RequestDisposal(requestId, state, cancel: true);

                state.ExitGate();

                await WaitWithTimeoutAsync(disposalTask).ConfigureAwait(false);

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
            // because RequestDisposal fires SignalRemoval *before* teardown starts. A stale call that observes removal
            // returns MissingEnumeratorError without entering the gate, releasing its invocation admission so StopAsync can drain.

            try
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (!state.TryEnterGate())
                {
                    using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, state.RemovalTokenSource.Token);
                    await state.WaitToEnterGateAsync(linkedCts.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (state.RemovalTokenSource.Token.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                return (EnumerationResult.MissingEnumeratorError, default);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                var disposalTask = RequestDisposal(requestId, state, cancel: true);

                await WaitWithTimeoutAsync(disposalTask).ConfigureAwait(false);

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

                try
                {
                    await moveNextTask.WaitAsync(longPollTimeout, _timeProvider, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    // Regardless of the task's state, we surface cancellation as the caller is the one who cancelled it.
                    result = (EnumerationResult.Canceled, null);
                    goto Done;
                }
                catch (TimeoutException)
                {
                    // Long-poll elapsed, or the task itself was cancelled.
                    // If the task is still in flight, we report a heartbeat; otherwise fall through and classify its outcome.
                    if (!moveNextTask.IsCompleted)
                    {
                        result = (EnumerationResult.Heartbeat, null);
                        goto Done;
                    }
                }
                catch
                {
                    // Ignore; the task faulted! We will classify the error from the task itself.
                }

                Debug.Assert(moveNextTask.IsCompleted);
                state.MoveNextTask = null;

                if (moveNextTask.IsCompletedSuccessfully)
                {
#pragma warning disable CA1849 // It won't be faulted if we reach this point!
                    result = moveNextTask.Result ? (EnumerationResult.Element, typedEnumerator.Current) : (EnumerationResult.Completed, null);
#pragma warning restore CA1849
                    goto Done;
                }

                if (moveNextTask.IsCanceled)
                {
                    result = (EnumerationResult.Canceled, null);
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
                var cancel = ((int)result.Status & CancelMask) != 0;
                var disposalTask = RequestDisposal(requestId, state, cancel);

                await WaitWithTimeoutAsync(disposalTask).ConfigureAwait(false);
            }

            return result;
        }

        ValueTask IAsyncEnumerableGrainExtension.DisposeAsync(Guid requestId) =>
            ((IAsyncEnumerableGrainExtension)this).DisposeAsync(requestId, CancellationToken.None);

        async ValueTask IAsyncEnumerableGrainExtension.DisposeAsync(Guid requestId, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            DisposalWork? disposalWork = null;
            var teardownTask = Task.CompletedTask;

            lock (_lock)
            {
                if (_enumerators.TryGetValue(requestId, out var state))
                {
                    // This RPC is the first to initiate teardown, or it joins existing teardown.
                    // GetOrCreateDisposalTask returns the fast-path completion signal, which we discard here
                    // as we want to await the actual teardown, not just cancellation acknowledgement.
                    GetOrCreateDisposalTask(state, cancel: true, out _, out disposalWork);

                    // GetOrCreateDisposalTask creates the disposal task AND the teardown one together under the lock, so it must not be null!
                    Debug.Assert(state.TeardownTask is not null);

                    teardownTask = state.TeardownTask!;
                }
                else
                {
                    // Enumerator was never started or is already fully torn down, nothing to do.
                }
            }

            if (disposalWork is { } work)
            {
                StartDisposal(work);
            }

            if (!teardownTask.IsCompletedSuccessfully)
            {
                await WaitWithTimeoutAsync(teardownTask).ConfigureAwait(false);
            }
        }

        private Task RequestDisposal(Guid requestId, EnumeratorState? expectedState, bool cancel)
        {
            DisposalWork? disposalWork;
            var disposalTask = Task.CompletedTask;

            lock (_lock)
            {
                EnumeratorState state;

                if (expectedState is null)
                {
                    if (!_enumerators.TryGetValue(requestId, out var current))
                    {
                        return disposalTask;
                    }

                    state = current;
                }
                else
                {
                    state = expectedState;
                }

                GetOrCreateDisposalTask(state, cancel, out disposalTask, out disposalWork);
            }

            if (disposalWork is { } work)
            {
                StartDisposal(work);
            }

            return disposalTask;
        }

        private static void GetOrCreateDisposalTask(EnumeratorState state, bool cancel, out Task disposalTask, out DisposalWork? work)
        {
            // We signal removal before teardown is created/queued. Every path that disposes a state
            // (RequestDisposal, an explicit dispose RPC, the extension's own DisposeAsync, and the cleanup loop)
            // goes through this method and they all acquire the lock. This token is never exposed to user code,
            // so the only registrations are internal linked-CTS continuations from MoveNextCoreAsync's slow-path gate acquisition.
            // Note: Cancel() is idempotent, so a concurrent caller that arrives after the first one is a no-op and simply observes the early-return below.
            // Note also: Cancel() must run synchronously (contrary to the client token) under _lock, to NOT break the ordering invariant expected.
            // This is the main reason why this method signature is void(... out Task disposalTask), so nobody awaits it!

            state.RemovalTokenSource.Cancel();

            if (state.DisposalTask is { } existingTask)
            {
                disposalTask = existingTask;
                work = null;

                return;
            }

            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            state.DisposalTask = completion.Task;

            var teardownCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            state.TeardownTask = teardownCompletion.Task;

            disposalTask = completion.Task;
            work = new DisposalWork(state, cancel, completion, teardownCompletion);
        }

        private void StartDisposal(DisposalWork work)
        {
            RunDisposalAsync().Ignore();

            async Task RunDisposalAsync()
            {
                // We want to yield control to the caller and NOT capture the context.
                await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);

                var state = work.State;
                var cancellationTask = Task.CompletedTask;

                // We signal cancellation *before* waiting for the gate. An in-flight MoveNextAsync may be
                // waiting for the producer, and cancellation is what gives it a chance to finish, because
                // it will notice the cancellation and fault thereby releasing the gate itself.
                if (work.Cancel)
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
                work.Completion.TrySetResult();

                // Now we start the teardown, this is serialized against in-flight invocations by the gate.
                var gateEntered = false;

                try
                {
                    // The cancellation signalling above ensures that we wont get stuck
                    // waiting here for the full ResponseTimeout duration.
                    await state.WaitToEnterGateAsync(CancellationToken.None).ConfigureAwait(false);

                    gateEntered = true;

                    // DisposeAsync must not overlap a running MoveNextAsync, so we wait until a running MoveNextAsync completes fully.
                    if (state.MoveNextTask is { } pendingMoveNextTask)
                    {
                        await WaitUntilCompletedAsync(pendingMoveNextTask).ConfigureAwait(false);
                    }

                    // Then we finalize cancellation
                    await WaitUntilCompletedAsync(cancellationTask).ConfigureAwait(false);

                    if (state.Enumerator is { } enumerator)
                    {
                        try
                        {
                            var enumeratorDisposeTask = enumerator.DisposeAsync().AsTask();
                            await WaitUntilCompletedAsync(enumeratorDisposeTask).ConfigureAwait(false);
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
        }

        private async Task WaitWithTimeoutAsync(Task task)
        {
            try
            {
                await task.WaitAsync(_messagingOptions.ResponseTimeout, _timeProvider).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                // The actual disposal task remains alive and retains responsibility for cleanup.
                // Request cancellation and disposal responses are therefore bounded, even when actual producer teardown is not!
                LogWarningErrorDisposingEnumerator(_logger, new TimeoutException("Timed out waiting for observer async enumerator disposal."));
            }
            catch (Exception exception)
            {
                LogWarningErrorDisposingEnumerator(_logger, exception);
            }
        }

        private async Task WaitUntilCompletedAsync(Task task)
        {
            while (!task.IsCompleted)
            {
                try
                {
                    await task.WaitAsync(_messagingOptions.ResponseTimeout, _timeProvider).ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    // It should be super rare for this to happen, but in case the producer takes a long time to complete its pending operation
                    // the teardown worker remains alive and does not call DisposeAsync() concurrently with the pending MoveNextAsync().
                    // This can become a risk of a deadlock, though callers really are supposed to observe cancellation, and generally not
                    // have code that hangs. Maybe we should consuder breaking after a wait round, maybe a ceiling like 3?!
                    LogWarningErrorDisposingEnumerator(_logger, new TimeoutException($"Timed out waiting for disposal. I will try again!"));
                }
                catch
                {
                    // The task faulted or was canceled, we observe its final outcome below but break out of the loop.
                    break;
                }
            }

            try
            {
                await task.ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                LogWarningErrorDisposingEnumerator(_logger, exception);
            }
        }

        public ValueTask DisposeAsync()
        {
            Task[] teardownTasks;
            List<DisposalWork> workItems = [];
            TaskCompletionSource completion;

            lock (_lock)
            {
                if (_disposeTask is not null)
                {
                    return new ValueTask(_disposeTask);
                }

                teardownTasks = new Task[_enumerators.Count];
                var index = 0;

                foreach (var state in _enumerators.Values)
                {
                    GetOrCreateDisposalTask(state, cancel: true, out _, out var disposalWork);

                    if (disposalWork is { } work)
                    {
                        workItems.Add(work);
                    }

                    teardownTasks[index++] = state.TeardownTask!;
                }

                // We do NOT clear _enumerators here. StartDisposal's finally block removes each state
                // as its teardown completes, preserving the reference checks in MoveNextCoreAsync.
                // And we create and hand off the promise to dispose this extension
                // (and all enumerators) to any (potentially concurrent) callers.

                completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

                _disposeTask = completion.Task;
            }

            CompleteDisposeAsync().Ignore();

            return new ValueTask(completion.Task);

            async Task CompleteDisposeAsync()
            {
                // We want to yield control to the caller and NOT capture the context.
                // This is not so important really as the caller thread will just execute the
                // StartDisposal(work) loop, and each StartDisposal will yield immedialty,
                // but its better to have it than not.
                await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);

                foreach (var work in workItems)
                {
                    StartDisposal(work);
                }

                try
                {
                    await _shutdownCts.CancelAsync().ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    LogWarningErrorCleaningUpEnumerators(_logger, exception);
                }

                try
                {
                    await _cleanupTask.ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    LogWarningErrorCleaningUpEnumerators(_logger, exception);
                }

                try
                {
                    // If a producer never completes its outstanding MoveNextAsync(), we wait max up to ResponseTimeout.
                    // We deliberately do NOT forcibly abandon the disposal workers though. These retain responsibility for finishing
                    // teardown if the producer eventually completes. This means that extension disposal is NOT a guarantee
                    // that every underlying enumerator has been disposed. Eventually they will though so long as user-code behaves!
                    await Task.WhenAll(teardownTasks).WaitAsync(_messagingOptions.ResponseTimeout).ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    // Individual disposal workers remain alive and continue waiting for in-flight producer operations.
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
                        _shutdownCts.Dispose();
                        OnAsyncEnumerableGrainObserverExtensionDisposed(this);
                    }
                    finally
                    {
                        completion.TrySetResult();
                    }
                }
            }
        }

        private readonly record struct DisposalWork(
            EnumeratorState State, bool Cancel,
            TaskCompletionSource Completion, // This should be awaited by in-flight invocations.          
            TaskCompletionSource TeardownCompletion); // This should be awaited by the extension's own DisposeAsync.

        // Contrary to the grain extension the enumerator state here is a class instead of a mutable struct.
        // The grain extension can mutate the dictionary's stored state under its (much safer) execution assumptions.
        // The state here though must retain a reference while asynchronous operations are waiting,
        // while the dictionary entry may be removed by another (concurrent) operation.
        // A request must never start on E1 state and continue on another E2 state.
        // Thats why instead of just checking the id we check also for reference equality!
        [SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable", Justification =
            "EnumeratorState owns a SemaphoreSlim and a CancellationTokenSource that are intentionally never disposed. " +
            "A stale MoveNextCoreAsync can still be racing (reading RemovalTokenSource.Token in CreateLinkedTokenSource, " +
            "or a concurrent sibling holding the gate while we do), so disposal would risk ObjectDisposedException. " +
            "Neither resource holds a native handle so we let the GC collect them instead.")]
        private sealed class EnumeratorState
        {
            private readonly SemaphoreSlim _gate = new(1, 1);

            public required Guid RequestId { get; init; }
            public required int MaxBatchSize { get; init; }

            public required CancellationTokenSource ClientTokenSource { get; init; }
            public CancellationTokenSource RemovalTokenSource { get; } = new();

            // These props should be accessed while the gate is held.
            public IAsyncDisposable? Enumerator { get; set; }
            public bool Seen { get; set; }
            public Task<bool>? MoveNextTask { get; set; }

            // These props should be accessed under the _lock of the extension.

            /// <summary>
            /// Fires immediately after cancellation is initiated, unblocking in-flight MoveNext.
            /// </summary>
            public Task? DisposalTask { get; set; }
            public bool IsDisposing => DisposalTask is not null;

            /// <summary>
            /// Fires when the producer's DisposeAsync completes, unblocking explicit dispose RPCs.
            /// </summary>
            public Task? TeardownTask { get; set; }

            public bool TryEnterGate() => _gate.Wait(0);
            public Task WaitToEnterGateAsync(CancellationToken cancellationToken) => _gate.WaitAsync(cancellationToken);
            public void ExitGate() => _gate.Release();
        }

        [LoggerMessage(Level = LogLevel.Warning, Message = "Error canceling observer async enumerator.")]
        private static partial void LogWarningErrorCancelingEnumerator(ILogger logger, Exception exception);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Error disposing observer async enumerator.")]
        private static partial void LogWarningErrorDisposingEnumerator(ILogger logger, Exception exception);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Error cleaning up observer async enumerators.")]
        private static partial void LogWarningErrorCleaningUpEnumerators(ILogger logger, Exception exception);
    }
}
