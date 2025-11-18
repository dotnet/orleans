using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
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
    /// <remarks>
    /// Calling <see cref="ISiloLifecycleStage.Register"/> in this stage will throw a <see cref="NotSupportedException"/>.
    /// </remarks>
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
    /// <see cref="Register(Func{CancellationToken, Task})"/> instead.</remarks>
    CancellationToken CancellationToken { get; }

    /// <summary>
    /// Registers a callback to be executed during this lifecycle stage.
    /// </summary>
    /// <param name="callback">
    /// <para>The asynchronous operation to perform.</para>
    /// <para><strong>Never <c>await</c> <see cref="Task"/></strong> for a callback as it will result in a deadlock!</para>
    /// </param>
    /// <remarks>
    /// Disposing the returned value removes the callback from the lifecycle stage.
    /// This is useful for components that have a shorter lifespan than the silo to prevent holding onto the reference,
    /// and ensure that cleanup logic is not executed for components that are no longer active.
    /// </remarks>
    IDisposable Register(Func<CancellationToken, Task> callback);
}

internal sealed class SiloLifetime(ILogger<SiloLifetime> logger) : ISiloLifetime, ILifecycleParticipant<ISiloLifecycle>
{
    private readonly SiloLifecycleStage _started = new ReadOnlySiloLifecycleStage(logger, "Started");
    private readonly SiloLifecycleStage _stopping = new SiloLifecycleStage(logger, "Stopping");
    private readonly SiloLifecycleStage _stopped = new SiloLifecycleStage(logger, "Stopped");

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

    private class ReadOnlySiloLifecycleStage(ILogger logger, string name) : SiloLifecycleStage(logger, name)
    {
        private readonly string _name = name;

        public override IDisposable Register(Func<CancellationToken, Task> callback) =>
            throw new NotSupportedException($"Registering callbacks for '{_name}' is not allowed. This event is for observation purposes only.");
    }

    private class SiloLifecycleStage(ILogger logger, string name) : ISiloLifecycleStage
    {
        // We use this so that late registrations can still be executed, otherwise
        // we'd need to rely on the TCS which means we'd need to set it *before* the callbacks
        // have been executed, ideally we should fire the TCS only after non-late registered callbacks have completed.
        private bool _isNotifyingOrHasCompleted;

        private readonly object _lock = new();
        private readonly List<Func<CancellationToken, Task>> _callbacks = [];
        private readonly CancellationTokenSource _cts = new();
        private readonly TaskCompletionSource _tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Task => _tcs.Task;
        public CancellationToken CancellationToken => _cts.Token;

        public virtual IDisposable Register(Func<CancellationToken, Task> callback)
        {
            ArgumentNullException.ThrowIfNull(callback);

            lock (_lock)
            {
                if (_isNotifyingOrHasCompleted)
                {
                    logger.LogInformation("Lifecycle stage = '{StageName}' has already completed. Executing callback immediately.", name);

                    // TODO: Maybe we should just throw?!
                    _ = Task.Run(() => ExecuteLateCallback(callback));

                    return new DisposableCallback(this, callback);
                }

                _callbacks.Add(callback);
            }

            return new DisposableCallback(this, callback);

            async Task ExecuteLateCallback(Func<CancellationToken, Task> callback)
            {
                try
                {
                    // The original token passed to NotifyCompleted (typically related to the silo startup/shutdown) must be "gone" by now.
                    // Since the stage has already completed, there is no impending timeout for this late registration, so we pass None to the callback.

                    await callback(CancellationToken.None).ConfigureAwait(false);
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

            List<Func<CancellationToken, Task>> snapshot;

            lock (_lock)
            {
                _isNotifyingOrHasCompleted = true;
                snapshot = [.. _callbacks];
            }

            // If we took the snapshot, no further registrations will be account for the sequential invocations.
            // Late registrations will still be executed though, just not in order anymore.

            var exceptions = new List<Exception>();

            foreach (var callback in snapshot)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                try
                {
                    // TODO: Not sure if we should WaitAsync in case the users ignore the token we pass.
                    // Or if we should wait indefinatly until the callback executes?!

                    await callback(cancellationToken).WaitAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    // Ignore, this is expected during hard shutdown.
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Error executing callback for silo lifecycle = '{StageName}'", name);
                    exceptions.Add(ex);
                }
            }

            try
            {
                _cts.Cancel();
            }
            catch (Exception ex)
            {
                // Should not happen if callers respect the contract to register callbacks with the proper method, but it can happen!
                logger.LogError(ex, "An exception occurred inside a CancellationToken callback for stage = '{StageName}'", name);

                // TODO: Should we add this exception to the list?!
                // I mean callers are not support to register callbacks with the token
            }

            if (exceptions.Count > 0)
            {
                // TODO: Maybe we should just log if we are shutting down!
                _tcs.SetException(new AggregateException(exceptions));
            }
            else
            {
                _tcs.SetResult();
            }
        }

        private void Unregister(Func<CancellationToken, Task> callback)
        {
            lock (_lock)
            {
                _callbacks.Remove(callback);
            }
        }

        private class DisposableCallback(SiloLifecycleStage stage, Func<CancellationToken, Task> callback) : IDisposable
        {
            public void Dispose() => stage.Unregister(callback);
        }
    }
}
