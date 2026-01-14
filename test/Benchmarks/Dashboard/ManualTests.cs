using Orleans.Dashboard.Metrics.History;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace Benchmarks.Dashboard
{
    internal class ManualTests
    {
        private const int HistorySize = 100;
        private readonly ITraceHistory history1 = new TraceHistory(HistorySize);
        private readonly List<TestTraces> testTraces;

        public ManualTests()
        {
            var startTime = DateTime.UtcNow;

            Setup(startTime, history1);

            testTraces = Helper.CreateTraces(startTime.AddSeconds(HistorySize), 10, 50, 10).ToList();
        }

        private static void Setup(DateTime startTime, ITraceHistory history)
        {
            for (var timeIndex = 0; timeIndex < HistorySize; timeIndex++)
            {
                var time = startTime.AddSeconds(timeIndex);

                foreach (var trace in Helper.CreateTraces(time, 10, 50, 10))
                {
                    history.Add(trace.Time, trace.Silo, trace.Traces);
                }
            }
        }

        public void Run()
        {
            Test("Add", history =>
            {
                foreach (var trace in testTraces)
                {
                    history.Add(trace.Time, trace.Silo, trace.Traces);
                }
            });

            Test("Query All", history =>
            {
                history.QueryAll();
            });

            Test("Query By Silo", history =>
            {
                history.QuerySilo("SILO_0");
            });

            Test("Query By Grain", history =>
            {
                history.QueryGrain("GRAIN_0");
            });

            Test("Query By Grain and Silo", history =>
            {
                history.GroupByGrainAndSilo();
            });

            Test("Query Aggregated", history =>
            {
                history.AggregateByGrainMethod();
            });
        }

        private void Test(string name, Action<ITraceHistory> action)
        {
            const int NumIterations = 1;

            var watch = Stopwatch.StartNew();

            for (var i = 0; i < NumIterations; i++)
            {
                action(history1);
            }

            watch.Start();

            Console.WriteLine("{0} V1: {1}", name, watch.Elapsed / NumIterations);
        }
    }
}
