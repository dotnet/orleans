using System;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Orleans.Runtime;

namespace Orleans.DurableJobs;

/// <summary>
/// Registers framework-owned durable job handlers for a grain activation.
/// </summary>
/// <remarks>
/// Registrations are activation-scoped. A matching feature handler takes precedence over
/// <see cref="IDurableJobHandler"/>. Multiple matching feature handlers are rejected as ambiguous.
/// The DurableJobs dependency injection registration is an infrastructure service and does not support
/// replacement or decoration. Replacing it is rejected explicitly when DurableJobs is configured or the
/// receiver is resolved so that registrations cannot be silently disconnected from dispatch.
/// </remarks>
public interface IDurableJobHandlerRegistry
{
    /// <summary>
    /// Registers <paramref name="handler"/> for this grain activation.
    /// </summary>
    /// <param name="handler">The activation-scoped feature handler.</param>
    /// <exception cref="InvalidOperationException">The handler is already registered.</exception>
    void Register(IDurableJobFeatureHandler handler);

    /// <summary>
    /// Registers <paramref name="handler"/> for this grain activation.
    /// </summary>
    /// <param name="handler">The activation-scoped feature handler.</param>
    /// <param name="requiresTurnIsolation">
    /// <see langword="true"/> to execute every handler poll under an activation-level exclusive turn lease.
    /// If the handler returns <see cref="DurableJobRunStatus.InProgress"/>, the lease is released between polls.
    /// </param>
    /// <exception cref="InvalidOperationException">The handler is already registered.</exception>
    void Register(IDurableJobFeatureHandler handler, bool requiresTurnIsolation) => Register(handler);
}

/// <summary>
/// Handles a framework-owned durable job for a grain activation.
/// </summary>
/// <remarks>
/// Delivery is at least once. The handler determines the durable job disposition and owns any
/// application-specific idempotency required when a completed invocation is delivered again.
/// </remarks>
public interface IDurableJobFeatureHandler
{
    /// <summary>
    /// Determines whether this handler handles the supplied durable job.
    /// </summary>
    /// <remarks>
    /// Implementations must be deterministic and side-effect free. This method can be called more than once
    /// for the same durable job name.
    /// </remarks>
    /// <param name="jobName">The durable job name.</param>
    /// <returns><see langword="true"/> when this handler handles the job name; otherwise, <see langword="false"/>.</returns>
    bool CanHandle(string jobName);

    /// <summary>
    /// Executes a durable job and returns its explicit disposition.
    /// </summary>
    /// <param name="context">The durable job execution context.</param>
    /// <param name="attemptCancellationToken">
    /// A token which cooperatively requests cancellation of the current execution attempt.
    /// The durable job remains eligible for another attempt.
    /// </param>
    /// <returns>The completed, polling, or failed disposition.</returns>
    ValueTask<DurableJobRunResult> ExecuteJobAsync(IJobRunContext context, CancellationToken attemptCancellationToken);
}

internal interface IDurableJobHandlerLookup
{
    CancellationToken ExecutionToken { get; }

    Task<TResult> StartExecution<TResult>(
        Func<CancellationToken, Task<TResult>> factory,
        bool holdTurnIsolation);

    bool TryGetHandler(string jobName, [NotNullWhen(true)] out IDurableJobFeatureHandler? handler);

    bool TryGetIsolatedHandler(string jobName, [NotNullWhen(true)] out IDurableJobFeatureHandler? handler);

    void EnableTurnIsolation() { }
}

internal sealed class DurableJobHandlerRegistry : IDurableJobHandlerRegistry, IDurableJobHandlerLookup
{
    private readonly List<Registration> _handlers = [];
    private readonly DurableJobTurnIsolation? _turnIsolation;
    private readonly DurableJobExecutionLifetime? _lifetime;

    public DurableJobHandlerRegistry(
        DurableJobTurnIsolation? turnIsolation = null,
        DurableJobExecutionLifetime? lifetime = null)
    {
        _turnIsolation = turnIsolation;
        _lifetime = lifetime;
    }

    public CancellationToken ExecutionToken => _lifetime?.Token ?? CancellationToken.None;

    public Task<TResult> StartExecution<TResult>(
        Func<CancellationToken, Task<TResult>> factory,
        bool holdTurnIsolation) =>
        _lifetime?.Start(token => StartExecutionCore(factory, token, holdTurnIsolation))
        ?? StartExecutionCore(factory, CancellationToken.None, holdTurnIsolation);

    private async Task<TResult> StartExecutionCore<TResult>(
        Func<CancellationToken, Task<TResult>> factory,
        CancellationToken cancellationToken,
        bool holdTurnIsolation)
    {
        if (!holdTurnIsolation || _turnIsolation is null)
        {
            return await factory(cancellationToken);
        }

        using var lease = await _turnIsolation.EnterOrdinaryAsync(cancellationToken);
        lease.Activate();
        return await factory(cancellationToken);
    }

    public void Register(IDurableJobFeatureHandler handler) => Register(handler, requiresTurnIsolation: false);

