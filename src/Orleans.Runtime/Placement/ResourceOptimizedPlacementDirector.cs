#nullable enable
using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;
using Orleans.Configuration;

namespace Orleans.Runtime.Placement;

// See: https://www.ledjonbehluli.com/posts/orleans_resource_placement_kalman/
internal sealed class ResourceOptimizedPlacementDirector : IPlacementDirector, ISiloStatisticsChangeListener
{
    private const int FourKiloByte = 4096;
    private readonly SiloAddress _localSilo;
    private readonly NormalizedWeights _weights;
    private readonly float _localSiloPreferenceMargin;
    private readonly ConcurrentDictionary<SiloAddress, ResourceStatistics> _siloStatistics = [];
    private readonly Task<SiloAddress> _cachedLocalSilo;

    public ResourceOptimizedPlacementDirector(
        ILocalSiloDetails localSiloDetails,
        DeploymentLoadPublisher deploymentLoadPublisher,
        IOptions<ResourceOptimizedPlacementOptions> options)
    {
        _localSilo = localSiloDetails.SiloAddress;
        _cachedLocalSilo = Task.FromResult(_localSilo);
        _weights = NormalizeWeights(options.Value);
        _localSiloPreferenceMargin = (float)options.Value.LocalSiloPreferenceMargin / 100;
        deploymentLoadPublisher.SubscribeToStatisticsChangeEvents(this);
    }

    private static NormalizedWeights NormalizeWeights(ResourceOptimizedPlacementOptions input)
    {
        int totalWeight = input.CpuUsageWeight + input.MemoryUsageWeight + input.AvailableMemoryWeight + input.MaxAvailableMemoryWeight + input.ActivationCountWeight;

        return totalWeight == 0 ? new(0f, 0f, 0f, 0f, 0f) :
            new(
                CpuUsageWeight: (float)input.CpuUsageWeight / totalWeight,
                MemoryUsageWeight: (float)input.MemoryUsageWeight / totalWeight,
                AvailableMemoryWeight: (float)input.AvailableMemoryWeight / totalWeight,
                MaxAvailableMemoryWeight: (float)input.MaxAvailableMemoryWeight / totalWeight,
                ActivationCountWeight: (float)input.ActivationCountWeight / totalWeight);
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
            throw new SiloUnavailableException($"Cannot place grain '{target.GrainIdentity}' because there are no compatible silos.");
        }

        if (compatibleSilos.Length == 1)
        {
            return Task.FromResult(compatibleSilos[0]);
        }

        if (_siloStatistics.IsEmpty)
        {
            return Task.FromResult(compatibleSilos[Random.Shared.Next(compatibleSilos.Length)]);
        }

        // It is good practice not to allocate more than 1[KB] on the stack
        // but the size of ValueTuple<int, ResourceStatistics> = 24 bytes, by increasing
        // the limit to 4[KB] we can stackalloc for up to 4096 / 24 ~= 170 silos in a cluster.
        (int Index, float Score, float? LocalSiloScore) pick;
        int compatibleSilosCount = compatibleSilos.Length;
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

        var localSiloScore = pick.LocalSiloScore;
        if (!localSiloScore.HasValue || context.LocalSiloStatus != SiloStatus.Active || localSiloScore.Value - _localSiloPreferenceMargin > pick.Score)
        {
            var bestCandidate = compatibleSilos[pick.Index];
            return Task.FromResult(bestCandidate);
        }

        return _cachedLocalSilo;

