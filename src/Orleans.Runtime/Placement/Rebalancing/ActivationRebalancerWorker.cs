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
using Orleans.Placement.Rebalancing;
using Orleans.Statistics;

#nullable enable

namespace Orleans.Runtime.Placement.Rebalancing;

// See: https://www.ledjonbehluli.com/posts/orleans_adaptive_rebalancing/

[KeepAlive, Immovable]
internal sealed partial class ActivationRebalancerWorker(
    TimeProvider timeProvider,
    DeploymentLoadPublisher loadPublisher,
    ILoggerFactory loggerFactory,
    ISiloStatusOracle siloStatusOracle,
    IInternalGrainFactory grainFactory,
    ILocalSiloDetails localSiloDetails,
    IOptions<ActivationRebalancerOptions> options,
    IFailedRebalancingSessionBackoffProvider backoffProvider)
        : Grain, IActivationRebalancerWorker, ISiloStatisticsChangeListener, IGrainMigrationParticipant
{
    private record struct ResourceStatistics(long MemoryUsage, int ActivationCount);

    [GenerateSerializer, Immutable, Alias("RebalancerState")]
    internal record struct RebalancerState(
        int StaleCycles, int FailedSessions,
        int RebalancingCycle, double LatestEntropy,
        DateTime? DisabledUntil, ImmutableArray<RebalancingStatistics> Statistics);

    private enum StopReason
    {
        /// <summary>
        /// A new session is about to start.
        /// </summary>
        SessionStarting,
        /// <summary>
        /// Current session has failed.
        /// </summary>
        SessionFailed,
        /// <summary>
        /// Current session has completed successfully till end
        /// </summary>
        SessionCompleted,
        /// <summary>
        /// Rebalancer was asked to suspend activity.
        /// </summary>
        RebalancerSuspended
    }

    private const string StateKey = "REBALANCER_STATE";

    private int _staleCycles;
    private int _failedSessions;
    private int _rebalancingCycle;
    private double _previousEntropy;
    private DateTime? _disabledUntil;
    private IGrainTimer? _sessionTimer;
    private IGrainTimer? _triggerTimer;
    private IGrainTimer? _monitorTimer;

    private readonly ActivationRebalancerOptions _options = options.Value;
    private readonly Dictionary<SiloAddress, ResourceStatistics> _siloStatistics = [];
    private readonly Dictionary<SiloAddress, RebalancingStatistics> _rebalancingStatistics = [];
    private readonly ILogger<ActivationRebalancerWorker> _logger = loggerFactory.CreateLogger<ActivationRebalancerWorker>();

    public override Task OnActivateAsync(CancellationToken cancellationToken)
    {
        _monitorTimer = this.RegisterGrainTimer(PeriodicallyReportToMonitor, new()
        {
            DueTime = TimeSpan.Zero,
            Period = IActivationRebalancerMonitor.WorkerReportPeriod,
        });

        _triggerTimer = this.RegisterGrainTimer(PeriodicallyTriggerRebalancing, new()
        {
            Interleave = true,
            Period = 0.5 * _options.SessionCyclePeriod, // make trigger-period twice as short as the session cycle-period.
            DueTime = _options.RebalancerDueTime
        });

        LogScheduledToStart(_options.RebalancerDueTime);

        loadPublisher.SubscribeToStatisticsChangeEvents(this);

        return Task.CompletedTask;
    }

    public override Task OnDeactivateAsync(DeactivationReason reason, CancellationToken cancellationToken)
    {
        loadPublisher.UnsubscribeStatisticsChangeEvents(this);
        return Task.CompletedTask;
    }

    public void OnDehydrate(IDehydrationContext context)
    {
        _rebalancingStatistics.Remove(localSiloDetails.SiloAddress); // remove stats as we are shutting-down

        context.TryAddValue<RebalancerState>(StateKey,
            new(_staleCycles, _failedSessions, _rebalancingCycle,
                _previousEntropy, _disabledUntil, [.. _rebalancingStatistics.Values]));
    }
    
    public void OnRehydrate(IRehydrationContext context)
    {
        if (context.TryGetValue<RebalancerState?>(StateKey, out var rebalancerState) &&
            rebalancerState is { } state)
        {
            _rebalancingCycle = state.RebalancingCycle;
            _staleCycles = state.StaleCycles;
            _failedSessions = state.FailedSessions;
            _previousEntropy = state.LatestEntropy;
            _disabledUntil = state.DisabledUntil;

            foreach (var statistics in state.Statistics)
            {
                _rebalancingStatistics.TryAdd(statistics.SiloAddress, statistics);
            }
        }
    }

    public void RemoveSilo(SiloAddress address)
    {
        _siloStatistics.Remove(address);
        _rebalancingStatistics.Remove(address);
    }

    public void SiloStatisticsChangeNotification(SiloAddress address, SiloRuntimeStatistics statistics)
    {
        ref var stats = ref CollectionsMarshal.GetValueRefOrAddDefault(_siloStatistics, address, out _);
        stats = new(statistics.EnvironmentStatistics.MemoryUsageBytes, statistics.ActivationCount);
    }

    public ValueTask<ImmutableArray<RebalancingStatistics>> GetStatistics() => new([.. _rebalancingStatistics.Values]);

    public ValueTask<SiloAddress> StartRebalancer() => new(localSiloDetails.SiloAddress);

    public Task ResumeRebalancing()
    {
        StartSession();
        return Task.CompletedTask;
    }

    public Task SuspendRebalancing(TimeSpan? duration)
    {
        StopSession(StopReason.RebalancerSuspended, duration);

        if (duration.HasValue)
        {
            LogSuspendedFor(duration.Value);
        }
        else
        {
            LogSuspended();
        }

        return Task.CompletedTask;
    }

    private async Task PeriodicallyReportToMonitor()
    {
        var tasks = new List<Task>();

        foreach (var silo in siloStatusOracle.GetActiveSilos())
        {
            tasks.Add(grainFactory.GetSystemTarget<IActivationRebalancerMonitor>
                (Constants.ActivationRebalancerMonitorType, silo)
                .Report(localSiloDetails.SiloAddress, [.. _rebalancingStatistics.Values]));
        }

        await Task.WhenAll(tasks);
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
            LogNotEnoughSilos();
            return;
        }

        var snapshot = _siloStatistics.ToDictionary();
        if (snapshot.Count < 2)
        {
            LogNotEnoughStatistics();
            return;
        }

        if (snapshot.Any(x => x.Value.MemoryUsage == 0))
        {
            LogInvalidSiloMemoryUsage(nameof(IEnvironmentStatisticsProvider));
            return;
        }

        _rebalancingCycle++;

        if (_staleCycles >= _options.MaxStaleCycles)
        {
            LogMaxStaleCyclesReached(_staleCycles);
            StopSession(StopReason.SessionFailed);

            return;
        }

        var totalActivations = snapshot.Sum(x => x.Value.ActivationCount);
        var meanMemoryUsage = ComputeHarmonicMean(snapshot.Select(x => x.Value.MemoryUsage).ToArray());
        var maximumEntropy = Math.Log(siloCount);
        var currentEntropy = ComputeEntropy(snapshot.Select(x => x.Value), totalActivations, meanMemoryUsage);
        var entropyDeviation = (maximumEntropy - currentEntropy) / maximumEntropy;

        if (entropyDeviation <= _options.AllowedEntropyDeviation)
        {
            // The deviation from maximum is practically considered "0" i.e: we've reached maximum.
            LogMaxEntropyDeviationReached(entropyDeviation, currentEntropy, maximumEntropy, _options.AllowedEntropyDeviation);
            StopSession(StopReason.SessionCompleted);

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

            LogInsufficientEntropyQuantum(entropyChange, _options.EntropyQuantum);

            _staleCycles++;
            _previousEntropy = currentEntropy;

            return;
        }

        if (_staleCycles > 0)
        {
            _staleCycles = 0;
            LogStaleCyclesReset();
        }

        if (_failedSessions > 0)
        {
            _failedSessions = 0;
            LogFailedSessionsReset();
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

            UpdateStatistics(lowSilo, highSilo, delta);
            LogSiloMigrations(delta, lowSilo, lowCount, lowCount + delta, highSilo, highCount, highCount - delta);
        }

        if (migrationTasks.Count > 0)
        {
            await Task.WhenAll(migrationTasks);
        }

        LogCycleOutcome(_rebalancingCycle, _staleCycles, _previousEntropy, currentEntropy, maximumEntropy, entropyDeviation);
        _previousEntropy = currentEntropy;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void UpdateStatistics(SiloAddress lowSilo, SiloAddress highSilo, int delta)
    {
        Debug.Assert(delta > 0);
        var now = timeProvider.GetUtcNow().DateTime;

        ref var lowStats = ref CollectionsMarshal.GetValueRefOrAddDefault(_rebalancingStatistics, lowSilo, out _);
        lowStats = new()
        {
            TimeStamp = now,
            SiloAddress = lowSilo,
            DispersedActivations = lowStats.DispersedActivations,
            AcquiredActivations = lowStats.AcquiredActivations + (ulong)delta
        };

        ref var highStats = ref CollectionsMarshal.GetValueRefOrAddDefault(_rebalancingStatistics, highSilo, out _);
        highStats = new()
        {
            TimeStamp = now,
            SiloAddress = highSilo,
            DispersedActivations = highStats.DispersedActivations + (ulong)delta,
            AcquiredActivations = highStats.AcquiredActivations
        };
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

    private void StartSession()
    {
        StopSession(StopReason.SessionStarting);

        _sessionTimer = this.RegisterGrainTimer(RunRebalancingCycle, new()
        {
            DueTime = TimeSpan.Zero,
            Period = _options.SessionCyclePeriod
        });

        LogSessionStarted();
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
            case StopReason.SessionStarting:
                {
                    _failedSessions = 0;
                    _disabledUntil = null;
                }
                break;
            case StopReason.SessionFailed:
                {
                    _failedSessions++;
                    DisableFor(backoffProvider.Next(_failedSessions));
                }
                break;
            case StopReason.SessionCompleted:
                {
                    _failedSessions = 0;
                    DisableFor(_options.SessionCyclePeriod);
                }
                break;
            case StopReason.RebalancerSuspended:
                {
                    _failedSessions = 0;
                    DisableFor(duration ?? Timeout.InfiniteTimeSpan);
                }
                break;
        }

        LogSessionStopped();

        void DisableFor(TimeSpan duration)
        {
            var disableFor = timeProvider.GetUtcNow().Add(duration).DateTime;

            _disabledUntil = !_disabledUntil.HasValue ? disableFor :
                (disableFor > _disabledUntil ? disableFor : _disabledUntil);
        }
    }
}