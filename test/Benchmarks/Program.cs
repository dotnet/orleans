using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using BenchmarkDotNet.Running;
using Benchmarks.MapReduce;
using Benchmarks.Serialization;
using Benchmarks.Ping;
using Benchmarks.Transactions;

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
                benchmark => benchmark.Bench().GetAwaiter().GetResult(),
                benchmark => benchmark.Teardown());
            },
            ["Serialization"] = () =>
            {
                BenchmarkRunner.Run<SerializationBenchmarks>();
            },
            ["Transactions.Memory"] = () =>
            {
                RunBenchmark(
                "Running Transactions benchmark",
                () =>
                {
                    var benchmark = new TransactionBenchmark();
                    benchmark.Setup();
                    return benchmark;
                },
                benchmark => benchmark.RunAsync().GetAwaiter().GetResult(),
                benchmark => benchmark.Teardown());
            },
            ["Transactions.Azure"] = () =>
            {
                RunBenchmark(
                "Running Transactions benchmark",
                () =>
                {
                    var benchmark = new TransactionBenchmark();
                    benchmark.AzureSetup();
                    return benchmark;
                },
                benchmark => benchmark.RunAsync().GetAwaiter().GetResult(),
                benchmark => benchmark.Teardown());
            },
            ["Ping"] = () =>
            {
                RunBenchmark(
                    "Running Ping benchmark",
                    () =>
                    {
                        var benchmark = new PingBenchmark();
                        benchmark.Setup();
                        return benchmark;
                    },
                    benchmark => benchmark.RunAsync().GetAwaiter().GetResult(),
                    benchmark => benchmark.Teardown());
            },
            ["SequentialPing"] = () =>
            {
                BenchmarkRunner.Run<SequentialPingBenchmark>();
            },
            ["PingForever"] = () =>
            {
                new SequentialPingBenchmark().PingForever().GetAwaiter().GetResult();
            },
            ["PingPongForever"] = () =>
            {
                new SequentialPingBenchmark().PingPongForever().GetAwaiter().GetResult();
            },
            ["PingPongForeverSaturate"] = () =>
            {
                new SequentialPingBenchmark().PingPongForever().GetAwaiter().GetResult();
            },
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
        }

        private static void RunBenchmark<T>(string name, Func<T> init, Action<T> benchmarkAction, Action<T> tearDown)
        {
            Console.WriteLine(name);
            var bench = init();
            var stopWatch = Stopwatch.StartNew();
            benchmarkAction(bench);
            Console.WriteLine($"Elapsed milliseconds: {stopWatch.ElapsedMilliseconds}");
            tearDown(bench);
            Console.WriteLine("Press any key to continue ...");
            Console.ReadLine();
        }
    }
}
