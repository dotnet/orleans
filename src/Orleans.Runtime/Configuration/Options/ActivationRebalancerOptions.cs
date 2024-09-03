using System;
using Microsoft.Extensions.Options;
using Orleans.Runtime;

namespace Orleans.Configuration;

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
    public static readonly TimeSpan DEFAULT_SESSION_CYCLE_PERIOD = TimeSpan.FromSeconds(10);

    /// <summary>
    /// The maximum, consecutive number of cycles, yielding no significant improvement to the cluster's entropy.
    /// </summary>
    /// <remarks>This value is inclusive, i.e. if this value is 'n', than the 'n+1' cycle will stop the current rebalancing session.</remarks>
    public int MaxStaleCycles { get; set; } = DEFAULT_MAX_STALE_CYCLES;

    /// <summary>
    /// The default value of <see cref="MaxStaleCycles"/>.
    /// </summary>
    public const int DEFAULT_MAX_STALE_CYCLES = 3;

    /// <summary>
    /// The minumum change in the entropy of the cluster that is considered an "improvement".
    /// When a total of n-consecutive stale cycles pass, during which the change in entropy is less than
    /// the quantum, than the current rebalancing session will stop. The "change" is a normalized value
    /// being relative to the maximum possible entropy.
    /// </summary>
    /// <remarks>Allowed range: (0-0.01)</remarks>
    public float EntropyQuantum { get; set; } = DEFAULT_ENTROPY_QUANTUM;

    /// <summary>
    /// The default value of <see cref="EntropyQuantum"/>.
    /// </summary>
    public const float DEFAULT_ENTROPY_QUANTUM = 0.001f;

    /// <summary>
    /// Represents the allowed entropy deviation between the cluster's current entropy, againt the theoretical maximum.
    /// Values lower than or equal to this are practically considered as "maximum", and the current rebalancing session will stop.
    /// </summary>
    /// <remarks>Allowed range is: (0-1)</remarks>
    public float MaxEntropyDeviation { get; set; } = DEFAULT_MAX_ENTROPY_DEVIATION;

    /// <summary>
    /// The default value of <see cref="MaxEntropyDeviation"/>.
    /// </summary>
    public const float DEFAULT_MAX_ENTROPY_DEVIATION = 0.01f;

    /// <summary>
    /// <para>Represents the weight that is given to the number of rebalancing cycles that have passed during a rebalancing session.</para>
    /// Changing this value has a far greater impact on the migration rate than <see cref="SiloNumberWeight"/>, and is suitable for controlling the session duration.
    /// Pick higher values if you want a faster migration rate.
    /// </summary>
    /// <remarks>Allowed range: (0-1]</remarks>
    public float CycleNumberWeight { get; set; } = DEFAULT_CYCLE_NUMBER_WEIGHT;

    /// <summary>
    /// The default value of <see cref="CycleNumberWeight"/>.
    /// </summary>
    public const float DEFAULT_CYCLE_NUMBER_WEIGHT = 0.1f;

    /// <summary>
    /// <para>Represents the weight that is given to the number of silos in the cluster during a rebalancing session.</para>
    /// Changing this value has a far lesser impact on the migration rate than <see cref="CycleNumberWeight"/>, and is suitable for fine-tuning.
    /// Pick lower values if you want a faster migration rate.
    /// </summary>
    /// <remarks>Allowed range: [0-1]</remarks>
    public float SiloNumberWeight { get; set; } = DEFAULT_SILO_NUMBER_WEIGHT;

    /// <summary>
    /// The default value of <see cref="SiloNumberWeight"/>.
    /// </summary>
    public const float DEFAULT_SILO_NUMBER_WEIGHT = 0.1f;
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

        if (_options.MaxStaleCycles <= 0)
        {
            throw new OrleansConfigurationException($"{nameof(ActivationRebalancerOptions.MaxStaleCycles)} must be greater than 0");
        }

        if (_options.EntropyQuantum <= 0 || _options.EntropyQuantum >= 0.01)
        {
            throw new OrleansConfigurationException($"{nameof(ActivationRebalancerOptions.EntropyQuantum)} must be in greater than 0, and less 1");
        }

        if (_options.MaxEntropyDeviation <= 0 || _options.MaxEntropyDeviation >= 1)
        {
            throw new OrleansConfigurationException($"{nameof(ActivationRebalancerOptions.MaxEntropyDeviation)} must be in greater than 0, and less 1");
        }

        if (_options.CycleNumberWeight <= 0 || _options.CycleNumberWeight > 1)
        {
            throw new OrleansConfigurationException($"{nameof(ActivationRebalancerOptions.CycleNumberWeight)} must be in greater than 0, and less or equal to 1");
        }

        if (_options.SiloNumberWeight < 0 || _options.SiloNumberWeight > 1)
        {
            throw new OrleansConfigurationException($"{nameof(ActivationRebalancerOptions.SiloNumberWeight)} must be in greater than or equal to 0, and less or equal to 1");
        }
    }
}