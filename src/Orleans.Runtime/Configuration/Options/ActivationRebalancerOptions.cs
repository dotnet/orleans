using System;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Options;
using Orleans.Placement.Rebalancing;
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
    /// Determines the time between two consecutive rebalancing cycles within a session.
    /// </summary>
    /// <remarks>It must be greater than 2 x <see cref="DeploymentLoadPublisherOptions.DeploymentLoadPublisherRefreshTime"/>.</remarks>
    public TimeSpan SessionCyclePeriod { get; set; } = DEFAULT_SESSION_CYCLE_PERIOD;

    /// <summary>
    /// The default value of <see cref="SessionCyclePeriod"/>.
    /// </summary>
    public static readonly TimeSpan DEFAULT_SESSION_CYCLE_PERIOD = TimeSpan.FromSeconds(30);

    /// <summary>
    /// The maximum consecutive number of cycles yielding no improvement to the cluster's entropy.
    /// </summary>
    /// <remarks>This value is inclusive, i.e. if this value is 'n', than the 'n+1' cycle will stop the current rebalancing session.</remarks>
    public int MaxStaleCycles { get; set; } = DEFAULT_MAX_STALE_CYCLES;

    /// <summary>
    /// The default value of <see cref="MaxStaleCycles"/>.
    /// </summary>
    public const int DEFAULT_MAX_STALE_CYCLES = 10;

    /// <summary>
    /// The minumum change in the entropy of the cluster that is considered an "improvement".
    /// When a total of n-consecutive stale cycles pass, during which the change in entropy is less than
    /// the quantum, than the current rebalancing session will stop.
    /// </summary>
    /// <remarks>Allowed range: (0-1)</remarks>
    public float EntropyQuantum { get; set; } = DEFAULT_ENTROPY_QUANTUM;

    /// <summary>
    /// The default value of <see cref="EntropyQuantum"/>.
    /// </summary>
    public const float DEFAULT_ENTROPY_QUANTUM = 1e-4f;

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
    /// Pick lower values if you want a slower migration rate.
    /// </summary>
    /// <remarks>Allowed range: (0-1]</remarks>
    public float CycleNumberWeight { get; set; } = DEFAULT_CYCLE_NUMBER_WEIGHT;

    /// <summary>
    /// The default value of <see cref="CycleNumberWeight"/>.
    /// </summary>
    public const float DEFAULT_CYCLE_NUMBER_WEIGHT = 0.1f;

    /// <summary>
    /// <para>Represents the weight that is given to the number of silo in the cluster during a rebalancing session.</para>
    /// Changing this value has a far lesser impact on the migration rate than <see cref="CycleNumberWeight"/>, and is suitable for fine-tuning.
    /// Pick lower values if you want a slower migration rate.
    /// </summary>
    /// <remarks>Allowed range: (0-1]</remarks>
    public float SiloNumberWeight { get; set; } = DEFAULT_SILO_NUMBER_WEIGHT;

    /// <summary>
    /// The default value of <see cref="SiloNumberWeight"/>.
    /// </summary>
    public const float DEFAULT_SILO_NUMBER_WEIGHT = 0.1f;

    public RebalancingParameters ToParameters() =>
        new(SessionCyclePeriod, MaxStaleCycles, EntropyQuantum,
            MaxEntropyDeviation, CycleNumberWeight, SiloNumberWeight);
}

internal sealed class ActivationRebalancerOptionsValidator(
    IOptions<ActivationRebalancerOptions> options,
    IOptions<DeploymentLoadPublisherOptions> publisherOptions) : IConfigurationValidator
{
    private readonly ActivationRebalancerOptions _options = options.Value;
    private readonly DeploymentLoadPublisherOptions _publisherOptions = publisherOptions.Value;

    public void ValidateConfiguration() => ThrowIfInvalid(_options.ToParameters(),
        _publisherOptions.DeploymentLoadPublisherRefreshTime);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void ThrowIfInvalid(RebalancingParameters parameters, TimeSpan deploymentLoadPublisherRefreshTime)
    {
        if (parameters.SessionCyclePeriod <= 2 * deploymentLoadPublisherRefreshTime)
        {
            throw new OrleansConfigurationException(
                $"{nameof(RebalancingParameters.SessionCyclePeriod)} must be greater than " +
                $"{$"2 x {nameof(DeploymentLoadPublisherOptions.DeploymentLoadPublisherRefreshTime)}"}");
        }

        if (parameters.MaxStaleCycles <= 0)
        {
            throw new OrleansConfigurationException($"{nameof(RebalancingParameters.MaxStaleCycles)} must be greater than 0");
        }

        if (parameters.EntropyQuantum <= 0 || parameters.EntropyQuantum >= 1)
        {
            throw new OrleansConfigurationException($"{nameof(RebalancingParameters.EntropyQuantum)} must be in greater than 0, and less 1");
        }

        if (parameters.MaxEntropyDeviation <= 0 || parameters.MaxEntropyDeviation >= 1)
        {
            throw new OrleansConfigurationException($"{nameof(RebalancingParameters.MaxEntropyDeviation)} must be in greater than 0, and less 1");
        }

        if (parameters.CycleNumberWeight <= 0 || parameters.CycleNumberWeight > 1)
        {
            throw new OrleansConfigurationException($"{nameof(RebalancingParameters.CycleNumberWeight)} must be in greater than 0, and less or equal to 1");
        }

        if (parameters.SiloNumberWeight <= 0 || parameters.SiloNumberWeight > 1)
        {
            throw new OrleansConfigurationException($"{nameof(RebalancingParameters.SiloNumberWeight)} must be in greater than 0, and less or equal to 1");
        }
    }
}