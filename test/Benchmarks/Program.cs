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
using Benchmarks.PooledQueueCacheBenchmark;

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
            ["SequentialPing"] = () =>
            {
                BenchmarkRunner.Run<PingBenchmark>();
            },
            ["ConcurrentPing"] = () =>
            {
                {
                    Console.WriteLine("## Client to Silo ##");
                    var test = new PingBenchmark(numSilos: 1, startClient: true);
                    test.PingConcurrent().GetAwaiter().GetResult();
                    test.Shutdown().GetAwaiter().GetResult();
                }
                GC.Collect();
                {
                    Console.WriteLine("## Client to 2 Silos ##");
                    var test = new PingBenchmark(numSilos: 2, startClient: true);
                    test.PingConcurrent().GetAwaiter().GetResult();
                    test.Shutdown().GetAwaiter().GetResult();
                }
                GC.Collect();
                {
                    Console.WriteLine("## Hosted Client ##");
                    var test = new PingBenchmark(numSilos: 1, startClient: false);
                    test.PingConcurrentHostedClient().GetAwaiter().GetResult();
                    test.Shutdown().GetAwaiter().GetResult();
                }
                GC.Collect();
                {
                    // All calls are cross-silo because the calling silo doesn't have any grain classes.
                    Console.WriteLine("## Silo to Silo ##");
                    var test = new PingBenchmark(numSilos: 2, startClient: false, grainsOnSecondariesOnly: true);
                    test.PingConcurrentHostedClient().GetAwaiter().GetResult();
                    test.Shutdown().GetAwaiter().GetResult();
                }
            },
            ["ConcurrentPing_OneSilo"] = () =>
            {
                new PingBenchmark(numSilos: 1, startClient: true).PingConcurrent().GetAwaiter().GetResult();
            },
            ["ConcurrentPing_TwoSilos"] = () =>
            {
                new PingBenchmark(numSilos: 2, startClient: true).PingConcurrent().GetAwaiter().GetResult();
            },
            ["ConcurrentPing_HostedClient"] = () =>
            {
                new PingBenchmark(numSilos: 1, startClient: false).PingConcurrentHostedClient().GetAwaiter().GetResult();
            },
            ["ConcurrentPing_SiloToSilo"] = () =>
            {
                new PingBenchmark(numSilos: 2, startClient: false, grainsOnSecondariesOnly: true).PingConcurrentHostedClient().GetAwaiter().GetResult();                
            },
            ["PingForever"] = () =>
            {
                new PingBenchmark().PingForever().GetAwaiter().GetResult();
            },
            ["PingPongForever"] = () =>
            {
                new PingBenchmark().PingPongForever().GetAwaiter().GetResult();
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
            ["PooledQueueCache"] = () =>
            {
                RunBenchmark(
                "Running Transactions benchmark",
                () =>
                {
                    var benchmark = new PooledQueueCacheBenchmarks();
                    benchmark.BenchmarkSetup();
                    return benchmark;
                },
                benchmark => benchmark.Run(),
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
