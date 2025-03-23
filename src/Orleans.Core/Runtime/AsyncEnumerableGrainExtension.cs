using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Internal;
using Orleans.Timers;

namespace Orleans.Runtime;

/// <summary>
/// Grain-side support for returning <see cref="IAsyncEnumerable{T}"/> from grain methods.
/// </summary>
internal sealed class AsyncEnumerableGrainExtension : IAsyncEnumerableGrainExtension, IAsyncDisposable, IDisposable
{
    private static readonly DiagnosticListener DiagnosticListener = new("Orleans.Runtime.AsyncEnumerableGrainExtension");
    private readonly Dictionary<Guid, EnumeratorState> _enumerators = [];
    private readonly ILogger<AsyncEnumerableGrainExtension> _logger;
    private readonly MessagingOptions _messagingOptions;

    // Internal for testing
    internal IGrainTimer Timer { get; }
    internal IGrainContext GrainContext { get; }

    /// <summary>
    /// Initializes a new <see cref="AsyncEnumerableGrainExtension"/> instance.
    /// </summary>
    /// <param name="grainContext">The grain which this extension is attached to.</param>
    public AsyncEnumerableGrainExtension(
        IGrainContext grainContext,
        IOptions<MessagingOptions> messagingOptions,
        ILogger<AsyncEnumerableGrainExtension> logger)
    {
        _logger = logger;
        GrainContext = grainContext;

        _messagingOptions = messagingOptions.Value;
        var registry = GrainContext.GetComponent<ITimerRegistry>();
        var cleanupPeriod = messagingOptions.Value.ResponseTimeout;
        Timer = registry.RegisterGrainTimer(
            GrainContext,
            static async (state, cancellationToken) => await state.RemoveExpiredAsync(cancellationToken),
            this,
            new()
            {
                DueTime = cleanupPeriod,
                Period = cleanupPeriod,
                Interleave = true,
                KeepAlive = false
            });
        OnAsyncEnumeratorGrainExtensionCreated(this);
    }

    /// <inheritdoc/>
    public ValueTask DisposeAsync(Guid requestId) => RemoveEnumeratorAsync(requestId);

    private async ValueTask RemoveExpiredAsync(CancellationToken cancellationToken)
    {
        List<Guid> toRemove = default;
        foreach (var (requestId, state) in _enumerators)
        {
            if (MarkAndCheck(requestId))
            {
                toRemove ??= [];
                toRemove.Add(requestId);
            }

            bool MarkAndCheck(Guid requestId)
            {
                ref var state = ref CollectionsMarshal.GetValueRefOrNullRef(_enumerators, requestId);
                if (Unsafe.IsNullRef(ref state))
                {
                    return false;
                }

                // Returns true if no flags were set.
                return state.ClearSeen();
            }
        }

        List<Task> tasks = default;
        if (toRemove is not null)
        {
            foreach (var requestId in toRemove)
            {
                var removeTask = RemoveEnumeratorAsync(requestId);
                if (!removeTask.IsCompletedSuccessfully)
                {
                    tasks ??= [];
                    tasks.Add(removeTask.AsTask());
                }
            }
        }

        if (tasks is { Count: > 0 })
        {
            await Task.WhenAll(tasks).WaitAsync(cancellationToken);
        }

        OnEnumeratorCleanupCompleted(this);
    }

    /// <inheritdoc/>
    public ValueTask<(EnumerationResult Status, object Value)> StartEnumeration<T>(Guid requestId, [Immutable] IAsyncEnumerableRequest<T> request)
    {
        ref var entry = ref CollectionsMarshal.GetValueRefOrAddDefault(_enumerators, requestId, out bool exists);
        if (exists)
        {
            return ThrowAlreadyExists();
        }

        request.SetTarget(GrainContext);
        var cts = CancellationTokenSource.CreateLinkedTokenSource(request.GetCancellationToken());
        var enumerable = request.InvokeImplementation();
        var enumerator = enumerable.GetAsyncEnumerator(cts.Token);
        entry.Enumerator = enumerator;
        entry.MaxBatchSize = request.MaxBatchSize;
        entry.CancellationTokenSource = cts;
        Debug.Assert(entry.MaxBatchSize > 0, "Max batch size must be positive.");
        return MoveNextCore(ref entry, requestId, enumerator);

        static ValueTask<(EnumerationResult Status, object Value)> ThrowAlreadyExists() => ValueTask.FromException<(EnumerationResult Status, object Value)>(new InvalidOperationException("An enumerator with the same id already exists."));
    }

