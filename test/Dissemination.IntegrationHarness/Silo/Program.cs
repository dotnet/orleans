using System.Diagnostics;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Connections;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Orleans.Configuration;
using Orleans.Runtime;
using Orleans.Runtime.MembershipService;
using Orleans.Runtime.Messaging;
using Orleans.Runtime.Scheduler;
using Orleans.Statistics;
using Orleans.TestingHost;

namespace Orleans.Dissemination.IntegrationHarness;

internal static class Program
{
    private static readonly MethodInfo PublishMethod = typeof(DeploymentLoadPublisher).GetMethod(
        "PublishStatistics", BindingFlags.Instance | BindingFlags.NonPublic)!;

    public static async Task<int> Main(string[] args)
    {
        if (args is not ["--silo", var configurationPath])
        {
            Console.Error.WriteLine("Expected --silo <configuration.json>.");
            return 2;
        }

        var configuration = JsonSerializer.Deserialize<NodeConfiguration>(await File.ReadAllTextAsync(configurationPath))!;
#if !NEW_RUNTIME
        if (configuration.Enabled)
        {
            throw new InvalidOperationException("The pinned old runtime cannot be opted into dissemination.");
        }
#endif
        using var lifetime = new CancellationTokenSource(TimeSpan.FromMinutes(45));
        using var observation = new Observation(configuration.FastRecovery);
        var membership = new FileMembershipTable(configuration);
        using var host = TestClusterHostFactory.CreateSiloHost(
            configuration.Name,
            new ConfigurationBuilder().Build(),
            builder => builder.ConfigureServices(services =>
            {
                services.AddSingleton(configuration);
                services.AddSingleton(observation);
                services.AddSingleton(membership);
                services.AddSingleton<IMembershipTable>(membership);
                services.AddSingleton<MembershipGossiper>();
                services.AddSingleton<GatedMembershipGossiper>();
                services.AddSingleton<IMembershipGossiper>(provider => provider.GetRequiredService<GatedMembershipGossiper>());
                services.AddSingleton<ControlTarget>();
                services.AddSingleton<ControlledCall>();
                services.Configure<ClusterOptions>(options =>
                {
                    options.ClusterId = configuration.ClusterId;
                    options.ServiceId = "dissemination-evidence";
                });
                services.Configure<SiloOptions>(options => options.SiloName = configuration.Name);
                services.Configure<EndpointOptions>(options =>
                {
                    options.AdvertisedIPAddress = IPAddress.Loopback;
                    options.SiloPort = configuration.SiloPort;
                    options.GatewayPort = 0;
                });
                services.Configure<ClusterMembershipOptions>(options =>
                {
                    // Partitions test dissemination repair, not the separately tested failure detector.
                    options.LivenessEnabled = false;
                    options.TableRefreshTimeout = TimeSpan.FromHours(1);
                    options.IAmAliveTablePublishTimeout = TimeSpan.FromHours(1);
                    options.MaxJoinAttemptTime = TimeSpan.FromSeconds(60);
                });
                services.Configure<DeploymentLoadPublisherOptions>(options =>
                    options.DeploymentLoadPublisherRefreshTime = TimeSpan.FromHours(1));
                services.Configure<SiloMessagingOptions>(options => options.ResponseTimeout = TimeSpan.FromSeconds(5));
                services.Configure<HostOptions>(options => options.ShutdownTimeout = TimeSpan.FromSeconds(15));
                services.Configure<SiloConnectionOptions>(options =>
                {
                    options.ConfigureSiloInboundConnection(connection => connection.Use(next => context => observation.Connection(context, next)));
                    options.ConfigureSiloOutboundConnection(connection => connection.Use(next => context => observation.Connection(context, next)));
                });
                services.AddLogging(logging =>
                {
                    logging.SetMinimumLevel(LogLevel.Warning).AddConsole(options =>
                        options.LogToStandardErrorThreshold = LogLevel.Trace);
                    if (configuration.ClusterId.StartsWith("bounded-shutdown-", StringComparison.Ordinal))
                    {
                        logging.AddFilter("Orleans.Runtime.GrainCallCancellationManager", LogLevel.Debug);
                    }
                });
#if NEW_RUNTIME
                NewRuntime.Configure(services, configuration);
#endif
            }));

        _ = host.Services.GetRequiredService<ControlTarget>();
        var monitor = MonitorParent(configuration.ParentProcessId, lifetime);
        try
        {
            using var startup = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            startup.CancelAfter(TimeSpan.FromSeconds(90));
            await host.StartAsync(startup.Token);
            var publisher = host.Services.GetRequiredService<DeploymentLoadPublisher>();
            // Commands own publication cadence; lifecycle shutdown retains its disposable timer reference.
            await publisher.RunOrQueueTask(() =>
            {
                var field = typeof(DeploymentLoadPublisher).GetField("_publishTimer", BindingFlags.Instance | BindingFlags.NonPublic)!;
                ((IDisposable)field.GetValue(publisher)!).Dispose();
                return Task.CompletedTask;
            });
            await Write(new Response(0, Snapshot(host.Services), null));

            while (await Console.In.ReadLineAsync(lifetime.Token) is { } line)
            {
                var command = JsonSerializer.Deserialize<Command>(line)!;
                using var operation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                operation.CancelAfter(TimeSpan.FromSeconds(60));
                try
                {
                    var result = await Execute(host.Services, command, operation.Token);
                    await Write(new Response(command.Id, result, null));
                    if (command.Operation == "stop")
                    {
                        break;
                    }
                }
                catch (Exception exception)
                {
                    await Write(new Response(command.Id, null, exception.ToString()));
                }
            }

            using var shutdown = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            await host.StopAsync(shutdown.Token).WaitAsync(shutdown.Token);
            // Drain the last one-second OS socket-counter bucket before recording shutdown cost.
            await Task.Delay(TimeSpan.FromMilliseconds(1100));
            await Write(new Response(-1, Snapshot(host.Services, detailed: true), null));
            return 0;
        }
        finally
        {
            await lifetime.CancelAsync();
            await monitor;
        }
    }

