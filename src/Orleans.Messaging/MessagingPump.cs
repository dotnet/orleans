using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Orleans.DurableJobs;
using Orleans.Journaling;
using Orleans.Runtime;

namespace Orleans.Messaging;

/// <summary>
/// Drives inbox processing for one activation.
/// </summary>
/// <remarks>
/// A single loop runs on the activation scheduler and services the inbox whenever it is signaled.
/// One durable job keeps waking the grain while the inbox holds work. Wake-ups may be early,
/// late, or duplicated: every pass derives the work to perform from current state.
/// The loop keeps the activation alive while a pass runs over queued work.
/// Work which only becomes due later relies on the durable job, so the activation can be collected while it waits.
/// </remarks>
internal sealed partial class MessagingPump : IDurableJobFeatureHandler, ILifecycleObserver
{
    internal const string JobName = "orleans.messaging.pump";
    private static readonly TimeSpan RecheckDelay = TimeSpan.FromSeconds(1);
    private readonly SingleWaiterAutoResetEvent _signal = new() { RunContinuationsAsynchronously = true };
    private readonly CancellationTokenSource _shutdown = new();
    private Inbox? _inbox;
    private Task? _loop;
    private TaskCompletionSource? _nextPass;
    private Task<DurableJob>? _scheduling;
    private bool _wakeupScheduled;
    private bool _keepingAlive;

    // Advances whenever a job run stops guaranteeing a future wake-up. A schedule requested in an earlier epoch may
    // belong to a job which has already run and completed, so it no longer guarantees a wake-up.
    private int _wakeupEpoch;
    private int _schedulingEpoch;

    public MessagingPump(IGrainContext grainContext, IJournaledStateManager stateManager, IDurableJobHandlerRegistry jobHandlers)
    {
        GrainContext = grainContext;
        StateManager = stateManager;
        Shared = grainContext.GetComponent<MessagingShared>()
            ?? throw new InvalidOperationException($"Messaging is not configured for grain type '{grainContext.GrainId.Type}'.");
        jobHandlers.Register(this);
        grainContext.ObservableLifecycle.Subscribe(nameof(MessagingPump), GrainLifecycleStage.Activate, this);

        // Recovery reports depth during SetupState, so releasing it there covers activations which fail before Activate.
        grainContext.ObservableLifecycle.Subscribe(nameof(MessagingPump), GrainLifecycleStage.SetupState, static _ => Task.CompletedTask, _ =>
        {
            _inbox?.ReleaseDepth();
            return Task.CompletedTask;
        });
    }

    public IGrainContext GrainContext { get; }

    public IJournaledStateManager StateManager { get; }

    public MessagingShared Shared { get; }

    /// <summary>Gets the token which is canceled when messaging stops for this activation.</summary>
    public CancellationToken ShutdownToken => _shutdown.Token;

    internal void Register(Inbox inbox) => _inbox = inbox;

    private Inbox Inbox => _inbox ?? throw new InvalidOperationException("The messaging inbox has not been registered.");

    public bool CanHandle(string jobName) => string.Equals(jobName, JobName, StringComparison.Ordinal);

    /// <summary>Requests a processing pass.</summary>
    public void Signal()
    {
        if (!_shutdown.IsCancellationRequested) _signal.Signal();
    }

    /// <summary>Starts the loop once the activation is servicing grain calls.</summary>
    public void EnsureStarted()
    {
        if (_loop is null && !_shutdown.IsCancellationRequested)
        {
            _loop = RunAsync();
        }
    }

    /// <summary>Ensures a durable job will wake this grain while messaging work remains.</summary>
    public async ValueTask EnsureWakeupAsync(CancellationToken cancellationToken)
    {
        if (_wakeupScheduled) return;
        if (_scheduling is null || _schedulingEpoch != _wakeupEpoch)
        {
            _schedulingEpoch = _wakeupEpoch;
            _scheduling = Shared.JobManager.ScheduleJobAsync(new ScheduleJobRequest
            {
                Target = GrainContext.GrainId,
                JobName = JobName,
                DueTime = Shared.JobTimeProvider.GetUtcNow()
            }, _shutdown.Token);
        }

        var scheduling = _scheduling;
        var epoch = _schedulingEpoch;
        try
        {
            await scheduling.WaitAsync(cancellationToken).ConfigureAwait(true);
            if (epoch == _wakeupEpoch) _wakeupScheduled = true;
        }
        finally
        {
            if (scheduling.IsCompleted && ReferenceEquals(_scheduling, scheduling)) _scheduling = null;
        }
    }

