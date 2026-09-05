namespace Orleans.Runtime;

/// <summary>
/// Defines functionality required for grain contexts which are subject to activation collection.
/// </summary>
internal interface ICollectibleGrainContext : IGrainContext
{
    /// <summary>
    /// Gets a value indicating whether the instance is exempt from collection.
    /// </summary>
    bool IsExemptFromCollection { get; }

    /// <summary>
    /// Gets the collection age limit, which defines how long an instance must be inactive before it is eligible for collection.
    /// </summary>
    TimeSpan CollectionAgeLimit { get; }

    /// <summary>
    /// Atomically evaluates collection eligibility and begins deactivation when the instance is eligible.
    /// </summary>
    /// <param name="reason">The reason for deactivation.</param>
    /// <param name="now">The current time.</param>
    /// <param name="ageLimit">The minimum idle duration.</param>
    /// <param name="respectKeepAlive">Whether a keep-alive request delays collection.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The collection action which the collector should perform.</returns>
    ActivationCollectionResult TryDeactivateForCollection(
        DeactivationReason reason,
        DateTime now,
        TimeSpan ageLimit,
        bool respectKeepAlive,
        CancellationToken cancellationToken);

    /// <summary>
    /// Delays activation collection until the specified duration has elapsed.
    /// </summary>
    /// <param name="timeSpan">The period to delay activation collection for.</param>
    void DelayDeactivation(TimeSpan timeSpan);
}

internal enum ActivationCollectionAction
{
    Remove,
    StartedDeactivation,
    Reschedule
}

internal readonly record struct ActivationCollectionResult(ActivationCollectionAction Action, TimeSpan RescheduleAfter)
{
    public static ActivationCollectionResult Remove { get; } = new(ActivationCollectionAction.Remove, default);

    public static ActivationCollectionResult StartedDeactivation { get; } = new(ActivationCollectionAction.StartedDeactivation, default);

    public static ActivationCollectionResult Reschedule(TimeSpan delay) => new(ActivationCollectionAction.Reschedule, delay);
}
