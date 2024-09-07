using System;

namespace Orleans.Placement.Rebalancing;

/// <summary>
/// Interface for types which listen to rebalancer status changes.
/// </summary>
public interface IActivationRebalancerStatusListener
{
    /// <summary>
    /// Triggered when rebalancer has been started or resumed.
    /// </summary>
    /// <remarks></remarks>
    void OnStarted();

    /// <summary>
    /// Triggered when rebalancer has been suspended.
    /// </summary>
    /// <param name="duration">The amount of time it will stay suspended, unless instructed to resume.</param>
    /// <remarks>A <see langword="null"/> value for <paramref name="duration"/> means it has been suspended indefinitely.</remarks>
    void OnStopped(TimeSpan? duration);
}