    /// <inheritdoc/>
    public ValueTask<(EnumerationResult Status, object Value)> MoveNext<T>(Guid requestId)
    {
        ref var entry = ref CollectionsMarshal.GetValueRefOrNullRef(_enumerators, requestId);
        if (Unsafe.IsNullRef(ref entry))
        {
            return new((EnumerationResult.MissingEnumeratorError, default));
        }

        if (entry.Enumerator is not IAsyncEnumerator<T> typedEnumerator)
        {
            throw new InvalidCastException("Attempted to access an enumerator of the wrong type.");
        }

        return MoveNextCore(ref entry, requestId, typedEnumerator);
    }

    private ValueTask<(EnumerationResult Status, object Value)> MoveNextCore<T>(
        ref EnumeratorState entry,
        Guid requestId,
        IAsyncEnumerator<T> typedEnumerator)
    {
        Debug.Assert(entry.MaxBatchSize > 0, "Max batch size must be positive.");
        entry.SetSeen();

        try
        {
            var currentBatchSize = 0;
            if (entry.MoveNextTask is null)
            {
                ValueTask<bool> moveNextValueTask;
                object result = null;
                do
                {
                    // Check if the enumerator has a result ready synchronously.
                    moveNextValueTask = typedEnumerator.MoveNextAsync();
                    if (moveNextValueTask.IsCompletedSuccessfully)
                    {
                        var hasValue = moveNextValueTask.Result;
                        if (hasValue)
                        {
                            // Account for the just-emitted element.
                            ++currentBatchSize;
                            var value = typedEnumerator.Current;
                            if (currentBatchSize == 1)
                            {
                                result = value;
                            }
                            else if (currentBatchSize == 2)
                            {
                                // Grow from a single element to a list.
                                result = new List<T> { (T)result, value };
                            }
                            else
                            {
                                ((List<T>)result).Add(value);
                            }
                        }
                        else
                        {
                            // Completed successfully, possibly with some final elements.
                            if (currentBatchSize == 0)
                            {
                                return OnTerminateAsync(requestId, EnumerationResult.Completed, default);
                            }
                            else if (currentBatchSize == 1)
                            {
                                return OnTerminateAsync(requestId, EnumerationResult.CompletedWithElement, result);
                            }

                            return OnTerminateAsync(requestId, EnumerationResult.CompletedWithBatch, result);
                        }
                    }
                    else
                    {
                        // The enumerator did not complete synchronously, so we need to await the result for subsequent elements.
                        entry.MoveNextTask = moveNextValueTask.AsTask();
                        break;
                    }
                } while (currentBatchSize < entry.MaxBatchSize);

                // If there are elements, return them now instead of waiting for the pending operation to complete.
                if (currentBatchSize == 1)
                {
                    return new((EnumerationResult.Element, result));
                }
                else if (currentBatchSize > 1)
                {
                    return new((EnumerationResult.Batch, result));
                }

                // There are no elements, so wait for the pending operation to complete.
            }

            // Prevent the enumerator from being collected while we are enumerating it.
            entry.SetBusy();
            return AwaitMoveNextAsync(requestId, typedEnumerator, entry.MoveNextTask);
        }
        catch (Exception exception)
        {
            return OnTerminateAsync(requestId, EnumerationResult.Error, exception);
        }
    }

