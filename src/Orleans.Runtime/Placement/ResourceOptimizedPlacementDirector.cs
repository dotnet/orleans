using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;
using Orleans.Runtime.Configuration.Options;

namespace Orleans.Runtime.Placement;

internal sealed class ResourceOptimizedPlacementDirector : IPlacementDirector, ISiloStatisticsChangeListener
{
    readonly record struct ResourceStatistics(float? CpuUsage, float? AvailableMemory, long? MemoryUsage, long? TotalPhysicalMemory);

    Task<SiloAddress> _cachedLocalSilo;

    readonly ILocalSiloDetails _localSiloDetails;
    readonly ResourceOptimizedPlacementOptions _options;
    readonly ConcurrentDictionary<SiloAddress, ResourceStatistics> siloStatistics = [];

    readonly DualModeKalmanFilter<float> _cpuUsageFilter = new();
    readonly DualModeKalmanFilter<float> _availableMemoryFilter = new();
    readonly DualModeKalmanFilter<long> _memoryUsageFilter = new();

    public ResourceOptimizedPlacementDirector(
        ILocalSiloDetails localSiloDetails,
        DeploymentLoadPublisher deploymentLoadPublisher,
        IOptions<ResourceOptimizedPlacementOptions> options)
    {
        _localSiloDetails = localSiloDetails;
        _options = options.Value;
        deploymentLoadPublisher?.SubscribeToStatisticsChangeEvents(this);
    }

    public Task<SiloAddress> OnAddActivation(PlacementStrategy strategy, PlacementTarget target, IPlacementContext context)
    {
        var compatibleSilos = context.GetCompatibleSilos(target);

        if (IPlacementDirector.GetPlacementHint(target.RequestContextData, compatibleSilos) is { } placementHint)
        {
            return Task.FromResult(placementHint);
        }

        if (compatibleSilos.Length == 0)
        {
            throw new SiloUnavailableException($"Cannot place grain with Id = [{target.GrainIdentity}], because there are no compatible silos.");
        }

        if (compatibleSilos.Length == 1)
        {
            return Task.FromResult(compatibleSilos[0]);
        }

        if (siloStatistics.IsEmpty)
        {
            return Task.FromResult(compatibleSilos[Random.Shared.Next(compatibleSilos.Length)]);
        }

        var bestCandidate = GetBestSiloCandidate(compatibleSilos);
        if (IsLocalSiloPreferable(context, compatibleSilos, bestCandidate.Value))
        {
            return _cachedLocalSilo ??= Task.FromResult(context.LocalSilo);
        }

        return Task.FromResult(bestCandidate.Key);
    }

    KeyValuePair<SiloAddress, float> GetBestSiloCandidate(SiloAddress[] compatibleSilos)
    {
        List<KeyValuePair<SiloAddress, ResourceStatistics>> relevantSilos = [];
        foreach (var silo in compatibleSilos)
        {
            if (siloStatistics.TryGetValue(silo, out var stats))
            {
                relevantSilos.Add(new(silo, stats));
            }
        }

        int chooseFrom = (int)Math.Ceiling(Math.Sqrt(relevantSilos.Count));
        Dictionary<SiloAddress, float> chooseFromSilos = [];

        while (chooseFromSilos.Count < chooseFrom)
        {
            int index = Random.Shared.Next(relevantSilos.Count);
            var pickedSilo = relevantSilos[index];

            relevantSilos.RemoveAt(index);

            float score = CalculateScore(pickedSilo.Value);
            chooseFromSilos.Add(pickedSilo.Key, score);
        }

        return chooseFromSilos.OrderByDescending(kv => kv.Value).First();
    }

    float CalculateScore(ResourceStatistics stats)
    {
        float normalizedCpuUsage = stats.CpuUsage.HasValue ? stats.CpuUsage.Value / 100f : 0f;

        if (stats.TotalPhysicalMemory.HasValue)
        {
            float normalizedAvailableMemory = stats.AvailableMemory.HasValue ? stats.AvailableMemory.Value / stats.TotalPhysicalMemory.Value : 0f;
            float normalizedMemoryUsage = stats.MemoryUsage.HasValue ? stats.MemoryUsage.Value / stats.TotalPhysicalMemory.Value : 0f;
            float normalizedTotalPhysicalMemory = stats.TotalPhysicalMemory.HasValue ? stats.TotalPhysicalMemory.Value / (1024 * 1024 * 1024) : 0f;

            return _options.CpuUsageWeight * normalizedCpuUsage +
                   _options.AvailableMemoryWeight * normalizedAvailableMemory +
                   _options.MemoryUsageWeight * normalizedMemoryUsage +
                   _options.TotalPhysicalMemoryWeight * normalizedTotalPhysicalMemory;
        }

        return _options.CpuUsageWeight * normalizedCpuUsage;
    }

