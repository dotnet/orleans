using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;
using Orleans.Runtime.Configuration.Options;

namespace Orleans.Runtime.Placement;

// details: https://www.ledjonbehluli.com/posts/orleans_resource_placement_kalman/
internal sealed class ResourceOptimizedPlacementDirector : IPlacementDirector, ISiloStatisticsChangeListener
{
    readonly record struct ResourceStatistics(float? CpuUsage, float? AvailableMemory, long? MemoryUsage, long? TotalPhysicalMemory, bool IsOverloaded);

    /// <summary>
    /// 1 / (1024 * 1024)
    /// </summary>
    private const float PhysicalMemoryScalingFactor = 0.00000095367431640625f;
    private const int OneKiloByte = 1024;

    private readonly ResourceOptimizedPlacementOptions _options;
    private readonly ConcurrentDictionary<SiloAddress, ResourceStatistics> _siloStatistics = [];

    private readonly DualModeKalmanFilter<float> _cpuUsageFilter = new();
    private readonly DualModeKalmanFilter<float> _availableMemoryFilter = new();
    private readonly DualModeKalmanFilter<long> _memoryUsageFilter = new();

    private Task<SiloAddress> _cachedLocalSilo;

    public ResourceOptimizedPlacementDirector(
        DeploymentLoadPublisher deploymentLoadPublisher,
        IOptions<ResourceOptimizedPlacementOptions> options)
    {
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

        if (_siloStatistics.IsEmpty)
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

    private KeyValuePair<SiloAddress, float> GetBestSiloCandidate(SiloAddress[] compatibleSilos)
    {
        List<KeyValuePair<SiloAddress, ResourceStatistics>> relevantSilos = [];
        foreach (var silo in compatibleSilos)
        {
            if (_siloStatistics.TryGetValue(silo, out var stats) && !stats.IsOverloaded)
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

        var orderedByLowestScore = chooseFromSilos.OrderBy(kv => kv.Value);

        // there could be more than 1 silo that has the same score, we pick 1 of them randomly so that we dont continuously pick the first one.
        var lowestScore = orderedByLowestScore.First().Value;
        var shortListedSilos = orderedByLowestScore.TakeWhile(p => p.Value == lowestScore).ToList();
        var winningSilo = shortListedSilos[Random.Shared.Next(shortListedSilos.Count)];

        return winningSilo;
    }

    private KeyValuePair<SiloAddress, float> GetBestSiloCandidate_V2(SiloAddress[] compatibleSilos)
    {
        KeyValuePair<int, float> pick;

        int compatibleSilosCount = compatibleSilos.Length;
        if (compatibleSilosCount * Unsafe.SizeOf<KeyValuePair<int, ResourceStatistics>>() <= OneKiloByte)  // it is good practice not to allocate more than 1 kilobyte of memory on the stack
        {
            pick = MakePick(stackalloc KeyValuePair<int, ResourceStatistics>[compatibleSilosCount]);
        }
        else
        {
            var relevantSilos = ArrayPool<KeyValuePair<int, ResourceStatistics>>.Shared.Rent(compatibleSilosCount);
            pick = MakePick(relevantSilos.AsSpan());
            ArrayPool<KeyValuePair<int, ResourceStatistics>>.Shared.Return(relevantSilos);
        }

        foreach (var silo in compatibleSilos)
        {
            if (silo.GetConsistentHashCode() == pick.Key)
            {
                return new KeyValuePair<SiloAddress, float>(silo, pick.Value);
            }
        }

        // It should never come to this point, unless 'GetConsistentHashCode' isnt consistent, which if its the case,
        // this code can act as a 'tester' for that. This would be exceptional, so its better to stop the program.
        throw new InvalidOperationException("No hash code from the list of compatible silos matched the picked silo's hash code.");

        KeyValuePair<int, float> MakePick(Span<KeyValuePair<int, ResourceStatistics>> relevantSilos)
        {
            int relevantSilosCount = 0;
            foreach (var silo in compatibleSilos)
            {
                if (_siloStatistics.TryGetValue(silo, out var stats) && !stats.IsOverloaded)
                {
                    relevantSilos[relevantSilosCount++] = new(silo.GetConsistentHashCode(), stats);
                }
            }

            int chooseFrom = (int)Math.Ceiling(Math.Sqrt(relevantSilosCount));
            var chooseFromSilos = Random.Shared.GetItems<KeyValuePair<int, ResourceStatistics>>(relevantSilos, chooseFrom).AsSpan();

            int cursor = 0;
            int addressHashCode = 0;
            float lowestScore = 1;

            while (cursor < chooseFrom)
            {
                var silo = chooseFromSilos[cursor];

                float siloScore = CalculateScore(silo.Value);
                // its very unlikley, but there could be more than 1 silo that has the same score,
                // so we apply some jittering to avoid pick the first one in the short-list.
                float scoreJitter = Random.Shared.NextSingle() / 100_000;

                if (siloScore + scoreJitter < lowestScore)
                {
                    lowestScore = siloScore;
                    addressHashCode = silo.Key;
                }

                cursor++;
            }

            return new(addressHashCode, lowestScore);
        }
    }

    private bool IsLocalSiloPreferable(IPlacementContext context, SiloAddress[] compatibleSilos, float bestCandidateScore)
    {
        if (context.LocalSiloStatus != SiloStatus.Active || !compatibleSilos.Contains(context.LocalSilo))
        {
            return false;
        }

        if (_siloStatistics.TryGetValue(context.LocalSilo, out var localStats))
        {
            float localScore = CalculateScore(localStats);
            float scoreDiff = Math.Abs(localScore - bestCandidateScore);

            if (_options.LocalSiloPreferenceMargin >= scoreDiff)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Always returns a value [0-1]
    /// </summary>
    /// <returns>
    /// score = cpu_weight * (cpu_usage / 100) +
    ///         mem_usage_weight * (mem_usage / physical_mem) +
    ///         mem_avail_weight * [1 - (mem_avail / physical_mem)]
    ///         physical_mem_weight * (1 / (1024 * 1024 * physical_mem)
    /// </returns>
    /// <remarks>physical_mem is represented in [MB] to keep the result within [0-1] in cases of silos having physical_mem less than [1GB]</remarks>
    private float CalculateScore(ResourceStatistics stats)
    {
        float normalizedCpuUsage = stats.CpuUsage.HasValue ? stats.CpuUsage.Value / 100f : 0f;

        if (stats.TotalPhysicalMemory is { } physicalMemory && physicalMemory > 0)
        {
            float normalizedMemoryUsage = stats.MemoryUsage.HasValue ? stats.MemoryUsage.Value / physicalMemory : 0f;
            float normalizedAvailableMemory = 1 - (stats.AvailableMemory.HasValue ? stats.AvailableMemory.Value / physicalMemory : 0f);
            float normalizedPhysicalMemory = PhysicalMemoryScalingFactor * physicalMemory;

            return _options.CpuUsageWeight * normalizedCpuUsage +
                   _options.MemoryUsageWeight * normalizedMemoryUsage +
                   _options.AvailableMemoryWeight * normalizedAvailableMemory +
                   _options.AvailableMemoryWeight * normalizedPhysicalMemory;
        }

        return _options.CpuUsageWeight * normalizedCpuUsage;
    }

    public void RemoveSilo(SiloAddress address)
         => _siloStatistics.TryRemove(address, out _);

    public void SiloStatisticsChangeNotification(SiloAddress address, SiloRuntimeStatistics statistics)
        => _siloStatistics.AddOrUpdate(
            key: address,
            addValue: new ResourceStatistics(
                statistics.CpuUsage,
                statistics.AvailableMemory,
                statistics.MemoryUsage,
                statistics.TotalPhysicalMemory,
                statistics.IsOverloaded),
            updateValueFactory: (_, _) =>
            {
                float estimatedCpuUsage = _cpuUsageFilter.Filter(statistics.CpuUsage);
                float estimatedAvailableMemory = _availableMemoryFilter.Filter(statistics.AvailableMemory);
                long estimatedMemoryUsage = _memoryUsageFilter.Filter(statistics.MemoryUsage);

                return new ResourceStatistics(
                    estimatedCpuUsage,
                    estimatedAvailableMemory,
                    estimatedMemoryUsage,
                    statistics.TotalPhysicalMemory,
                    statistics.IsOverloaded);
            });

    private sealed class DualModeKalmanFilter<T> where T : unmanaged, INumber<T>
    {
        private readonly KalmanFilter _slowFilter = new(T.Zero);
        private readonly KalmanFilter _fastFilter = new(T.CreateChecked(0.01));
        
        private FilterRegime _regime = FilterRegime.Slow;

        private enum FilterRegime
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

        private sealed class KalmanFilter(T processNoiseCovariance)
        {
            private readonly T _processNoiseCovariance = processNoiseCovariance;

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
