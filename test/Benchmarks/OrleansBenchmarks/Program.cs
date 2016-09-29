using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using BenchmarkDotNet.Running;
using OrleansBenchmarks.MapReduce;

namespace OrleansBenchmarks
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
                benchmark => benchmark.Bench().Wait(),
                benchmark => benchmark.Teardown());
            },
            ["Serialization"] = () =>
            {
                var summary = BenchmarkRunner.Run<SerializationBenchmarks.SerializationBenchmarks>();
            }
        };

        // requires benchmark name or 'All' word as first parameter
        static void Main(string[] args)
        {
            if (args.Length > 0 && args[0].Equals("all", StringComparison.InvariantCultureIgnoreCase))
            {
                Console.WriteLine("Running full benchmarks suite");
                _benchmarks.Select(pair => pair.Value).ToList().ForEach(action => action());
                return;
            }

            if (args.Length == 0 || !_benchmarks.ContainsKey(args[0]))
            {
                Console.WriteLine("Please, select benchmark, list of available:");
                _benchmarks
                    .Select(pair => pair.Key)
                    .ToList()
                    .ForEach(Console.WriteLine);
                Console.WriteLine("All");
                return;
            }

            _benchmarks[args[0]]();

            Console.Read();
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
