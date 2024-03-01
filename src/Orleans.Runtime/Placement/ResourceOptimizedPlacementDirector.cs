using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;
using Orleans.Configuration;

namespace Orleans.Runtime.Placement;

// See: https://www.ledjonbehluli.com/posts/orleans_resource_placement_kalman/
internal sealed class ResourceOptimizedPlacementDirector : IPlacementDirector, ISiloStatisticsChangeListener
{
    /// <summary>
    /// 1 / (1024 * 1024)
    /// </summary>
    private const float MaxAvailableMemoryScalingFactor = 0.00000095367431640625f;
    private const int FourKiloByte = 4096;

    private readonly NormalizedWeights _weights;
    private readonly float _localSiloPreferenceMargin;
    private readonly ConcurrentDictionary<SiloAddress, ResourceStatistics> _siloStatistics = [];

    private Task<SiloAddress> _cachedLocalSilo;

    public ResourceOptimizedPlacementDirector(
        DeploymentLoadPublisher deploymentLoadPublisher,
        IOptions<ResourceOptimizedPlacementOptions> options)
    {
        _weights = NormalizeWeights(options.Value);
        _localSiloPreferenceMargin = (float)options.Value.LocalSiloPreferenceMargin / 100;
        deploymentLoadPublisher.SubscribeToStatisticsChangeEvents(this);
    }

    private static NormalizedWeights NormalizeWeights(ResourceOptimizedPlacementOptions input)
    {
        int totalWeight = input.CpuUsageWeight + input.MemoryUsageWeight + input.AvailableMemoryWeight + input.MaxAvailableMemoryWeight;

        return totalWeight == 0 ? new(0f, 0f, 0f, 0f) :
            new(
                CpuUsageWeight: (float)input.CpuUsageWeight / totalWeight,
                MemoryUsageWeight: (float)input.MemoryUsageWeight / totalWeight,
                AvailableMemoryWeight: (float)input.AvailableMemoryWeight / totalWeight,
                MaxAvailableMemoryWeight: (float)input.MaxAvailableMemoryWeight / totalWeight);
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
        (int Index, float Score) pick;
        int compatibleSilosCount = compatibleSilos.Length;

        // It is good practice not to allocate more than 1[KB] on the stack
        // but the size of ValueTuple<int, ResourceStatistics> = 24 bytes, by increasing
        // the limit to 4[KB] we can stackalloc for up to 4096 / 24 ~= 170 silos in a cluster.
        if (compatibleSilosCount * Unsafe.SizeOf<(int, ResourceStatistics)>() <= FourKiloByte)
        {
            pick = MakePick(stackalloc (int, ResourceStatistics)[compatibleSilosCount]);
        }
        else
        {
            var relevantSilos = ArrayPool<(int, ResourceStatistics)>.Shared.Rent(compatibleSilosCount);
            pick = MakePick(relevantSilos.AsSpan());
            ArrayPool<(int, ResourceStatistics)>.Shared.Return(relevantSilos);
        }

        return new KeyValuePair<SiloAddress, float>(compatibleSilos[pick.Index], pick.Score);

        (int, float) MakePick(Span<(int, ResourceStatistics)> relevantSilos)
        {
            // Get all compatible silos which aren't overloaded
            int relevantSilosCount = 0;
            for (var i = 0; i < compatibleSilos.Length; ++i)
            {
                var silo = compatibleSilos[i];
                if (_siloStatistics.TryGetValue(silo, out var stats))
                {
                    if (!stats.IsOverloaded)
                    {
                        relevantSilos[relevantSilosCount++] = new(i, stats);
                    }
                }
            }

            // Limit to the number of candidates added.
            relevantSilos = relevantSilos[0..relevantSilosCount];
            Debug.Assert(relevantSilos.Length == relevantSilosCount);

            // Pick K silos from the list of compatible silos, where K is equal to the square root of the number of silos.
            // Eg, from 10 silos, we choose from 4.
            int candidateCount = (int)Math.Ceiling(Math.Sqrt(relevantSilosCount));
            ShufflePrefix(relevantSilos, candidateCount);
            var candidates = relevantSilos[0..candidateCount];

            (int Index, float Score) pick = (0, 1f);

            foreach (var (index, statistics) in candidates)
            {
                float score = CalculateScore(in statistics);

                // It's very unlikely, but there could be more than 1 silo that has the same score,
                // so we apply some jittering to avoid pick the first one in the short-list.
                float scoreJitter = Random.Shared.NextSingle() / 100_000f;

                if (score + scoreJitter < pick.Score)
                {
                    pick = (index, score);
                }
            }

            return pick;
        }

        // Variant of the Modern Fisher-Yates shuffle which stops after shuffling the first `prefixLength` elements,
        // which are the only elements we are interested in.
        // See: https://en.wikipedia.org/wiki/Fisher%E2%80%93Yates_shuffle
        static void ShufflePrefix(Span<(int SiloIndex, ResourceStatistics SiloStatistics)> values, int prefixLength)
        {
            Debug.Assert(prefixLength >= 0 && prefixLength <= values.Length);

            var max = values.Length;
            for (var i = 0; i < prefixLength; i++)
            {
                var chosen = Random.Shared.Next(i, max);
                if (chosen != i)
                {
                    (values[chosen], values[i]) = (values[i], values[chosen]);
                }
            }
        }
    }

