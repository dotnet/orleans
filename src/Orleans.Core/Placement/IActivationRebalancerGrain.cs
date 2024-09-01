using System;
using System.Threading.Tasks;

namespace Orleans.Placement;

/// <summary>
/// Interface for management of activation rebalancing.
/// </summary>
[Alias("IActivationRebalancerGrain")]
public interface IActivationRebalancerGrain : IGrainWithIntegerKey
{
    /// <summary>
    /// The key to the only activation of this grain type.
    /// </summary>
    /// <remarks><strong>Attempts to create another activation of this
    /// type will result in an <see cref="InvalidOperationException"/>.</strong></remarks>
    public const int Key = 0;

    /// <summary>
    /// Resumes the rebalancer if its suspended, otherwise its a no-op.
    /// </summary>
    [Alias("ResumeRebalancing")] Task ResumeRebalancing();
    /// <summary>
    /// Suspends the rebalancer if its operating, otherwise its a no-op.
    /// </summary>
    /// <param name="duration">
    /// The amount of time to suspend the rebalancer.
    /// <para><see langword="null"/> means suspend indefinitely.</para>
    /// </param>
    [Alias("SuspendRebalancing")] Task SuspendRebalancing(TimeSpan? duration);
}

[Alias("Orleans.Placement.Rebalancing.IInternalActivationRebalancerGrain")]
internal interface IInternalActivationRebalancerGrain : IActivationRebalancerGrain
{
    /// <summary>
    /// Starts the rebalancer if its not started yet, otherwise its a no-op.
    /// </summary>
    [Alias("StartRebalancing")] Task StartRebalancing();
}
