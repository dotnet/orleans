using System;
using System.Diagnostics;

namespace Orleans.DurableJobs;

/// <summary>
/// Tracks a single invocation of <see cref="IDurableJobHandler.ExecuteJobAsync"/>, recording the
/// associated metrics via <see cref="DurableJobsInstruments"/> and the distributed-tracing activity
/// via <see cref="DurableJobsDiagnostics"/>. Callers must call exactly one of <see cref="Completed"/>,
/// <see cref="Canceled"/>, or <see cref="Failed"/> before disposal.
/// </summary>
internal readonly struct HandlerExecutionTracker : IDisposable
{
    private readonly TimeProvider _timeProvider;
    private readonly DurableJobsInstruments _durableJobsInstruments;
    private readonly long _startTimestamp;
    private readonly Activity? _activity;

    public HandlerExecutionTracker(TimeProvider timeProvider, DurableJobsInstruments durableJobsInstruments, IJobRunContext context)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(durableJobsInstruments);
        ArgumentNullException.ThrowIfNull(context);

        _timeProvider = timeProvider;
        _durableJobsInstruments = durableJobsInstruments;
        _startTimestamp = timeProvider.GetTimestamp();
        _durableJobsInstruments.OnHandlerExecutionStarted();
        _activity = DurableJobsDiagnostics.StartHandlerActivity(context.Job, context.DequeueCount, context.RunId);
    }

    /// <summary>
    /// Records a successful handler execution and marks the activity as <see cref="ActivityStatusCode.Ok"/>.
    /// </summary>
    public void Completed()
    {
        _durableJobsInstruments.OnHandlerExecutionCompleted(_timeProvider.GetElapsedTime(_startTimestamp));
        _activity?.SetStatus(ActivityStatusCode.Ok);
    }

    /// <summary>
    /// Records a canceled handler execution. The activity status is left unchanged so that
    /// disposal preserves the default (unset) status, matching the behaviour of the original implementation.
    /// </summary>
    public void Canceled()
    {
        _durableJobsInstruments.OnHandlerExecutionCanceled(_timeProvider.GetElapsedTime(_startTimestamp));
    }

    /// <summary>
    /// Records a failed handler execution and attaches the supplied exception to the activity.
    /// </summary>
    public void Failed(Exception exception)
    {
        _durableJobsInstruments.OnHandlerExecutionFailed(_timeProvider.GetElapsedTime(_startTimestamp));
        DurableJobsDiagnostics.SetError(_activity, exception);
    }

    public void Dispose() => _activity?.Dispose();
}
