using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Orleans.DurableJobs;
using Orleans.Journaling;
using Orleans.Runtime;

namespace Orleans.Messaging;

/// <summary>
/// Drives inbox processing and outbox delivery for one activation.
/// </summary>
/// <remarks>
/// A single loop runs on the activation scheduler and services both queues whenever it is signaled.
/// One durable job keeps waking the grain while either queue or a registered <see cref="IInboxWorkSource"/> holds work
/// which the activation can perform; inbox messages wait for a handler without a wake-up.
/// Work staged with an earlier due time than the current wake-up schedules an earlier one, and the job which a newer,
/// earlier wake-up supersedes completes when it runs.
/// Wake-ups may be early, late, or duplicated: every pass derives the work to perform from current state.
/// Wake-up bookkeeping runs on the activation scheduler: journal writes prepare on it and serialize with job runs.
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
    private Outbox? _outbox;
    private Task? _loop;
    private TaskCompletionSource? _nextPass;
    private Task<DurableJob>? _scheduling;
    private DateTimeOffset _schedulingDueTime;
    private bool _keepingAlive;

    // The job which wakes the grain next, if any, and its due time. A job run which completes clears it.
    private string? _wakeupJobId;
    private DateTimeOffset _wakeupDueTime;

    // Advances whenever a job run completes. A schedule requested in an earlier epoch may belong to a job which has
    // already run and completed, so it no longer guarantees a wake-up.
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
            _outbox?.ReleaseDepth();
            return Task.CompletedTask;
        });
    }

    public IGrainContext GrainContext { get; }

    public IJournaledStateManager StateManager { get; }

    public MessagingShared Shared { get; }

    /// <summary>Gets the token which is canceled when messaging stops for this activation.</summary>
    public CancellationToken ShutdownToken => _shutdown.Token;

    internal void Register(Inbox inbox) => _inbox = inbox;

    internal void Register(Outbox outbox) => _outbox = outbox;

    private Inbox Inbox => _inbox ?? throw new InvalidOperationException("The messaging inbox has not been registered.");

    private Outbox Outbox => _outbox ?? throw new InvalidOperationException("The messaging outbox has not been registered.");

    public bool CanHandle(string jobName) => string.Equals(jobName, JobName, StringComparison.Ordinal);

    /// <summary>Requests a processing pass.</summary>
    public void Signal()
    {
        if (!_shutdown.IsCancellationRequested) _signal.Signal();
    }

    /// <summary>Requests a processing pass and ensures a durable wake-up for work which has become processable.</summary>
    public void RequestWakeup()
    {
        if (_shutdown.IsCancellationRequested) return;
        _signal.Signal();
        RepairWakeupAsync().Ignore();
    }

    /// <summary>Starts the loop once the activation is servicing grain calls.</summary>
    public void EnsureStarted()
    {
        if (_loop is null && !_shutdown.IsCancellationRequested)
        {
            _loop = RunAsync();
        }
    }

    /// <summary>
    /// Ensures a durable job will wake this grain no later than <paramref name="dueTime"/>, on the Durable Jobs clock.
    /// </summary>
    public async ValueTask EnsureWakeupAsync(DateTimeOffset dueTime, CancellationToken cancellationToken)
    {
        // Work which is already due needs a wake-up due now, which any tracked or scheduling wake-up due by now satisfies.
        var now = Shared.JobTimeProvider.GetUtcNow();
        if (dueTime < now) dueTime = now;
        if (_wakeupJobId is not null && _wakeupDueTime <= dueTime) return;
        if (_scheduling is null || _schedulingEpoch != _wakeupEpoch || _schedulingDueTime > dueTime)
        {
            _schedulingEpoch = _wakeupEpoch;
            _schedulingDueTime = dueTime;
            _scheduling = Shared.JobManager.ScheduleJobAsync(new ScheduleJobRequest
            {
                Target = GrainContext.GrainId,
                JobName = JobName,
                DueTime = _schedulingDueTime
            }, _shutdown.Token);
        }

        var scheduling = _scheduling;
        var epoch = _schedulingEpoch;
        try
        {
            var job = await scheduling.WaitAsync(cancellationToken).ConfigureAwait(true);
            if (epoch == _wakeupEpoch && (_wakeupJobId is null || job.DueTime < _wakeupDueTime))
            {
                _wakeupJobId = job.Id;
                _wakeupDueTime = job.DueTime;
            }
        }
        finally
        {
            if (scheduling.IsCompleted && ReferenceEquals(_scheduling, scheduling)) _scheduling = null;
        }
    }

    public async ValueTask<DurableJobRunResult> ExecuteJobAsync(IJobRunContext context, CancellationToken cancellationToken)
    {
        // A running job keeps rescheduling itself until both queues and every work source are empty.
        var jobId = context.Job.Id;
        if (_wakeupJobId is null)
        {
            _wakeupJobId = jobId;
            _wakeupDueTime = context.Job.DueTime;
        }
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
            CompleteWakeup(jobId);
            if (!_shutdown.IsCancellationRequested && (Inbox.HasWork || Outbox.Count > 0))
            {
                RepairWakeupAsync().Ignore();
            }

            throw;
        }

        var now = Shared.JobTimeProvider.GetUtcNow();
        var inboxDelay = Inbox.GetNextWakeupDelay(now);
        var outboxDelay = Outbox.GetNextWakeupDelay(now);
        if (inboxDelay is null && outboxDelay is null)
        {
            CompleteWakeup(jobId);
            return DurableJobRunResult.Completed;
        }

        var delay = Min(inboxDelay, outboxDelay);
        var next = MessagingTime.AddClamped(now, delay > TimeSpan.Zero ? delay : RecheckDelay);
        if (_wakeupJobId is not null && _wakeupJobId != jobId && _wakeupDueTime <= next)
        {
            // Another job wakes the grain first, and its run reschedules for any remaining work.
            CompleteWakeup(jobId);
            return DurableJobRunResult.Completed;
        }

        _wakeupJobId = jobId;
        _wakeupDueTime = next;
        return DurableJobRunResult.RescheduleAt(next);
    }

    private void CompleteWakeup(string jobId)
    {
        if (_wakeupJobId == jobId) _wakeupJobId = null;
        _wakeupEpoch++;
    }

    private static TimeSpan Min(TimeSpan? left, TimeSpan? right) =>
        left is { } l ? right is { } r && r < l ? r : l : right!.Value;

    private async Task RepairWakeupAsync()
    {
        try
        {
            await EnsureWakeupAsync(Shared.JobTimeProvider.GetUtcNow(), CancellationToken.None).ConfigureAwait(true);
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
                progress |= await Outbox.AdvanceAsync(cancellationToken).ConfigureAwait(true);
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
        await Outbox.StartAsync(cancellationToken).ConfigureAwait(true);
        await Inbox.StartAsync(cancellationToken).ConfigureAwait(true);
        if (Inbox.HasWork || Outbox.Count > 0)
        {
            await EnsureWakeupAsync(Shared.JobTimeProvider.GetUtcNow(), cancellationToken).ConfigureAwait(true);
        }
    }

    public async Task OnStop(CancellationToken cancellationToken)
    {
        var scheduling = _scheduling;
        Stop();

        // Grain calls finish before deactivation stops the lifecycle, so the loop and outbox deliveries own every remaining write.
        if (_loop is { } loop)
        {
            await loop.ConfigureAwait(ConfigureAwaitOptions.ContinueOnCapturedContext | ConfigureAwaitOptions.SuppressThrowing);
        }

        if (scheduling is not null)
        {
            await ((Task)scheduling).ConfigureAwait(ConfigureAwaitOptions.ContinueOnCapturedContext | ConfigureAwaitOptions.SuppressThrowing);
        }

        await Outbox.StopAsync().ConfigureAwait(true);
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
