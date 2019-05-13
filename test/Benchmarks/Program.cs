using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using BenchmarkDotNet.Running;
using Benchmarks.MapReduce;
using Benchmarks.Serialization;
using Benchmarks.Ping;
using Benchmarks.Transactions;
using Benchmarks.GrainStorage;

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
                    var benchmark = new TransactionBenchmark(2, 20000, 5000);
                    benchmark.MemorySetup();
                    return benchmark;
                },
                benchmark => benchmark.RunAsync().GetAwaiter().GetResult(),
                benchmark => benchmark.Teardown());
            },
            ["Transactions.Memory.Throttled"] = () =>
            {
                RunBenchmark(
                "Running Transactions benchmark",
                () =>
                {
                    var benchmark = new TransactionBenchmark(2, 200000, 15000);
                    benchmark.MemoryThrottledSetup();
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
                    var benchmark = new TransactionBenchmark(2, 20000, 5000);
                    benchmark.AzureSetup();
                    return benchmark;
                },
                benchmark => benchmark.RunAsync().GetAwaiter().GetResult(),
                benchmark => benchmark.Teardown());
            },
            ["Transactions.Azure.Throttled"] = () =>
            {
                RunBenchmark(
                "Running Transactions benchmark",
                () =>
                {
                    var benchmark = new TransactionBenchmark(2, 200000, 15000);
                    benchmark.AzureThrottledSetup();
                    return benchmark;
                },
                benchmark => benchmark.RunAsync().GetAwaiter().GetResult(),
                benchmark => benchmark.Teardown());
            },
            ["Transactions.Azure.Overloaded"] = () =>
            {
                RunBenchmark(
                "Running Transactions benchmark",
                () =>
                {
                    var benchmark = new TransactionBenchmark(2, 200000, 15000);
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
            ["GrainStorage.Memory"] = () =>
            {
                RunBenchmark(
                "Running grain storage benchmark against memory",
                () =>
                {
                    var benchmark = new GrainStorageBenchmark();
                    benchmark.MemorySetup();
                    return benchmark;
                },
                benchmark => benchmark.RunAsync().GetAwaiter().GetResult(),
                benchmark => benchmark.Teardown());
            },
            ["GrainStorage.AzureTable"] = () =>
            {
                RunBenchmark(
                "Running grain storage benchmark against Azure Table",
                () =>
                {
                    var benchmark = new GrainStorageBenchmark();
                    benchmark.AzureTableSetup();
                    return benchmark;
                },
                benchmark => benchmark.RunAsync().GetAwaiter().GetResult(),
                benchmark => benchmark.Teardown());
            },
            ["GrainStorage.AzureBlob"] = () =>
            {
                RunBenchmark(
                "Running grain storage benchmark against Azure Blob",
                () =>
                {
                    var benchmark = new GrainStorageBenchmark();
                    benchmark.AzureBlobSetup();
                    return benchmark;
                },
                benchmark => benchmark.RunAsync().GetAwaiter().GetResult(),
                benchmark => benchmark.Teardown());
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
            Console.WriteLine("Press any key to continue ...");
            tearDown(bench);
            Console.ReadLine();
        }
    }
}
