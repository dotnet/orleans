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
            public int Completed => Successes + Failures;
            public double ElapsedSeconds => (EndTimestamp - StartTimestamp) / StopwatchTickPerSecond;
            public double RequestsPerSecond => Completed / ElapsedSeconds;
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
            numWorkers = maxConcurrency;
            this.blocksPerWorker = blocksPerWorker;
            this.requestsPerBlock = requestsPerBlock;
            this.issueRequest = issueRequest;
            this.getStateForWorker = getStateForWorker;
            this.logIntermediateResults = logIntermediateResults;
            tasks = new Task[maxConcurrency];
            states = new TState[maxConcurrency];
        }

        public async Task Warmup()
        {
            ResetBetweenRuns();
            var completedBlockReader = completedBlocks.Reader;

            for (var ree = 0; ree < numWorkers; ree++)
            {
                states[ree] = getStateForWorker(ree);
                tasks[ree] = RunWorker(states[ree], requestsPerBlock, 3);
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

        public async Task Run()
        {
            ResetBetweenRuns();
            var completedBlockReader = completedBlocks.Reader;

            // Start the run.
            for (var i = 0; i < numWorkers; i++)
            {
                tasks[i] = RunWorker(states[i], requestsPerBlock, blocksPerWorker);
            }

            _ = Task.Run(async () => { try { await Task.WhenAll(tasks); } catch { } finally { completedBlocks.Writer.Complete(); } });
            var blocks = new List<WorkBlock>(numWorkers * blocksPerWorker);
            var blocksPerReport = numWorkers * blocksPerWorker / 5;
            var nextReportBlockCount = blocksPerReport;
            while (true)
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
                    Console.WriteLine("    " + PrintReport(0));
                }
            }

            if (logIntermediateResults) Console.WriteLine("  Total: " + PrintReport(0));
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
            var completedBlockWriter = completedBlocks.Writer;
            while (numBlocks > 0)
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
                await completedBlockWriter.WriteAsync(workBlock).ConfigureAwait(false);
                --numBlocks;
            }
        }
    }
}