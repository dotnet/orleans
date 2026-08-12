using System;

namespace Orleans.Runtime.MembershipService;

internal sealed class PhiAccrualFailureDetector
{
    private const double Threshold = 8;
    private const int MaxSampleSize = 100;
    private const int MinimumSampleCount = 4;
    private static readonly double ThresholdStandardDeviations = CalculateThresholdStandardDeviations();

    private readonly double[] _samples = new double[MaxSampleSize];
    private readonly TimeSpan _initialTimeout;
    private readonly double _minimumStandardDeviationMilliseconds;
    private int _nextSampleIndex;
    private int _sampleCount;
    private double _sampleSum;
    private double _squaredSampleSum;

    public PhiAccrualFailureDetector(TimeSpan initialTimeout)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(initialTimeout, TimeSpan.Zero);

        _initialTimeout = initialTimeout;
        _minimumStandardDeviationMilliseconds = initialTimeout.TotalMilliseconds / (2 * ThresholdStandardDeviations);
    }

    public int SampleCount => _sampleCount;

    public void RecordResponseTime(TimeSpan responseTime)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(responseTime, TimeSpan.Zero);

        var sample = responseTime.TotalMilliseconds;
        if (_sampleCount == MaxSampleSize)
        {
            var replacedSample = _samples[_nextSampleIndex];
            _sampleSum -= replacedSample;
            _squaredSampleSum -= replacedSample * replacedSample;
        }
        else
        {
            _sampleCount++;
        }

        _samples[_nextSampleIndex] = sample;
        _nextSampleIndex = (_nextSampleIndex + 1) % MaxSampleSize;
        _sampleSum += sample;
        _squaredSampleSum += sample * sample;
    }

    public TimeSpan GetTimeout() =>
        _sampleCount < MinimumSampleCount
            ? _initialTimeout
            : TimeSpan.FromMilliseconds(Math.Min(GetEstimatedTimeoutMilliseconds(), TimeSpan.MaxValue.TotalMilliseconds));

    public TimeSpan GetTimeout(TimeSpan minimumTimeout, TimeSpan maximumTimeout, int extensionFactor)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(minimumTimeout, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumTimeout, minimumTimeout);
        ArgumentOutOfRangeException.ThrowIfLessThan(extensionFactor, 1);

        var timeoutTicks = GetTimeout().Ticks * (double)extensionFactor;
        return TimeSpan.FromTicks((long)Math.Clamp(timeoutTicks, minimumTimeout.Ticks, maximumTimeout.Ticks));
    }

    internal static double CalculatePhi(double elapsedMilliseconds, double meanMilliseconds, double standardDeviationMilliseconds)
    {
        // Logistic approximation of the normal CDF used by Akka's Phi Accrual implementation.
        var y = (elapsedMilliseconds - meanMilliseconds) / standardDeviationMilliseconds;
        var e = Math.Exp(-y * (1.5976 + (0.070566 * y * y)));
        return elapsedMilliseconds > meanMilliseconds
            ? -Math.Log10(e / (1 + e))
            : -Math.Log10(1 - (1 / (1 + e)));
    }

    private double GetEstimatedTimeoutMilliseconds()
    {
        var mean = _sampleSum / _sampleCount;
        var variance = Math.Max(0, (_squaredSampleSum / _sampleCount) - (mean * mean));
        var standardDeviation = Math.Max(Math.Sqrt(variance), _minimumStandardDeviationMilliseconds);
        return mean + (ThresholdStandardDeviations * standardDeviation);
    }

    private static double CalculateThresholdStandardDeviations()
    {
        var lower = 0d;
        var upper = 10d;
        for (var i = 0; i < 64; i++)
        {
            var midpoint = (lower + upper) / 2;
            if (CalculatePhi(midpoint, 0, 1) < Threshold)
            {
                lower = midpoint;
            }
            else
            {
                upper = midpoint;
            }
        }

        return upper;
    }
}
