using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Orleans.Runtime;

namespace Orleans.DurableJobs;

/// <summary>
/// Registers framework-owned durable job handlers for a grain activation.
/// </summary>
public interface IDurableJobHandlerRegistry
{
    /// <summary>
    /// Registers <paramref name="handler"/> for jobs with the supplied name.
    /// </summary>
    /// <param name="requiresTurnIsolation">
    /// <see langword="true"/> to execute the handler as a complete grain turn. Turn-isolated handlers return a
    /// terminal or reschedule result and cannot return <see cref="DurableJobRunStatus.InProgress"/>.
    /// </param>
    void Register(string jobName, IDurableJobFeatureHandler handler, bool requiresTurnIsolation = false);
}

/// <summary>
/// Handles a framework-owned durable job.
/// </summary>
public interface IDurableJobFeatureHandler
{
    /// <summary>
    /// Executes a durable job and returns its explicit disposition.
    /// </summary>
    ValueTask<DurableJobRunResult> ExecuteJobAsync(IJobRunContext context, CancellationToken cancellationToken);
}

internal sealed class DurableJobHandlerRegistry : IDurableJobHandlerRegistry
{
    private readonly Dictionary<string, Registration> _handlers = new(StringComparer.Ordinal);

    public void Register(string jobName, IDurableJobFeatureHandler handler, bool requiresTurnIsolation = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobName);
        ArgumentNullException.ThrowIfNull(handler);

        if (!_handlers.TryAdd(jobName, new(handler, requiresTurnIsolation)))
        {
            throw new InvalidOperationException($"A durable job feature handler is already registered for '{jobName}'.");
        }
    }

    internal bool TryGetHandler(string jobName, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out IDurableJobFeatureHandler? handler) =>
        TryGetHandler(jobName, requiresTurnIsolation: null, out handler);

    internal bool TryGetIsolatedHandler(string jobName, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out IDurableJobFeatureHandler? handler) =>
        TryGetHandler(jobName, requiresTurnIsolation: true, out handler);

    private bool TryGetHandler(
        string jobName,
        bool? requiresTurnIsolation,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out IDurableJobFeatureHandler? handler)
    {
        if (_handlers.TryGetValue(jobName, out var registration)
            && (!requiresTurnIsolation.HasValue || registration.RequiresTurnIsolation == requiresTurnIsolation.Value))
        {
            handler = registration.Handler;
            return true;
        }

        handler = null;
        return false;
    }

    private readonly record struct Registration(
        IDurableJobFeatureHandler Handler,
        bool RequiresTurnIsolation);
}

[Alias("Orleans.DurableJobs.IDurableJobFeatureReceiverExtension")]
internal interface IDurableJobFeatureReceiverExtension : IGrainExtension
{
    ValueTask<DurableJobRunResult?> TryHandleFeatureJobAsync(
        IJobRunContext context,
        CancellationToken cancellationToken);
}

internal sealed partial class DurableJobFeatureReceiverExtension(
    DurableJobHandlerRegistry handlers,
    DurableJobReceiverExtensionShared shared) : IDurableJobFeatureReceiverExtension
{
    public async ValueTask<DurableJobRunResult?> TryHandleFeatureJobAsync(
        IJobRunContext context,
        CancellationToken cancellationToken)
    {
        if (!handlers.TryGetIsolatedHandler(context.Job.Name, out var handler))
        {
            return null;
        }

        using var tracker = shared.BeginHandlerExecution(context);
        try
        {
            var result = await handler.ExecuteJobAsync(context, cancellationToken)
                ?? throw new InvalidOperationException(
                    $"Durable job feature handler for '{context.Job.Name}' returned a null result.");
            if (result.IsInProgress)
            {
                throw new InvalidOperationException(
                    $"Turn-isolated durable job feature handler for '{context.Job.Name}' returned InProgress.");
            }
            tracker.Completed();
            return result;
        }
        catch (OperationCanceledException)
        {
            tracker.Canceled();
            throw;
        }
        catch (Exception exception)
        {
            tracker.Failed(exception);
            LogErrorExecutingFeatureJob(shared.Logger, exception, context.Job.Id, context.Job.TargetGrainId);
            return DurableJobRunResult.Failed(exception);
        }
    }

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Error executing durable feature job {JobId} on grain {GrainId}")]
    private static partial void LogErrorExecutingFeatureJob(
        ILogger logger,
        Exception exception,
        string jobId,
        GrainId grainId);
}
