using System;
using System.Threading.Tasks;
using System.Threading.Channels;
using System.Collections.Generic;
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
            public int Completed => this.Successes + this.Failures;
            public double ElapsedSeconds => (this.EndTimestamp - this.StartTimestamp) / StopwatchTickPerSecond;
            public double RequestsPerSecond => this.Completed / this.ElapsedSeconds;
        }

        private Channel<WorkBlock> completedBlocks;
        private readonly Func<TState, ValueTask> issueRequest;
        private readonly Func<int, TState> getStateForWorker;
        private readonly bool logIntermediateResults;
        private readonly Task[] tasks;
        private readonly TState[] states;
        private readonly int numWorkers;
        private readonly int blocksPerWorker;
        private readonly int requestsPerBlock;

        public ConcurrentLoadGenerator(
            int maxConcurrency,
            int blocksPerWorker,
            int requestsPerBlock,
            Func<TState, ValueTask> issueRequest,
            Func<int, TState> getStateForWorker,
            bool logIntermediateResults = false)
        {
            this.numWorkers = maxConcurrency;
            this.blocksPerWorker = blocksPerWorker;
            this.requestsPerBlock = requestsPerBlock;
            this.issueRequest = issueRequest;
            this.getStateForWorker = getStateForWorker;
            this.logIntermediateResults = logIntermediateResults;
            this.tasks = new Task[maxConcurrency];
            this.states = new TState[maxConcurrency];
        }

        public async Task Warmup()
        {
            this.ResetBetweenRuns();
            var completedBlockReader = this.completedBlocks.Reader;

            for (var ree = 0; ree < this.numWorkers; ree++)
            {
                this.states[ree] = getStateForWorker(ree);
                this.tasks[ree] = this.RunWorker(this.states[ree], this.requestsPerBlock, 3);
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

        public async Task Run()
        {
            this.ResetBetweenRuns();
            var completedBlockReader = this.completedBlocks.Reader;

            // Start the run.
            for (var i = 0; i < this.numWorkers; i++)
            {
                this.tasks[i] = this.RunWorker(this.states[i], this.requestsPerBlock, this.blocksPerWorker);
            }

            _ = Task.Run(async () => { try { await Task.WhenAll(this.tasks); } catch { } finally { this.completedBlocks.Writer.Complete(); } });
            var blocks = new List<WorkBlock>(this.numWorkers * this.blocksPerWorker);
            var blocksPerReport = this.numWorkers * this.blocksPerWorker / 5;
            var nextReportBlockCount = blocksPerReport;
            while (true)
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
                    Console.WriteLine("    " + PrintReport(0));
                }
            }

            if (this.logIntermediateResults) Console.WriteLine("  Total: " + PrintReport(0));
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
            var completedBlockWriter = this.completedBlocks.Writer;
            while (numBlocks > 0)
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
                await completedBlockWriter.WriteAsync(workBlock).ConfigureAwait(false);
                --numBlocks;
            }
        }
    }
}