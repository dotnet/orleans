using System;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;

namespace Orleans.DurableJobs;

/// <summary>
/// Registers framework-owned durable job handlers for a grain activation.
/// </summary>
/// <remarks>
/// Registrations are activation-scoped and job names are compared using <see cref="StringComparer.Ordinal"/>.
/// A registered feature handler takes precedence over <see cref="IDurableJobHandler"/> for the same job name.
/// The DurableJobs dependency injection registration is an infrastructure service and does not support
/// replacement or decoration. Replacing it is rejected explicitly when DurableJobs is configured or the
/// receiver is resolved so that registrations cannot be silently disconnected from dispatch.
/// </remarks>
public interface IDurableJobHandlerRegistry
{
    /// <summary>
    /// Registers <paramref name="handler"/> for jobs with the supplied name.
    /// </summary>
    /// <param name="jobName">The case-sensitive durable job name.</param>
    /// <param name="handler">The activation-scoped feature handler.</param>
    /// <exception cref="InvalidOperationException">A handler is already registered for <paramref name="jobName"/>.</exception>
    void Register(string jobName, IDurableJobFeatureHandler handler);
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
    private readonly Dictionary<string, IDurableJobFeatureHandler> _handlers = new(StringComparer.Ordinal);

    public void Register(string jobName, IDurableJobFeatureHandler handler)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobName);
        ArgumentNullException.ThrowIfNull(handler);

        if (!_handlers.TryAdd(jobName, handler))
        {
            throw new InvalidOperationException($"A durable job feature handler is already registered for '{jobName}'.");
        }
    }

    public bool TryGetHandler(string jobName, [NotNullWhen(true)] out IDurableJobFeatureHandler? handler) =>
        _handlers.TryGetValue(jobName, out handler);
}
