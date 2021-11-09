using System;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Orleans.Runtime;

namespace Orleans.TestingHost
{
    /// <summary>
    /// The entry point for standalone silo processes. See <see cref="StandaloneSiloHandle" />.
    /// </summary>
    public static class StandaloneSiloHost
    {
        public const string SiloAddressLog = "#### SILO ";
        public const string GatewayAddressLog = "#### GATEWAY ";
        public const string StartedLog = "#### STARTED";
        public const string ShutdownCommand = "#### SHUTDOWN";

        public static async Task Main(string[] args)
        {
            if (args.Length < 2)
            {
                Console.Error.WriteLine("Expected JSON-serialized configuration to be provided as an argument");
            }

            var monitorProcessId = int.Parse(args[0], NumberStyles.Integer, CultureInfo.InvariantCulture);
            var serializedConfiguration = args[1];
            var configuration = TestClusterHostFactory.DeserializeConfiguration(serializedConfiguration);
            var name = configuration["SiloName"];
            using var host = TestClusterHostFactory.CreateSiloHost(name, configuration);
            try
            {
                var cts = new CancellationTokenSource();
                Console.CancelKeyPress += (sender, eventArgs) => cts.Cancel();

                ListenForShutdownCommand(cts);
                MonitorParentProcess(monitorProcessId);

                await host.StartAsync(cts.Token);

                // This is a special marker line.
                var localSiloDetails = (ILocalSiloDetails)host.Services.GetService(typeof(ILocalSiloDetails));
                Console.WriteLine($"{SiloAddressLog}{localSiloDetails.SiloAddress.ToParsableString()}");
                Console.WriteLine($"{GatewayAddressLog}{localSiloDetails.GatewayAddress.ToParsableString()}");
                Console.WriteLine(StartedLog);

                await cts.Token.WhenCancelled();

                await host.StopAsync(CancellationToken.None);
            }
            finally
            {
                if (host is IAsyncDisposable asyncDisposable)
                {
                    await asyncDisposable.DisposeAsync();
                }
                else
                {
                    host.Dispose();
                }
            }
        }

        private static void MonitorParentProcess(int monitorProcessId)
        {
            if (monitorProcessId > 0)
            {
                Console.WriteLine($"Monitoring parent process {monitorProcessId}");
                Process.GetProcessById(monitorProcessId).Exited += (o, a) =>
                {
                    Console.Error.WriteLine($"Parent process {monitorProcessId} has exited");
                    Environment.Exit(0);
                };

                _ = Task.Factory.StartNew(async _ =>
                {
                    try
                    {
                        while (true)
                        {
                            await Task.Delay(5000);
                            if (!Process.GetProcesses().Any(p => p.Id == monitorProcessId))
                            {
                                Console.Error.WriteLine($"Parent process {monitorProcessId} has exited");
                                Environment.Exit(0);
                            }
                        }
                    }
                    catch
                    {
                        // Ignore all errors.
                    }
                },
                null,
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default);
            }
        }

        private static void ListenForShutdownCommand(CancellationTokenSource cts)
        {
            // Start a thread to monitor for the shutdown command from standard input.
            _ = Task.Factory.StartNew(_ =>
            {
                try
                {
                    while (true)
                    {
                        var text = Console.ReadLine();
                        if (string.Equals(text, ShutdownCommand, StringComparison.Ordinal))
                        {
                            Console.WriteLine("Shutdown requested");
                            cts.Cancel();
                            return;
                        }
                    }
                }
                catch
                {
                    // Ignore all errors.
                }
            },
            null,
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);
        }

        private static Task WhenCancelled(this CancellationToken token)
        {
            if (token.IsCancellationRequested)
            {
                return Task.CompletedTask;
            }

            var waitForCancellation = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
            token.Register(obj =>
            {
                var tcs = (TaskCompletionSource<object>)obj;
                tcs.TrySetResult(null);
            }, waitForCancellation);

            return waitForCancellation.Task;
        }
    }
}