    public void Register(IDurableJobFeatureHandler handler, bool requiresTurnIsolation)
    {
        ArgumentNullException.ThrowIfNull(handler);

        foreach (var registeredHandler in _handlers)
        {
            if (ReferenceEquals(registeredHandler.Handler, handler))
            {
                throw new InvalidOperationException("The durable job feature handler is already registered.");
            }
        }

        _handlers.Add(new(handler, requiresTurnIsolation));
        if (requiresTurnIsolation)
        {
            _turnIsolation?.Enable();
        }
    }

    public bool TryGetHandler(string jobName, [NotNullWhen(true)] out IDurableJobFeatureHandler? handler) =>
        TryGetHandler(jobName, requiresTurnIsolation: false, out handler);

    public bool TryGetIsolatedHandler(string jobName, [NotNullWhen(true)] out IDurableJobFeatureHandler? handler) =>
        TryGetHandler(jobName, requiresTurnIsolation: true, out handler);

    public void EnableTurnIsolation() => _turnIsolation?.Enable();

    private bool TryGetHandler(
        string jobName,
        bool requiresTurnIsolation,
        [NotNullWhen(true)] out IDurableJobFeatureHandler? handler)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobName);

        Registration? match = null;
        foreach (var candidate in _handlers)
        {
            if (!candidate.Handler.CanHandle(jobName))
            {
                continue;
            }

            if (match is not null)
            {
                throw new InvalidOperationException(
                    $"Multiple durable job feature handlers match job '{jobName}': "
                    + $"'{match.Value.Handler.GetType().FullName}' and '{candidate.Handler.GetType().FullName}'.");
            }

            match = candidate;
        }

        if (match is { } registration && registration.RequiresTurnIsolation == requiresTurnIsolation)
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
        CancellationToken attemptCancellationToken);
}

internal sealed partial class DurableJobFeatureReceiverExtension : IDurableJobFeatureReceiverExtension
{
    private const int MaxCompletedAttempts = 65_536;

    private readonly IDurableJobHandlerLookup _handlers;
    private readonly DurableJobReceiverExtensionShared _shared;
    private readonly DurableJobTurnIsolation _turnIsolation;
    private readonly object _attemptLock = new();
    private readonly Dictionary<(string JobId, long ExecutionGeneration, int DequeueCount), DurableJobRunResult> _attempts = [];
    private readonly Queue<(string JobId, long ExecutionGeneration, int DequeueCount)> _completedAttempts = [];

    public DurableJobFeatureReceiverExtension(
        IDurableJobHandlerLookup handlers,
        DurableJobReceiverExtensionShared shared,
        DurableJobTurnIsolation? turnIsolation = null)
    {
        _handlers = handlers;
        _shared = shared;
        _turnIsolation = turnIsolation ?? new DurableJobTurnIsolation();
    }

    public async ValueTask<DurableJobRunResult?> TryHandleFeatureJobAsync(
        IJobRunContext context,
        CancellationToken attemptCancellationToken)
    {
        if (!_handlers.TryGetIsolatedHandler(context.Job.Name, out var handler))
        {
            return null;
        }

        var key = (context.Job.Id, context.Job.ExecutionGeneration, context.DequeueCount);
        lock (_attemptLock)
        {
            if (_attempts.TryGetValue(key, out var cached) && !cached.IsInProgress)
            {
                return cached;
            }
        }

        using var gateCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            attemptCancellationToken,
            _handlers.ExecutionToken);
        using var lease = await _turnIsolation.EnterIsolatedAsync(gateCancellation.Token);
        lease.Activate();
        lock (_attemptLock)
        {
            if (_attempts.TryGetValue(key, out var cached) && !cached.IsInProgress)
            {
                return cached;
            }
        }

        using var tracker = _shared.BeginHandlerExecution(context);
        try
        {
            var result = await _handlers.StartExecution(
                token => handler.ExecuteJobAsync(context, token).AsTask(),
                holdTurnIsolation: false)
                ?? throw new InvalidOperationException(
                    $"Durable job feature handler for '{context.Job.Name}' returned a null result.");
            CacheResult(key, result);
            tracker.RecordResult(result);
            return result;
        }
        catch (OperationCanceledException) when (_handlers.ExecutionToken.IsCancellationRequested)
        {
            tracker.AttemptCanceled();
            throw;
        }
        catch (Exception exception)
        {
            tracker.Failed(exception);
            LogErrorExecutingFeatureJob(_shared.Logger, exception, context.Job.Id, context.Job.TargetGrainId);
            var result = DurableJobRunResult.Failed(exception);
            CacheResult(key, result);
            return result;
        }
    }

    private void CacheResult(
        (string JobId, long ExecutionGeneration, int DequeueCount) key,
        DurableJobRunResult result)
    {
        lock (_attemptLock)
        {
            _attempts[key] = result;
            if (result.IsInProgress)
            {
                _attempts.Remove(key);
                return;
            }

            _completedAttempts.Enqueue(key);
            while (_completedAttempts.Count > MaxCompletedAttempts
                && _completedAttempts.TryDequeue(out var expired))
            {
                _attempts.Remove(expired);
            }
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
