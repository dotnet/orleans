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
    private readonly int _minConcurrency;
    private readonly int _maxConcurrency;
    private readonly int _initialConcurrency;
    private readonly int _maxStableRounds;

    private Channel<WorkBlock> _completedBlocks;
    private volatile int _currentConcurrency;
    private CancellationTokenSource _cts;

    // Hill climbing state
    private double _bestThroughput;
    private double _lastThroughput;
    private int _bestConcurrency;
    private int _stepSize;
    private int _direction; // 1 = increasing, -1 = decreasing
    private int _stableCount;
    private int _roundsSinceBestChanged;
    private const int StableThreshold = 3; // Number of consecutive non-improvements before changing direction

    public int CurrentConcurrency => _currentConcurrency;
    public int BestConcurrency => _bestConcurrency;
    public double BestThroughput => _bestThroughput;
    public bool Converged { get; private set; }

    public AdaptiveConcurrencyLoadGenerator(
        Func<TState, ValueTask> issueRequest,
        Func<int, TState> getStateForWorker,
        int requestsPerBlock = 500,
        TimeSpan? warmupDuration = null,
        TimeSpan? measurementInterval = null,
        int minConcurrency = 1,
        int maxConcurrency = 2000,
        int initialConcurrency = 100,
        int maxStableRounds = 0)
    {
        _issueRequest = issueRequest;
        _getStateForWorker = getStateForWorker;
        _requestsPerBlock = requestsPerBlock;
        _warmupDuration = warmupDuration ?? TimeSpan.FromSeconds(5);
        _measurementInterval = measurementInterval ?? TimeSpan.FromSeconds(5);
        _minConcurrency = minConcurrency;
        _maxConcurrency = maxConcurrency;
        _initialConcurrency = initialConcurrency;
        _currentConcurrency = initialConcurrency;
        _stepSize = Math.Max(1, initialConcurrency / 10);
        _direction = 1;
        _maxStableRounds = maxStableRounds; // 0 = run forever
    }

    public async Task RunForeverAsync(CancellationToken cancellationToken = default)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        Console.WriteLine($"Starting adaptive load generator with initial concurrency: {_initialConcurrency}");
        Console.WriteLine($"Warmup duration: {_warmupDuration.TotalSeconds}s, Measurement interval: {_measurementInterval.TotalSeconds}s");
        Console.WriteLine($"Concurrency range: [{_minConcurrency}, {_maxConcurrency}]");
        if (_maxStableRounds > 0)
            Console.WriteLine($"Will terminate after {_maxStableRounds} rounds with no improvement to best");
        Console.WriteLine();

        // Warmup phase
        Console.WriteLine("=== WARMUP PHASE ===");
        await RunPhaseAsync(_warmupDuration, isWarmup: true);
        GC.Collect();

        Console.WriteLine();
        Console.WriteLine("=== TUNING PHASE ===");
        Console.WriteLine($"{"Time",-12} {"Concurrency",12} {"Throughput",14} {"Best",14} {"BestConc",10} {"Action",-20}");
        Console.WriteLine(new string('-', 82));

        var startTime = DateTime.UtcNow;

        while (!_cts.Token.IsCancellationRequested)
        {
            var throughput = await RunPhaseAsync(_measurementInterval, isWarmup: false);
            var elapsed = DateTime.UtcNow - startTime;

            var action = ApplyHillClimbing(throughput);

            var elapsedStr = elapsed.ToString(@"hh\:mm\:ss\.f");
            Console.WriteLine($"{elapsedStr,-12} {_currentConcurrency,12} {throughput,14:N0}/s {_bestThroughput,14:N0}/s {_bestConcurrency,10} {action,-20}");

            // Check for convergence
            if (_maxStableRounds > 0 && _roundsSinceBestChanged >= _maxStableRounds)
            {
                Converged = true;
                Console.WriteLine();
                Console.WriteLine($"Converged after {_roundsSinceBestChanged} rounds with no improvement.");
                break;
            }
        }
    }

    private async Task<double> RunPhaseAsync(TimeSpan duration, bool isWarmup)
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

        var throughput = await aggregator;

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

        return throughput;
    }

    private async Task<double> AggregateBlocksAsync(TimeSpan duration, bool isWarmup, CancellationTokenSource workerCts)
    {
        var reader = _completedBlocks.Reader;
        var startTime = Stopwatch.GetTimestamp();
        var endTime = startTime + (long)(duration.TotalSeconds * StopwatchTickPerSecond);

        long totalCompleted = 0;
        long totalSuccesses = 0;
        long totalFailures = 0;
        long minStartTime = long.MaxValue;
        long maxEndTime = long.MinValue;

        while (Stopwatch.GetTimestamp() < endTime && !_cts.Token.IsCancellationRequested)
        {
            try
            {
                var readTask = reader.WaitToReadAsync(_cts.Token).AsTask();
                var completed = await readTask.WaitAsync(TimeSpan.FromMilliseconds(100));

                if (!completed) continue;

                while (reader.TryRead(out var block))
                {
                    totalCompleted += block.Completed;
                    totalSuccesses += block.Successes;
                    totalFailures += block.Failures;
                    if (block.StartTimestamp < minStartTime) minStartTime = block.StartTimestamp;
                    if (block.EndTimestamp > maxEndTime) maxEndTime = block.EndTimestamp;
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
            totalCompleted += block.Completed;
            totalSuccesses += block.Successes;
            totalFailures += block.Failures;
            if (block.StartTimestamp < minStartTime) minStartTime = block.StartTimestamp;
            if (block.EndTimestamp > maxEndTime) maxEndTime = block.EndTimestamp;
        }

        if (totalCompleted == 0 || maxEndTime <= minStartTime)
        {
            return 0;
        }

        var totalSeconds = (maxEndTime - minStartTime) / StopwatchTickPerSecond;
        var throughput = totalCompleted / totalSeconds;

        if (isWarmup)
        {
            var failureInfo = totalFailures > 0 ? $" ({totalFailures} failures)" : "";
            Console.WriteLine($"  Warmup: {throughput:N0}/s, {totalCompleted:N0} requests in {totalSeconds:F1}s{failureInfo}");
        }

        return throughput;
    }

    private string ApplyHillClimbing(double currentThroughput)
    {
        string action;
        bool isNewBest = currentThroughput > _bestThroughput;

        // Always track the actual best
        if (isNewBest)
        {
            _bestThroughput = currentThroughput;
            _bestConcurrency = _currentConcurrency;
            _roundsSinceBestChanged = 0;
        }
        else
        {
            _roundsSinceBestChanged++;
        }

        // Determine if this is a meaningful improvement (resets stable count)
        // or just noise within measurement variance
        bool meaningfulImprovement = currentThroughput > _lastThroughput * 1.005; // 0.5% threshold

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

        _lastThroughput = currentThroughput;
        return action;
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