        (int PickIndex, float PickScore, float? LocalSiloScore) MakePick(scoped Span<(int, ResourceStatistics)> relevantSilos)
        {
            // Get all compatible silos which aren't overloaded
            int relevantSilosCount = 0;
            float maxMaxAvailableMemory = 0;
            int maxActivationCount = 0;
            ResourceStatistics? localSiloStatistics = null;
            for (var i = 0; i < compatibleSilos.Length; ++i)
            {
                var silo = compatibleSilos[i];
                if (_siloStatistics.TryGetValue(silo, out var stats))
                {
                    if (!stats.IsOverloaded)
                    {
                        relevantSilos[relevantSilosCount++] = new(i, stats);
                    }

                    if (stats.MaxAvailableMemory > maxMaxAvailableMemory)
                    {
                        maxMaxAvailableMemory = stats.MaxAvailableMemory;
                    }

                    if (stats.ActivationCount > maxActivationCount)
                    {
                        maxActivationCount = stats.ActivationCount;
                    }

                    if (silo.Equals(_localSilo))
                    {
                        localSiloStatistics = stats;
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
                float score = CalculateScore(in statistics, maxMaxAvailableMemory, maxActivationCount);

                // It's very unlikely, but there could be more than 1 silo that has the same score,
                // so we apply some jittering to avoid pick the first one in the short-list.
                float scoreJitter = Random.Shared.NextSingle() / 100_000f;

                if (score + scoreJitter < pick.Score)
                {
                    pick = (index, score);
                }
            }

            float? localSiloScore = null;
            if (localSiloStatistics.HasValue && !localSiloStatistics.Value.IsOverloaded)
            {
                var localStats = localSiloStatistics.Value;
                localSiloScore = CalculateScore(in localStats, maxMaxAvailableMemory, maxActivationCount);
            }

            return (pick.Index, pick.Score, localSiloScore);
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

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private float CalculateScore(ref readonly ResourceStatistics stats, float maxMaxAvailableMemory, int maxActivationCount)
    {
        float normalizedCpuUsage = stats.CpuUsage / 100f;
        Debug.Assert(normalizedCpuUsage >= 0f && normalizedCpuUsage <= 1.01f, "CPU usage should be normalized to [0, 1] range");
        float score = _weights.CpuUsageWeight * normalizedCpuUsage;

        if (stats.MaxAvailableMemory > 0)
        {
            float normalizedMemoryUsage = stats.NormalizedMemoryUsage;
            Debug.Assert(normalizedMemoryUsage >= 0f && normalizedMemoryUsage <= 1.01f, "Memory usage should be normalized to [0, 1] range");
            float normalizedAvailableMemory = stats.NormalizedAvailableMemory;
            Debug.Assert(normalizedAvailableMemory >= 0f && normalizedAvailableMemory <= 1.01f, "Available memory should be normalized to [0, 1] range");
            float normalizedMaxAvailableMemory = stats.MaxAvailableMemory / maxMaxAvailableMemory;
            Debug.Assert(normalizedMaxAvailableMemory >= 0f && normalizedMaxAvailableMemory <= 1.01f, "Max available memory should be normalized to [0, 1] range");

            score += _weights.MemoryUsageWeight * normalizedMemoryUsage +
                     _weights.AvailableMemoryWeight * (1 - normalizedAvailableMemory) +
                     _weights.MaxAvailableMemoryWeight * (1 - normalizedMaxAvailableMemory);
        }

        var normalizedActivationCount = stats.ActivationCount / (float)maxActivationCount;
        Debug.Assert(normalizedActivationCount >= 0f && normalizedActivationCount <= 1.01f, "Activation count should be normalized to [0, 1] range");
        score += _weights.ActivationCountWeight * normalizedActivationCount;

        Debug.Assert(score >= 0f && score <= 1.01f, "Score should be normalized to [0, 1] range");

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

    private record NormalizedWeights(float CpuUsageWeight, float MemoryUsageWeight, float AvailableMemoryWeight, float MaxAvailableMemoryWeight, float ActivationCountWeight);
    private readonly record struct ResourceStatistics(bool IsOverloaded, float CpuUsage, float NormalizedMemoryUsage, float NormalizedAvailableMemory, float MaxAvailableMemory, int ActivationCount)
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ResourceStatistics FromRuntime(SiloRuntimeStatistics statistics)
            => new(
                IsOverloaded: statistics.IsOverloaded,
                CpuUsage: statistics.EnvironmentStatistics.FilteredCpuUsagePercentage,
                NormalizedMemoryUsage: statistics.EnvironmentStatistics.NormalizedFilteredMemoryUsage,
                NormalizedAvailableMemory: statistics.EnvironmentStatistics.NormalizedFilteredAvailableMemory,
                MaxAvailableMemory: statistics.EnvironmentStatistics.MaximumAvailableMemoryBytes,
                ActivationCount: statistics.ActivationCount);
    }
}
