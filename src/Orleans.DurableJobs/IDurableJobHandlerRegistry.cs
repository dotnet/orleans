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
/// Delivery is at least once. The receiving activation identifies an attempt by job ID, execution generation,
/// and dequeue count, suppressing duplicate deliveries for the configured completed-attempt retention period,
/// subject to its bounded cache. Application-specific durable state provides deduplication across activation
/// failure or migration.
/// </remarks>
public interface IDurableJobFeatureHandler
{
    /// <summary>
    /// Executes a durable job and returns its explicit disposition.
    /// </summary>
    /// <param name="context">The durable job execution context.</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>The completed, polling, or failed disposition.</returns>
    ValueTask<DurableJobRunResult> ExecuteJobAsync(IJobRunContext context, CancellationToken cancellationToken);
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
