using System;
using Microsoft.Extensions.Options;
using Orleans.Runtime;

namespace Orleans.Configuration;

public sealed class ActiveRebalancingOptions
{
    /// <summary>
    /// <para>
    /// The maximum number of edges to retain in-memory during a rebalancing cycle. An edge represents how many calls were made from one grain to another.
    /// </para>
    /// <para>
    /// If this number is N, it does not mean that N activations will be migrated after a rebalancing cycle.
    /// It also does not mean that if any activation ranked very high, that it will rank high at the next cycle.
    /// At the most extreme case, the number of activations that will be migrated, will equal this number, so this should give you some idea as to setting a reasonable value for this.
    /// </para>
    /// </summary>
    /// <remarks>
    /// In order to preserve memory, the most heaviest links are recorded in a probabilistic way, so there is an inherent error associated with that.
    /// That error is inversely proportional to this value, so values under 100 are not recommended. If you notice that the system is not converging fast enough, do consider increasing this number.
    /// </remarks>
    public uint MaxEdgeCount { get; set; } =



        10 * 



        DEFAULT_MAX_EDGE_COUNT;

    /// <summary>
    /// The default value of <see cref="MaxEdgeCount"/>.
    /// </summary>
    public const uint DEFAULT_MAX_EDGE_COUNT = 10_000;

    /// <summary>
    /// The minimum time given to this silo to gather statistics before triggering the first rebalancing cycle.
    /// </summary>
    /// <remarks>The actual due time is picked randomly between this and <see cref="MaxRebalancingDueTime"/>.</remarks>
    public TimeSpan MinRebalancingDueTime { get; set; } = DEFAULT_MINUMUM_REBALANCING_DUE_TIME;

    /// <summary>
    /// The default value of <see cref="MinRebalancingDueTime"/>.
    /// </summary>
    public static readonly TimeSpan DEFAULT_MINUMUM_REBALANCING_DUE_TIME = TimeSpan.FromMinutes(1);

    /// <summary>
    /// The maximum time given to this silo to gather statistics before triggering the first rebalancing cycle.
    /// </summary>
    /// <remarks>
    /// <para>The actual due time is picked randomly between this and <see cref="MinRebalancingDueTime"/>.</para>
    /// <para>For optimal results, you should aim to give this an extra 10 seconds x the maximum anticipated silo count in the cluster.</para>
    /// </remarks>
    public TimeSpan MaxRebalancingDueTime { get; set; } = DEFAULT_MAXIMUM_REBALANCING_DUE_TIME;

    /// <summary>
    /// The default value of <see cref="MaxRebalancingDueTime"/>.
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

    /// <summary>
    /// The maximum number of unprocessed edges to buffer. If this number is exceeded, the oldest edges will be discarded.
    /// </summary>
    public int MaxUnprocessedEdges { get; set; } = DEFAULT_MAX_UNPROCESSED_EDGES;

    /// <summary>
    /// The default value of <see cref="MaxUnprocessedEdges"/>.
    /// </summary>
    public const int DEFAULT_MAX_UNPROCESSED_EDGES = 100_000;
}

internal sealed class ActiveRebalancingOptionsValidator(IOptions<ActiveRebalancingOptions> options) : IConfigurationValidator
{
    private readonly ActiveRebalancingOptions _options = options.Value;

    public void ValidateConfiguration()
    {
        if (_options.MaxEdgeCount == 0)
        {
            ThrowMustBeGreaterThanZero(nameof(ActiveRebalancingOptions.MaxEdgeCount));
        }

        if (_options.MinRebalancingDueTime == TimeSpan.Zero)
        {
            ThrowMustBeGreaterThanZero(nameof(ActiveRebalancingOptions.MinRebalancingDueTime));
        }

        if (_options.MaxRebalancingDueTime == TimeSpan.Zero)
        {
            ThrowMustBeGreaterThanZero(nameof(ActiveRebalancingOptions.MaxRebalancingDueTime));
        }

        if (_options.RebalancingPeriod == TimeSpan.Zero)
        {
            ThrowMustBeGreaterThanZero(nameof(ActiveRebalancingOptions.RebalancingPeriod));
        }

        if (_options.RecoveryPeriod == TimeSpan.Zero)
        {
            ThrowMustBeGreaterThanZero(nameof(ActiveRebalancingOptions.RecoveryPeriod));
        }

        if (_options.MaxRebalancingDueTime <= _options.MinRebalancingDueTime)
        {
            ThrowMustBeGreaterThan(nameof(ActiveRebalancingOptions.MaxRebalancingDueTime), nameof(ActiveRebalancingOptions.MinRebalancingDueTime));
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