using System.Globalization;
using System.Net;
using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Orleans;
using Orleans.Configuration;
using Orleans.Hosting;
using Orleans.Runtime;
using ReleasedPeer;
using ReleasedPeer.Contracts;

var siloPort = int.Parse(args[0], CultureInfo.InvariantCulture);
var gatewayPort = int.Parse(args[1], CultureInfo.InvariantCulture);
var primaryPort = int.Parse(args[2], CultureInfo.InvariantCulture);
var cluster = args[3];
var clientOnly = args.Length > 4 && args[4] == "client";
var control = new ProbeControl();
var probe = new RuntimeProbe();
var outputLock = new object();
void Emit(object value)
{
    lock (outputLock)
    {
        Console.WriteLine(JsonSerializer.Serialize(value));
    }
}

var hostBuilder = new HostBuilder()
    .ConfigureLogging(logging => logging.ClearProviders())
    .ConfigureServices(services => services.AddSingleton(control));
if (clientOnly)
{
    hostBuilder.UseOrleansClient(clientBuilder => clientBuilder
        .UseLocalhostClustering(gatewayPort, clusterId: cluster, serviceId: cluster)
        .Configure<ClientMessagingOptions>(options => options.ResponseTimeout = TimeSpan.FromSeconds(30)));
}
else
{
    hostBuilder.UseOrleans(silo => silo
        .UseLocalhostClustering(siloPort, gatewayPort, new IPEndPoint(IPAddress.Loopback, primaryPort), clusterId: cluster, serviceId: cluster)
        .Configure<SiloMessagingOptions>(options => options.ResponseTimeout = TimeSpan.FromSeconds(30))
        .Configure<ClusterMembershipOptions>(options => options.TableRefreshTimeout = TimeSpan.FromMilliseconds(200)));
}
using var host = hostBuilder.Build();
await host.StartAsync();
if (!clientOnly)
{
    probe.Install(host.Services);
}
probe.Observed = message => Emit(new { Kind = "message", Message = message });
var runtime = Assembly.Load("Orleans.Runtime");
var core = Assembly.Load("Orleans.Core");
Emit(new
{
    Kind = "ready",
    Version = runtime.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion,
    Mvid = runtime.ManifestModule.ModuleVersionId,
    Path = runtime.Location,
    CoreVersion = core.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion,
    CoreMvid = core.ManifestModule.ModuleVersionId,
    CorePath = core.Location,
    Silo = clientOnly ? "" : host.Services.GetRequiredService<ILocalSiloDetails>().SiloAddress.ToParsableString()
});

var client = host.Services.GetRequiredService<IClusterClient>();
var work = new List<Task>();
var stopped = false;
while (await Console.In.ReadLineAsync() is { } line)
{
    var command = JsonSerializer.Deserialize<PeerCommand>(line)!;
    switch (command.Kind)
    {
        case "prime":
            await Reply(command, async () => await client.GetGrain<IRetirementProbeGrain>(command.Grain).Locate());
            break;
        case "invoke":
            work.Add(Reply(command, async () =>
            {
                RequestContext.Set(ProbeControl.ContextKey, command.Context);
                if (!string.IsNullOrEmpty(command.Target))
                {
                    RequestContext.Set("PlacementHintKey", SiloAddress.FromParsableString(command.Target));
                }
                try
                {
                    return await client.GetGrain<IRetirementProbeGrain>(command.Grain).Execute(command.Operation, command.Argument);
                }
                finally
                {
                    RequestContext.Clear();
                }
            }));
            break;
        case "block":
            work.Add(Reply(command, async () =>
            {
                var running = client.GetGrain<IRetirementProbeGrain>(command.Grain).Block();
                await control.RunningEntered.Task.WaitAsync(TimeSpan.FromSeconds(30));
                Emit(new { Kind = "running" });
                await running;
                return true;
            }));
            break;
        case "release-running":
            control.ReleaseRunning.TrySetResult();
            Emit(new { Kind = "reply", command.Id, Value = true });
            break;
        case "hold-deactivation":
            control.HoldDeactivation = true;
            Emit(new { Kind = "reply", command.Id, Value = true });
            break;
        case "stop":
            work.Add(Reply(command, async () =>
            {
                // Freeze activation admission synchronously before the command
                // acknowledges retirement; do not race release of a running
                // method against the asynchronous silo lifecycle walk.
                control.GrainContext?.Deactivate(new(DeactivationReasonCode.ShuttingDown, "Released-peer graceful retirement barrier."));
                var stop = host.StopAsync();
                await control.DeactivationEntered.Task.WaitAsync(TimeSpan.FromSeconds(30));
                Emit(new { Kind = "deactivating" });
                await stop;
                stopped = true;
                return true;
            }));
            break;
        case "release-deactivation":
            control.ReleaseDeactivation.TrySetResult();
            Emit(new { Kind = "reply", command.Id, Value = true });
            break;
        case "entries":
            Emit(new { Kind = "reply", command.Id, Value = control.Entries.ToArray() });
            break;
        case "exit":
            control.ReleaseDeactivation.TrySetResult();
            control.ReleaseActivation.TrySetResult();
            control.ReleaseRunning.TrySetResult();
            if (!stopped)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                await host.StopAsync(timeout.Token);
            }
            await Task.WhenAll(work);
            return;
        default:
            throw new InvalidOperationException($"Unknown command: {command.Kind}");
    }
}

async Task Reply<T>(PeerCommand command, Func<Task<T>> action)
{
    try
    {
        Emit(new { Kind = "reply", command.Id, Value = await action() });
    }
    catch (Exception exception)
    {
        Emit(new { Kind = "reply", command.Id, Error = exception.ToString() });
    }
}

public sealed record PeerCommand(
    string Kind, int Id, Guid Grain = default, Guid Operation = default,
    int Argument = 0, string Context = "", string Target = "");
