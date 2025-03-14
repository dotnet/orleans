using System;

namespace Orleans.Configuration;

/// <summary>
/// Options that apply globally to stateless worker grains.
/// </summary>
public class StatelessWorkerOptions
{
    /// <summary>
    /// When set to <see langword="true"/>, idle workers will be proactively deactivated by the runtime.
    /// Otherwise if <see langword="false"/>, than the workers will be deactivated according to <see cref="GrainCollectionOptions.CollectionAge"/>.
    /// </summary>
    /// <remarks>You can read more on this <see href="https://www.ledjonbehluli.com/posts/orleans_adaptive_stateless_worker/">here</see></remarks>
    public bool RemoveIdleWorkers { get; set; } = DEFAULT_REMOVE_IDLE_WORKERS;

    /// <summary>
    /// The default value for <see cref="RemoveIdleWorkers"/>.
    /// </summary>
    public const bool DEFAULT_REMOVE_IDLE_WORKERS = true;

    /// <summary>
    /// The minimum time between consecutive worker collections.
    /// </summary>
    /// <remarks>This setting has no effect if <see cref="RemoveIdleWorkers"/> is <see langword="false"/>.</remarks>
    public TimeSpan RemoveIdleWorkersBackoffPeriod { get; set; } = DEFAULT_REMOVE_IDLE_WORKERS_BACKOFF_PERIOD;

    /// <summary>
    /// The default value for <see cref="RemoveIdleWorkersBackoffPeriod"/>.
    /// </summary>
    public static readonly TimeSpan DEFAULT_REMOVE_IDLE_WORKERS_BACKOFF_PERIOD = TimeSpan.FromSeconds(1);
}