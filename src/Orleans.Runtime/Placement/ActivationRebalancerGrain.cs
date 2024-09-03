using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Placement;
using Orleans.Statistics;

#pragma warning disable IDE0130 // For more granular log filtering
namespace Orleans.Runtime.Placement.Rebalancing;
#pragma warning restore IDE0130

#nullable enable

// See: https://www.ledjonbehluli.com/posts/orleans_adaptive_rebalancing/

[KeepAlive, Immovable]
internal sealed class ActivationRebalancerGrain(
    TimeProvider timeProvider,
    DeploymentLoadPublisher loadPublisher,
    ISiloStatusOracle siloStatusOracle,
    IInternalGrainFactory grainFactory,
    ILogger<ActivationRebalancerGrain> logger,
    IOptions<ActivationRebalancerOptions> options,
    IFailedRebalancingSessionBackoffProvider backoffProvider)
        : Grain, IInternalActivationRebalancerGrain, ISiloStatisticsChangeListener
{
    private record struct ResourceStatistics(long MemoryUsage, int ActivationCount);
    private enum StopReason { StartingSession, FailedSession, Suspend }

    private int _rebalancingCycle;
    private uint _staleCycles;
    private double _previousEntropy;
    private uint _failedSessionCount; 
    private DateTime? _disabledUntil;
    private IGrainTimer? _sessionTimer;
    private IGrainTimer? _triggerTimer;

    private readonly ActivationRebalancerOptions _options = options.Value;
    private readonly Dictionary<SiloAddress, ResourceStatistics> _siloStatistics = [];
    private readonly Dictionary<SiloAddress, SiloRebalancingStatistics> _rebalancingStatistics = [];

    public override Task OnActivateAsync(CancellationToken cancellationToken)
    {
        loadPublisher.SubscribeToStatisticsChangeEvents(this);
        return Task.CompletedTask;
    }

    public override Task OnDeactivateAsync(DeactivationReason reason, CancellationToken cancellationToken)
    {
        loadPublisher.UnsubscribeStatisticsChangeEvents(this);
        return Task.CompletedTask;
    }

    public void RemoveSilo(SiloAddress address) => _siloStatistics.Remove(address);

    public void SiloStatisticsChangeNotification(SiloAddress address, SiloRuntimeStatistics statistics)
    {
        ref var stats = ref CollectionsMarshal.GetValueRefOrAddDefault(_siloStatistics, address, out _);
        stats = new(statistics.EnvironmentStatistics.MemoryUsageBytes, statistics.ActivationCount);
    }

    public ValueTask<ImmutableArray<SiloRebalancingStatistics>> GetStatistics() => new([.. _rebalancingStatistics.Values]);

    public Task StartRebalancer()
    {
        ThrowIfInvalidGrainKey();

        if (_triggerTimer is null)
        {
            var period = 0.5 * _options.SessionCyclePeriod; // make trigger-period twice as short as the session cycle-period.

            _triggerTimer = this.RegisterGrainTimer(PeriodicallyTriggerRebalancing, new()
            {
                Interleave = true,
                Period = period,
                DueTime = _options.RebalancerDueTime
            });

            if (logger.IsEnabled(LogLevel.Trace))
            {
                logger.LogTrace("Activation rebalancer has been scheduled to start after {Duration}", period);
            }
        }

        return Task.CompletedTask;
    }

    public Task ResumeRebalancing()
    {
        ThrowIfInvalidGrainKey();
        StartSession();

        return Task.CompletedTask;
    }

    public Task SuspendRebalancing(TimeSpan? duration)
    {
        ThrowIfInvalidGrainKey();
        StopSession(StopReason.Suspend, duration);

        if (logger.IsEnabled(LogLevel.Trace))
        {
            if (duration.HasValue)
            {
                logger.LogTrace("I have been told to suspend rebalancing for {Duration}.", duration.Value);
            }
            else
            {
                logger.LogTrace("I have been told to suspend rebalancing indefinitely.");
            }
        };

        return Task.CompletedTask;
    }

    private Task PeriodicallyTriggerRebalancing()
    {
        if (_sessionTimer != null) 
        {
            return Task.CompletedTask; // session exists
        }

        if (_disabledUntil.HasValue && _disabledUntil.Value > timeProvider.GetUtcNow())
        {
            return Task.CompletedTask; // rebalancer is suspended
        }

        StartSession();
        return Task.CompletedTask;
    }

    private async Task RunRebalancingCycle()
    {
        var siloCount = siloStatusOracle.GetActiveSilos().Length;
        if (siloCount < 2)
        {
            if (logger.IsEnabled(LogLevel.Trace))
            {
                logger.LogTrace("Can not continue with rebalancing because there are less than 2 silos.");
            }

            return;
        }

        var snapshot = _siloStatistics.ToDictionary();
        if (snapshot.Count < 2)
        {
            if (logger.IsEnabled(LogLevel.Trace))
            {
                logger.LogTrace("Can not continue with rebalancing because I have statistics information for less than 2 silos.");
            }

            return;
        }

        if (snapshot.Any(x => x.Value.MemoryUsage == 0))
        {
            if (logger.IsEnabled(LogLevel.Warning))
            {
                logger.LogWarning(
                    "Can not continue with rebalancing because at least one of the silos is reporting 0 memory usage. " +
                    "This can indicated that there is no implementation of {ProviderName}", nameof(IEnvironmentStatisticsProvider));
            }

            return;
        }

        _rebalancingCycle++;

        if (_staleCycles >= _options.MaxStaleCycles)
        {
            if (logger.IsEnabled(LogLevel.Trace))
            {
                logger.LogTrace(
                    "The current rebalancing session has stopped due to {StaleCycles} stale " +
                    "cycles having passed, which is the maximum allowed.", _staleCycles);
            }

            StopSession(StopReason.FailedSession);
            return;
        }

        var totalActivations = snapshot.Sum(x => x.Value.ActivationCount);
        var meanMemoryUsage = ComputeHarmonicMean(snapshot.Select(x => x.Value.MemoryUsage).ToArray());
        var maximumEntropy = Math.Log(siloCount);
        var currentEntropy = ComputeEntropy(snapshot.Select(x => x.Value), totalActivations, meanMemoryUsage);
        var entropyDeviation = (maximumEntropy - currentEntropy) / maximumEntropy;

        if (entropyDeviation <= _options.MaxEntropyDeviation)
        {
            // The deviation from maximum is practically considered "0" i.e: we've reached maximum.
            if (logger.IsEnabled(LogLevel.Trace))
            {
                logger.LogWarning(
                    "The current rebalancing session has stopped due to a {EntropyDeviation} " +
                    "entropy deviation between the current {CurrentEntropy} and maximum possible {MaximumEntropy}. " +
                    "The difference is less than the required {MaxEntropyDeviation} deviation.",
                    entropyDeviation, currentEntropy, maximumEntropy, _options.MaxEntropyDeviation);
            }

            StopSession(StopReason.FailedSession);
            return;
        }

        // We use the normalized, absolute entropy change, because it is more useful for understanding how significant
        // the change is, relative to the maximum possible. Values closer to 1 reflect higher significance than those closer to 0.
        // Since max entropy is a function of the natural log of the cluster's size, this value is very robust against changes
        // in silo number within the cluster.

        var entropyChange = (float)Math.Abs((currentEntropy - _previousEntropy) / maximumEntropy);
        Debug.Assert(entropyChange >= 0 && entropyChange <= 1);

        if (entropyChange < _options.EntropyQuantum)
        {
            // Entropy change is too low to be considered an improvement, chances are we are reaching the maximum, or the system
            // is dynamically changing too fast i.e. new activations are being created at a high rate with an imbalanced distribution,
            // we need to start "cooling-down". As a matter of fact, entropy could also become negative if the current entropy is less
            // than the previous, due to many activation changes happening during this and the previous cycle.
            if (logger.IsEnabled(LogLevel.Trace))
            {
                logger.LogWarning(
                    "The relative change in entropy {EntropyDifference} is less than the quantum {EntropyQuantum}. " +
                    "This is practically not considered an improvement, therefor this cycle will be marked as stale.",
                    entropyChange, _options.EntropyQuantum);
            }

            _staleCycles++;
            _previousEntropy = currentEntropy;

            return;
        }

        if (_staleCycles > 0)
        {
            _staleCycles = 0;
            if (logger.IsEnabled(LogLevel.Trace))
            {
                logger.LogTrace("Stale cycle count has been reset as we are improving now.");
            }
        }

        if (_failedSessionCount > 0)
        {
            _failedSessionCount = 0;
            if (logger.IsEnabled(LogLevel.Trace))
            {
                logger.LogTrace("Failed session count has been reset as we are improving now.");
            }
        }

        var idealDistributions = snapshot.Select(x => new ValueTuple<SiloAddress, double>
            // n_i = (N / S) * (M_m / m_i)
            (x.Key, ((double)totalActivations / siloCount) * (meanMemoryUsage / x.Value.MemoryUsage)))
            .ToDictionary();

        var alpha = currentEntropy / maximumEntropy;
        var scalingFactor = ComputeAdaptiveScaling(siloCount, _rebalancingCycle);
        var addressPairs = FormSiloPairs(snapshot);
        var migrationTasks = new List<Task>();

        for (var i = 0; i < addressPairs.Count; i++)
        {
            (var lowSilo, var highSilo) = addressPairs[i];

            if (lowSilo.IsSameLogicalSilo(highSilo))
            {
                continue;
            }

            var difference = Math.Abs(
                (snapshot[lowSilo].ActivationCount - idealDistributions[lowSilo]) -
                (snapshot[highSilo].ActivationCount - idealDistributions[highSilo]));

            var delta = (int)(alpha * scalingFactor * (difference / 2));
            if (delta == 0)
            {
                continue;
            }

            var lowCount = snapshot[lowSilo].ActivationCount;
            var highCount = snapshot[highSilo].ActivationCount;

            if (delta > highCount)
            {
                delta = highCount;
            }

            migrationTasks.Add(grainFactory
                .GetSystemTarget<ISiloControl>(Constants.SiloControlType, highSilo)
                .MigrateRandomActivations(lowSilo, delta));

            UpdateStatistics(lowSilo, highSilo, (uint)delta);

            if (logger.IsEnabled(LogLevel.Trace))
            {
                logger.LogTrace(
                    "I have decided to migrate {Delta} activations.\n" +
                    "Adjusted activations for {LowSilo} will be [{LowSiloPreActivations} -> {LowSiloPostActivations}].\n" +
                    "Adjusted activations for {HighSilo} will be [{HighSiloPreActivations} -> {HighSiloPostActivations}].",
                    delta, lowSilo, lowCount, lowCount + delta, highSilo, highCount, highCount - delta);
            }
        }

        if (migrationTasks.Count > 0)
        {
            await Task.WhenAll(migrationTasks);
        }

        if (logger.IsEnabled(LogLevel.Trace))
        {
            logger.LogInformation(
                "Rebalancing cycle {RebalancingCycle} has finished. " +
                "[ Stale Cycles: { StaleCycles} | Previous Entropy: {PreviousEntropy} | " +
                "Current Entropy: {CurrentEntropy} | Maximum Entropy: {MaximumEntropy} | Entropy Difference: {EntropyDiff} ]",
                _rebalancingCycle, _staleCycles, _previousEntropy, currentEntropy, maximumEntropy, entropyDeviation);
        }

        _previousEntropy = currentEntropy;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void UpdateStatistics(SiloAddress lowSilo, SiloAddress highSilo, uint delta)
    {
        var now = timeProvider.GetUtcNow().DateTime;

        ref var lowStats = ref CollectionsMarshal.GetValueRefOrAddDefault(_rebalancingStatistics, lowSilo, out _);
        lowStats = new(now, lowSilo, lowStats.DispersedActivations, lowStats.AcquiredActivations + delta);

        ref var highStats = ref CollectionsMarshal.GetValueRefOrAddDefault(_rebalancingStatistics, highSilo, out _);
        highStats = new(now, highSilo, highStats.DispersedActivations + delta, highStats.AcquiredActivations);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static double ComputeEntropy(
        IEnumerable<ResourceStatistics> statistics,
        int totalActivations, double meanMemoryUsage)
    {
        Debug.Assert(totalActivations > 0);
        Debug.Assert(meanMemoryUsage > 0f);

        var ratios = statistics.Select(x =>
            // p_i = (n_i / N) * (m_i / M_m)
            ((double)x.ActivationCount / totalActivations) * (x.MemoryUsage / meanMemoryUsage))
            .ToList();

        var ratiosSum = ratios.Sum();
        var normalizedRatios = ratios.Select(r => r / ratiosSum).ToList();

        const double epsilon = 1e-10d;

        var entropy = -normalizedRatios.Sum(p =>
        {
            var value = Math.Max(p, epsilon);  // to avoid log(0)
            return value * Math.Log(value);    // - sum(p_i * log(p_i))
        });

        Debug.Assert(entropy > 0f);

        return entropy;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static double ComputeHarmonicMean(long[] memoryUsages)
    {
        var result = 0d;

        foreach (var value in memoryUsages)
        {
            Debug.Assert(value > 0);
            result += 1.0 / value;
        }

        return memoryUsages.Length / result;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private double ComputeAdaptiveScaling(int siloCount, int rebalancingCycle)
    {
        Debug.Assert(rebalancingCycle > 0);

        var cycleFactor = 1 - Math.Exp(-_options.CycleNumberWeight * rebalancingCycle);
        var siloFactor = 1 / (1 + _options.SiloNumberWeight * (siloCount - 1));

        return (double)(cycleFactor * siloFactor);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static List<(SiloAddress, SiloAddress)> FormSiloPairs(
        Dictionary<SiloAddress, ResourceStatistics> statistics)
    {
        var pairs = new List<(SiloAddress, SiloAddress)>();
        var sorted = statistics.OrderBy(x => x.Value.ActivationCount).ToList();

        var left = 0;
        var right = sorted.Count - 1;

        while (left < right)
        {
            pairs.Add((sorted[left].Key, sorted[right].Key));

            left++;
            right--;
        }

        if (left == right) // odd number of silos
        {
            pairs.Add((sorted[left].Key, sorted[left].Key)); // pair this silo with itself 
        }

        return pairs;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ThrowIfInvalidGrainKey()
    {
        if (this.GetPrimaryKeyLong() != IActivationRebalancerGrain.Key)
        {
            throw new InvalidOperationException(
                $"Creation of an activation with any key other than {IActivationRebalancerGrain.Key} is prohibited.");
        }
    }

    private void StartSession()
    {
        StopSession(StopReason.StartingSession);

        _sessionTimer = this.RegisterGrainTimer(RunRebalancingCycle, new()
        {
            DueTime = TimeSpan.Zero,
            Period = _options.SessionCyclePeriod
        });

        if (logger.IsEnabled(LogLevel.Trace))
        {
            logger.LogTrace("I have started a new rebalancing session.");
        };
    }

    private void StopSession(StopReason reason, TimeSpan? duration = null)
    {
        _previousEntropy = 0;
        _rebalancingCycle = 0;
        _staleCycles = 0;
        _sessionTimer?.Dispose();
        _sessionTimer = null;

        switch (reason)
        {
            case StopReason.StartingSession:
                {
                    _failedSessionCount = 0;
                    _disabledUntil = null;
                }
                break;
            case StopReason.FailedSession:
                {
                    _failedSessionCount++;
                    DisableFor(backoffProvider.Next(_failedSessionCount));
                }
                break;
            case StopReason.Suspend:
                {
                    _failedSessionCount = 0;
                    DisableFor(duration ?? Timeout.InfiniteTimeSpan);
                }
                break;
        }

        if (logger.IsEnabled(LogLevel.Trace))
        {
            logger.LogTrace("I have stopped my current rebalancing session.");
        };

        void DisableFor(TimeSpan duration)
        {
            var disableFor = timeProvider.GetUtcNow().Add(duration).DateTime;

            _disabledUntil = !_disabledUntil.HasValue ? disableFor :
                (disableFor > _disabledUntil ? disableFor : _disabledUntil);
        }
    }
}