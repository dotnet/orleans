using System.Threading.Channels;
using System.Diagnostics;

namespace Benchmarks.Ping
{
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
        private readonly bool _logIntermediateResults;
        private readonly Task[] _tasks;
        private readonly TState[] _states;
        private readonly int _numWorkers;
        private readonly int _blocksPerWorker;
        private readonly int _requestsPerBlock;

        public ConcurrentLoadGenerator(
            int maxConcurrency,
            int blocksPerWorker,
            int requestsPerBlock,
            Func<TState, ValueTask> issueRequest,
            Func<int, TState> getStateForWorker,
            bool logIntermediateResults = false)
        {
            this._numWorkers = maxConcurrency;
            this._blocksPerWorker = blocksPerWorker;
            this._requestsPerBlock = requestsPerBlock;
            this._issueRequest = issueRequest;
            this._getStateForWorker = getStateForWorker;
            this._logIntermediateResults = logIntermediateResults;
            this._tasks = new Task[maxConcurrency];
            this._states = new TState[maxConcurrency];
        }

        public async Task Warmup()
        {
            this.ResetBetweenRuns();
            var completedBlockReader = this._completedBlocks.Reader;

            for (var ree = 0; ree < this._numWorkers; ree++)
            {
                this._states[ree] = _getStateForWorker(ree);
                this._tasks[ree] = this.RunWorker(this._states[ree], this._requestsPerBlock, 3);
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

        public async Task Run()
        {
            this.ResetBetweenRuns();
            var completedBlockReader = this._completedBlocks.Reader;

            // Start the run.
            for (var i = 0; i < this._numWorkers; i++)
            {
                this._tasks[i] = this.RunWorker(this._states[i], this._requestsPerBlock, this._blocksPerWorker);
            }

            _ = Task.Run(async () => { try { await Task.WhenAll(this._tasks); } catch { } finally { this._completedBlocks.Writer.Complete(); } });
            var blocks = new List<WorkBlock>(this._numWorkers * this._blocksPerWorker);
            var blocksPerReport = this._numWorkers * this._blocksPerWorker / 5;
            var nextReportBlockCount = blocksPerReport;
            while (true)
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
                    Console.WriteLine("    " + PrintReport(0));
                }
            }

            if (this._logIntermediateResults) Console.WriteLine("  Total: " + PrintReport(0));
            else Console.WriteLine(PrintReport(0));

            string PrintReport(int statingBlockIndex)
            {
                if (blocks.Count == 0) return "No blocks completed";
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
                var failureString = failures == 0 ? string.Empty : $" with {failures} failures";
                return $"{ratePerSecond,6}/s {successes,7} reqs in {totalSeconds,6:0.000}s{failureString}";
            }
        }

        private async Task RunWorker(TState state, int requestsPerBlock, int numBlocks)
        {
            var completedBlockWriter = this._completedBlocks.Writer;
            while (numBlocks > 0)
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
                await completedBlockWriter.WriteAsync(workBlock).ConfigureAwait(false);
                --numBlocks;
            }
        }
    }
}