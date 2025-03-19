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
    /// The time to inspect for idle workers.
    /// </summary>
    /// <remarks>This setting has no effect if <see cref="RemoveIdleWorkers"/> is <see langword="false"/>.</remarks>
    public TimeSpan IdleWorkersInspectionPeriod { get; set; } = DEFAULT_IDLE_WORKERS_INSPECTION_PERIOD;

    /// <summary>
    /// The default value for <see cref="IdleWorkersInspectionPeriod"/>.
    /// </summary>
    public static readonly TimeSpan DEFAULT_IDLE_WORKERS_INSPECTION_PERIOD = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// The minumun, consecutive number of idle cycles any given worker must exibit before it is deemed enough to remove the worker.
    /// </summary>
    public int MinIdleCyclesBeforeRemoval { get; set; }

    /// <summary>
    /// The default value for <see cref="MinIdleCyclesBeforeRemoval"/>.
    /// </summary>
    public const int DEFAULT_MIN_IDLE_CYCLES_BEFORE_REMOVAL = 3;
}