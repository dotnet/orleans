using System;

namespace Orleans.Configuration;

/// <summary>
/// Options that apply globally to stateless worker grains.
/// </summary>
public class StatelessWorkerOptions
{
    /// <summary>
    /// When set to <see langword="true"/>, all stateless worker grains will use proactive worker collection.
    /// This means that the runtime will proactively deactivate idle workers. Otherwise if <see langword="false"/>, than the
    /// workers will be deactivated according to <see cref="GrainCollectionOptions.CollectionAge"/>.
    /// </summary>
    /// <remarks>You can read more on this <see href="https://www.ledjonbehluli.com/posts/orleans_adaptive_stateless_worker/">here</see></remarks>
    public bool UseProactiveWorkerCollection { get; set; } = DEFAULT_USE_PROACTIVE_WORKER_COLLECTION;

    /// <summary>
    /// The default value for <see cref="UseProactiveWorkerCollection"/>.
    /// </summary>
    public const bool DEFAULT_USE_PROACTIVE_WORKER_COLLECTION = false;

    /// <summary>
    /// The minimum time between consecutive worker collections.
    /// </summary>
    /// <remarks>This setting has no effect if <see cref="UseProactiveWorkerCollection"/> is <see langword="false"/>.</remarks>
    public TimeSpan ProactiveWorkerCollectionBackoffPeriod { get; set; } = DEFAULT_PROACTIVE_WORKER_COLLECTION_BACKOFF_PERIOD;

    /// <summary>
    /// The default value for <see cref="ProactiveWorkerCollectionBackoffPeriod"/>.
    /// </summary>
    public static readonly TimeSpan DEFAULT_PROACTIVE_WORKER_COLLECTION_BACKOFF_PERIOD = TimeSpan.FromSeconds(1);
}