    private bool IsLocalSiloPreferable(IPlacementContext context, SiloAddress[] compatibleSilos, float bestCandidateScore)
    {
        if (context.LocalSiloStatus != SiloStatus.Active || !compatibleSilos.Contains(context.LocalSilo))
        {
            return false;
        }

        if (!_siloStatistics.TryGetValue(context.LocalSilo, out var localStats))
        {
            return false;
        }

        if (localStats.IsOverloaded)
        {
            return false;
        }

        var localSiloScore = CalculateScore(in localStats);
        return localSiloScore - _localSiloPreferenceMargin <= bestCandidateScore;
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
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private float CalculateScore(ref readonly ResourceStatistics stats) // as size of ResourceStatistics > IntPtr, we pass it by (readonly)-reference to avoid potential defensive copying
    {
        float normalizedCpuUsage = stats.CpuUsage / 100f;
        float score = _weights.CpuUsageWeight * normalizedCpuUsage;

        if (stats.MaxAvailableMemory > 0)
        {
            float maxAvailableMemory = stats.MaxAvailableMemory; // cache locally

            float normalizedMemoryUsage = stats.MemoryUsage / maxAvailableMemory;
            float normalizedAvailableMemory = 1 - stats.AvailableMemory / maxAvailableMemory;
            float normalizedMaxAvailableMemoryWeight = MaxAvailableMemoryScalingFactor * maxAvailableMemory;

            score += _weights.MemoryUsageWeight * normalizedMemoryUsage +
                     _weights.AvailableMemoryWeight * normalizedAvailableMemory +
                     _weights.MaxAvailableMemoryWeight * normalizedMaxAvailableMemoryWeight;
        }

        Debug.Assert(score >= 0f && score <= 1f);

        return score;
    }

    public void RemoveSilo(SiloAddress address)
         => _siloStatistics.TryRemove(address, out _);

    public void SiloStatisticsChangeNotification(SiloAddress address, SiloRuntimeStatistics statistics)
        => _siloStatistics.AddOrUpdate(
            key: address,
            factoryArgument: statistics,
            addValueFactory: static (_, statistics) => ResourceStatistics.FromRuntime(statistics),
            updateValueFactory: static (_, _, statistics) => ResourceStatistics.FromRuntime(statistics));

    private record NormalizedWeights(float CpuUsageWeight, float MemoryUsageWeight, float AvailableMemoryWeight, float MaxAvailableMemoryWeight);
    private readonly record struct ResourceStatistics(bool IsOverloaded, float CpuUsage, float MemoryUsage, float AvailableMemory, float MaxAvailableMemory)
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ResourceStatistics FromRuntime(SiloRuntimeStatistics statistics)
            => new(
                IsOverloaded: statistics.IsOverloaded,
                CpuUsage: statistics.EnvironmentStatistics.CpuUsagePercentage,
                MemoryUsage: statistics.EnvironmentStatistics.MemoryUsageBytes,
                AvailableMemory: statistics.EnvironmentStatistics.AvailableMemoryBytes,
                MaxAvailableMemory: statistics.EnvironmentStatistics.MaximumAvailableMemoryBytes);
    }
}