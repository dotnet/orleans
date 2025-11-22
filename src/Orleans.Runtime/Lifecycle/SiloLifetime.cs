using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Orleans.Runtime;

#nullable enable

/// <summary>
/// Allows consumers to observe and participate in the silo's lifecycle.
/// </summary>
public interface ISiloLifetime
{
    /// <summary>
    /// Triggered when the silo has fully started and is ready to accept traffic.
    /// </summary>
    ISiloLifecycleStage Started { get; }

    /// <summary>
    /// Triggered when the silo is beginning the shutdown process.
    /// </summary>
    ISiloLifecycleStage Stopping { get; }

    /// <summary>
    /// Triggered when the silo has completed its shutdown process.
    /// </summary>
    ISiloLifecycleStage Stopped { get; }
}

/// <summary>
/// Represents a specific stage in the silo's lifecycle.
/// </summary>
public interface ISiloLifecycleStage
{
    /// <summary>
    /// Gets a task that completes when this stage completes.
    /// </summary>
    Task Task { get; }

    /// <summary>
    /// Gets a cancellation token that is triggered when this stage completes.
    /// </summary>
    /// <remarks>Avoid registering callbacks in this token, prefer
    /// <see cref="Register(Func{object?, CancellationToken, Task}, object?, bool)"/> instead.</remarks>
    CancellationToken Token { get; }

    /// <summary>
    /// Registers a callback to be executed during this lifecycle stage.
    /// </summary>
    /// <param name="callback">
    /// <para>The asynchronous operation to perform.</para>
    /// <para><strong>Never <c>await</c> <see cref="Task"/></strong> inside a callback, as it will result in a deadlock!</para>
    /// </param>
    /// <param name="terminateOnError">
    /// If <c>true</c>, the silo will shut down if there is a failure;
    /// otherwise an error will be logged and the silo will continue to the next stage.
    /// </param>
    /// <param name="state">An optional state to pass.</param>
    /// <remarks>
    /// Disposing the returned value removes the callback from the lifecycle stage.
    /// This is useful for components that have a shorter lifespan than the silo to prevent holding onto the reference,
    /// and ensure that cleanup logic is not executed for components that are no longer active.
    /// </remarks>
    IDisposable Register(Func<object?, CancellationToken, Task> callback, object? state = null, bool terminateOnError = true);
}

internal sealed class SiloLifetime(ILogger<SiloLifetime> logger) : ISiloLifetime, ILifecycleParticipant<ISiloLifecycle>
{
    private readonly SiloLifecycleStage _started = new(logger, "Started");
    private readonly SiloLifecycleStage _stopping = new(logger, "Stopping");
    private readonly SiloLifecycleStage _stopped = new(logger, "Stopped");

    public ISiloLifecycleStage Started => _started;
    public ISiloLifecycleStage Stopping => _stopping;
    public ISiloLifecycleStage Stopped => _stopped;

    public void Participate(ISiloLifecycle lifecycle)
    {
        lifecycle.Subscribe(
            observerName: $"{nameof(SiloLifetime)}.{nameof(Started)}",
            stage: ServiceLifecycleStage.Active,
            onStart: _started.NotifyCompleted,
            onStop: _ => Task.CompletedTask);

        lifecycle.Subscribe(
            observerName: $"{nameof(SiloLifetime)}.{nameof(Stopping)}",
            stage: ServiceLifecycleStage.Active,
            onStart: _ => Task.CompletedTask,
            onStop: _stopping.NotifyCompleted);

        lifecycle.Subscribe(
            observerName: $"{nameof(SiloLifetime)}.{nameof(Stopped)}",
            stage: ServiceLifecycleStage.RuntimeInitialize - 1,
            onStart: _ => Task.CompletedTask,
            onStop: _stopped.NotifyCompleted);
    }

    private class SiloLifecycleStage(ILogger logger, string name) : ISiloLifecycleStage
    {
        // We use this so that late registrations can still be executed, otherwise
        // we'd need to rely on the TCS which means we'd need to set it *before* the callbacks
        // have been executed, ideally we should fire the TCS only after non-late registered callbacks have completed.
        private bool _isNotifyingOrHasCompleted;

        private readonly object _lock = new();
        private readonly List<StageParticipant> _participants = [];
        private readonly CancellationTokenSource _cts = new();
        private readonly TaskCompletionSource _tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Task => _tcs.Task;
        public CancellationToken Token => _cts.Token;

        public IDisposable Register(Func<object?, CancellationToken, Task> callback, object? state, bool terminateOnError)
        {
            ArgumentNullException.ThrowIfNull(callback);

            var participant = new StageParticipant(this, callback, state, terminateOnError);

            lock (_lock)
            {
                if (_isNotifyingOrHasCompleted)
                {
                    logger.LogInformation("Lifecycle stage = '{StageName}' has already completed. Executing callback immediately.", name);

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

                    await participant.Execute(CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Error executing late-registered callback for silo lifecycle stage = '{StageName}'", name);
                }
            }
        }

        public async Task NotifyCompleted(CancellationToken cancellationToken)
        {
            Debug.Assert(!_isNotifyingOrHasCompleted, "This should not be called twice!");

            List<StageParticipant> snapshot;

            lock (_lock)
            {
                _isNotifyingOrHasCompleted = true;
                snapshot = [.. _participants];
            }

            // If we took the snapshot, no further registrations will be account for the sequential invocations.
            // Late registrations will still be executed though, just not in order anymore.

            var exceptions = new List<Exception>();

            foreach (var participant in snapshot)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                try
                {
                    // TODO: Not sure if we should WaitAsync in case the users ignore the token we pass.
                    // Or if we should wait indefinatly until the callback executes?!

                    await participant.Execute(cancellationToken).WaitAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    // Ignore, this is expected during hard shutdown.
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Error executing callback for silo lifecycle = '{StageName}'", name);

                    exceptions.Add(ex);

                    if (participant.TerminateOnError)
                    { 
                        // We signal cancellation so any BG-running participants that observe the stage can stop.
                        CancelStage();

                        // In case there was a previous error but the participant chose to not terminate, we include that error aswell.
                        var aggException = new AggregateException(exceptions);

                        _tcs.SetException(aggException);

                        throw aggException;
                    }
                }
            }

            CancelStage();

            if (exceptions.Count > 0)
            {
                _tcs.SetException(new AggregateException(exceptions));
            }
            else
            {
                _tcs.SetResult();
            }
        }

        private void CancelStage()
        {
            try
            {
                _cts.Cancel();
            }
            catch (Exception ex)
            {
                // Should not happen if callers respect the contract to register callbacks with the proper method, but it can happen!
                logger.LogError(ex, "An exception occurred inside a CancellationToken callback for stage = '{StageName}'", name);
            }
        }

        private void Unregister(StageParticipant participant)
        {
            lock (_lock)
            {
                _participants.Remove(participant);
            }
        }

        private record StageParticipant(SiloLifecycleStage Stage,
            Func<object?, CancellationToken, Task> Callback, object? State, bool TerminateOnError) : IDisposable
        {
            public Task Execute(CancellationToken cancellationToken) => Callback(State, cancellationToken);
            void IDisposable.Dispose() => Stage.Unregister(this);
        }
    }
}
