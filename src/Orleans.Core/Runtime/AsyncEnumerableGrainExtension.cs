using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
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
    private const long EnumeratorExpirationMilliseconds = 10_000; 
    private readonly Dictionary<Guid, EnumeratorState> _enumerators = new();
    private readonly IGrainContext _grainContext;
    private readonly MessagingOptions _messagingOptions;
    private readonly IDisposable _timer;

    /// <summary>
    /// Initializes a new <see cref="AsyncEnumerableGrainExtension"/> instance.
    /// </summary>
    /// <param name="grainContext">The grain which this extension is attached to.</param>
    public AsyncEnumerableGrainExtension(IGrainContext grainContext, IOptions<MessagingOptions> messagingOptions)
    {
        _grainContext = grainContext;
        _messagingOptions = messagingOptions.Value;
        var registry = _grainContext.GetComponent<ITimerRegistry>();
        _timer = registry.RegisterTimer(
            _grainContext,
            static async state => await ((AsyncEnumerableGrainExtension)state).RemoveExpiredAsync(),
            this,
            TimeSpan.FromSeconds(EnumeratorExpirationMilliseconds),
            TimeSpan.FromSeconds(EnumeratorExpirationMilliseconds));
    }

    /// <inheritdoc/>
    public ValueTask DisposeAsync(Guid requestId)
    {
        if (_enumerators.Remove(requestId, out var enumerator) && enumerator.Enumerator is { } value)
        {
            return value.DisposeAsync();
        }

        return default;
    }

    public async ValueTask RemoveExpiredAsync()
    {
        List<Guid> toRemove = default;
        foreach (var (requestId, state) in _enumerators)
        {
            if (state.LastSeenTimer.ElapsedMilliseconds > EnumeratorExpirationMilliseconds
                && state.MoveNextTask is null or { IsCompleted: true })
            {
                toRemove ??= new List<Guid>();
                toRemove.Add(requestId);
            }
        }

        List<Task> tasks = default;
        if (toRemove is not null)
        {
            foreach (var requestId in toRemove)
            {
                _enumerators.Remove(requestId, out var state);
                state.MoveNextTask?.Ignore();
                var disposeTask = state.Enumerator.DisposeAsync();
                if (!disposeTask.IsCompletedSuccessfully)
                {
                    tasks ??= new List<Task>();
                    tasks.Add(disposeTask.AsTask());
                }
            }
        }

        if (tasks is { Count: > 0 })
        {
            await Task.WhenAll(tasks);
        }
    }

    /// <inheritdoc/>
    public ValueTask<(EnumerationResult Status, object Value)> StartEnumeration<T>(Guid requestId, [Immutable] IAsyncEnumerableRequest<T> request)
    {
        request.SetTarget(_grainContext);
        var enumerable = request.InvokeImplementation();
        var enumerator = enumerable.GetAsyncEnumerator();
        ref var entry = ref CollectionsMarshal.GetValueRefOrAddDefault(_enumerators, requestId, out bool exists);
        if (exists)
        {
            return ThrowAlreadyExists(enumerator);
        }

        entry.Enumerator = enumerator;
        entry.LastSeenTimer.Restart();
        entry.MaxBatchSize = request.MaxBatchSize;
        Debug.Assert(entry.MaxBatchSize > 0, "Max batch size must be positive.");
        return MoveNextAsync(ref entry, requestId, enumerator);

        static async ValueTask<(EnumerationResult Status, object Value)> ThrowAlreadyExists(IAsyncEnumerator<T> enumerator)
        {
            await enumerator.DisposeAsync();
            throw new InvalidOperationException("An enumerator with the same ID already exists.");
        }
    }

    /// <inheritdoc/>
    public ValueTask<(EnumerationResult Status, object Value)> MoveNext<T>(Guid requestId)
    {
        ref var entry = ref CollectionsMarshal.GetValueRefOrNullRef(_enumerators, requestId);
        if (Unsafe.IsNullRef(ref entry))
        {
            return new((EnumerationResult.MissingEnumeratorError, default));
        }

        entry.LastSeenTimer.Restart();
        if (entry.Enumerator is not IAsyncEnumerator<T> typedEnumerator)
        {
            throw new InvalidCastException("Attempted to access an enumerator of the wrong type.");
        }

        return MoveNextAsync(ref entry, requestId, typedEnumerator);
    }

    private ValueTask<(EnumerationResult Status, object Value)> MoveNextAsync<T>(
        ref EnumeratorState entry,
        Guid requestId,
        IAsyncEnumerator<T> typedEnumerator)
    {
        Debug.Assert(entry.MaxBatchSize > 0, "Max batch size must be positive.");
        try
        {
            if (entry.MoveNextTask is null)
            {
                ValueTask<bool> moveNextValueTask;
                var currentBatchSize = 0;
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
                                return OnComplete(requestId, typedEnumerator);
                            }
                            else if (currentBatchSize == 1)
                            {
                                return new((EnumerationResult.CompletedWithElement, result));
                            }

                            return new((EnumerationResult.CompletedWithBatch, result));
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

            return AwaitMoveNextAsync(requestId, typedEnumerator, entry.MoveNextTask);
        }
        catch (Exception exception)
        {
            return OnError(requestId, typedEnumerator, exception);
        }
    }

    private async ValueTask<(EnumerationResult Status, object Value)> AwaitMoveNextAsync<T>(Guid requestId, IAsyncEnumerator<T> typedEnumerator, Task<bool> moveNextTask)
    {
        try
        {
            // Wait up to half the response timeout for the MoveNextAsync task to complete.
            using var cancellation = new CancellationTokenSource(_messagingOptions.ResponseTimeout / 2);

            // Wait for either the MoveNextAsync task to complete or the cancellation token to be cancelled.
            var completedTask = await Task.WhenAny(moveNextTask, cancellation.Token.WhenCancelled());
            if (completedTask == moveNextTask)
            {
                OnMoveNext(requestId);
                var hasValue = moveNextTask.GetAwaiter().GetResult();
                if (hasValue)
                {
                    return (EnumerationResult.Element, typedEnumerator.Current);
                }
                else
                {
                    _enumerators.Remove(requestId);
                    await typedEnumerator.DisposeAsync();
                    return (EnumerationResult.Completed, default);
                }
            }

            return (EnumerationResult.Heartbeat, default);
        }
        catch
        {
            _enumerators.Remove(requestId);
            await typedEnumerator.DisposeAsync();
            throw;
        }
    }

    private async ValueTask<(EnumerationResult Status, object Value)> OnComplete<T>(Guid requestId, IAsyncEnumerator<T> enumerator)
    {
        _enumerators.Remove(requestId, out var state);
        state.MoveNextTask?.Ignore();
        await enumerator.DisposeAsync();
        return (EnumerationResult.Completed, default);
    }
    
    private async ValueTask<(EnumerationResult Status, object Value)> OnError<T>(Guid requestId, IAsyncEnumerator<T> enumerator, Exception exception)
    {
        _enumerators.Remove(requestId, out var state);
        state.MoveNextTask?.Ignore();
        await enumerator.DisposeAsync();
        ExceptionDispatchInfo.Throw(exception);
        return default;
    }

    private void OnMoveNext(Guid requestId)
    {
        ref var state = ref CollectionsMarshal.GetValueRefOrNullRef(_enumerators, requestId);
        if (Unsafe.IsNullRef(ref state))
        {
            return;
        }

        state.LastSeenTimer.Restart();
        state.MoveNextTask = null;
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
                if (enumerator.Enumerator is { } value)
                {
                    await value.DisposeAsync();
                }
            }
        }

        _timer.Dispose();
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        _timer.Dispose();
    }

    private struct EnumeratorState
    {
        public IAsyncDisposable Enumerator;
        public Task<bool> MoveNextTask;
        public CoarseStopwatch LastSeenTimer;
        public int MaxBatchSize;
    }
}