    private async ValueTask<(EnumerationResult Status, object Value)> AwaitMoveNextAsync<T>(Guid requestId, IAsyncEnumerator<T> typedEnumerator, Task<bool> moveNextTask)
    {
        try
        {
            // Wait for either the MoveNextAsync task to complete or the polling timeout to elapse.
            var longPollingTimeout = _messagingOptions.ConfiguredResponseTimeout / 2;
            await moveNextTask.WaitAsync(longPollingTimeout).SuppressThrowing();

            // Update the enumerator state to indicate that we are not currently waiting for MoveNextAsync to complete.
            // If the MoveNextAsync task completed then clear that now, too.
            UpdateEnumeratorState(requestId, clearMoveNextTask: moveNextTask.IsCompleted);
            void UpdateEnumeratorState(Guid requestId, bool clearMoveNextTask)
            {
                ref var state = ref CollectionsMarshal.GetValueRefOrNullRef(_enumerators, requestId);
                if (Unsafe.IsNullRef(ref state))
                {
                    return;
                }

                state.ClearBusy();
                if (clearMoveNextTask)
                {
                    state.MoveNextTask = null;
                }
            }

            if (moveNextTask.IsCompletedSuccessfully)
            {
                var hasValue = moveNextTask.GetAwaiter().GetResult();
   
                if (hasValue)
                {
                    return (EnumerationResult.Element, typedEnumerator.Current);
                }
                else
                {
                    await RemoveEnumeratorAsync(requestId);
                    return (EnumerationResult.Completed, default);
                }
            }
            else if (moveNextTask.IsCanceled)
            {
                await RemoveEnumeratorAsync(requestId);
                return (EnumerationResult.Canceled, default);
            }
            else if (moveNextTask.Exception is { } moveNextException)
            {
                // Completed, but not successfully.
                var exception = moveNextException.InnerExceptions.Count == 1 ? moveNextException.InnerException : moveNextException;
                await RemoveEnumeratorAsync(requestId);
                return (EnumerationResult.Error, exception);
            }

            return (EnumerationResult.Heartbeat, default);
        }
        catch (Exception exception)
        {
            await RemoveEnumeratorAsync(requestId);
            return (EnumerationResult.Error, exception);
        }
    }

    private async ValueTask RemoveEnumeratorAsync(Guid requestId)
    {
        if (_enumerators.Remove(requestId, out var state))
        {
            await DisposeEnumeratorAsync(state);
        }
    }

    private async ValueTask<(EnumerationResult Status, object Value)> OnTerminateAsync(Guid requestId, EnumerationResult status, object value)
    {
        await RemoveEnumeratorAsync(requestId);
        return (status, value);
    }
    
    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (_enumerators.Count > 0)
        {
            var enumerators = new List<EnumeratorState>(_enumerators.Values);
            _enumerators.Clear();

            foreach (var enumerator in enumerators)
            {
                await DisposeEnumeratorAsync(enumerator);
            }
        }

        Timer.Dispose();
    }

    private async ValueTask DisposeEnumeratorAsync(EnumeratorState enumerator)
    {
        try
        {
            enumerator.CancellationTokenSource.Cancel();
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Error cancelling enumerator.");
        }

        try
        {
            using var cts = new CancellationTokenSource(_messagingOptions.ResponseTimeout);
            if (enumerator.MoveNextTask is { } task)
            {
                await task.WaitAsync(cts.Token).SuppressThrowing();
            }

            if (enumerator.MoveNextTask is null or { IsCompleted: true } && enumerator.Enumerator is { } value)
            {
                await value.DisposeAsync().AsTask().WaitAsync(cts.Token).SuppressThrowing();
            }
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Error disposing enumerator.");
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        Timer.Dispose();
    }

    private static void OnAsyncEnumeratorGrainExtensionCreated(AsyncEnumerableGrainExtension extension)
    {
        if (DiagnosticListener.IsEnabled())
        {
            DiagnosticListener.Write(nameof(OnAsyncEnumeratorGrainExtensionCreated), extension);
        }
    }

    private static void OnEnumeratorCleanupCompleted(AsyncEnumerableGrainExtension extension)
    {
        if (DiagnosticListener.IsEnabled())
        {
            DiagnosticListener.Write(nameof(OnEnumeratorCleanupCompleted), extension);
        }
    }

    private struct EnumeratorState
    {
        private const int SeenFlag = 0x01;
        private const int BusyFlag = 0x10;
        private int _flags;
        public IAsyncDisposable Enumerator;
        public Task<bool> MoveNextTask;
        public int MaxBatchSize;
        internal CancellationTokenSource CancellationTokenSource;
        public void SetSeen() => _flags |= SeenFlag;
        public void SetBusy() => _flags |= BusyFlag | SeenFlag;
        public void ClearBusy() => _flags = SeenFlag; // Clear the 'Busy' flag, but set the 'Seen' flag.
        public bool ClearSeen()
        {
            // Clear the 'Seen' flag and check if any flags were set previously.
            var isExpired = _flags == 0;
            _flags &= ~SeenFlag;
            return isExpired;
        }
    }
}
