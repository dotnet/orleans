using System;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;

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
    bool TryGetHandler(string jobName, [NotNullWhen(true)] out IDurableJobFeatureHandler? handler);
}

internal sealed class DurableJobHandlerRegistry : IDurableJobHandlerRegistry, IDurableJobHandlerLookup
{
    private readonly List<IDurableJobFeatureHandler> _handlers = [];

    public void Register(IDurableJobFeatureHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);

        foreach (var registeredHandler in _handlers)
        {
            if (ReferenceEquals(registeredHandler, handler))
            {
                throw new InvalidOperationException("The durable job feature handler is already registered.");
            }
        }

        _handlers.Add(handler);
    }

    public bool TryGetHandler(string jobName, [NotNullWhen(true)] out IDurableJobFeatureHandler? handler)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobName);

        handler = null;
        foreach (var candidate in _handlers)
        {
            if (!candidate.CanHandle(jobName))
            {
                continue;
            }

            if (handler is not null)
            {
                throw new InvalidOperationException(
                    $"Multiple durable job feature handlers match job '{jobName}': "
                    + $"'{handler.GetType().FullName}' and '{candidate.GetType().FullName}'.");
            }

            handler = candidate;
        }

        return handler is not null;
    }
}
