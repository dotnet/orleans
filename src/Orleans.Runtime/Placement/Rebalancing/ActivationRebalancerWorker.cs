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
    DeploymentLoadPublisher loadPublisher,
    ILoggerFactory loggerFactory,
    ISiloStatusOracle siloStatusOracle,
    IInternalGrainFactory grainFactory,
    ILocalSiloDetails localSiloDetails,
    IOptions<ActivationRebalancerOptions> options,
    IFailedRebalancingSessionBackoffProvider backoffProvider)
        : Grain, IActivationRebalancerWorker, ISiloStatisticsChangeListener, IGrainMigrationParticipant
{
    private readonly record struct ResourceStatistics(long MemoryUsage, int ActivationCount);

    [GenerateSerializer, Immutable, Alias("RebalancerState")]
    internal readonly record struct RebalancerState(
        int StaleCycles, int FailedSessions,
        int RebalancingCycle, double LatestEntropy, double Imabalance,
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
    private double _imbalance;
    private DateTime? _suspendedUntil;
    private IGrainTimer? _sessionTimer;
    private IGrainTimer? _triggerTimer;
    private IGrainTimer? _monitorTimer;

    private readonly ActivationRebalancerOptions _options = options.Value;
    private readonly Dictionary<SiloAddress, ResourceStatistics> _siloStatistics = [];
    private readonly Dictionary<SiloAddress, RebalancingStatistics> _rebalancingStatistics = [];
    private readonly ILogger<ActivationRebalancerWorker> _logger = loggerFactory.CreateLogger<ActivationRebalancerWorker>();

    private DateTime UtcNow
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => Runtime.TimeProvider.GetUtcNow().UtcDateTime;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool IsSuspended(DateTime? suspendedUntil) =>
        suspendedUntil.HasValue && suspendedUntil.Value > UtcNow;

    public override Task OnActivateAsync(CancellationToken cancellationToken)
    {
        _monitorTimer = this.RegisterGrainTimer(ReportAllMonitors, new()
        {
            DueTime = TimeSpan.Zero,
            Period = IActivationRebalancerMonitor.WorkerReportPeriod,
        });

        _triggerTimer = this.RegisterGrainTimer(TriggerRebalancing, new()
        {
            Interleave = true,
            Period = 0.5 * _options.SessionCyclePeriod, // Make trigger-period half that of the session cycle-period.
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
        _rebalancingStatistics.Remove(localSiloDetails.SiloAddress);   // Remove this silo's rebalancing stats, as we are shutting down.

        context.TryAddValue<RebalancerState>(StateKey,
            new(_staleCycles, _failedSessions, _rebalancingCycle,
                _previousEntropy, _imbalance, _suspendedUntil, [.. _rebalancingStatistics.Values]));
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
            _suspendedUntil = state.DisabledUntil;
            _imbalance = state.Imabalance;

            foreach (var statistics in state.Statistics)
            {
                _rebalancingStatistics.TryAdd(statistics.SiloAddress, statistics);
            }
        }
    }

    public void RemoveSilo(SiloAddress silo)
    {
        _siloStatistics.Remove(silo);
        _rebalancingStatistics.Remove(silo); // Remove that silo's rebalancing stats, as it has been removed.
    }

    public void SiloStatisticsChangeNotification(SiloAddress address, SiloRuntimeStatistics statistics)
    {
        ref var stats = ref CollectionsMarshal.GetValueRefOrAddDefault(_siloStatistics, address, out _);
        stats = new(statistics.EnvironmentStatistics.MemoryUsageBytes, statistics.ActivationCount);
    }

    public ValueTask<RebalancingReport> GetReport() => new(BuildReport());

    public async Task ResumeRebalancing()
    {
        StartSession();
        await ReportAllMonitors();
    }

    public async Task SuspendRebalancing(TimeSpan? duration)
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

        await ReportAllMonitors();
    }

    private async Task ReportAllMonitors()
    {
        var tasks = new List<Task>();
        var report = BuildReport();
       
        foreach (var silo in siloStatusOracle.GetActiveSilos())
        {
            tasks.Add(grainFactory.GetSystemTarget<IActivationRebalancerMonitor>
                (Constants.ActivationRebalancerMonitorType, silo).Report(report));
        }

        await Task.WhenAll(tasks);
    }

    private RebalancingReport BuildReport()
    {
        var until = _suspendedUntil; // take a copy since _triggerTimer interleaves
        var suspended = IsSuspended(until);

        return new RebalancingReport()
        {
            Host = localSiloDetails.SiloAddress,
            Status = suspended ? RebalancerStatus.Suspended : RebalancerStatus.Executing,
            SuspensionDuration = suspended ? until!.Value - UtcNow : null,
            ClusterImbalance = _imbalance,
            Statistics = [.. _rebalancingStatistics.Values]
        };
    }

    private Task TriggerRebalancing()
    {
        if (_sessionTimer != null) 
        {
            return Task.CompletedTask;
        }

        if (IsSuspended(_suspendedUntil))
        {
            return Task.CompletedTask;
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
            LogInvalidSiloMemory(nameof(IEnvironmentStatisticsProvider));
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
        var meanMemoryUsage = ComputeHarmonicMean(snapshot.Values);
        var maximumEntropy = Math.Log(siloCount);
        var currentEntropy = ComputeEntropy(snapshot.Values, totalActivations, meanMemoryUsage);
        var entropyDeviation = (maximumEntropy - currentEntropy) / maximumEntropy;

        _imbalance = entropyDeviation;

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

        var entropyChange = Math.Abs((currentEntropy - _previousEntropy) / maximumEntropy);
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
        var now = UtcNow;

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
        Dictionary<SiloAddress, ResourceStatistics>.ValueCollection values,
        int totalActivations, double meanMemoryUsage)
    {
        Debug.Assert(totalActivations > 0);
        Debug.Assert(meanMemoryUsage > 0);

        var ratios = values.Select(x =>
            // p_i = (n_i / N) * (m_i / M_m)
            ((double)x.ActivationCount / totalActivations) * (x.MemoryUsage / meanMemoryUsage));

        var ratiosSum = ratios.Sum();
        var normalizedRatios = ratios.Select(r => r / ratiosSum);

        const double epsilon = 1e-10d;

        var entropy = -normalizedRatios.Sum(p =>
        {
            var value = Math.Max(p, epsilon);  // Avoid log(0)
            return value * Math.Log(value);    // - sum(p_i * log(p_i))
        });

        Debug.Assert(entropy > 0);

        return entropy;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static double ComputeHarmonicMean(Dictionary<SiloAddress, ResourceStatistics>.ValueCollection values)
    {
        var result = 0d;

        foreach (var value in values)
        {
            var count = value.ActivationCount;
            Debug.Assert(count > 0);
            result += 1.0 / count;
        }

        return values.Count / result;
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

        if (left == right) // Odd number of silos
        {
            pairs.Add((sorted[left].Key, sorted[left].Key)); // Pair this silo with itself 
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
                    _suspendedUntil = null;
                }
                break;
            case StopReason.SessionFailed:
                {
                    _failedSessions++;
                    SuspendFor(backoffProvider.Next(_failedSessions));
                }
                break;
            case StopReason.SessionCompleted:
                {
                    _failedSessions = 0;
                    SuspendFor(_options.SessionCyclePeriod);
                }
                break;
            case StopReason.RebalancerSuspended:
                {
                    _failedSessions = 0;
                    if (duration.HasValue)
                    {
                        SuspendFor(duration.Value);
                    }
                    else
                    {
                        _suspendedUntil = DateTime.MaxValue;
                    }
                }
                break;
        }

        LogSessionStopped();

        void SuspendFor(TimeSpan duration)
        {
            var suspendUntil = UtcNow.Add(duration);

            _suspendedUntil = !_suspendedUntil.HasValue ? suspendUntil :
                (suspendUntil > _suspendedUntil ? suspendUntil : _suspendedUntil);
        }
    }
}