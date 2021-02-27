using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using BenchmarkDotNet.Running;
using Benchmarks.MapReduce;
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
                    test.PingConcurrentHostedClient(blocksPerWorker: 10).GetAwaiter().GetResult();
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
            ["ConcurrentPing_HostedClient_Forever"] = () =>
            {
                var benchmark = new PingBenchmark(numSilos: 1, startClient: false);
                Console.WriteLine("Press any key to begin.");
                Console.ReadKey();
                Console.WriteLine("Press any key to end.");
                Console.WriteLine("## Hosted Client ##");
                while (!Console.KeyAvailable)
                {
                    benchmark.PingConcurrentHostedClient().GetAwaiter().GetResult();
                }

                Console.WriteLine("Interrupted by user");
            },
            ["ConcurrentPing_SiloToSilo"] = () =>
            {
                new PingBenchmark(numSilos: 2, startClient: false, grainsOnSecondariesOnly: true).PingConcurrentHostedClient(blocksPerWorker: 10).GetAwaiter().GetResult();                
            },
            ["ConcurrentPing_SiloToSilo_Forever"] = () =>
            {
                Console.WriteLine("Press any key to begin.");
                Console.ReadKey();
                Console.WriteLine("Press any key to end.");
                Console.WriteLine("## Silo to Silo ##");
                while (!Console.KeyAvailable)
                {
                    var test = new PingBenchmark(numSilos: 2, startClient: false, grainsOnSecondariesOnly: true);
                    Console.WriteLine("Starting");
                    test.PingConcurrentHostedClient(blocksPerWorker: 10).GetAwaiter().GetResult();
                    Console.WriteLine("Stopping");
                    test.Shutdown().GetAwaiter().GetResult();
                }

                Console.WriteLine("Interrupted by user");
            },
            ["ConcurrentPing_SiloToSilo_Long"] = () =>
            {
                new PingBenchmark(numSilos: 2, startClient: false, grainsOnSecondariesOnly: true).PingConcurrentHostedClient(blocksPerWorker: 1000).GetAwaiter().GetResult();
            },
            ["ConcurrentPing_OneSilo_Forever"] = () =>
            {
                new PingBenchmark(numSilos: 1, startClient: true).PingConcurrentForever().GetAwaiter().GetResult();
            },
            ["PingOnce"] = () =>
            {
                new PingBenchmark().Ping().GetAwaiter().GetResult();
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
                    var benchmark = new GrainStorageBenchmark(10, 10000, TimeSpan.FromSeconds( 30 ));
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
                    var benchmark = new GrainStorageBenchmark(100, 10000, TimeSpan.FromSeconds( 30 ));
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
                    var benchmark = new GrainStorageBenchmark(10, 10000, TimeSpan.FromSeconds( 30 ));
                    benchmark.AzureBlobSetup();
                    return benchmark;
                },
                benchmark => benchmark.RunAsync().GetAwaiter().GetResult(),
                benchmark => benchmark.Teardown());
            },
            ["GrainStorage.AdoNet"] = () =>
            {
                RunBenchmark(
                "Running grain storage benchmark against AdoNet",
                () =>
                {
                    var benchmark = new GrainStorageBenchmark(100, 10000, TimeSpan.FromSeconds( 30 ));
                    benchmark.AdoNetSetup();
                    return benchmark;
                },
                benchmark => benchmark.RunAsync().GetAwaiter().GetResult(),
                benchmark => benchmark.Teardown());
            },
        };

        // requires benchmark name or 'All' word as first parameter
        public static void Main(string[] args)
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
