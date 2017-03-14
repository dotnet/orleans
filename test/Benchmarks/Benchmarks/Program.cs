using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using BenchmarkDotNet.Running;
using Benchmarks.MapReduce;
using Benchmarks.Serialization;

namespace Benchmarks
{
    class Program
    {
        private static readonly Dictionary<string, Action> _benchmarks = new Dictionary<string, Action>
        {
            ["MapReduce"] = () =>
            {
                RunBenchmark(
                "Running MapReduce benchmark", 
                () =>
                {
                    var mapReduceBenchmark = new MapReduceBenchmark();
                    mapReduceBenchmark.BenchmarkSetup();
                    return mapReduceBenchmark;
                },
                     benchmark =>
                     {
                         benchmark.Bench().Wait();
                     },
                benchmark => benchmark.Teardown());
            },
            ["Serialization"] = () =>
            {
            }
        };

        // requires benchmark name or 'All' word as first parameter
        static void Main(string[] args)
        {
            _benchmarks["MapReduce"]();
        }

        private static void RunBenchmark<T>(string name, Func<T> init, Action<T> benchmarkAction, Action<T> tearDown)
        {
            Console.WriteLine(name);
            var bench = init();
            var stopWatch = Stopwatch.StartNew();
            benchmarkAction(bench);
            Console.WriteLine($"Elapsed milliseconds: {stopWatch.ElapsedMilliseconds}");
            tearDown(bench);
            Console.ReadLine();
        }
    }
}
