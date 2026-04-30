using System.Diagnostics;
using Benchmarks.Scenarios.MapReduce;
using Benchmarks.Scenarios.Ping;
using Benchmarks.Scenarios.Transactions;
using Benchmarks.Scenarios.GrainStorage;

namespace Benchmarks.Scenarios;

internal class Program
{
    private static readonly Dictionary<string, Action<string[]>> _scenarios = new Dictionary<string, Action<string[]>>
    {
        ["MapReduce"] = _ =>
        {
            RunScenario(
            "Running MapReduce scenario",
            () =>
            {
                var scenario = new MapReduceScenario();
                scenario.Setup();
                return scenario;
            },
            scenario => scenario.RunAsync().GetAwaiter().GetResult(),
            scenario => scenario.Teardown());
        },
        ["Transactions.Memory"] = _ =>
        {
            RunScenario(
            "Running Transactions scenario",
            () =>
            {
                var scenario = new TransactionScenario(2, 20000, 5000);
                scenario.MemorySetup();
                return scenario;
            },
            scenario => scenario.RunAsync().GetAwaiter().GetResult(),
            scenario => scenario.Teardown());
        },
        ["Transactions.Memory.Throttled"] = _ =>
        {
            RunScenario(
            "Running Transactions scenario",
            () =>
            {
                var scenario = new TransactionScenario(2, 200000, 15000);
                scenario.MemoryThrottledSetup();
                return scenario;
            },
            scenario => scenario.RunAsync().GetAwaiter().GetResult(),
            scenario => scenario.Teardown());
        },
        ["Transactions.Azure"] = _ =>
        {
            RunScenario(
            "Running Transactions scenario",
            () =>
            {
                var scenario = new TransactionScenario(2, 20000, 5000);
                scenario.AzureSetup();
                return scenario;
            },
            scenario => scenario.RunAsync().GetAwaiter().GetResult(),
            scenario => scenario.Teardown());
        },
        ["Transactions.Azure.Throttled"] = _ =>
        {
            RunScenario(
            "Running Transactions scenario",
            () =>
            {
                var scenario = new TransactionScenario(2, 200000, 15000);
                scenario.AzureThrottledSetup();
                return scenario;
            },
            scenario => scenario.RunAsync().GetAwaiter().GetResult(),
            scenario => scenario.Teardown());
        },
        ["Transactions.Azure.Overloaded"] = _ =>
        {
            RunScenario(
            "Running Transactions scenario",
            () =>
            {
                var scenario = new TransactionScenario(2, 200000, 15000);
                scenario.AzureSetup();
                return scenario;
            },
            scenario => scenario.RunAsync().GetAwaiter().GetResult(),
            scenario => scenario.Teardown());
        },
        ["ConcurrentPing"] = _ =>
        {
            {
                Console.WriteLine("## Client to Silo ##");
                using var test = new PingScenario(numSilos: 1, startClient: true);
                test.PingConcurrent().GetAwaiter().GetResult();
                test.Shutdown().GetAwaiter().GetResult();
            }
            {
                Console.WriteLine("## Client to 2 Silos ##");
                using var test = new PingScenario(numSilos: 2, startClient: true);
                test.PingConcurrent().GetAwaiter().GetResult();
                test.Shutdown().GetAwaiter().GetResult();
            }
            {
                Console.WriteLine("## Hosted Client ##");
                using var test = new PingScenario(numSilos: 1, startClient: false);
                test.PingConcurrentHostedClient().GetAwaiter().GetResult();
                test.Shutdown().GetAwaiter().GetResult();
            }
            {
                // All calls are cross-silo because the calling silo doesn't have any grain classes.
                Console.WriteLine("## Silo to Silo ##");
                using var test = new PingScenario(numSilos: 2, startClient: false, grainsOnSecondariesOnly: true);
                test.PingConcurrentHostedClient(blocksPerWorker: 10).GetAwaiter().GetResult();
                test.Shutdown().GetAwaiter().GetResult();
            }
            {
                Console.WriteLine("## Hosted Client ##");
                using var test = new PingScenario(numSilos: 1, startClient: false);
                test.PingConcurrentHostedClient().GetAwaiter().GetResult();
                test.Shutdown().GetAwaiter().GetResult();
            }
        },
        ["ConcurrentPing_OneSilo"] = _ =>
        {
            using var test = new PingScenario(numSilos: 1, startClient: true);
            test.PingConcurrent().GetAwaiter().GetResult();
            test.Shutdown().GetAwaiter().GetResult();
        },
        ["ConcurrentPing_TwoSilos"] = _ =>
        {
            using var test = new PingScenario(numSilos: 2, startClient: true);
            test.PingConcurrent().GetAwaiter().GetResult();
            test.Shutdown().GetAwaiter().GetResult();
        },
        ["ConcurrentPing_TwoSilos_Forever"] = _ =>
        {
            Console.WriteLine("## Client to 2 Silos ##");
            using var test = new PingScenario(numSilos: 2, startClient: true);
            test.PingConcurrentForever().GetAwaiter().GetResult();
        },
        ["ConcurrentPing_HostedClient"] = _ =>
        {
            using var test = new PingScenario(numSilos: 1, startClient: false);
            test.PingConcurrentHostedClient().GetAwaiter().GetResult();
            test.Shutdown().GetAwaiter().GetResult();
        },
        ["ConcurrentPing_HostedClient_Forever"] = _ =>
        {
            using var scenario = new PingScenario(numSilos: 1, startClient: false);
            Console.WriteLine("Press any key to end.");
            Console.WriteLine("## Hosted Client ##");
            using var cts = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) =>
            {
                e.Cancel = true;
                cts.Cancel();
            };
            while (!cts.IsCancellationRequested)
            {
                scenario.PingConcurrentHostedClient().GetAwaiter().GetResult();
            }

            scenario.Shutdown().GetAwaiter().GetResult();
            Console.WriteLine("Interrupted by user");
        },
        ["ConcurrentPing_SiloToSilo"] = _ =>
        {
            using var test = new PingScenario(numSilos: 2, startClient: false, grainsOnSecondariesOnly: true);
            test.PingConcurrentHostedClient(blocksPerWorker: 10).GetAwaiter().GetResult();
            test.Shutdown().GetAwaiter().GetResult();
        },
        ["ConcurrentPing_SiloToSilo_Forever"] = _ =>
        {
            //Console.WriteLine("Press any key to begin.");
            //Console.ReadKey();
            Console.WriteLine("Press any key to end.");
            Console.WriteLine("## Silo to Silo ##");
            while (!Console.KeyAvailable)
            {
                Console.WriteLine("Initializing");
                using var test = new PingScenario(numSilos: 2, startClient: false, grainsOnSecondariesOnly: true);
                Console.WriteLine("Starting");
                test.PingConcurrentHostedClient(blocksPerWorker: 100).GetAwaiter().GetResult();
                Console.WriteLine("Stopping");
                test.Shutdown().GetAwaiter().GetResult();
                Console.WriteLine("Stopped");
            }

            Console.WriteLine("Interrupted by user");
        },
        ["ConcurrentPing_SiloToSilo_Long"] = _ =>
        {
            using var test = new PingScenario(numSilos: 2, startClient: false, grainsOnSecondariesOnly: true);
            test.PingConcurrentHostedClient(blocksPerWorker: 1000).GetAwaiter().GetResult();
            test.Shutdown().GetAwaiter().GetResult();
        },
        ["AdaptivePing"] = _ =>
        {
            // Default: HostedClient mode with hill climbing concurrency tuning
            var scenario = new AdaptivePingScenario(AdaptivePingScenario.ScenarioMode.HostedClient);
            scenario.RunAsync().GetAwaiter().GetResult();
            scenario.ShutdownAsync().GetAwaiter().GetResult();
        },
        ["AdaptivePing_HostedClient"] = _ =>
        {
            var scenario = new AdaptivePingScenario(AdaptivePingScenario.ScenarioMode.HostedClient);
            scenario.RunAsync().GetAwaiter().GetResult();
            scenario.ShutdownAsync().GetAwaiter().GetResult();
        },
        ["AdaptivePing_ClientToOneSilo"] = _ =>
        {
            var scenario = new AdaptivePingScenario(AdaptivePingScenario.ScenarioMode.ExternalClient, numSilos: 1);
            scenario.RunAsync().GetAwaiter().GetResult();
            scenario.ShutdownAsync().GetAwaiter().GetResult();
        },
        ["AdaptivePing_ClientToTwoSilos"] = _ =>
        {
            var scenario = new AdaptivePingScenario(AdaptivePingScenario.ScenarioMode.ExternalClient, numSilos: 2);
            scenario.RunAsync().GetAwaiter().GetResult();
            scenario.ShutdownAsync().GetAwaiter().GetResult();
        },
        ["AdaptivePing_SiloToSilo"] = _ =>
        {
            var scenario = new AdaptivePingScenario(AdaptivePingScenario.ScenarioMode.SiloToSilo, numSilos: 2);
            scenario.RunAsync().GetAwaiter().GetResult();
            scenario.ShutdownAsync().GetAwaiter().GetResult();
        },
        ["AdaptivePing_All"] = _ =>
        {
            AdaptivePingScenario.RunAllScenariosAsync().GetAwaiter().GetResult();
        },
        ["ConcurrentPing_OneSilo_Forever"] = _ =>
        {
            new PingScenario(numSilos: 1, startClient: true).PingConcurrentForever().GetAwaiter().GetResult();
        },
        ["PingOnce"] = _ =>
        {
            new PingScenario().Ping().GetAwaiter().GetResult();
        },
        ["PingForever"] = _ =>
        {
            new PingScenario().PingForever().GetAwaiter().GetResult();
        },
        ["PingPongForever"] = _ =>
        {
            new PingScenario().PingPongForever().GetAwaiter().GetResult();
        },
        ["PingForever_Min_Threads"] = _ =>
        {
            ThreadPool.SetMaxThreads(1, 1);
            new PingScenario().PingForever().GetAwaiter().GetResult();
        },
        ["FanoutForever"] = _ =>
        {
            new FanoutScenario().PingForever().GetAwaiter().GetResult();
        },
        ["StatelessWorker"] = _ =>
        {
            RunScenario("", () => new StatelessWorkerScenario(),
            scenario => scenario.RunAsync().GetAwaiter().GetResult(),
            scenario => scenario.Dispose());
        },
        ["GrainStorage.Memory"] = _ =>
        {
            RunScenario(
            "Running grain storage scenario against memory",
            () =>
            {
                var scenario = new GrainStorageScenario(10, 10000, TimeSpan.FromSeconds( 30 ));
                scenario.MemorySetup();
                return scenario;
            },
            scenario => scenario.RunAsync().GetAwaiter().GetResult(),
            scenario => scenario.Teardown());
        },
        ["GrainStorage.AzureTable"] = _ =>
        {
            RunScenario(
            "Running grain storage scenario against Azure Table",
            () =>
            {
                var scenario = new GrainStorageScenario(100, 10000, TimeSpan.FromSeconds( 30 ));
                scenario.AzureTableSetup();
                return scenario;
            },
            scenario => scenario.RunAsync().GetAwaiter().GetResult(),
            scenario => scenario.Teardown());
        },
        ["GrainStorage.AzureBlob"] = _ =>
        {
            RunScenario(
            "Running grain storage scenario against Azure Blob",
            () =>
            {
                var scenario = new GrainStorageScenario(10, 10000, TimeSpan.FromSeconds( 30 ));
                scenario.AzureBlobSetup();
                return scenario;
            },
            scenario => scenario.RunAsync().GetAwaiter().GetResult(),
            scenario => scenario.Teardown());
        },
        ["GrainStorage.AdoNet"] = _ =>
        {
            RunScenario(
            "Running grain storage scenario against AdoNet",
            () =>
            {
                var scenario = new GrainStorageScenario(100, 10000, TimeSpan.FromSeconds( 30 ));
                scenario.AdoNetSetup();
                return scenario;
            },
            scenario => scenario.RunAsync().GetAwaiter().GetResult(),
            scenario => scenario.Teardown());
        },
    };

    // requires scenario name or 'All' word as first parameter
    public static void Main(string[] args)
    {
        var slicedArgs = args.Skip(1).ToArray();
        if (args.Length > 0 && args[0].Equals("all", StringComparison.InvariantCultureIgnoreCase))
        {
            Console.WriteLine("Running full scenarios suite");
            _scenarios.Select(pair => pair.Value).ToList().ForEach(action => action(slicedArgs));
            return;
        }

        if (args.Length == 0 || !_scenarios.ContainsKey(args[0]))
        {
            Console.WriteLine("Please, select scenario, list of available:");
            _scenarios
                .Select(pair => pair.Key)
                .ToList()
                .ForEach(Console.WriteLine);
            Console.WriteLine("All");
            return;
        }

        _scenarios[args[0]](slicedArgs);
    }

    private static void RunScenario<T>(string name, Func<T> init, Action<T> scenarioAction, Action<T> tearDown)
    {
        Console.WriteLine(name);
        var scenario = init();
        var stopWatch = Stopwatch.StartNew();
        scenarioAction(scenario);
        Console.WriteLine($"Elapsed milliseconds: {stopWatch.ElapsedMilliseconds}");
        Console.WriteLine("Press any key to continue ...");
        tearDown(scenario);
        Console.ReadLine();
    }
}
