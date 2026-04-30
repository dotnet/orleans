using System.Diagnostics;
using System.Threading.Channels;

namespace Benchmarks.Ping;

/// <summary>
/// A load generator that runs indefinitely and uses a hill climbing algorithm
/// to continuously tune concurrency for maximum throughput.
/// </summary>
public sealed class AdaptiveConcurrencyLoadGenerator<TState>
{
    private static readonly double StopwatchTickPerSecond = Stopwatch.Frequency;

    private readonly Func<TState, ValueTask> _issueRequest;
    private readonly Func<int, TState> _getStateForWorker;
    private readonly int _requestsPerBlock;
    private readonly TimeSpan _warmupDuration;
    private readonly TimeSpan _measurementInterval;
    private readonly TimeSpan _sampleInterval;
    private readonly int _minConcurrency;
    private readonly int _maxConcurrency;
    private readonly int _initialConcurrency;
    private readonly int _maxStableRounds;
    private readonly double _minimumRelativeImprovement;

    private Channel<WorkBlock> _completedBlocks;
    private volatile int _currentConcurrency;
    private CancellationTokenSource _cts;

    // Hill climbing state
    private double _bestThroughput;
    private Measurement _bestMeasurement;
    private Measurement _lastMeasurement;
    private int _bestConcurrency;
    private int _stepSize;
    private int _direction; // 1 = increasing, -1 = decreasing
    private int _stableCount;
    private int _roundsSinceBestChanged;
    private const int StableThreshold = 3; // Number of consecutive non-improvements before changing direction
    private const int DefaultInitialStepSize = 50;
    private const int MinimumSampleCount = 4;
    private const double DefaultMinimumRelativeImprovement = 0.005;
    private const double StatisticalConfidence = 0.95;

    public int CurrentConcurrency => _currentConcurrency;
    public int BestConcurrency => _bestConcurrency;
    public double BestThroughput => _bestThroughput;
    public bool Converged { get; private set; }

    public AdaptiveConcurrencyLoadGenerator(
        Func<TState, ValueTask> issueRequest,
        Func<int, TState> getStateForWorker,
        int requestsPerBlock = 100,
        TimeSpan? warmupDuration = null,
        TimeSpan? measurementInterval = null,
        int minConcurrency = 1,
        int maxConcurrency = 2000,
        int initialConcurrency = 100,
        int maxStableRounds = 0,
        int initialStepSize = DefaultInitialStepSize,
        TimeSpan? sampleInterval = null,
        double minimumRelativeImprovement = DefaultMinimumRelativeImprovement)
    {
        var resolvedWarmupDuration = warmupDuration ?? TimeSpan.FromSeconds(5);
        var resolvedMeasurementInterval = measurementInterval ?? TimeSpan.FromSeconds(2);
        var resolvedSampleInterval = sampleInterval ?? TimeSpan.FromMilliseconds(250);

        if (resolvedWarmupDuration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(warmupDuration), "Warmup duration must be positive.");
        }