    public async ValueTask<DurableJobRunResult> ExecuteJobAsync(IJobRunContext context, CancellationToken cancellationToken)
    {
        // A running job keeps rescheduling itself until the inbox is empty.
        _wakeupScheduled = true;
        EnsureStarted();
        var pass = _nextPass ??= new(TaskCreationOptions.RunContinuationsAsynchronously);
        _signal.Signal();
        try
        {
            await pass.Task.WaitAsync(cancellationToken).ConfigureAwait(true);
        }
        catch
        {
            // This attempt no longer guarantees a future wake-up.
            ClearWakeup();
            if (!_shutdown.IsCancellationRequested && Inbox.Count > 0)
            {
                RepairWakeupAsync().Ignore();
            }

            throw;
        }

        var now = Shared.JobTimeProvider.GetUtcNow();
        if (Inbox.GetNextWakeupDelay(now) is not { } delay)
        {
            ClearWakeup();
            return DurableJobRunResult.Completed;
        }

        return DurableJobRunResult.RescheduleAt(
            MessagingTime.AddClamped(now, delay > TimeSpan.Zero ? delay : RecheckDelay));
    }

    private void ClearWakeup()
    {
        _wakeupScheduled = false;
        _wakeupEpoch++;
    }

    private async Task RepairWakeupAsync()
    {
        try
        {
            await EnsureWakeupAsync(CancellationToken.None).ConfigureAwait(true);
        }
        catch (Exception exception) when (!_shutdown.IsCancellationRequested)
        {
            // Recovery re-establishes the wake-up for remaining work.
            Fail(exception, "Scheduling the messaging wake-up failed.");
        }
    }

    private async Task RunAsync()
    {
        // Callers start the loop from grain calls and job executions; queue the first pass to the activation
        // scheduler so it never runs on, and nests within, the caller's stack.
        await Task.Yield();
        var cancellationToken = _shutdown.Token;
        while (!cancellationToken.IsCancellationRequested)
        {
            SetKeepAlive(Inbox.Count > 0);
            var pass = _nextPass;
            _nextPass = null;
            var progress = false;
            try
            {
                progress = await Inbox.ProcessReadyAsync(cancellationToken).ConfigureAwait(true);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
            catch (Exception exception)
            {
                // Expected failures are handled within the pass, so an escaping exception leaves state inconsistent with the journal.
                progress = false;
                LogPassFailed(Shared.PumpLogger, exception, GrainContext.GrainId);
                Fail(exception, "Messaging processing pass failed.");
            }

            pass?.TrySetResult();
            if (!progress)
            {
                SetKeepAlive(false);
                await _signal.WaitAsync().ConfigureAwait(true);
            }
        }

        SetKeepAlive(false);
        _nextPass?.TrySetCanceled(cancellationToken);
    }

    private void SetKeepAlive(bool keepAlive)
    {
        if (keepAlive == _keepingAlive || GrainContext is not IActivationKeepAlive activation) return;
        _keepingAlive = keepAlive;
        if (keepAlive)
        {
            activation.AcquireKeepAlive();
        }
        else
        {
            activation.ReleaseKeepAlive();
        }
    }

    public async Task OnStart(CancellationToken cancellationToken)
    {
        await Inbox.StartAsync(cancellationToken).ConfigureAwait(true);
        if (Inbox.Count > 0)
        {
            await EnsureWakeupAsync(cancellationToken).ConfigureAwait(true);
        }
    }

    public async Task OnStop(CancellationToken cancellationToken)
    {
        var scheduling = _scheduling;
        Stop();

        // Grain calls finish before deactivation stops the lifecycle, so the loop owns every remaining write.
        if (_loop is { } loop)
        {
            await loop.ConfigureAwait(ConfigureAwaitOptions.ContinueOnCapturedContext | ConfigureAwaitOptions.SuppressThrowing);
        }

        if (scheduling is not null)
        {
            await ((Task)scheduling).ConfigureAwait(ConfigureAwaitOptions.ContinueOnCapturedContext | ConfigureAwaitOptions.SuppressThrowing);
        }
    }

    /// <summary>Stops processing for this activation.</summary>
    internal void Stop()
    {
        if (_shutdown.IsCancellationRequested) return;
        try
        {
            _shutdown.Cancel();
        }
        catch (AggregateException exception)
        {
            LogCancellationFailed(Shared.PumpLogger, exception);
        }
        finally
        {
            _signal.Signal();
        }
    }

    /// <summary>Stops messaging and deactivates the grain after an unrecoverable failure.</summary>
    internal void Fail(Exception exception, string description)
    {
        if (_shutdown.IsCancellationRequested) return;
        Stop();
        try
        {
            GrainContext.Deactivate(new DeactivationReason(DeactivationReasonCode.ApplicationError, exception, description));
        }
        catch (Exception deactivationException)
        {
            LogDeactivationFailed(Shared.PumpLogger, deactivationException);
        }
    }


    [LoggerMessage(Level = LogLevel.Error, Message = "Messaging processing pass failed for grain {GrainId}")]
    private static partial void LogPassFailed(ILogger logger, Exception exception, GrainId grainId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Requesting deactivation after a messaging failure failed.")]
    private static partial void LogDeactivationFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "A messaging cancellation callback failed during shutdown.")]
    private static partial void LogCancellationFailed(ILogger logger, Exception exception);
}
