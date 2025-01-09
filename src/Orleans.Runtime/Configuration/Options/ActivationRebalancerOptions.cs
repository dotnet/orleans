using System;
using Microsoft.Extensions.Options;
using Orleans.Runtime;

namespace Orleans.Configuration;

/// <summary>
/// Options for configuring activation rebalancing.
/// </summary>
public sealed class ActivationRebalancerOptions
{
    /// <summary>
    /// The due time for the rebalancer to start the very first session.
    /// </summary>
    public TimeSpan RebalancerDueTime { get; set; } = DEFAULT_REBALANCER_DUE_TIME;

    /// <summary>
    /// The default value of <see cref="RebalancerDueTime"/>.
    /// </summary>
    public static readonly TimeSpan DEFAULT_REBALANCER_DUE_TIME = TimeSpan.FromSeconds(60);

    /// <summary>
    /// The time between two consecutive rebalancing cycles within a session.
    /// </summary>
    /// <remarks>It must be greater than 2 x <see cref="DeploymentLoadPublisherOptions.DeploymentLoadPublisherRefreshTime"/>.</remarks>
    public TimeSpan SessionCyclePeriod { get; set; } = DEFAULT_SESSION_CYCLE_PERIOD;

    /// <summary>
    /// The default value of <see cref="SessionCyclePeriod"/>.
    /// </summary>
    public static readonly TimeSpan DEFAULT_SESSION_CYCLE_PERIOD = TimeSpan.FromSeconds(15);

    /// <summary>
    /// The maximum, consecutive number of cycles, yielding no significant improvement to the cluster's entropy.
    /// </summary>
    /// <remarks>This value is inclusive, i.e. if this value is 'n', then the 'n+1' cycle will stop the current rebalancing session.</remarks>
    public int MaxStagnantCycles { get; set; } = DEFAULT_MAX_STAGNANT_CYCLES;

    /// <summary>
    /// The default value of <see cref="MaxStagnantCycles"/>.
    /// </summary>
    public const int DEFAULT_MAX_STAGNANT_CYCLES = 3;

    /// <summary>
    /// The minimum change in the entropy of the cluster that is considered an improvement.
    /// When a total of n-consecutive stagnant cycles pass, during which the change in entropy is less than
    /// the quantum, then the current rebalancing session will stop. The change is a normalized value
    /// being relative to the maximum possible entropy.
    /// </summary>
    /// <remarks>Allowed range: (0-0.1]</remarks>
    public double EntropyQuantum { get; set; } = DEFAULT_ENTROPY_QUANTUM;

    /// <summary>
    /// The default value of <see cref="EntropyQuantum"/>.
    /// </summary>
    public const double DEFAULT_ENTROPY_QUANTUM = 0.0001d;

    /// <summary>
    /// Represents the allowed entropy deviation between the cluster's current entropy, against the theoretical maximum.
    /// Values lower than this are practically considered as "maximum", and the current rebalancing session will stop.
    /// This acts as a base rate if <see cref="ScaleAllowedEntropyDeviation"/> is set to <see langword="true"/>.
    /// </summary>
    /// <remarks>Allowed range is: (0-0.1]</remarks>
    public double AllowedEntropyDeviation { get; set; } = DEFAULT_ALLOWED_ENTROPY_DEVIATION;

    /// <summary>
    /// The default value of <see cref="AllowedEntropyDeviation"/>.
    /// </summary>
    public const double DEFAULT_ALLOWED_ENTROPY_DEVIATION = 0.0001d;

    /// <summary>
    /// Determines whether <see cref="AllowedEntropyDeviation"/> should be scaled dynamically
    /// based on the total number of activations. When set to <see langword="true"/>, the allowed entropy
    /// deviation will increase logarithmically after reaching <see cref="ScaledEntropyDeviationActivationThreshold"/>,
    /// and will cap at <see cref="MAX_SCALED_ENTROPY_DEVIATION"/>.
    /// </summary>
    /// <remarks>This is in place because a deviation of say 10 activations has far lesser
    /// impact on a total of 100,000 activations than it does for say 1,000 activations.</remarks>
    public bool ScaleAllowedEntropyDeviation { get; set; } = DEFAULT_SCALE_ALLOWED_ENTROPY_DEVIATION;

    /// <summary>
    /// The default value of <see cref="ScaleAllowedEntropyDeviation"/>.
    /// </summary>
    public const bool DEFAULT_SCALE_ALLOWED_ENTROPY_DEVIATION = true;

    /// <summary>
    /// The maximum value allowed when <see cref="ScaleAllowedEntropyDeviation"/> is <see langword="true"/>.
    /// </summary>
    public const double MAX_SCALED_ENTROPY_DEVIATION = 0.1d;

    /// <summary>
    /// Determines the number of activations that must be active during any rebalancing cycle, in order for <see cref="ScaleAllowedEntropyDeviation"/>
    /// (if, and only if, its <see langword="true"/>) to begin scaling the <see cref="AllowedEntropyDeviation"/>.
    /// </summary>
    /// <remarks>
    /// <para>Allowed range: [1000-∞)</para>
    /// <para><strong>Values lower than the default are discouraged.</strong></para>
    /// </remarks>
    public int ScaledEntropyDeviationActivationThreshold { get; set; } = DEFAULT_SCALED_ENTROPY_DEVIATION_ACTIVATION_THRESHOLD;

