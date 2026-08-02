using System;
using System.Threading;
using System.Threading.Tasks;

namespace Orleans.DurableJobs;

/// <summary>
/// Registers framework-owned durable job handlers for a grain activation.
/// </summary>
public interface IDurableJobHandlerRegistry
{
    /// <summary>
    /// Registers <paramref name="handler"/> for jobs with the supplied name.
    /// </summary>
    void Register(string jobName, IDurableJobFeatureHandler handler);
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

    internal bool TryGetHandler(string jobName, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out IDurableJobFeatureHandler? handler) =>
        _handlers.TryGetValue(jobName, out handler);
}
