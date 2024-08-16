using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Placement.Rebalancing;

namespace Orleans.Runtime.Placement.Rebalancing;

#nullable enable

// See: https://www.ledjonbehluli.com/posts/orleans_adaptive_rebalancing/
[KeepAlive]
internal sealed class ActivationRebalancerGrain(
    IOptions<ActivationRebalancerOptions> rebalancerOptions,
    IOptions<DeploymentLoadPublisherOptions> publisherOptions,
    ILogger<ActivationRebalancerGrain> logger,
    ISiloStatusOracle siloStatusOracle,
    IInternalGrainFactory grainFactory,
    DeploymentLoadPublisher loadPublisher)
        : Grain, IActivationRebalancerGrain, ISiloStatisticsChangeListener
{
    private record struct ResourceStatistics(long MemoryUsage, int ActivationCount);

    private int _rebalancingCycle;
    private int _staleCycles;
    private double _previousEntropy;
    private bool _hasDueTimeElapsed;
    private DateTime? _disabledUntil;
    private DateTime? _firstTriggerTime;
    private IGrainTimer? _sessionTimer;
    private IGrainTimer? _triggerTimer;
    private RebalancingParameters _parameters;

    private readonly TimeSpan _rebalancerDueTime = rebalancerOptions.Value.RebalancerDueTime;
    private readonly TimeSpan _publisherRefreshTime = publisherOptions.Value.DeploymentLoadPublisherRefreshTime;
    private readonly Dictionary<SiloAddress, ResourceStatistics> _siloStatistics = [];

    private bool HasSession
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _sessionTimer != null;
    }

    private bool HasStopped
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _disabledUntil.HasValue && _disabledUntil.Value > DateTime.UtcNow;
    }

    private bool DueTimeElapsed
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            if (_hasDueTimeElapsed)
            {
                return true;
            }

            var elapsed = _firstTriggerTime.HasValue && DateTime.UtcNow >= _firstTriggerTime.Value.Add(_rebalancerDueTime);
            _hasDueTimeElapsed = elapsed;

            return _hasDueTimeElapsed;
        }
    }

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

    public Task ResumeRebalancing()
    {
        if (logger.IsEnabled(LogLevel.Trace))
        {
            logger.LogTrace("I have been told to resume rebalancing.");
        };

        StartSession(_parameters);
        return Task.CompletedTask;
    }

    public Task SuspendRebalancing(TimeSpan? duration)
    {
        StopSession(duration);

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

    public Task TriggerRebalancing(RebalancingParameters parameters)
    {
        ActivationRebalancerOptionsValidator.ThrowIfInvalid(parameters, _publisherRefreshTime);

        if (_triggerTimer is null)
        {
            _parameters = parameters;
            _firstTriggerTime = DateTime.UtcNow;
            _triggerTimer = this.RegisterGrainTimer(() =>
                PeriodicallyTriggerRebalancing(_parameters), new()
                {
                    DueTime = _rebalancerDueTime,
                    Period = 2 * _parameters.SessionCyclePeriod // make trigger-period twice as long as the (session) cycle-period.
                });

            return Task.CompletedTask;
        }

        if (!DueTimeElapsed)
        {
            if (logger.IsEnabled(LogLevel.Trace))
            {
                logger.LogTrace(
                    "A request for rebalancing has arrived, but my due time has not yet elapsed. " +
                    "I will start with a session once time is due.");
            }

            return Task.CompletedTask;
        }

        if (logger.IsEnabled(LogLevel.Trace))
        {
            logger.LogTrace(
                "A request to perform rebalancing has arrived. " +
                "I will drop any on-going session, and will proceed with this one instead.");
        }

        StartSession(parameters);
        return Task.CompletedTask;
    }

    private Task PeriodicallyTriggerRebalancing(RebalancingParameters parameters)
    {
        if (HasSession)
        {
            if (logger.IsEnabled(LogLevel.Trace))
            {
                logger.LogTrace(
                    "A request for rebalancing has arrived, but I am currently in a rebalancing session. " +
                    "I will ignore this request, and will proceed with my session.");
            }

            return Task.CompletedTask;
        }

        if (HasStopped)
        {
            if (logger.IsEnabled(LogLevel.Trace))
            {
                Debug.Assert(_disabledUntil.HasValue);
                var durationLeft = _disabledUntil.Value - DateTime.UtcNow;

                logger.LogTrace(
                    "A request for rebalancing has arrived, but I have been told to suspend rebalancing. " +
                    "I will ignore this request until {Duration} has passed, or I have been told to resume again.",
                    durationLeft);
            }
        }

        StartSession(parameters);
        return Task.CompletedTask;
    }

    private void StartSession(RebalancingParameters parameters)
    {
        StopSession();

        _sessionTimer = this.RegisterGrainTimer(() => RunRebalancingCycle(parameters), new()
        {
            DueTime = TimeSpan.Zero,
            Period = parameters.SessionCyclePeriod
        });

        if (logger.IsEnabled(LogLevel.Trace))
        {
            logger.LogTrace(
                "I have started a rebalancing session and will run according to {Parameters}",
                parameters.ToString());
        };
    }

    private async Task RunRebalancingCycle(RebalancingParameters parameters)
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

        _rebalancingCycle++;

        if (_staleCycles > parameters.MaxStaleCycles)
        {
            if (logger.IsEnabled(LogLevel.Trace))
            {
                logger.LogTrace(
                    "The current rebalancing session has stopped due to {StaleCycles} stale " +
                    "cycles having passed, while the maximum allowed is {MaxStaleCycles}",
                    _staleCycles, parameters.MaxStaleCycles);
            }

            StopSession();
            return;
        }

        var totalActivations = snapshot.Sum(x => x.Value.ActivationCount);
        var meanMemoryUsage = ComputeHarmonicMean(snapshot.Select(x => x.Value.MemoryUsage).ToArray());
        var maximumEntropy = Math.Log(siloCount);
        var currentEntropy = ComputeEntropy(snapshot.Select(x => x.Value), totalActivations, meanMemoryUsage);
        var entropyDeviation = (maximumEntropy - currentEntropy) / maximumEntropy;

        if (entropyDeviation <= parameters.MaxEntropyDeviation)
        {
            // The deviation from maximum is practically considered "0" i.e: we've reached maximum.
            if (logger.IsEnabled(LogLevel.Trace))
            {
                logger.LogTrace(
                    "The current rebalancing session has stopped due to a {EntropyDeviation} " +
                    "entropy deviation between the current {CurrentEntropy} and maximum possible {MaximumEntropy}. " +
                    "The difference is less than the required {MaxEntropyDeviation} deviation.",
                    entropyDeviation, currentEntropy, maximumEntropy, parameters.MaxEntropyDeviation);
            }

            StopSession();
            return;
        }

        var entropyDifference = currentEntropy - _previousEntropy;
        if (entropyDifference < parameters.EntropyQuantum)
        {
            // Entropy difference is too low to be considered an improvement, chances are we are reaching the maximum, or the system
            // is dynamically changing too fast i.e. new activations are being created at a high rate, we need to start "cooling-down".
            // As a matter of fact, entropy could also become negative if the current entropy is less than the previous, due to many
            // activation changes happening during this and the previous cycle.
            if (logger.IsEnabled(LogLevel.Trace))
            {
                logger.LogTrace(
                    "The change in entropy {EntropyDifference} is less than the quantum {EntropyQuantum}. " +
                    "This is practically not considered an improvement, therefor this cycle will be marked as stale.",
                    entropyDifference, parameters.EntropyQuantum);
            }

            _staleCycles++;
            return;
        }

        var idealDistributions = snapshot.Select(x => new ValueTuple<SiloAddress, double>
            // n_i = (N / S) * (M_avg / m_i)
            (x.Key, ((double)totalActivations / siloCount) * (meanMemoryUsage / x.Value.MemoryUsage)))
            .ToDictionary();

        var alpha = currentEntropy / maximumEntropy;
        var scalingFactor = ComputeAdaptiveScaling(ref parameters, siloCount, _rebalancingCycle);
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

            var highCount = snapshot[highSilo].ActivationCount;
            if (delta > highCount)
            {
                delta = highCount;
            }

            migrationTasks.Add(grainFactory
                .GetSystemTarget<ISiloControl>(Constants.SiloControlType, highSilo)
                .MigrateRandomActivations(lowSilo, delta));

            if (logger.IsEnabled(LogLevel.Trace))
            {
                var lowCount = snapshot[lowSilo].ActivationCount;

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
    private static double ComputeEntropy(
        IEnumerable<ResourceStatistics> statistics,
        int totalActivations, double meanMemoryUsage)
    {
        Debug.Assert(totalActivations > 0);
        Debug.Assert(meanMemoryUsage > 0f);

        var ratios = statistics.Select(x =>
            // p_i = (n_i / N) * (m_i / M_avg)
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
            result += 1.0 / value;
        }

        return memoryUsages.Length / result;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static double ComputeAdaptiveScaling(
        ref readonly RebalancingParameters parameters,
        int siloCount, int rebalancingCycle)
    {
        Debug.Assert(rebalancingCycle > 0);

        var cycleFactor = 1 - Math.Exp(-parameters.CycleNumberWeight * rebalancingCycle);
        var siloFactor = 1 / (1 + parameters.SiloNumberWeight * (siloCount - 1));

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

    public void RemoveSilo(SiloAddress address) => _siloStatistics.Remove(address);

    public void SiloStatisticsChangeNotification(SiloAddress address, SiloRuntimeStatistics statistics)
    {
        ref var stats = ref CollectionsMarshal.GetValueRefOrAddDefault(_siloStatistics, address, out _);
        stats = new(statistics.EnvironmentStatistics.MemoryUsageBytes, statistics.ActivationCount);
    }

    private void StopSession(TimeSpan? duration = null)
    {
        _previousEntropy = 0;
        _rebalancingCycle = 0;
        _staleCycles = 0;
        _disabledUntil = duration.HasValue ? DateTime.UtcNow.Add(duration.Value) : DateTime.MaxValue;
        _sessionTimer?.Dispose();
        _sessionTimer = null;
    }
}
