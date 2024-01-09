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
    readonly record struct ResourceStatistics(
       float? CpuUsage,
       float? AvailableMemory,
       long? MemoryUsage,
       long? TotalPhysicalMemory,
       bool IsOverloaded);

    Task<SiloAddress> _cachedLocalSilo;
    readonly SiloAddress _localAddress;
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

        var selectedSilo = GetSiloWithHighestScore(compatibleSilos);


        return Task.FromResult(selectedSilo);
    }

    SiloAddress GetSiloWithHighestScore(SiloAddress[] compatibleSilos)
    {
        List<KeyValuePair<SiloAddress, ResourceStatistics>> relevantSilos = [];
        foreach (var silo in compatibleSilos)
        {
            if (siloStatistics.TryGetValue(silo, out var stats) && !stats.IsOverloaded)
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

        return chooseFromSilos.OrderByDescending(kv => kv.Value).FirstOrDefault().Key;
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

    public void RemoveSilo(SiloAddress address)
         => siloStatistics.TryRemove(address, out _);

    public void SiloStatisticsChangeNotification(SiloAddress address, SiloRuntimeStatistics statistics)
        => siloStatistics.AddOrUpdate(
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
                    // since we now got a measurement we can use it to set the filter's 'estimate',
                    // in addition we set the 'error covariance' to 0, indicating we want to fully
                    // trust the measurement (now the new 'estimate') to reach the actual signal asap.
                    _fastFilter.SetState(_measurement, T.Zero);

                    // ensure we recalculate since we changed the 'error covariance'
                    fastEstimate = _fastFilter.Filter(_measurement);

                    _regime = FilterRegime.Fast;
                }

                return fastEstimate;
            }
            else
            {
                if (_regime == FilterRegime.Fast)
                {
                    // since the slow filter will accumulate the changes, we want to reset its state
                    // so that it aligns with the current peak of the fast filter so we get a slower
                    // decay that is always aligned with the latest fast filter state and not the overall
                    // accumulated state of the whole signal over its lifetime.
                    _slowFilter.SetState(_fastFilter.PriorEstimate, _fastFilter.PriorErrorCovariance);

                    // ensure we recalculate since we changed both the 'estimate' and 'error covariance'
                    slowEstimate = _slowFilter.Filter(_measurement);

                    _regime = FilterRegime.Slow;
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
                #region Prediction Step

                #region Formula
                // ^x_k = A * x_k-1 + B * u_k
                #endregion

                #region Simplification
                // ^x_k = x_k-1
                #endregion

                #region Explanation
                // As every resource statistics is a single entity in our case, we have a 1-dimensional signal problem, so every entity in our model is a scalar, not a matrix.
                // uk is the control signal which incorporate external information about how the system is expected to behave between measurements. We have no idea how the CPU usage is going to behave between measurements, therefor we have no control signal, so uk = 0.
                // B is the control input matrix, but since uk = 0 this means we don't need to bother with it.
                // A is the state transition matrix, and we established that we have a 1-dimensional signal problem, so this is now a scalar. Same as with uk, we have no idea how the CPU usage is going to transition, therefor A = 1.
                // We just established that A = 1, and since A is a unitary scalar, this means AT which is the transpose of A, is AT = 1.
                #endregion

                T estimate = PriorEstimate;
                T errorCovariance = PriorErrorCovariance + _processNoiseCovariance;

                #endregion

                #region Correction Step

                #region Formulas
                //  * K_k = (P_k * H_T) / (H * P_k * H_T + R)
                //  * ^x_k = x_k + K_k * (z_k - H * x_k)
                //  * ^P_k = (I - K_k * H) * P_k;
                #endregion

                #region Simplifications
                //  * K_k = P_k / (P_k + 1);
                //  * ^x_k = x_k + K_k * (z_k - x_k)
                //  * ^P_k = (1 - K_k) * P_k;
                #endregion

                #region Explanation
                // Same as with the prediction, we deal only with scalars, not matrices.
                // H is the observation matrix, which acts as a bridge between the internal model A, and the external measurements R. We can set H = 1, which indicates that the measurements directly correspond to the state variables without any transformations or scaling factors.
                // Since H = 1, it follows that HT = 1.
                // R is the measurement covariance matrix, which represents the influence of the measurements relative to the predicted state. We set this value to R = 1, which indicates that all measurements are assumed to have the same level of uncertainty, and there is no correlation between different measurements.
                #endregion

                T gain = errorCovariance / (errorCovariance + T.One);
                T newEstimate = estimate + gain * (measurement - estimate);
                T newErrorCovariance = (T.One - gain) * errorCovariance;

                PriorEstimate = newEstimate;
                PriorErrorCovariance = newErrorCovariance;

                #endregion

                return newEstimate;
            }
        }
    }
}