        if (resolvedMeasurementInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(measurementInterval), "Measurement interval must be positive.");
        }

        if (resolvedSampleInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(sampleInterval), "Sample interval must be positive.");
        }

        if (double.IsNaN(minimumRelativeImprovement) || minimumRelativeImprovement < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(minimumRelativeImprovement), "Minimum relative improvement must be non-negative.");
        }

        if (requestsPerBlock <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(requestsPerBlock), "Requests per block must be positive.");
        }

        if (initialStepSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(initialStepSize), "Initial step size must be positive.");
        }

        _issueRequest = issueRequest;
        _getStateForWorker = getStateForWorker;
        _requestsPerBlock = requestsPerBlock;
        _warmupDuration = resolvedWarmupDuration;
        _measurementInterval = resolvedMeasurementInterval;
        _sampleInterval = resolvedSampleInterval;
        _minConcurrency = minConcurrency;
        _maxConcurrency = maxConcurrency;
        _initialConcurrency = initialConcurrency;
        _currentConcurrency = initialConcurrency;
        _stepSize = initialStepSize;
        _direction = 1;
        _maxStableRounds = maxStableRounds; // 0 = run forever
        _minimumRelativeImprovement = minimumRelativeImprovement;
    }

    public async Task RunForeverAsync(CancellationToken cancellationToken = default)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        Console.WriteLine($"Starting adaptive load generator with initial concurrency: {_initialConcurrency}");
        Console.WriteLine($"Warmup duration: {_warmupDuration.TotalSeconds}s, Measurement interval: {_measurementInterval.TotalSeconds}s, Sample interval: {GetEffectiveSampleInterval(_measurementInterval).TotalMilliseconds:N0}ms");
        Console.WriteLine($"Concurrency range: [{_minConcurrency}, {_maxConcurrency}], Initial step size: {_stepSize}");
        Console.WriteLine($"New best requires >{_minimumRelativeImprovement:P1} improvement at {StatisticalConfidence:P0} confidence.");
        if (_maxStableRounds > 0)
            Console.WriteLine($"Will terminate after {_maxStableRounds} rounds with no statistically significant improvement to best");
        Console.WriteLine();

        // Warmup phase
        Console.WriteLine("=== WARMUP PHASE ===");
        await RunPhaseAsync(_warmupDuration, isWarmup: true);
        GC.Collect();

        Console.WriteLine();
        Console.WriteLine("=== TUNING PHASE ===");
        Console.WriteLine($"{"Time",-12} {"Concurrency",12} {"Samples",7} {"Throughput",14} {"Best",14} {"BestConc",10} {"Action",-28}");
        Console.WriteLine(new string('-', 99));

        var startTime = DateTime.UtcNow;

        while (!_cts.Token.IsCancellationRequested)
        {
            var measuredConcurrency = _currentConcurrency;
            var measurement = await RunPhaseAsync(_measurementInterval, isWarmup: false);
            var throughput = measurement.Throughput;
            var elapsed = DateTime.UtcNow - startTime;

            var action = ApplyHillClimbing(measurement);

            var elapsedStr = elapsed.ToString(@"hh\:mm\:ss\.f");
            Console.WriteLine($"{elapsedStr,-12} {measuredConcurrency,12} {measurement.SampleCount,7} {throughput,14:N0}/s {_bestThroughput,14:N0}/s {_bestConcurrency,10} {action,-28}");

            // Check for convergence
            if (_maxStableRounds > 0 && _roundsSinceBestChanged >= _maxStableRounds)
            {
                Converged = true;
                Console.WriteLine();
                Console.WriteLine($"Converged after {_roundsSinceBestChanged} rounds with no statistically significant improvement.");
                break;
            }
        }
    }

    private async Task<Measurement> RunPhaseAsync(TimeSpan duration, bool isWarmup)
    {
        _completedBlocks = Channel.CreateUnbounded<WorkBlock>(
            new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = false,
                AllowSynchronousContinuations = false
            });

        var workerTasks = new List<Task>();
        // Link to main cancellation token so Ctrl+C stops workers immediately
        using var workerCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        var states = new Dictionary<int, TState>();

        // Start initial workers
        for (int i = 0; i < _currentConcurrency; i++)
        {
            var state = _getStateForWorker(i);
            states[i] = state;
            workerTasks.Add(RunWorkerAsync(state, i, workerCts.Token));
        }

        var aggregator = Task.Run(() => AggregateBlocksAsync(duration, isWarmup, workerCts));

        var measurement = await aggregator;

        // Signal workers to stop
        await workerCts.CancelAsync();
        _completedBlocks.Writer.Complete();

        // Wait for workers with timeout
        try
        {
            await Task.WhenAll(workerTasks).WaitAsync(TimeSpan.FromSeconds(2));
        }
        catch (TimeoutException)
        {
            // Workers didn't stop in time, continue anyway
        }
        catch (OperationCanceledException)
        {
            // Expected
        }

        return measurement;
    }

    private async Task<Measurement> AggregateBlocksAsync(TimeSpan duration, bool isWarmup, CancellationTokenSource workerCts)
    {
        var reader = _completedBlocks.Reader;
        var startTime = Stopwatch.GetTimestamp();
        var endTime = startTime + (long)(duration.TotalSeconds * StopwatchTickPerSecond);
        var sampleInterval = GetEffectiveSampleInterval(duration);
        var sampleTicks = Math.Max(1, (long)(sampleInterval.TotalSeconds * StopwatchTickPerSecond));
        var plannedSampleCount = Math.Max(1, (int)Math.Ceiling((endTime - startTime) / (double)sampleTicks));
        var completionsBySample = new List<long>(plannedSampleCount);
        for (var i = 0; i < plannedSampleCount; i++)
        {
            completionsBySample.Add(0);
        }

        long totalCompleted = 0;
        long totalFailures = 0;
        long minStartTime = long.MaxValue;
        long maxEndTime = long.MinValue;

        void RecordBlock(WorkBlock block)
        {
            totalCompleted += block.Completed;
            totalFailures += block.Failures;
            if (block.StartTimestamp < minStartTime) minStartTime = block.StartTimestamp;
            if (block.EndTimestamp > maxEndTime) maxEndTime = block.EndTimestamp;

            var sampleIndex = GetSampleIndex(block.EndTimestamp);
            completionsBySample[sampleIndex] += block.Completed;
        }

        int GetSampleIndex(long timestamp)
        {
            if (timestamp <= startTime)
            {
                return 0;
            }

            var elapsedTicks = timestamp - startTime;
            var sampleIndex = (int)((elapsedTicks - 1) / sampleTicks);
            while (sampleIndex >= completionsBySample.Count)
            {
                completionsBySample.Add(0);
            }

            return sampleIndex;
        }

        while (Stopwatch.GetTimestamp() < endTime && !_cts.Token.IsCancellationRequested)
        {
            try
            {
                var readTask = reader.WaitToReadAsync(_cts.Token).AsTask();
                var completed = await readTask.WaitAsync(TimeSpan.FromMilliseconds(100));

                if (!completed) continue;

                while (reader.TryRead(out var block))
                {
                    RecordBlock(block);
                }
            }
            catch (TimeoutException)
            {
                // Continue waiting
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        // Signal workers to stop
        workerCts.Cancel();

        // Drain remaining blocks
        while (reader.TryRead(out var block))
        {
            RecordBlock(block);
        }

        var totalSeconds = totalCompleted == 0 || maxEndTime <= minStartTime
            ? duration.TotalSeconds
            : (maxEndTime - minStartTime) / StopwatchTickPerSecond;
        var throughput = totalSeconds > 0 ? totalCompleted / totalSeconds : 0;
        var sampleEndTime = maxEndTime > endTime ? maxEndTime : endTime;
        var samples = CreateThroughputSamples(completionsBySample, startTime, sampleEndTime, sampleTicks);
        var measurement = new Measurement(throughput, samples);

        if (isWarmup)
        {
            var failureInfo = totalFailures > 0 ? $" ({totalFailures} failures)" : "";
            Console.WriteLine($"  Warmup: {throughput:N0}/s, {totalCompleted:N0} requests in {totalSeconds:F1}s{failureInfo}");
        }

        return measurement;
    }

    private string ApplyHillClimbing(Measurement measurement)
    {
        var currentThroughput = measurement.Throughput;
        string action;
        bool isNewBest = !_bestMeasurement.HasValue || IsStatisticallySignificantImprovement(measurement, _bestMeasurement);

        if (isNewBest)
        {
            _bestThroughput = currentThroughput;
            _bestConcurrency = _currentConcurrency;
            _bestMeasurement = measurement;
            _roundsSinceBestChanged = 0;
        }
        else
        {
            _roundsSinceBestChanged++;
        }

        // Determine if this is a meaningful improvement (resets stable count)
        // or just noise within measurement variance.
        bool meaningfulImprovement = isNewBest || !_lastMeasurement.HasValue || IsStatisticallySignificantImprovement(measurement, _lastMeasurement);

        if (meaningfulImprovement)
        {
            _stableCount = 0;

            // Continue in the same direction
            var newConcurrency = _currentConcurrency + (_direction * _stepSize);
            newConcurrency = Math.Clamp(newConcurrency, _minConcurrency, _maxConcurrency);

            if (newConcurrency != _currentConcurrency)
            {
                _currentConcurrency = newConcurrency;
                action = isNewBest ? $"New best! {_direction * _stepSize:+#;-#;0}" : $"Improving {_direction * _stepSize:+#;-#;0}";
            }
            else
            {
                // Hit boundary, reverse direction
                _direction = -_direction;
                action = "Hit boundary";
            }
        }
        else
        {
            _stableCount++;

            if (_stableCount >= StableThreshold)
            {
                // No improvement for a while
                _stableCount = 0;

                if (_stepSize > 1)
                {
                    // Reduce step size for finer tuning
                    _stepSize = Math.Max(1, _stepSize / 2);
                    _direction = -_direction; // Try the other direction with smaller steps
                    action = $"Refine step={_stepSize}";
                }
                else
                {
                    // At minimum step, jump back toward best and try again
                    _direction = _bestConcurrency > _currentConcurrency ? 1 : -1;
                    _stepSize = Math.Max(1, Math.Abs(_currentConcurrency - _bestConcurrency) / 2);
                    if (_stepSize < 1) _stepSize = Math.Max(1, _bestConcurrency / 10);
                    action = $"Reset toward best";
                }

                var newConcurrency = _currentConcurrency + (_direction * _stepSize);
                newConcurrency = Math.Clamp(newConcurrency, _minConcurrency, _maxConcurrency);
                _currentConcurrency = newConcurrency;
            }
            else
            {
                // Continue probing in current direction
                var newConcurrency = _currentConcurrency + (_direction * _stepSize);
                newConcurrency = Math.Clamp(newConcurrency, _minConcurrency, _maxConcurrency);

                if (newConcurrency == _currentConcurrency)
                {
                    // Hit boundary, reverse
                    _direction = -_direction;
                    newConcurrency = _currentConcurrency + (_direction * _stepSize);
                    newConcurrency = Math.Clamp(newConcurrency, _minConcurrency, _maxConcurrency);
                    action = "Boundary, reverse";
                }
                else
                {
                    action = $"Probing ({_stableCount}/{StableThreshold})";
                }

                _currentConcurrency = newConcurrency;
            }
        }

        _lastMeasurement = measurement;
        return action;
    }

    private TimeSpan GetEffectiveSampleInterval(TimeSpan duration) => _sampleInterval <= duration ? _sampleInterval : duration;

    private static double[] CreateThroughputSamples(IReadOnlyList<long> completionsBySample, long startTime, long endTime, long sampleTicks)
    {
        var samples = new double[completionsBySample.Count];
        for (var i = 0; i < samples.Length; i++)
        {
            var sampleStart = startTime + (i * sampleTicks);
            var sampleEnd = Math.Min(endTime, sampleStart + sampleTicks);
            var sampleSeconds = Math.Max((sampleEnd - sampleStart) / StopwatchTickPerSecond, double.Epsilon);
            samples[i] = completionsBySample[i] / sampleSeconds;
        }

        return samples;
    }

    private bool IsStatisticallySignificantImprovement(Measurement candidate, Measurement baseline)
    {
        if (!baseline.HasValue)
        {
            return true;
        }

        if (candidate.SampleMean <= baseline.SampleMean * (1 + _minimumRelativeImprovement))
        {
            return false;
        }

        if (candidate.SampleCount < MinimumSampleCount || baseline.SampleCount < MinimumSampleCount)
        {
            return candidate.Throughput > baseline.Throughput * (1 + _minimumRelativeImprovement);
        }

        var candidateVarianceContribution = candidate.SampleVariance / candidate.SampleCount;
        var baselineVarianceContribution = baseline.SampleVariance / baseline.SampleCount;
        var standardError = Math.Sqrt(candidateVarianceContribution + baselineVarianceContribution);
        if (standardError <= double.Epsilon)
        {
            return true;
        }

        var degreesOfFreedom = CalculateWelchDegreesOfFreedom(
            candidateVarianceContribution,
            candidate.SampleCount,
            baselineVarianceContribution,
            baseline.SampleCount);
        var requiredDifference = GetTwoSidedTCriticalValue95(degreesOfFreedom) * standardError;
        return candidate.SampleMean - baseline.SampleMean > requiredDifference;
    }

    private static double CalculateWelchDegreesOfFreedom(double firstVarianceContribution, int firstSampleCount, double secondVarianceContribution, int secondSampleCount)
    {
        var numerator = Math.Pow(firstVarianceContribution + secondVarianceContribution, 2);
        var denominator =
            (Math.Pow(firstVarianceContribution, 2) / (firstSampleCount - 1)) +
            (Math.Pow(secondVarianceContribution, 2) / (secondSampleCount - 1));

        return denominator <= double.Epsilon ? double.PositiveInfinity : numerator / denominator;
    }

    private static double GetTwoSidedTCriticalValue95(double degreesOfFreedom)
    {
        if (double.IsPositiveInfinity(degreesOfFreedom))
        {
            return 1.960;
        }

        var df = Math.Max(1, (int)Math.Floor(degreesOfFreedom));
        return df switch
        {
            1 => 12.706,
            2 => 4.303,
            3 => 3.182,
            4 => 2.776,
            5 => 2.571,
            6 => 2.447,
            7 => 2.365,
            8 => 2.306,
            9 => 2.262,
            10 => 2.228,
            11 => 2.201,
            12 => 2.179,
            13 => 2.160,
            14 => 2.145,
            15 => 2.131,
            16 => 2.120,
            17 => 2.110,
            18 => 2.101,
            19 => 2.093,
            20 => 2.086,
            <= 25 => 2.060,
            <= 30 => 2.042,
            <= 40 => 2.021,
            <= 60 => 2.000,
            <= 120 => 1.980,
            _ => 1.960
        };
    }

    private readonly struct Measurement
    {
        public Measurement(double throughput, double[] samples)
        {
            Throughput = throughput;
            SampleCount = samples.Length;
            SampleMean = SampleCount == 0 ? throughput : samples.Average();
            SampleVariance = CalculateSampleVariance(samples, SampleMean);
            HasValue = true;
        }

        public bool HasValue { get; }
        public double Throughput { get; }
        public int SampleCount { get; }
        public double SampleMean { get; }
        public double SampleVariance { get; }

        private static double CalculateSampleVariance(double[] samples, double mean)
        {
            if (samples.Length < 2)
            {
                return 0;
            }

            var sumOfSquares = 0d;
            foreach (var sample in samples)
            {
                var delta = sample - mean;
                sumOfSquares += delta * delta;
            }

            return sumOfSquares / (samples.Length - 1);
        }
    }

    private async Task RunWorkerAsync(TState state, int workerId, CancellationToken cancellationToken)
    {
        var writer = _completedBlocks.Writer;

        while (!cancellationToken.IsCancellationRequested)
        {
            var workBlock = new WorkBlock { StartTimestamp = Stopwatch.GetTimestamp() };

            while (workBlock.Completed < _requestsPerBlock && !cancellationToken.IsCancellationRequested)
            {
                try
                {
                    await _issueRequest(state).ConfigureAwait(false);
                    workBlock.Successes++;
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch
                {
                    workBlock.Failures++;
                }
            }

            workBlock.EndTimestamp = Stopwatch.GetTimestamp();

            if (workBlock.Completed > 0)
            {
                try
                {
                    await writer.WriteAsync(workBlock, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (ChannelClosedException)
                {
                    break;
                }
            }
        }
    }

    private struct WorkBlock
    {
        public long StartTimestamp;
        public long EndTimestamp;
        public int Successes;
        public int Failures;
        public readonly int Completed => Successes + Failures;
    }
}
