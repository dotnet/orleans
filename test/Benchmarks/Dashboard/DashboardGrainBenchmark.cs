using System;
using System.Collections.Generic;
using System.Linq;
using BenchmarkDotNet.Attributes;
using Orleans.Dashboard.Metrics.History;
using System.Collections;

namespace Benchmarks.Dashboard
{
    [ShortRunJob]
    [MemoryDiagnoser]
    internal class DashboardGrainBenchmark
    {
        [Params(10)]
        public int SiloCount { get; set; }

        [Params(50)]
        public int GrainTypeCount { get; set; }

        [Params(10)]
        public int GrainMethodCount { get; set; }

        [Params(100)]
        public int HistorySize { get; set; }

        [ParamsSource(nameof(Histories))]
        public ITraceHistory History { get; set; }

        public IEnumerable<ITraceHistory> Histories
        {
            get
            {
                yield return new TraceHistory(HistorySize);
            }
        }

        [GlobalSetup]
        public void Setup()
        {
            var startTime = DateTime.UtcNow;

            Setup(startTime, History);

            testTraces = Helper.CreateTraces(startTime.AddSeconds(HistorySize), SiloCount, GrainTypeCount, GrainMethodCount).ToList();
        }

        private List<TestTraces> testTraces;

        [Benchmark]
        public void Test_Add_TraceHistory()
        {
            foreach (var trace in testTraces)
            {
                History.Add(trace.Time, trace.Silo, trace.Traces);
            }
        }
        
        [Benchmark]
        public ICollection Test_QueryAll_TraceHistory()
        {
            return History.QueryAll();
        }

        [Benchmark]
        public ICollection Test_QuerySilo_TraceHistory()
        {
            return History.QuerySilo("SILO_0");
        }

        [Benchmark]
        public ICollection Test_QueryGrain_TraceHistory()
        {
            return History.QueryGrain("GRAIN_0");
        }

        [Benchmark]
        public ICollection Test_GroupByGrainAndSilo_TraceHistory()
        {
            return History.GroupByGrainAndSilo().ToList();
        }
        
        [Benchmark]
        public ICollection Test_AggregateByGrainMethod_TraceHistory()
        {
            return History.AggregateByGrainMethod().ToList();
        }

        private void Setup(DateTime startTime, ITraceHistory history)
        {
            for (var timeIndex = 0; timeIndex < HistorySize; timeIndex++)
            {
                var time = startTime.AddSeconds(timeIndex);

                foreach (var trace in Helper.CreateTraces(time, SiloCount, GrainTypeCount, GrainMethodCount))
                {
                    history.Add(trace.Time, trace.Silo, trace.Traces);
                }
            }
        }
    }
}
