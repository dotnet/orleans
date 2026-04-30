#nullable enable
using System.Net;
using BenchmarkGrainInterfaces.Ping;
using BenchmarkGrains.Ping;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Orleans.Configuration;

namespace Benchmarks.Scenarios.Ping;

/// <summary>
/// Scenario for grain-to-grain communication latency and throughput using simple ping operations.
/// </summary>
public class PingScenario : IAsyncDisposable, IDisposable
{
    private readonly ConsoleCancelEventHandler? _onCancelEvent;
    private readonly List<IHost> hosts = new List<IHost>();
    private readonly IPingGrain grain;
    private readonly IClusterClient client;
    private readonly IHost? clientHost;

    public PingScenario() : this(1, false) { }

    public PingScenario(int numSilos, bool startClient, bool grainsOnSecondariesOnly = false)
    {
        for (var i = 0; i < numSilos; ++i)
        {
            var primary = i == 0 ? null : new IPEndPoint(IPAddress.Loopback, 11111);
            var hostBuilder = new HostBuilder().UseOrleans((ctx, siloBuilder) =>
            {
                siloBuilder.UseLocalhostClustering(
                    siloPort: 11111 + i,
                    gatewayPort: 30000 + i,
                    primarySiloEndpoint: primary);

                if (i == 0 && grainsOnSecondariesOnly)
                {
                    siloBuilder.Configure<GrainTypeOptions>(options => options.Classes.Remove(typeof(PingGrain)));
                }
            });

            var host = hostBuilder.Build();

            host.StartAsync().GetAwaiter().GetResult();
            this.hosts.Add(host);
        }

        if (grainsOnSecondariesOnly) Thread.Sleep(4000);

        if (startClient)
        {
            var hostBuilder = new HostBuilder().UseOrleansClient((ctx, clientBuilder) =>
            {
                if (numSilos == 1)
                {
                    clientBuilder.UseLocalhostClustering();
                }
                else
                {
                    var gateways = Enumerable.Range(30000, numSilos).Select(i => new IPEndPoint(IPAddress.Loopback, i)).ToArray();
                    clientBuilder.UseStaticClustering(gateways);
                }
            });

            this.clientHost = hostBuilder.Build();
            this.clientHost.StartAsync().GetAwaiter().GetResult();

            this.client = this.clientHost.Services.GetRequiredService<IClusterClient>();
            var grainFactory = this.client;

            this.grain = grainFactory.GetGrain<IPingGrain>(Guid.NewGuid().GetHashCode());
            this.grain.Run().AsTask().GetAwaiter().GetResult();
        }
        else
        {
            var host = hosts[0];
            this.client = host.Services.GetRequiredService<IClusterClient>();
            this.grain = client.GetGrain<IPingGrain>(Guid.NewGuid().GetHashCode());
            this.grain.Run().AsTask().GetAwaiter().GetResult();
        }

        _onCancelEvent = CancelPressed;
        Console.CancelKeyPress += _onCancelEvent;
    }

    private void CancelPressed(object? sender, ConsoleCancelEventArgs e)
    {
        Environment.Exit(0);
    }

    public ValueTask Ping() => grain.Run();

    public async Task PingForever()
    {
        while (true)
        {
            await grain.Run();
        }
    }

    public Task PingConcurrentForever() => this.Run(
        runs: int.MaxValue,
        grainFactory: this.client,
        blocksPerWorker: 10);

    public Task PingConcurrent() => this.Run(
        runs: 10,
        grainFactory: this.client,
        blocksPerWorker: 10);

    public Task PingConcurrentHostedClient(int blocksPerWorker = 30) => this.Run(
        runs: 10,
        grainFactory: this.hosts[0].Services.GetRequiredService<IGrainFactory>(),
        blocksPerWorker: blocksPerWorker);

    private async Task Run(int runs, IGrainFactory grainFactory, int blocksPerWorker)
    {
        var loadGenerator = new ConcurrentLoadGenerator<IPingGrain>(
            maxConcurrency: 100,
            blocksPerWorker: blocksPerWorker,
            requestsPerBlock: 500,
            issueRequest: g => g.Run(),
            getStateForWorker: workerId => grainFactory.GetGrain<IPingGrain>(workerId));
        await loadGenerator.Warmup();
        while (runs-- > 0) await loadGenerator.Run();
    }

    public async Task PingPongForever()
    {
        var other = this.client.GetGrain<IPingGrain>(Guid.NewGuid().GetHashCode());
        while (true)
        {
            await grain.PingPongInterleave(other, 100);
        }
    }

    public async Task Shutdown()
    {
        if (clientHost is { } client)
        {
            await client.StopAsync();
            await DisposeAsync(client);
        }

        this.hosts.Reverse();
        foreach (var host in this.hosts)
        {
            await host.StopAsync();
            await DisposeAsync(host);
        }
    }

    public void Dispose()
    {
        (this.client as IDisposable)?.Dispose();
        this.hosts.ForEach(h => h.Dispose());

        Console.CancelKeyPress -= _onCancelEvent;
    }

    public async ValueTask DisposeAsync()
    {
        await DisposeAsync(this.client);
        var allHosts = ((IEnumerable<IHost>)this.hosts).Reverse().ToList();
        foreach (var host in hosts)
        {
            await DisposeAsync(host);
        }

        Console.CancelKeyPress -= _onCancelEvent;
    }

    private static async ValueTask DisposeAsync(object? obj)
    {
        if (obj is IAsyncDisposable iad)
        {
            await iad.DisposeAsync();
        }
        else if (obj is IDisposable id)
        {
            id.Dispose();
        }
    }
}
