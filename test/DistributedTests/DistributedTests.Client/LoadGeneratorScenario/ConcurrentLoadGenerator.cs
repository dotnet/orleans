using System.Threading.Channels;
using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace DistributedTests.Client
{
    public struct LoadGeneratorReport
    {
        public long Completed { get; set; }

        public long Successes { get; set; }

        public long Failures { get; set; }

        public double TotalDuration { get; set; }

        public int BlocksCompleted { get; set; }

        public readonly long RatePerSecond => (long)(Completed / TotalDuration);

        public override readonly string ToString()
        {
            if (BlocksCompleted == 0) return "No blocks completed";
            var failureString = Failures == 0 ? string.Empty : $" with {Failures} failures";
            return $"{RatePerSecond,6}/s {Successes,7} reqs in {TotalDuration,6:0.000}s{failureString}";
        }
    }

    public sealed class ConcurrentLoadGenerator<TState>
    {
        private static readonly double StopwatchTickPerSecond = Stopwatch.Frequency;
        private struct WorkBlock
        {
            public long StartTimestamp { get; set; }
            public long EndTimestamp { get; set; }
            public int Successes { get; set; }
            public int Failures { get; set; }
            public readonly int Completed => this.Successes + this.Failures;
            public readonly double ElapsedSeconds => (this.EndTimestamp - this.StartTimestamp) / StopwatchTickPerSecond;
            public readonly double RequestsPerSecond => this.Completed / this.ElapsedSeconds;
        }

        private Channel<WorkBlock> _completedBlocks;
        private readonly Func<TState, ValueTask> _issueRequest;
        private readonly Func<int, TState> _getStateForWorker;
        private readonly ILogger _logger;
        private readonly bool _logIntermediateResults;
        private readonly Task[] _tasks;
        private readonly TState[] _states;
        private readonly int _numWorkers;
        private readonly int _blocksPerWorker;
        private readonly int _requestsPerBlock;

        public ConcurrentLoadGenerator(
            int numWorkers,
            int blocksPerWorker,
            int requestsPerBlock,
            Func<TState, ValueTask> issueRequest,
            Func<int, TState> getStateForWorker,
            ILogger logger,
            bool logIntermediateResults = false)
        {
            this._numWorkers = numWorkers;
            this._blocksPerWorker = blocksPerWorker;
            this._requestsPerBlock = requestsPerBlock;
            this._issueRequest = issueRequest;
            this._getStateForWorker = getStateForWorker;
            this._logger = logger;
            this._logIntermediateResults = logIntermediateResults;
            this._tasks = new Task[numWorkers];
            this._states = new TState[numWorkers];
        }

        public async Task Warmup()
        {
            this.ResetBetweenRuns();
            var completedBlockReader = this._completedBlocks.Reader;

            for (var ree = 0; ree < this._numWorkers; ree++)
            {
                this._states[ree] = _getStateForWorker(ree);
                this._tasks[ree] = this.RunWorker(this._states[ree], this._requestsPerBlock, 3, default);
            }

            // Wait for warmup to complete.
            await Task.WhenAll(this._tasks);

            // Ignore warmup blocks.
            while (completedBlockReader.TryRead(out _)) ;
            GC.Collect();
            GC.Collect();
            GC.Collect();
        }

        private void ResetBetweenRuns()
        {
            this._completedBlocks = Channel.CreateUnbounded<WorkBlock>(
                new UnboundedChannelOptions
                {
                    SingleReader = true,
                    SingleWriter = false,
                    AllowSynchronousContinuations = false
                });
        }

        public async Task<LoadGeneratorReport> Run(CancellationToken ct)
        {
            this.ResetBetweenRuns();
            var completedBlockReader = this._completedBlocks.Reader;

            // Start the run.
            for (var i = 0; i < this._numWorkers; i++)
            {
                this._tasks[i] = this.RunWorker(this._states[i], this._requestsPerBlock, this._blocksPerWorker, ct);
            }

            var completion = Task.WhenAll(this._tasks);
            _ = Task.Run(async () => { try { await completion; } catch { } finally { this._completedBlocks.Writer.Complete(); } });
            // Do not allocated a list with a too high capacity
            var blocks = new List<WorkBlock>(this._numWorkers * Math.Min(100, this._blocksPerWorker));
            var blocksPerReport = this._numWorkers * Math.Min(100, this._blocksPerWorker) / 5;
            var nextReportBlockCount = blocksPerReport;
            while (!completion.IsCompleted)
            {
                var more = await completedBlockReader.WaitToReadAsync();
                if (!more) break;
                while (completedBlockReader.TryRead(out var block))
                {
                    blocks.Add(block);
                }

                if (this._logIntermediateResults && blocks.Count >= nextReportBlockCount)
                {
                    nextReportBlockCount += blocksPerReport;
                    _logger.LogInformation("    " + BuildReport(0));
                }
            }

            var finalReport = BuildReport(0);

            if (this._logIntermediateResults) _logger.LogInformation("  Total: " + finalReport);
            else _logger.LogInformation(finalReport.ToString());

            return finalReport;

            LoadGeneratorReport BuildReport(int statingBlockIndex)
            {
                if (blocks.Count == 0) return default;
                var successes = 0;
                var failures = 0;
                long completed = 0;
                var reportBlocks = 0;
                long minStartTime = long.MaxValue;
                long maxEndTime = long.MinValue;
                for (var i = statingBlockIndex; i < blocks.Count; i++)
                {
                    var b = blocks[i];
                    ++reportBlocks;
                    successes += b.Successes;
                    failures += b.Failures;
                    completed += b.Completed;
                    if (b.StartTimestamp < minStartTime) minStartTime = b.StartTimestamp;
                    if (b.EndTimestamp > maxEndTime) maxEndTime = b.EndTimestamp;
                }

                var totalSeconds = (maxEndTime - minStartTime) / StopwatchTickPerSecond;
                var ratePerSecond = (long)(completed / totalSeconds);
                return new LoadGeneratorReport
                {
                    Completed = completed,
                    Successes = successes,
                    Failures = failures,
                    BlocksCompleted = reportBlocks,
                    TotalDuration = totalSeconds,
                };
            }
        }

        private async Task RunWorker(TState state, int requestsPerBlock, int numBlocks, CancellationToken ct)
        {
            var completedBlockWriter = this._completedBlocks.Writer;
            while (numBlocks > 0 && !ct.IsCancellationRequested)
            {
                var workBlock = new WorkBlock();
                workBlock.StartTimestamp = Stopwatch.GetTimestamp();
                while (workBlock.Completed < requestsPerBlock)
                {
                    try
                    {
                        await this._issueRequest(state).ConfigureAwait(false);
                        ++workBlock.Successes;
                    }
                    catch
                    {
                        ++workBlock.Failures;
                    }
                }

                workBlock.EndTimestamp = Stopwatch.GetTimestamp();
                await completedBlockWriter.WriteAsync(workBlock);
                --numBlocks;
            }
        }
    }
}