using System;
using System.Threading.Tasks;
using System.Threading.Channels;
using System.Collections.Generic;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using System.Threading;

namespace DistributedTests.Client
{
    public struct LoadGeneratorReport
    {
        public long Completed { get; set; }

        public long Successes { get; set; }

        public long Failures { get; set; }

        public double TotalDuration { get; set; }

        public int BlocksCompleted { get; set; }

        public long RatePerSecond => (long)(Completed / TotalDuration);

        public override string ToString()
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
            public int Completed => this.Successes + this.Failures;
            public double ElapsedSeconds => (this.EndTimestamp - this.StartTimestamp) / StopwatchTickPerSecond;
            public double RequestsPerSecond => this.Completed / this.ElapsedSeconds;
        }

        private Channel<WorkBlock> completedBlocks;
        private readonly Func<TState, ValueTask> issueRequest;
        private readonly Func<int, TState> getStateForWorker;
        private readonly ILogger logger;
        private readonly bool logIntermediateResults;
        private readonly Task[] tasks;
        private readonly TState[] states;
        private readonly int numWorkers;
        private readonly int blocksPerWorker;
        private readonly int requestsPerBlock;

        public ConcurrentLoadGenerator(
            int numWorkers,
            int blocksPerWorker,
            int requestsPerBlock,
            Func<TState, ValueTask> issueRequest,
            Func<int, TState> getStateForWorker,
            ILogger logger,
            bool logIntermediateResults = false)
        {
            this.numWorkers = numWorkers;
            this.blocksPerWorker = blocksPerWorker;
            this.requestsPerBlock = requestsPerBlock;
            this.issueRequest = issueRequest;
            this.getStateForWorker = getStateForWorker;
            this.logger = logger;
            this.logIntermediateResults = logIntermediateResults;
            this.tasks = new Task[numWorkers];
            this.states = new TState[numWorkers];
        }

        public async Task Warmup()
        {
            this.ResetBetweenRuns();
            var completedBlockReader = this.completedBlocks.Reader;

            for (var ree = 0; ree < this.numWorkers; ree++)
            {
                this.states[ree] = getStateForWorker(ree);
                this.tasks[ree] = this.RunWorker(this.states[ree], this.requestsPerBlock, 3, default);
            }

            // Wait for warmup to complete.
            await Task.WhenAll(this.tasks);

            // Ignore warmup blocks.
            while (completedBlockReader.TryRead(out _));
            GC.Collect();
            GC.Collect();
            GC.Collect();
        }

        private void ResetBetweenRuns()
        {
            this.completedBlocks = Channel.CreateUnbounded<WorkBlock>(
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
            var completedBlockReader = this.completedBlocks.Reader;

            // Start the run.
            for (var i = 0; i < this.numWorkers; i++)
            {
                this.tasks[i] = this.RunWorker(this.states[i], this.requestsPerBlock, this.blocksPerWorker, ct);
            }

            var completion = Task.WhenAll(this.tasks);
            _ = Task.Run(async () => { try { await completion; } catch { } finally { this.completedBlocks.Writer.Complete(); } });
            // Do not allocated a list with a too high capacity
            var blocks = new List<WorkBlock>(this.numWorkers * Math.Min(100, this.blocksPerWorker));
            var blocksPerReport = this.numWorkers * Math.Min(100, this.blocksPerWorker) / 5;
            var nextReportBlockCount = blocksPerReport;
            while (!completion.IsCompleted)
            {
                var more = await completedBlockReader.WaitToReadAsync();
                if (!more) break;
                while (completedBlockReader.TryRead(out var block))
                {
                    blocks.Add(block);
                }

                if (this.logIntermediateResults && blocks.Count >= nextReportBlockCount)
                {
                    nextReportBlockCount += blocksPerReport;
                    logger.LogInformation("    " + BuildReport(0));
                }
            }

            var finalReport = BuildReport(0);

            if (this.logIntermediateResults) logger.LogInformation("  Total: " + finalReport);
            else logger.LogInformation(finalReport.ToString());

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
            var completedBlockWriter = this.completedBlocks.Writer;
            while (numBlocks > 0 && !ct.IsCancellationRequested)
            {
                var workBlock = new WorkBlock();
                workBlock.StartTimestamp = Stopwatch.GetTimestamp();
                while (workBlock.Completed < requestsPerBlock)
                {
                    try
                    {
                        await this.issueRequest(state).ConfigureAwait(false);
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