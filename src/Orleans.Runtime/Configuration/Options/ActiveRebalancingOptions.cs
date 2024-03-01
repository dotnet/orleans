using System;
using Microsoft.Extensions.Options;
using Orleans.Runtime;

namespace Orleans.Configuration;

public sealed class ActiveRebalancingOptions
{
    /// <summary>
    /// <para>
    /// Represents the number of the most heaviest communication links to monitor, in-between any rebalancing cycle.
    /// A communicatin link, refers to any call between any two grain activations. This number controls how many of such links will be recorded.
    /// </para>
    /// <para>
    /// If this number is N, it does not mean that N activations will be migrated after a rebalancing cycle.
    /// It also does not mean that if any activation ranked very high, that it will rank high at the next cycle.
    /// At the most extreme case, the number of activations that will be migrated, will equal this number, so this should give you some idea as to setting a reasonable value for this.
    /// </para>
    /// </summary>
    /// <remarks>
    /// In order to preserve memory, the most heaviest links are recorded in a probabilistic way, so there is an inherent error associated with that.
    /// That error is inversely proportional to this value, so values under 100 are not recomended. If you notice that the system is not converging fast enough, do consider increasing this number.
    /// </remarks>
    public uint TopHeaviestCommunicationLinks { get; set; } = DEFAULT_TOP_HEAVIEST_COMMUNICATION_LINKS;
    /// <summary>
    /// The default value of <see cref="TopHeaviestCommunicationLinks"/>.
    /// </summary>
    public const uint DEFAULT_TOP_HEAVIEST_COMMUNICATION_LINKS = 10_000;

    /// <summary>
    /// The minimum time given to this silo to gather statistics before triggering the first rebalancing cycle.
    /// </summary>
    /// <remarks>The actual due time is picked randomly between this and <see cref="MaximumRebalancingDueTime"/>.</remarks>
    public TimeSpan MinimumRebalancingDueTime { get; set; } = DEFAULT_MINUMUM_REBALANCING_DUE_TIME;
    /// <summary>
    /// The default value of <see cref="MinimumRebalancingDueTime"/>.
    /// </summary>
    public static readonly TimeSpan DEFAULT_MINUMUM_REBALANCING_DUE_TIME = TimeSpan.FromMinutes(1);

    /// <summary>
    /// The maximum time given to this silo to gather statistics before triggering the first rebalancing cycle.
    /// </summary>
    /// <remarks>
    /// <para>The actual due time is picked randomly between this and <see cref="MinimumRebalancingDueTime"/>.</para>
    /// <para>For optimal results, you should aim to give this an extra 10 seconds x the maximum anticipated silo count in the cluster.</para>
    /// </remarks>
    public TimeSpan MaximumRebalancingDueTime { get; set; } = DEFAULT_MAXIMUM_REBALANCING_DUE_TIME;
    /// <summary>
    /// The default value of <see cref="MaximumRebalancingDueTime"/>.
    /// </summary>
    public static readonly TimeSpan DEFAULT_MAXIMUM_REBALANCING_DUE_TIME = TimeSpan.FromMinutes(2);

    /// <summary>
    /// The cycle upon which this silo will trigger a rebalancing session with another silo.
    /// </summary>
    /// <remarks>Must be greater than <see cref="RecoveryPeriod"/>, you should aim for at least 2 times that of <see cref="RecoveryPeriod"/>.</remarks>
    public TimeSpan RebalancingPeriod { get; set; } = DEFAULT_REBALANCING_PERIOD;
    /// <summary>
    /// The default value of <see cref="RebalancingPeriod"/>.
    /// </summary>
    public static readonly TimeSpan DEFAULT_REBALANCING_PERIOD = TimeSpan.FromMinutes(2);

    /// <summary>
    /// The minimum time needed for a silo to recover from a previous rebalancing.
    /// Until this time has elapsed, this silo will not take part in any rebalancing attempt from another silo.
    /// </summary>
    /// <remarks>
    /// <para>
    /// While this silo will refuse rebalancing attempts from other silos, if <see cref="RebalancingPeriod"/> falls within this period, than
    /// this silo will attempt a rebalancing with another silo, but this silo will be the initiator, not the other way around.
    /// </para>
    /// <para>Must be less than <see cref="RebalancingPeriod"/>, you should aim for at least 1/2 times that of <see cref="RebalancingPeriod"/>.</para>
    /// </remarks>
    public TimeSpan RecoveryPeriod { get; set; } = DEFAULT_RECOVERY_PERIOD;
    /// <summary>
    /// The default value of <see cref="RecoveryPeriod"/>.
    /// </summary>
    public static readonly TimeSpan DEFAULT_RECOVERY_PERIOD = TimeSpan.FromMinutes(1);
}

internal sealed class ActiveRebalancingOptionsValidator(IOptions<ActiveRebalancingOptions> options) : IConfigurationValidator
{
    private readonly ActiveRebalancingOptions _options = options.Value;

    public void ValidateConfiguration()
    {
        if (_options.TopHeaviestCommunicationLinks == 0)
        {
            ThrowMustBeGreaterThanZero(nameof(ActiveRebalancingOptions.TopHeaviestCommunicationLinks));
        }

        if (_options.MinimumRebalancingDueTime == TimeSpan.Zero)
        {
            ThrowMustBeGreaterThanZero(nameof(ActiveRebalancingOptions.MinimumRebalancingDueTime));
        }

        if (_options.MaximumRebalancingDueTime == TimeSpan.Zero)
        {
            ThrowMustBeGreaterThanZero(nameof(ActiveRebalancingOptions.MaximumRebalancingDueTime));
        }

        if (_options.RebalancingPeriod == TimeSpan.Zero)
        {
            ThrowMustBeGreaterThanZero(nameof(ActiveRebalancingOptions.RebalancingPeriod));
        }

        if (_options.RecoveryPeriod == TimeSpan.Zero)
        {
            ThrowMustBeGreaterThanZero(nameof(ActiveRebalancingOptions.RecoveryPeriod));
        }

        if (_options.MaximumRebalancingDueTime <= _options.MinimumRebalancingDueTime)
        {
            ThrowMustBeGreaterThan(nameof(ActiveRebalancingOptions.MaximumRebalancingDueTime), nameof(ActiveRebalancingOptions.MinimumRebalancingDueTime));
        }

        if (_options.RebalancingPeriod <= _options.RecoveryPeriod)
        {
            ThrowMustBeGreaterThan(nameof(ActiveRebalancingOptions.RebalancingPeriod), nameof(ActiveRebalancingOptions.RecoveryPeriod));
        }
    }

    private static void ThrowMustBeGreaterThanZero(string propertyName)
        => throw new OrleansConfigurationException($"{propertyName} must be greater than 0");

    private static void ThrowMustBeGreaterThan(string name1, string name2)
        => throw new OrleansConfigurationException($"{name1} must be greater than {name2}");
}