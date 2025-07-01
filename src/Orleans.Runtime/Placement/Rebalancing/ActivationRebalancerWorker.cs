using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Placement;
using Orleans.Placement.Rebalancing;

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
    IFailedSessionBackoffProvider backoffProvider)
        : Grain, IActivationRebalancerWorker, ISiloStatisticsChangeListener, IGrainMigrationParticipant
{
    private readonly record struct ResourceStatistics(long MemoryUsage, int ActivationCount);

    [GenerateSerializer, Immutable, Alias("RebalancerState")]
    internal readonly record struct RebalancerState(
        int StagnantCycles, int FailedSessions,
        int RebalancingCycle, double LatestEntropy, double EntropyDeviation,
        TimeSpan? SuspensionDuration, ImmutableArray<RebalancingStatistics> Statistics);

    private enum StopReason
    {
        /// <summary>
        /// A new session is about to start.
        /// </summary>
        SessionStarting,
        /// <summary>
        /// Current session has stagnated.
        /// </summary>
        SessionStagnated,
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

    private int _stagnantCycles;
    private int _failedSessions;
    private int _rebalancingCycle;
    private double _previousEntropy;
    private double _entropyDeviation;
    private long _suspendedUntilTs;
    private IGrainTimer? _sessionTimer;
    private IGrainTimer? _triggerTimer;
    private IGrainTimer? _monitorTimer;

    private readonly ActivationRebalancerOptions _options = options.Value;
    private readonly Dictionary<SiloAddress, ResourceStatistics> _siloStatistics = [];
    private readonly Dictionary<SiloAddress, RebalancingStatistics> _rebalancingStatistics = [];
    private readonly ILogger<ActivationRebalancerWorker> _logger = loggerFactory.CreateLogger<ActivationRebalancerWorker>();

    private TimeSpan? RemainingSuspensionDuration => Runtime.TimeProvider.GetElapsedTime(Runtime.TimeProvider.GetTimestamp(), _suspendedUntilTs) switch
    {
        { } result when result > TimeSpan.Zero => result,
        _ => null
    };

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
        context.TryAddValue<RebalancerState>(StateKey,
            new(_stagnantCycles, _failedSessions, _rebalancingCycle,
                _previousEntropy, _entropyDeviation, RemainingSuspensionDuration, [.. _rebalancingStatistics.Values]));
    }
    
    public void OnRehydrate(IRehydrationContext context)
    {
        if (context.TryGetValue<RebalancerState>(StateKey, out var rebalancerState) &&
            rebalancerState is { } state)
        {
            _rebalancingCycle = state.RebalancingCycle;
            _stagnantCycles = state.StagnantCycles;
            _failedSessions = state.FailedSessions;
            _previousEntropy = state.LatestEntropy;
            _entropyDeviation = state.EntropyDeviation;

            foreach (var statistics in state.Statistics)
            {
                if (siloStatusOracle.IsDeadSilo(statistics.SiloAddress))
                {
                    continue;
                }

                _rebalancingStatistics.TryAdd(statistics.SiloAddress, statistics);
            }

            if (state.SuspensionDuration is { } value)
            {
                SuspendFor(value);
            }
        }
    }

    void ISiloStatisticsChangeListener.RemoveSilo(SiloAddress silo)
    {
        GrainContext.Scheduler.QueueAction(() =>
        {
            _siloStatistics.Remove(silo);
            _rebalancingStatistics.Remove(silo); // Remove that silo's rebalancing stats, as it has been removed.
        });
    }

    void ISiloStatisticsChangeListener.SiloStatisticsChangeNotification(SiloAddress address, SiloRuntimeStatistics statistics)
    {
        GrainContext.Scheduler.QueueAction(()
            => _siloStatistics[address] = new(statistics.EnvironmentStatistics.MemoryUsageBytes, statistics.ActivationCount));
    }

    public ValueTask<RebalancingReport> GetReport() => new(BuildReport());

    public async Task ResumeRebalancing()
    {
        StartSession();
        await ReportAllMonitors(CancellationToken.None);
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

        await ReportAllMonitors(CancellationToken.None);
    }

    private async Task ReportAllMonitors(CancellationToken cancellationToken)
    {
        var tasks = new List<Task>();
        var report = BuildReport();
       
        foreach (var silo in siloStatusOracle.GetActiveSilos())
        {
            tasks.Add(grainFactory.GetSystemTarget<IActivationRebalancerMonitor>
                (Constants.ActivationRebalancerMonitorType, silo).Report(report));
        }

        await Task.WhenAll(tasks).WaitAsync(cancellationToken);
    }

    private RebalancingReport BuildReport()
    {
        var suspensionRemaining = RemainingSuspensionDuration;

        return new RebalancingReport()
        {
            Host = localSiloDetails.SiloAddress,
            Status = suspensionRemaining is { } ? RebalancerStatus.Suspended : RebalancerStatus.Executing,
            SuspensionDuration = suspensionRemaining,
            ClusterImbalance = _entropyDeviation,
            Statistics = [.. _rebalancingStatistics.Values]
        };
    }

    private Task TriggerRebalancing()
    {
        if (_sessionTimer != null) 
        {
            return Task.CompletedTask;
        }

        if (RemainingSuspensionDuration.HasValue)
        {
            return Task.CompletedTask;
        }

        StartSession();
        return Task.CompletedTask;
    }

    private async Task RunRebalancingCycle(CancellationToken cancellationToken)
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
            LogInvalidSiloMemory();
            return;
        }

        _rebalancingCycle++;

        if (_stagnantCycles >= _options.MaxStagnantCycles)
        {
            LogMaxStagnantCyclesReached(_stagnantCycles);
            StopSession(StopReason.SessionStagnated);

            return;
        }

        var totalActivations = snapshot.Sum(x => x.Value.ActivationCount);
        var meanMemoryUsage = ComputeHarmonicMean(snapshot.Values);
        var maximumEntropy = Math.Log(siloCount);
        var currentEntropy = ComputeEntropy(snapshot.Values, totalActivations, meanMemoryUsage);
        var allowedDeviation = ComputeAllowedEntropyDeviation(totalActivations);
        var entropyDeviation = (maximumEntropy - currentEntropy) / maximumEntropy;

        _entropyDeviation = entropyDeviation;

        if (entropyDeviation < allowedDeviation)
        {
            // The deviation from maximum is practically considered "0" i.e: we've reached maximum.
            LogMaxEntropyDeviationReached(entropyDeviation, currentEntropy, maximumEntropy, allowedDeviation);
            StopSession(StopReason.SessionCompleted);

            return;
        }

        // We use the normalized, absolute entropy change, because it is more useful for understanding how significant
        // the change is, relative to the maximum possible. Values closer to 1 reflect higher significance than those closer to 0.
        // Since max entropy is a function of the natural log of the cluster's size, this value is very robust against changes
        // in silo number within the cluster.

        var entropyChange = Math.Abs((currentEntropy - _previousEntropy) / maximumEntropy);
        Debug.Assert(entropyChange is >= 0 and <= 1);

        if (entropyChange < _options.EntropyQuantum)
        {
            // Entropy change is too low to be considered an improvement, chances are we are reaching the maximum, or the system
            // is dynamically changing too fast i.e. new activations are being created at a high rate with an imbalanced distribution,
            // we need to start "cooling-down". As a matter of fact, entropy could also become negative if the current entropy is less
            // than the previous, due to many activation changes happening during this and the previous cycle.

            LogInsufficientEntropyQuantum(entropyChange, _options.EntropyQuantum);

            _stagnantCycles++;
            _previousEntropy = currentEntropy;

            return;
        }

        if (_stagnantCycles > 0)
        {
            _stagnantCycles = 0;
            LogStagnantCyclesReset();
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

            if (delta > _options.ActivationMigrationCountLimit)
            {
                delta = _options.ActivationMigrationCountLimit;
            }

            migrationTasks.Add(grainFactory
                .GetSystemTarget<ISiloControl>(Constants.SiloControlType, highSilo)
                .MigrateRandomActivations(lowSilo, delta));

            UpdateStatistics(lowSilo, highSilo, delta);
            LogSiloMigrations(delta, lowSilo, lowCount, lowCount + delta, highSilo, highCount, highCount - delta);
        }

        if (migrationTasks.Count > 0)
        {
            await Task.WhenAll(migrationTasks).WaitAsync(cancellationToken);
        }

        LogCycleOutcome(_rebalancingCycle, _stagnantCycles, _previousEntropy, currentEntropy, maximumEntropy, entropyDeviation);
        _previousEntropy = currentEntropy;
    }

    private void UpdateStatistics(SiloAddress lowSilo, SiloAddress highSilo, int delta)
    {
        Debug.Assert(delta > 0);
        var now = Runtime.TimeProvider.GetUtcNow().DateTime;

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

    private double ComputeAllowedEntropyDeviation(int totalActivations)
    {
        if (!_options.ScaleAllowedEntropyDeviation || totalActivations < _options.ScaledEntropyDeviationActivationThreshold)
        {
            return _options.AllowedEntropyDeviation;
        }

        Debug.Assert(totalActivations > 0);

        var logFactor = (int)Math.Log10(totalActivations / _options.ScaledEntropyDeviationActivationThreshold);
        var adjustedDeviation = _options.AllowedEntropyDeviation * Math.Pow(10, logFactor);

        return Math.Min(adjustedDeviation, ActivationRebalancerOptions.MAX_SCALED_ENTROPY_DEVIATION);
    }

    private double ComputeAdaptiveScaling(int siloCount, int rebalancingCycle)
    {
        Debug.Assert(rebalancingCycle > 0);

        var cycleFactor = 1 - Math.Exp(-_options.CycleNumberWeight * rebalancingCycle);
        var siloFactor = 1 / (1 + _options.SiloNumberWeight * (siloCount - 1));

        return (double)(cycleFactor * siloFactor);
    }

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
        _stagnantCycles = 0;
        _sessionTimer?.Dispose();
        _sessionTimer = null;

        switch (reason)
        {
            case StopReason.SessionStarting:
                {
                    _failedSessions = 0;
                    _suspendedUntilTs = 0;
                }
                break;
            case StopReason.SessionStagnated:
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
                        _suspendedUntilTs = long.MaxValue;
                    }
                }
                break;
        }

        LogSessionStopped();
    }

    private void SuspendFor(TimeSpan duration)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(duration, TimeSpan.Zero);
        var now = Runtime.TimeProvider.GetTimestamp();
        var suspendUntil = now + (long)(Runtime.TimeProvider.TimestampFrequency * duration.TotalSeconds);
        if (suspendUntil < now)
        {
            // Clamp overflow at max value.
            suspendUntil = long.MaxValue;
        }

        _suspendedUntilTs = Math.Max(_suspendedUntilTs, suspendUntil);
    }
}
