using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Fabric;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using GrainInterfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orleans;
using Orleans.Configuration;
using Orleans.Hosting;

namespace StatelessCalculatorClient
{
    class Program
    {
        static void Main(string[] args)
        {
            Run(args).Wait();
        }

        private static async Task Run(string[] args)
        {
            var serviceName = new Uri("fabric:/StatelessCalculatorApp/StatelessCalculatorService");

            var builder = new ClientBuilder();

            builder.Configure<ClusterOptions>(options =>
            {
                options.ServiceId = serviceName.ToString();
                options.ClusterId = "development";
            });

            // TODO: Pick a clustering provider and configure it here.
            builder.UseAzureStorageClustering(options => options.ConnectionString = "UseDevelopmentStorage=true");

            // Add the application assemblies.
            builder.ConfigureApplicationParts(parts => parts.AddApplicationPart(typeof(ICalculatorGrain).Assembly));

            // Optional: configure logging.
            builder.ConfigureLogging(logging => logging.AddDebug());

            // Create the client and connect to the cluster.
            var client = builder.Build();
            await client.Connect();

            double result;
            if (args.Length < 1)
            {

                Console.WriteLine(
                    $"Usage: {Assembly.GetExecutingAssembly()} <operation> [operand]\n\tOperations: get, set, add, subtract, multiple, divide");
                return;
            }

            var value = args.Length > 1 ? double.Parse(args[1]) : 0;

            var calculator = client.GetGrain<ICalculatorGrain>(Guid.Empty);
            var observer = new CalculatorObserver();
            var observerReference = await client.CreateObjectReference<ICalculatorObserver>(observer);
            var cancellationTokenSource = new CancellationTokenSource();
            var subscriptionTask = StaySubscribed(calculator, observerReference, cancellationTokenSource.Token);

            switch (args[0].ToLower())
            {
                case "stress":
                    result = await StressTest(client);
                    break;
                case "add":
                case "+":
                    result = await calculator.Add(value);
                    break;
                case "subtract":
                case "-":
                    result = await calculator.Subtract(value);
                    break;
                case "multiply":
                case "*":
                    result = await calculator.Multiply(value);
                    break;
                case "divide":
                case "/":
                    result = await calculator.Divide(value);
                    break;
                case "set":
                    result = await calculator.Set(value);
                    break;
                case "get":
                default:
                    result = await calculator.Get();
                    break;
            }

            Console.WriteLine(result);
            Console.WriteLine("Listening for updates to calculations. Press any key to exit.");
            Console.ReadKey();
            cancellationTokenSource.Cancel();
            await subscriptionTask;
        }

        private static async Task StaySubscribed(ICalculatorGrain grain, ICalculatorObserver observer, CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), token);
                    await grain.Subscribe(observer);
                }
                catch (Exception exception)
                {
                    Console.WriteLine($"Exception while trying to subscribe for updates: {exception}");
                }
            }
        }

        private class CalculatorObserver : ICalculatorObserver {
            public void CalculationUpdated(double value)
            {
                Console.WriteLine($"Calculation updated: {value}");
            }
        }

        private static async Task<double> StressTest(IGrainFactory grainFactory)
        {
            Console.WriteLine("[Enter] = exit");
            Console.WriteLine("[Space] = status");
            Console.WriteLine("[b] = rebalance grains");

            double total = 0;
            var i = 0;
            var stopwatch = Stopwatch.StartNew();
            var success = 0;
            var fail = 0;
            var run = true;
            List<ICalculatorGrain> grains = null;
            while (run)
            {
                if (grains == null) grains = GetGrains(grainFactory);

                i++;
                try
                {
                    total += await grains[i % grains.Count].Add(total);
                    success++;
                }
                catch
                {
                    fail++;
                }

                if (Console.KeyAvailable)
                {
                    var key = Console.ReadKey(true);
                    if (key.Key == ConsoleKey.Enter || key.Key == ConsoleKey.Escape ||
                        key.Key == ConsoleKey.C && (key.Modifiers & ConsoleModifiers.Control) != 0) run = false;
                    if (key.Key == ConsoleKey.B) grains = null;
                    Console.WriteLine($"Successes: {success}, Failures: {fail}, Elapsed: {stopwatch.Elapsed}");
                }
            }

            return total;
        }

        private static List<ICalculatorGrain> GetGrains(IGrainFactory grainFactory)
        {
            var grains = new List<ICalculatorGrain>(20);
            for (var i = 0; i < grains.Capacity; i++) grains.Add(grainFactory.GetGrain<ICalculatorGrain>(Guid.NewGuid()));
            return grains;
        }
    }
}