    private static async Task<NodeSnapshot> Execute(IServiceProvider services, Command command, CancellationToken cancellationToken)
    {
        var membership = services.GetRequiredService<FileMembershipTable>();
        var manager = services.GetRequiredService<IMembershipManager>();
        var publisher = services.GetRequiredService<DeploymentLoadPublisher>();
        switch (command.Operation)
        {
            case "snapshot":
            case "stop":
                break;
            case "measure":
                return Snapshot(services, detailed: true);
            case "verify-load-comparison":
            {
                var original = new EnvironmentStatistics(10, 20, 100, 100, 900, 900, 1000);
                var changed = new EnvironmentStatistics(10, 35, 100, 100, 900, 900, 1000);
                return Snapshot(services) with
                {
                    ComparisonProbe = new(
                        StateComparison.Serialize(new { EnvironmentStatistics = original }),
                        StateComparison.Serialize(new { EnvironmentStatistics = changed }),
                        typeof(EnvironmentStatistics).GetFields(BindingFlags.Instance | BindingFlags.Public)
                            .Select(field => field.Name).ToArray()),
                };
            }
            case "refresh":
                await manager.Refresh(null, cancellationToken);
                break;
            case "publish":
                for (var index = 0; index < command.Count; index++)
                {
                    await publisher.RunOrQueueTask(() => (Task)PublishMethod.Invoke(publisher, [cancellationToken])!)
                        .WaitAsync(cancellationToken);
                }

                break;
            case "partition":
            {
                var observation = services.GetRequiredService<Observation>();
                if (command.Value)
                {
                    observation.Partition();
                }
                else
                {
                    await DrainConnections(services, peer: null, cancellationToken);
                    observation.ResumeConnections();
                }

                break;
            }
            case "drain-connections":
                await DrainConnections(services, command.Peer, cancellationToken);
                break;
            case "resume-connections":
                services.GetRequiredService<Observation>().ResumeConnections();
                break;
            case "isolate-membership":
                services.GetRequiredService<GatedMembershipGossiper>().Suppressed = command.Value;
                await membership.FreezeReads(command.Value, cancellationToken);
                break;
            case "forget-load":
                await publisher.RunOrQueueTask(() =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    publisher.PeriodicStatistics.TryRemove(SiloAddress.FromParsableString(command.Peer!), out _);
                    return Task.CompletedTask;
                }).WaitAsync(cancellationToken);
                break;
            case "retained-memory":
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                break;
            case "echo":
            {
                var peer = services.GetRequiredService<IInternalGrainFactory>().GetSystemTarget<IControlTarget>(
                    ControlTarget.TargetType, SiloAddress.FromParsableString(command.Peer!));
                var processId = await peer.Echo(cancellationToken).WaitAsync(cancellationToken);
                return Snapshot(services) with { RemoteProcessId = processId };
            }
            case "probe-echo":
            {
                var peer = services.GetRequiredService<IInternalGrainFactory>().GetSystemTarget<IControlTarget>(
                    ControlTarget.TargetType, SiloAddress.FromParsableString(command.Peer!));
                using var probeCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                try
                {
                    var request = peer.Echo(probeCancellation.Token);
                    request.Ignore();
                    var processId = await request.WaitAsync(TimeSpan.FromSeconds(2), cancellationToken);
                    return Snapshot(services) with { RemoteProcessId = processId };
                }
                catch (Exception exception) when (exception is ConnectionFailedException
                    or SiloUnavailableException or OrleansMessageRejectionException or TimeoutException)
                {
                    return Snapshot(services) with
                    {
                        ProbeError = $"{exception.GetType().FullName}: {exception.Message}",
                        ProbeTimedOut = exception is TimeoutException,
                    };
                }
                finally
                {
                    await probeCancellation.CancelAsync();
                }
            }
            case "start-cancel-rpc":
            {
                var peer = services.GetRequiredService<IInternalGrainFactory>().GetSystemTarget<IControlTarget>(
                    ControlTarget.TargetType, SiloAddress.FromParsableString(command.Peer!));
                var call = services.GetRequiredService<ControlledCall>();
                // Match dissemination's silo-system-target calling context, not a hosted-client call.
                await services.GetRequiredService<ControlTarget>().RunOrQueueTask(() =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    call.Start(command.Peer!, peer.Hold);
                    return Task.CompletedTask;
                }).WaitAsync(cancellationToken);

                break;
            }
            case "cancel-started-rpc":
                await services.GetRequiredService<ControlledCall>().Cancel(cancellationToken);
                break;
            default:
#if NEW_RUNTIME
                return await NewRuntime.Execute(services, command, cancellationToken);
#else
                throw new NotSupportedException($"The pinned old runtime does not support '{command.Operation}'.");
#endif
        }

        return Snapshot(services);
    }

    private static Task DrainConnections(IServiceProvider services, string? peer, CancellationToken cancellationToken)
    {
        var address = peer is null ? null : SiloAddress.FromParsableString(peer);
        return services.GetRequiredService<Observation>().DrainConnections(
            context =>
            {
                var connection = (SiloConnection)context.Features.Get<Connection>()!;
                // Inbound handshakes have no remote silo identity until their preamble completes.
                return address is null || connection.RemoteSiloAddress is null || connection.RemoteSiloAddress.Equals(address);
            },
            context => context.Features.Get<Connection>()!.CloseAsync(new ConnectionAbortedException("Harness partition")),
            cancellationToken);
    }

    internal static NodeSnapshot Snapshot(IServiceProvider services, bool? detailed = null)
    {
        var includeDetails = detailed ?? services.GetRequiredService<NodeConfiguration>().FastRecovery;
        var manager = services.GetRequiredService<IMembershipManager>();
        var publisher = services.GetRequiredService<DeploymentLoadPublisher>();
        var observation = services.GetRequiredService<Observation>();
        var membership = manager.CurrentSnapshot;
        using var process = Process.GetCurrentProcess();
        var socket = observation.SocketSnapshot;
        var result = new NodeSnapshot
        {
            CapturedAtUtc = DateTimeOffset.UtcNow,
            Identity = Identity(),
            Address = services.GetRequiredService<ILocalSiloDetails>().SiloAddress.ToParsableString(),
            MembershipVersion = membership.Version.Value,
            Membership = new(membership.Entries.ToDictionary(
                pair => pair.Key.ToParsableString(),
                pair => JsonSerializer.Serialize(FileMembershipTable.EntryData.From(pair.Value))), StringComparer.Ordinal),
            ActiveMembers = membership.Entries.Values.Where(entry => entry.Status == SiloStatus.Active)
                .Select(entry => entry.SiloAddress.ToParsableString()).Order(StringComparer.Ordinal).ToArray(),
            Load = new(publisher.PeriodicStatistics.ToDictionary(
                pair => pair.Key.ToParsableString(), pair => StateComparison.Serialize(pair.Value)), StringComparer.Ordinal),
            LoadVersions = new(publisher.PeriodicStatistics.ToDictionary(
                pair => pair.Key.ToParsableString(), pair => pair.Value.DateTime.Ticks), StringComparer.Ordinal),
            UnconfirmedPeers = [],
            OriginatorTargets = [],
            ForwardingTargets = [],
            Metrics = includeDetails ? observation.Metrics() : [],
            Applies = includeDetails ? observation.Applies() : [],
            TransportBytesWritten = observation.BytesWritten,
            TransportBytesRead = observation.BytesRead,
            SocketBytesSent = socket.Sent,
            SocketBytesReceived = socket.Received,
            SocketCounterSamples = socket.Samples,
            CpuMilliseconds = process.TotalProcessorTime.TotalMilliseconds,
            AllocatedBytes = GC.GetTotalAllocatedBytes(precise: true),
            ManagedHeapBytes = GC.GetTotalMemory(forceFullCollection: false),
            WorkingSetBytes = process.WorkingSet64,
            PrivateBytes = process.PrivateMemorySize64,
            PendingControlCalls = services.GetRequiredService<ControlTarget>().Pending,
            StartedControlCalls = services.GetRequiredService<ControlTarget>().Started,
            CancelledControlCalls = services.GetRequiredService<ControlTarget>().Cancelled,
            ControlCancellationSignals = services.GetRequiredService<ControlTarget>().CancellationSignals,
            ControlTokenCanBeCanceled = services.GetRequiredService<ControlTarget>().TokenCanBeCanceled,
            ControlTokenCancelledOnEntry = services.GetRequiredService<ControlTarget>().TokenCancelledOnEntry,
            ControlStartedAtUtc = services.GetRequiredService<ControlTarget>().StartedAtUtc,
            ControlCancellationObservedAtUtc = services.GetRequiredService<ControlTarget>().CancellationObservedAtUtc,
            OutboundControlCall = services.GetRequiredService<ControlledCall>().Snapshot,
            Partitioned = observation.Partitioned,
            LegacyGossipSuppressed = services.GetRequiredService<GatedMembershipGossiper>().Suppressed,
            MembershipReadsFrozen = services.GetRequiredService<FileMembershipTable>().ReadsFrozen,
        };
#if NEW_RUNTIME
        result = NewRuntime.Decorate(services, result);
#endif
        return result;
    }

    private static BinaryIdentity Identity()
    {
        // Hash once: periodic snapshots must not read the runtime image repeatedly.
        return CachedIdentity.Value;
    }

    private static readonly Lazy<BinaryIdentity> CachedIdentity = new(() =>
    {
        var assembly = typeof(Silo).Assembly;
        var metadata = typeof(Program).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .ToDictionary(attribute => attribute.Key, attribute => attribute.Value);
        return new(
            Environment.ProcessId, metadata["HarnessRuntime"]!, metadata["HarnessSourceRevision"]!,
            assembly.GetName().Name!, assembly.GetName().Version!.ToString(),
            assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion,
            assembly.ManifestModule.ModuleVersionId.ToString(),
            Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(assembly.Location))),
            assembly.Location,
            assembly.GetType("Orleans.Runtime.Dissemination.DisseminationSystemTarget") is not null)
        {
            Assemblies = AppDomain.CurrentDomain.GetAssemblies()
                .Where(candidate => !candidate.IsDynamic && candidate.GetName().Name!.StartsWith("Orleans", StringComparison.Ordinal))
                .ToDictionary(candidate => candidate.GetName().Name!, candidate => new AssemblyProof(
                    candidate.GetName().Version!.ToString(),
                    candidate.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "",
                    candidate.ManifestModule.ModuleVersionId.ToString(),
                    Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(candidate.Location))),
                    candidate.Location)),
        };
    });

    private static async Task MonitorParent(int parentProcessId, CancellationTokenSource lifetime)
    {
        try
        {
            using var parent = Process.GetProcessById(parentProcessId);
            await parent.WaitForExitAsync(lifetime.Token);
            await lifetime.CancelAsync();
        }
        catch (ArgumentException)
        {
            await lifetime.CancelAsync();
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
        }
    }

    private static Task Write(Response response) => Console.Out.WriteLineAsync("HARNESS " + JsonSerializer.Serialize(response));
}