    bool IsLocalSiloPreferable(IPlacementContext context, SiloAddress[] compatibleSilos, float bestCandidateScore)
    {
        if (context.LocalSiloStatus != SiloStatus.Active ||
           !compatibleSilos.Contains(context.LocalSilo))
        {
            return false;
        }

        if (siloStatistics.TryGetValue(context.LocalSilo, out var localStats))
        {
            float localScore = CalculateScore(localStats);
            float localScoreMargin = localScore * _options.LocalSiloPreferenceMargin;

            if (localScore + localScoreMargin >= bestCandidateScore)
            {
                return true;
            }
        }

        return false;
    }

    public void RemoveSilo(SiloAddress address)
         => siloStatistics.TryRemove(address, out _);

    public void SiloStatisticsChangeNotification(SiloAddress address, SiloRuntimeStatistics statistics)
        => siloStatistics.AddOrUpdate(
            key: address,
            addValue: new ResourceStatistics(
                statistics.CpuUsage,
                statistics.AvailableMemory,
                statistics.MemoryUsage,
                statistics.TotalPhysicalMemory),
            updateValueFactory: (_, _) =>
            {
                float estimatedCpuUsage = _cpuUsageFilter.Filter(statistics.CpuUsage);
                float estimatedAvailableMemory = _availableMemoryFilter.Filter(statistics.AvailableMemory);
                long estimatedMemoryUsage = _memoryUsageFilter.Filter(statistics.MemoryUsage);

                return new ResourceStatistics(
                    estimatedCpuUsage,
                    estimatedAvailableMemory,
                    estimatedMemoryUsage,
                    statistics.TotalPhysicalMemory);
            });

    // details: https://www.ledjonbehluli.com/posts/orleans_resource_placement_kalman/
    sealed class DualModeKalmanFilter<T> where T : unmanaged, INumber<T>
    {
        readonly KalmanFilter _slowFilter = new(T.Zero);
        readonly KalmanFilter _fastFilter = new(T.CreateChecked(0.01));
        
        FilterRegime _regime = FilterRegime.Slow;

        enum FilterRegime
        {
            Slow,
            Fast
        }

        public T Filter(T? measurement)
        {
            T _measurement = measurement ?? T.Zero;

            T slowEstimate = _slowFilter.Filter(_measurement);
            T fastEstimate = _fastFilter.Filter(_measurement);

            if (_measurement > slowEstimate)
            {
                if (_regime == FilterRegime.Slow)
                {
                    _regime = FilterRegime.Fast;
                    _fastFilter.SetState(_measurement, T.Zero);
                    fastEstimate = _fastFilter.Filter(_measurement);
                }

                return fastEstimate;
            }
            else
            {
                if (_regime == FilterRegime.Fast)
                {
                    _regime = FilterRegime.Slow;
                    _slowFilter.SetState(_fastFilter.PriorEstimate, _fastFilter.PriorErrorCovariance);
                    slowEstimate = _slowFilter.Filter(_measurement);
                }

                return slowEstimate;
            }
        }

        sealed class KalmanFilter(T processNoiseCovariance)
        {
            readonly T _processNoiseCovariance = processNoiseCovariance;

            public T PriorEstimate { get; private set; } = T.Zero;
            public T PriorErrorCovariance { get; private set; } = T.One;

            public void SetState(T estimate, T errorCovariance)
            {
                PriorEstimate = estimate;
                PriorErrorCovariance = errorCovariance;
            }

            public T Filter(T measurement)
            {
                T estimate = PriorEstimate;
                T errorCovariance = PriorErrorCovariance + _processNoiseCovariance;

                T gain = errorCovariance / (errorCovariance + T.One);
                T newEstimate = estimate + gain * (measurement - estimate);
                T newErrorCovariance = (T.One - gain) * errorCovariance;

                PriorEstimate = newEstimate;
                PriorErrorCovariance = newErrorCovariance;

                return newEstimate;
            }
        }
    }
}
