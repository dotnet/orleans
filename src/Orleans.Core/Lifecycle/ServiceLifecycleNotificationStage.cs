using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Orleans;

#nullable enable

/// <summary>
/// Represents a specific stage in the client / silo lifecycle.
/// </summary>
public interface IServiceLifecycleStage
{
    /// <summary>
    /// Gets a cancellation token that is triggered when this stage completes.
    /// </summary>
    /// <remarks>Avoid registering callbacks in this token, prefer
    /// <see cref="Register(Func{object?, CancellationToken, Task}, object?, bool)"/> instead.</remarks>
    CancellationToken Token { get; }

    /// <summary>
    /// Waits for this lifecycle stage to complete.
    /// </summary>
    /// <param name="cancellationToken">
    /// A token used to cancel the wait. This does not cancel the lifecycle stage itself!
    /// </param>
    Task WaitAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Registers a callback to be executed during this lifecycle stage.
    /// </summary>
    /// <param name="callback">
    /// <para>The asynchronous operation to perform.</para>
    /// <para><strong>Never call <see cref="WaitAsync(CancellationToken)"/></strong> inside a callback, as it will result in a deadlock!</para>
    /// </param>
    /// <param name="terminateOnError">
    /// If <c>true</c>, the client / silo will shut down if there is a failure;
    /// otherwise an error will be logged and the client / silo will continue to the next stage.
    /// </param>
    /// <param name="state">An optional state to pass.</param>
    /// <remarks>
    /// Disposing the returned value removes the callback from the lifecycle stage.
    /// This is useful for components that have a shorter lifespan than the client / silo to prevent holding onto the reference,
    /// and ensure that cleanup logic is not executed for components that are no longer active.
    /// </remarks>
    IDisposable Register(Func<object?, CancellationToken, Task> callback, object? state = null, bool terminateOnError = true);
}

internal sealed partial class ServiceLifecycleNotificationStage(ILogger logger, string name) : IServiceLifecycleStage
{
    // We use this so that late registrations can still be executed, otherwise
    // we'd need to rely on the TCS which means we'd need to set it *before* the callbacks
    // have been executed, ideally we should fire the TCS only after non-late registered callbacks have completed.
    private bool _isNotifyingOrHasCompleted;

    private readonly object _lock = new();
    private readonly List<StageParticipant> _participants = [];
    private readonly CancellationTokenSource _cts = new();
    private readonly TaskCompletionSource _tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public CancellationToken Token => _cts.Token;

    public Task WaitAsync(CancellationToken cancellationToken) => _tcs.Task.WaitAsync(cancellationToken);

    public IDisposable Register(Func<object?, CancellationToken, Task> callback, object? state, bool terminateOnError)
    {
        ArgumentNullException.ThrowIfNull(callback);

        var participant = new StageParticipant(this, callback, state, terminateOnError);

        lock (_lock)
        {
            if (_isNotifyingOrHasCompleted)
            {
                LogStageAlreadyCompleted(logger, name);

                _ = Task.Run(() => ExecuteLateCallback(participant));

                return participant;
            }

            _participants.Add(participant);
        }

        return participant;

        async Task ExecuteLateCallback(StageParticipant participant)
        {
            try
            {
                // The original token passed to NotifyCompleted (typically related to the silo startup/shutdown) must be "gone" by now.
                // Since the stage has already completed, there is no impending timeout for this late registration, so we pass CancellationToken.None.
                // For late participants we do not check for termination!

                await participant.ExecuteAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                LogLateCallbackError(logger, ex, name);
            }
        }
    }

    public async Task NotifyCompleted(CancellationToken cancellationToken)
    {
        List<StageParticipant>? snapshot;

        lock (_lock)
        {
            if (_isNotifyingOrHasCompleted)
            {
                snapshot = null;
            }
            else
            {
                _isNotifyingOrHasCompleted = true;
                snapshot = [.. _participants];
            }
        }

        if (snapshot is null)
        {
            await _tcs.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        var tasks = new List<Task>(snapshot.Count + 1)
            {
                CancelTokenAsync()
            };

        foreach (var participant in snapshot)
        {
            tasks.Add(ExecuteParticipantAsync(participant, cancellationToken));
        }

        var allTasks = Task.WhenAll(tasks);

        try
        {
            await allTasks.ConfigureAwait(false);
            _tcs.SetResult();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _tcs.TrySetCanceled(cancellationToken);
        }
        catch (Exception ex)
        {
            // Note that awaiting WhenAll returns only the first exception, and we want to show all, if there are multiple.
            if (allTasks.Exception is { } aggEx)
            {
                var flattened = aggEx.Flatten();

                if (flattened.InnerExceptions.Count == 1)
                {
                    // For cleaner reporting in case one callback throws.
                    _tcs.SetException(flattened.InnerExceptions[0]);
                }
                else
                {
                    // Otherwise we let the user see all failures.
                    _tcs.SetException(flattened);
                }
            }
            else
            {
                // Unlikely but hey!
                _tcs.SetException(ex);
            }

            // We throw here regardless, because it's the callback participant who controls whether to TerminateOnError or not.
            throw;
        }
    }

    private async Task CancelTokenAsync()
    {
        try
        {
            await _cts.CancelAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Should not happen if callers respect the contract to register
            // callbacks with the proper method, but it can happen!
            LogCancellationCallbackError(logger, ex, name);
        }
    }

    private async Task ExecuteParticipantAsync(StageParticipant participant, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return;
        }

        try
        {
            await participant.ExecuteAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // If the upstream token triggered this, we rethrow so WhenAll knows we stopped due to cancellation.
            throw;
        }
        catch (Exception ex)
        {
            LogCallbackError(logger, ex, name);

            if (participant.TerminateOnError)
            {
                // This will cause WhenAll to fault, eventually triggering _tcs.SetException above.
                // NotifyCompleted relies on us to throw in case TerminateOnError is set to true.
                throw;
            }
        }
    }

    private void Unregister(StageParticipant participant)
    {
        lock (_lock)
        {
            _participants.Remove(participant);
        }
    }

    private record StageParticipant(ServiceLifecycleNotificationStage Stage,
        Func<object?, CancellationToken, Task> Callback, object? State, bool TerminateOnError) : IDisposable
    {
        public Task ExecuteAsync(CancellationToken cancellationToken) => Callback(State, cancellationToken);
        void IDisposable.Dispose() => Stage.Unregister(this);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Lifecycle stage = '{StageName}' has already completed. Executing callback immediately.")]
    public static partial void LogStageAlreadyCompleted(ILogger logger, string stageName);

    [LoggerMessage(Level = LogLevel.Error, Message = "Error executing late-registered callback for lifecycle stage = '{StageName}'")]
    public static partial void LogLateCallbackError(ILogger logger, Exception exception, string stageName);

    [LoggerMessage(Level = LogLevel.Information, Message = "Lifecycle stage = '{StageName}' has been canceled.")]
    public static partial void LogStageCanceled(ILogger logger, string stageName);

    [LoggerMessage(Level = LogLevel.Error, Message = "Error executing callback for lifecycle stage = '{StageName}'")]
    public static partial void LogCallbackError(ILogger logger, Exception exception, string stageName);

    [LoggerMessage(Level = LogLevel.Error, Message = "An exception occurred inside a CancellationToken callback for lifecycle stage = '{StageName}'")]
    public static partial void LogCancellationCallbackError(ILogger logger, Exception exception, string stageName);
}
