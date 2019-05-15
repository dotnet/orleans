using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using Orleans.Runtime;

namespace Silo
{
    [ShortRunJob, RunOncePerIteration]
    public class AsyncPipelineSlimBenchmarks
    {
        public const int ConstTaskCount = 1 << 10;

        public IEnumerable<Task> Tasks;
        public IEnumerable<Func<Task>> Actions;

        [GlobalSetup]
        public void GlobalSetup()
        {
            Tasks = Enumerable.Range(0, TaskCount).Select(_ => Task.Delay(1));
            Actions = Enumerable.Range(0, TaskCount).Select<int, Func<Task>>(_ => () => Task.Delay(1));
        }

        [Params(1, 2, 4, 8, 16)]
        public int Capacity { get; set; }

        [Params(ConstTaskCount)]
        public int TaskCount { get; set; }

        [Benchmark(Baseline = true, OperationsPerInvoke = ConstTaskCount)]
        public void AsyncPipeline()
        {
            var pipeline = new AsyncPipeline(Capacity);
            pipeline.AddRange(Tasks);
            pipeline.Wait();
        }

        [Benchmark(OperationsPerInvoke = ConstTaskCount)]
        public void AsyncPipelineSlim()
        {
            var pipeline = new AsyncPipelineSlim(Capacity);
            pipeline.AddRange(Actions);
            pipeline.Wait();
        }
    }
}