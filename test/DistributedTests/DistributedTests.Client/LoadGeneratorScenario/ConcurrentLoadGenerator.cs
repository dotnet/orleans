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
            public readonly int Completed => Successes + Failures;
            public readonly double ElapsedSeconds => (EndTimestamp - StartTimestamp) / StopwatchTickPerSecond;
            public double RequestsPerSecond => Completed / ElapsedSeconds;
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
            tasks = new Task[numWorkers];
            states = new TState[numWorkers];
        }

        public async Task Warmup()
        {
            ResetBetweenRuns();
            var completedBlockReader = completedBlocks.Reader;

            for (var ree = 0; ree < numWorkers; ree++)
            {
                states[ree] = getStateForWorker(ree);
                tasks[ree] = RunWorker(states[ree], requestsPerBlock, 3, default);
            }

            // Wait for warmup to complete.
            await Task.WhenAll(tasks);

            // Ignore warmup blocks.
            while (completedBlockReader.TryRead(out _));
            GC.Collect();
            GC.Collect();
            GC.Collect();
        }

        private void ResetBetweenRuns()
        {
            completedBlocks = Channel.CreateUnbounded<WorkBlock>(
                new UnboundedChannelOptions
                {
                    SingleReader = true,
                    SingleWriter = false,
                    AllowSynchronousContinuations = false
                });
        }

        public async Task<LoadGeneratorReport> Run(CancellationToken ct)
        {
            ResetBetweenRuns();
            var completedBlockReader = completedBlocks.Reader;

            // Start the run.
            for (var i = 0; i < numWorkers; i++)
            {
                tasks[i] = RunWorker(states[i], requestsPerBlock, blocksPerWorker, ct);
            }

            var completion = Task.WhenAll(tasks);
            _ = Task.Run(async () => { try { await completion; } catch { } finally { completedBlocks.Writer.Complete(); } });
            // Do not allocated a list with a too high capacity
            var blocks = new List<WorkBlock>(numWorkers * Math.Min(100, blocksPerWorker));
            var blocksPerReport = numWorkers * Math.Min(100, blocksPerWorker) / 5;
            var nextReportBlockCount = blocksPerReport;
            while (!completion.IsCompleted)
            {
                var more = await completedBlockReader.WaitToReadAsync();
                if (!more) break;
                while (completedBlockReader.TryRead(out var block))
                {
                    blocks.Add(block);
                }

                if (logIntermediateResults && blocks.Count >= nextReportBlockCount)
                {
                    nextReportBlockCount += blocksPerReport;
                    logger.LogInformation("    " + BuildReport(0));
                }
            }

            var finalReport = BuildReport(0);

            if (logIntermediateResults) logger.LogInformation("  Total: " + finalReport);
            else logger.LogInformation(finalReport.ToString());

            return finalReport;

            LoadGeneratorReport BuildReport(int statingBlockIndex)
            {
                if (blocks.Count == 0) return default;
                var successes = 0;
                var failures = 0;
                long completed = 0;
                var reportBlocks = 0;
                var minStartTime = long.MaxValue;
                var maxEndTime = long.MinValue;
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
            var completedBlockWriter = completedBlocks.Writer;
            while (numBlocks > 0 && !ct.IsCancellationRequested)
            {
                var workBlock = new WorkBlock();
                workBlock.StartTimestamp = Stopwatch.GetTimestamp();
                while (workBlock.Completed < requestsPerBlock)
                {
                    try
                    {
                        await issueRequest(state).ConfigureAwait(false);
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