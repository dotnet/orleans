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
    public int MaxEdgeCount { get; set; } = DEFAULT_MAX_EDGE_COUNT;

    /// <summary>
    /// The default value of <see cref="MaxEdgeCount"/>.
    /// </summary>
    public const int DEFAULT_MAX_EDGE_COUNT = 10_000;

    /// <summary>
    /// The minimum time between initiating a rebalancing cycle.
    /// </summary>
    /// <remarks>The actual due time is picked randomly between this and <see cref="MaxRebalancingPeriod"/>.</remarks>
    public TimeSpan MinRebalancingPeriod { get; set; } = DEFAULT_MINUMUM_REBALANCING_PERIOD;

    /// <summary>
    /// The default value of <see cref="MinRebalancingPeriod"/>.
    /// </summary>
    public static readonly TimeSpan DEFAULT_MINUMUM_REBALANCING_PERIOD = TimeSpan.FromMinutes(1);

    /// <summary>
    /// The maximum time between initiating a rebalancing cycle.
    /// </summary>
    /// <remarks>
    /// <para>The actual due time is picked randomly between this and <see cref="MinRebalancingPeriod"/>.</para>
    /// <para>For optimal results, you should aim to give this an extra 10 seconds multiplied by the maximum anticipated silo count in the cluster.</para>
    /// </remarks>
    public TimeSpan MaxRebalancingPeriod { get; set; } = DEFAULT_MAXIMUM_REBALANCING_PERIOD;

    /// <summary>
    /// The default value of <see cref="MaxRebalancingPeriod"/>.
    /// </summary>
    public static readonly TimeSpan DEFAULT_MAXIMUM_REBALANCING_PERIOD = TimeSpan.FromMinutes(2);

    /// <summary>
    /// The minimum time needed for a silo to recover from a previous rebalancing.
    /// Until this time has elapsed, this silo will not take part in any rebalancing attempt from another silo.
    /// </summary>
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

    /// <summary>
    /// Gets or sets a value indicating whether to enable the local vertex filter. This filter tracks which
    /// vertices are well-partitioned (moving them from the local host would be detrimental) and collapses them
    /// into a single per-silo vertex to reduce the space required to track edges involving that vertex. The result
    /// is a reduction in accuracy but a potentially significant increase in effectiveness of the rebalancer, since
    /// well-partitioned edges will not dominate the top-K data structure, leaving sufficient room to track
    /// non-well-partitioned vertices. This is enabled by default.
    /// </summary>
    public bool AnchoringFilterEnabled { get; set; } = DEFAULT_ANCHORING_FILTER_ENABLED;

    /// <summary>
    /// The default value of <see cref="AnchoringFilterEnabled"/>.
    /// </summary>
    public const bool DEFAULT_ANCHORING_FILTER_ENABLED = true;

    /// <summary>
    /// The maximum allowed error rate when <see cref="AnchoringFilterEnabled"/> is set to <see langword="true"/>, otherwise this does not apply.
    /// </summary>
    /// <remarks>Allowed range: [0.001 - 0.01](0.1% - 1%)</remarks>
    public double ProbabilisticFilteringMaxAllowedErrorRate { get; set; } = DEFAULT_PROBABILISTIC_FILTERING_MAX_ALLOWED_ERROR;

    /// <summary>
    /// The default value of <see cref="ProbabilisticFilteringMaxAllowedErrorRate"/>.
    /// </summary>
    public const double DEFAULT_PROBABILISTIC_FILTERING_MAX_ALLOWED_ERROR = 0.01d;
}

internal sealed class ActiveRebalancingOptionsValidator(IOptions<ActiveRebalancingOptions> options) : IConfigurationValidator
{
    private readonly ActiveRebalancingOptions _options = options.Value;

    public void ValidateConfiguration()
    {
        if (_options.MaxEdgeCount <= 0)
        {
            ThrowMustBeGreaterThanZero(nameof(ActiveRebalancingOptions.MaxEdgeCount));
        }

        if (_options.MaxUnprocessedEdges <= 0)
        {
            ThrowMustBeGreaterThanZero(nameof(ActiveRebalancingOptions.MaxUnprocessedEdges));
        }

        if (_options.MinRebalancingPeriod <= TimeSpan.Zero)
        {
            ThrowMustBeGreaterThanZero(nameof(ActiveRebalancingOptions.MinRebalancingPeriod));
        }

        if (_options.MaxRebalancingPeriod <= TimeSpan.Zero)
        {
            ThrowMustBeGreaterThanZero(nameof(ActiveRebalancingOptions.MaxRebalancingPeriod));
        }

        if (_options.RecoveryPeriod <= TimeSpan.Zero)
        {
            ThrowMustBeGreaterThanZero(nameof(ActiveRebalancingOptions.RecoveryPeriod));
        }

        if (_options.MaxRebalancingPeriod < _options.MinRebalancingPeriod)
        {
            ThrowMustBeGreaterThanOrEqualTo(nameof(ActiveRebalancingOptions.MaxRebalancingPeriod), nameof(ActiveRebalancingOptions.MinRebalancingPeriod));
        }

        if (_options.MinRebalancingPeriod < _options.RecoveryPeriod)
        {
            ThrowMustBeGreaterThanOrEqualTo(nameof(ActiveRebalancingOptions.MinRebalancingPeriod), nameof(ActiveRebalancingOptions.RecoveryPeriod));
        }

        if (_options.ProbabilisticFilteringMaxAllowedErrorRate < 0.001d || _options.ProbabilisticFilteringMaxAllowedErrorRate > 0.01d)
        {
            throw new OrleansConfigurationException($"{nameof(ActiveRebalancingOptions.ProbabilisticFilteringMaxAllowedErrorRate)} must be inclusive between [0.001 - 0.01](0.1% - 1%)");
        }
    }

    private static void ThrowMustBeGreaterThanZero(string propertyName)
        => throw new OrleansConfigurationException($"{propertyName} must be greater than 0");

    private static void ThrowMustBeGreaterThanOrEqualTo(string name1, string name2)
        => throw new OrleansConfigurationException($"{name1} must be greater than or equal to {name2}");
}