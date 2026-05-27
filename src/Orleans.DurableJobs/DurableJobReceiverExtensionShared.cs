using System;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Hosting;

namespace Orleans.DurableJobs;

/// <summary>
/// Singleton dependencies shared by every <see cref="DurableJobReceiverExtension"/> instance on a silo.
/// Centralising these dependencies keeps the per-grain extension constructor small and provides a single
/// place to add additional cross-cutting helpers (such as factories for tracking-operation structs).
/// </summary>
internal sealed class DurableJobReceiverExtensionShared
{
    public DurableJobReceiverExtensionShared(
        ILogger<DurableJobReceiverExtension> logger,
        IOptions<DurableJobsOptions> options,
        IOptions<SiloMessagingOptions> messagingOptions,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(options);

        Logger = logger;
        MessagingOptions = messagingOptions.Value;
        Options = options.Value;
        TimeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// Gets the logger used by every <see cref="DurableJobReceiverExtension"/> instance on the silo.
    /// </summary>
    public ILogger<DurableJobReceiverExtension> Logger { get; }

    public SiloMessagingOptions MessagingOptions { get; }

    /// <summary>
    /// Gets the time provider used by every <see cref="DurableJobReceiverExtension"/> instance on the silo.
    /// </summary>
    public TimeProvider TimeProvider { get; }

    /// <summary>
    /// Gets the durable jobs options used by every <see cref="DurableJobReceiverExtension"/> instance on the silo.
    /// </summary>
    public DurableJobsOptions Options { get; }

    /// <summary>
    /// Begins tracking an invocation of <see cref="IDurableJobHandler.ExecuteJobAsync"/>, emitting the
    /// corresponding metrics and starting a distributed-tracing activity for the lifetime of the returned tracker.
    /// </summary>
    public HandlerExecutionTracker BeginHandlerExecution(IJobRunContext context) => new(TimeProvider, context);
}