    /// <summary>
    /// The default value of <see cref="ScaledEntropyDeviationActivationThreshold"/>.
    /// </summary>
    public const int DEFAULT_SCALED_ENTROPY_DEVIATION_ACTIVATION_THRESHOLD = 10_000;

    /// <summary>
    /// <para>Represents the weight that is given to the number of rebalancing cycles that have passed during a rebalancing session.</para>
    /// Changing this value has a far greater impact on the migration rate than <see cref="SiloNumberWeight"/>, and is suitable for controlling the session duration.
    /// <para>Pick higher values if you want a faster migration rate.</para>
    /// </summary>
    /// <remarks>Allowed range: (0-1]</remarks>
    public double CycleNumberWeight { get; set; } = DEFAULT_CYCLE_NUMBER_WEIGHT;

    /// <summary>
    /// The default value of <see cref="CycleNumberWeight"/>.
    /// </summary>
    public const double DEFAULT_CYCLE_NUMBER_WEIGHT = 0.1d;

    /// <summary>
    /// <para>Represents the weight that is given to the number of silos in the cluster during a rebalancing session.</para>
    /// Changing this value has a far lesser impact on the migration rate than <see cref="CycleNumberWeight"/>, and is suitable for fine-tuning.
    /// <para>Pick lower values if you want a faster migration rate.</para>
    /// </summary>
    /// <remarks>Allowed range: [0-1]</remarks>
    public double SiloNumberWeight { get; set; } = DEFAULT_SILO_NUMBER_WEIGHT;

    /// <summary>
    /// The default value of <see cref="SiloNumberWeight"/>.
    /// </summary>
    public const double DEFAULT_SILO_NUMBER_WEIGHT = 0.1d;

    /// <summary>
    /// The maximum allowed number of activations that can be migrated at any given cycle.
    /// </summary>
    public int ActivationMigrationCountLimit { get; set; } = DEFAULT_ACTIVATION_MIGRATION_COUNT_LIMIT;

    /// <summary>
    /// The default value for <see cref="ActivationMigrationCountLimit"/>.
    /// The default is practically no limit.
    /// </summary>
    public const int DEFAULT_ACTIVATION_MIGRATION_COUNT_LIMIT = int.MaxValue;
}

internal sealed class ActivationRebalancerOptionsValidator(
    IOptions<ActivationRebalancerOptions> options,
    IOptions<DeploymentLoadPublisherOptions> publisherOptions) : IConfigurationValidator
{
    private readonly ActivationRebalancerOptions _options = options.Value;
    private readonly DeploymentLoadPublisherOptions _publisherOptions = publisherOptions.Value;

    public void ValidateConfiguration()
    {
        if (_options.SessionCyclePeriod < 2 * _publisherOptions.DeploymentLoadPublisherRefreshTime)
        {
            throw new OrleansConfigurationException(
                $"{nameof(ActivationRebalancerOptions.SessionCyclePeriod)} must be at least " +
                $"{$"2 x {nameof(DeploymentLoadPublisherOptions.DeploymentLoadPublisherRefreshTime)}"}");
        }

        if (_options.MaxStagnantCycles <= 0)
        {
            throw new OrleansConfigurationException($"{nameof(ActivationRebalancerOptions.MaxStagnantCycles)} must be greater than 0");
        }

        if (_options.EntropyQuantum <= 0d || _options.EntropyQuantum > 0.1d)
        {
            throw new OrleansConfigurationException($"{nameof(ActivationRebalancerOptions.EntropyQuantum)} must be in greater than 0, and less or equal 0.1");
        }

        if (_options.AllowedEntropyDeviation <= 0d || _options.AllowedEntropyDeviation > 0.1d)
        {
            throw new OrleansConfigurationException($"{nameof(ActivationRebalancerOptions.AllowedEntropyDeviation)} must be in greater than 0, and less or equal 0.1");
        }

        if (_options.CycleNumberWeight <= 0d || _options.CycleNumberWeight > 1d)
        {
            throw new OrleansConfigurationException($"{nameof(ActivationRebalancerOptions.CycleNumberWeight)} must be in greater than 0, and less or equal to 1");
        }

        if (_options.SiloNumberWeight < 0d || _options.SiloNumberWeight > 1d)
        {
            throw new OrleansConfigurationException($"{nameof(ActivationRebalancerOptions.SiloNumberWeight)} must be in greater than or equal to 0, and less or equal to 1");
        }

        if (_options.ActivationMigrationCountLimit < 1)
        {
            throw new OrleansConfigurationException($"{nameof(ActivationRebalancerOptions.ActivationMigrationCountLimit)} must be greater than 0");
        }

        if (_options.ScaledEntropyDeviationActivationThreshold < 1_000)
        {
            throw new OrleansConfigurationException($"{nameof(ActivationRebalancerOptions.ScaledEntropyDeviationActivationThreshold)} must be greater than or equal to 1000");
        }
    }
}