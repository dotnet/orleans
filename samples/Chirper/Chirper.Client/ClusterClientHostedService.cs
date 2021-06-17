using System;
using System.Threading;
using System.Threading.Tasks;
using Chirper.Grains;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Orleans;
using Spectre.Console;

namespace Chirper.Client
{
    public class ClusterClientHostedService : IHostedService
    {
        public ClusterClientHostedService()
        {
            Client = new ClientBuilder()
                .UseLocalhostClustering()
                .ConfigureApplicationParts(parts => parts.AddApplicationPart(typeof(IChirperAccount).Assembly))
                .Build();
        }

        public IClusterClient Client { get; }

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            await AnsiConsole.Status().StartAsync("Connecting to server", async ctx =>
            {
                ctx.Spinner(Spinner.Known.Dots);
                ctx.Status = "Connecting...";

                await Client.Connect(async error =>
                {
                    AnsiConsole.MarkupLine("[bold red]Error:[/] error connecting to server!");
                    AnsiConsole.WriteException(error);
                    ctx.Status = "Waiting to retry...";
                    await Task.Delay(TimeSpan.FromSeconds(2));
                    ctx.Status = "Retrying connection...";
                    return true;
                });

                ctx.Status = "Connected!";
            });
        }

        public async Task StopAsync(CancellationToken cancellationToken)
        {
            await AnsiConsole.Status().StartAsync("Disconnecting...", async ctx =>
            {
                ctx.Spinner(Spinner.Known.Dots);
                var cancellation = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                using var _ = cancellationToken.Register(() => cancellation.TrySetCanceled(cancellationToken));
                await Task.WhenAny(Client.Close(), cancellation.Task);
            });
        }
    }
}
