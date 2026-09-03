using System;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Orleans.Runtime;

namespace Orleans.TestingHost
{
    /// <summary>
    /// The entry point for standalone silo processes. See <see cref="StandaloneSiloHandle" />.
    /// </summary>
    public static class StandaloneSiloHost
    {
        /// <summary>
        /// The standard output prefix which precedes the silo address.
        /// </summary>
        public const string SiloAddressLog = "#### SILO ";

        /// <summary>
        /// The standard output prefix which precedes the gateway address.
        /// </summary>
        public const string GatewayAddressLog = "#### GATEWAY ";

        /// <summary>
        /// The standard output message which indicates that the silo has started.
        /// </summary>
        public const string StartedLog = "#### STARTED";

        /// <summary>
        /// The standard input command which requests that the silo shut down.
        /// </summary>
        public const string ShutdownCommand = "#### SHUTDOWN";

        /// <summary>
        /// Runs a standalone silo process.
        /// </summary>
        /// <param name="args">
        /// The command-line arguments. The first argument is the identifier of the process to monitor, and the second is the
        /// serialized silo configuration.
        /// </param>
        /// <returns>A task which completes when the silo has shut down.</returns>
        public static async Task Main(string[] args)
        {
            if (args.Length < 2)
            {
                Console.Error.WriteLine("Expected JSON-serialized configuration to be provided as an argument");
            }

            var monitorProcessId = int.Parse(args[0], NumberStyles.Integer, CultureInfo.InvariantCulture);
            var serializedConfiguration = args[1];
            var configuration = TestClusterHostFactory.DeserializeConfiguration(serializedConfiguration);
            if (string.Equals(configuration["AttachDebugger"], "true", StringComparison.OrdinalIgnoreCase))
            {
                Debugger.Launch();
            }

            var name = configuration["SiloName"];
            using var host = TestClusterHostFactory.CreateSiloHost(name!, configuration);
            try
            {
                var cts = new CancellationTokenSource();
                using var stoppedRegistration = host.Services
                    .GetRequiredService<IHostApplicationLifetime>()
                    .ApplicationStopped.Register(cts.Cancel);
                Console.CancelKeyPress += (sender, eventArgs) => cts.Cancel();

                ListenForShutdownCommand(cts);
                MonitorParentProcess(monitorProcessId);

                await host.StartAsync(cts.Token);

                // This is a special marker line.
                var localSiloDetails = (ILocalSiloDetails)host.Services.GetService(typeof(ILocalSiloDetails))!;
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
                            if (!Array.Exists(Process.GetProcesses(), p => p.Id == monitorProcessId))
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

            var waitForCancellation = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
            token.Register(obj =>
            {
                var tcs = (TaskCompletionSource<object?>)obj!;
                tcs.TrySetResult(null);
            }, waitForCancellation);

            return waitForCancellation.Task;
        }
    }
}
