#if NEW_RUNTIME
using System.Collections.Immutable;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Runtime;
using Orleans.Runtime.Dissemination;
using Orleans.Runtime.MembershipService;

namespace Orleans.Dissemination.IntegrationHarness;

internal static class NewRuntime
{
    public static void Configure(IServiceCollection services, NodeConfiguration configuration)
    {
        // The disabled leg must exercise untouched production defaults, not explicit Enabled=false.
        if (!configuration.Enabled)
        {
            return;
        }

        if (configuration.FastRecovery)
        {
            services.AddSingleton<DisseminationTreeGate>();
            services.AddSingleton<IIncomingGrainCallFilter>(provider => provider.GetRequiredService<DisseminationTreeGate>());
        }

        services.Configure<DisseminationOptions>(options =>
        {
            options.Enabled = true;
            if (configuration.FastRecovery)
            {
                options.Overlay.AntiEntropyInterval = TimeSpan.FromMilliseconds(250);
                options.Overlay.AntiEntropyPeerCount = 2;
                options.Overlay.MinFanOutFactor = 2;
                options.Overlay.MaxFanOutFactor = 2;
                options.MaxConcurrentSends = 4;
            }
        });
        services.Configure<ClusterMembershipOptions>(options => Enable(options.Dissemination, configuration.FastRecovery));
        services.Configure<DeploymentLoadPublisherOptions>(options => Enable(options.Dissemination, configuration.FastRecovery));
    }

    private static void Enable(DisseminationNamespaceOptions options, bool fastRecovery)
    {
        options.Enabled = true;
        if (fastRecovery)
        {
            options.ExpectedUpdateCadence = TimeSpan.FromMilliseconds(100);
            options.MaxCoalescingDelay = TimeSpan.FromMilliseconds(25);
            options.StaleItemTtl = TimeSpan.FromSeconds(1);
        }
    }

    public static NodeSnapshot Decorate(IServiceProvider services, NodeSnapshot snapshot)
    {
        var options = services.GetRequiredService<IOptions<DisseminationOptions>>().Value;
        if (!options.Enabled)
        {
            return snapshot with
            {
                NamespaceEnabled = services.GetRequiredService<IOptions<DeploymentLoadPublisherOptions>>().Value.Dissemination.Enabled,
            };
        }

        var ns = services.GetRequiredService<DeploymentLoadStatisticsDisseminationNamespace>();
        var topology = services.GetRequiredService<DisseminationMembership>().CurrentSnapshots.ActiveMembers;
        return snapshot with
        {
            Enabled = options.Enabled,
            NamespaceEnabled = ns.Options.Enabled,
            UnconfirmedPeers = services.GetRequiredService<IDisseminationService>()
                .GetUnconfirmedPeers(ns).Select(address => address.ToParsableString()).Order(StringComparer.Ordinal).ToArray(),
            OriginatorTargets = topology.OriginatorTreeTargets.Select(address => address.ToParsableString()).ToArray(),
            ForwardingTargets = topology.ForwardingTreeTargets.Select(address => address.ToParsableString()).ToArray(),
            TopologyMembers = topology.Members.Select(address => address.ToParsableString()).ToArray(),
            Fanout = options.Overlay.GetFanOutFactor(topology.Members.Length),
            TreeGate = services.GetService<DisseminationTreeGate>()?.Snapshot,
        };
    }

    public static async Task<NodeSnapshot> Execute(IServiceProvider services, Command command, CancellationToken cancellationToken)
    {
        var manager = services.GetRequiredService<IMembershipManager>();
        var dissemination = services.GetRequiredService<IDisseminationService>();
        var ns = services.GetRequiredService<MembershipDisseminationNamespace>();
        var local = services.GetRequiredService<ILocalSiloDetails>().SiloAddress;
        switch (command.Operation)
        {
            case "block-tree":
            {
                var gate = services.GetRequiredService<DisseminationTreeGate>();
                if (command.Value)
                {
                    await gate.BlockAndDrain(cancellationToken);
                }
                else
                {
                    gate.Open();
                }

                break;
            }
            case "probe-tree":
            {
                var peer = services.GetRequiredService<IInternalGrainFactory>()
                    .GetSystemTarget<IDisseminationSystemTarget>(
                        Constants.DisseminationSystemTargetType, SiloAddress.FromParsableString(command.Peer!));
                try
                {
                    await peer.PushBroadcast(new() { Sender = local }, cancellationToken);
                    return Program.Snapshot(services) with { TreeProbeRejected = false };
                }
                catch (InvalidOperationException exception) when (exception.Message == InFlightCallGate.ClosedMessage)
                {
                    return Program.Snapshot(services) with { TreeProbeRejected = true };
                }
            }
            case "probe":
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                deadline.CancelAfter(TimeSpan.FromSeconds(8));
                try
                {
                    var peer = services.GetRequiredService<IInternalGrainFactory>()
                        .GetSystemTarget<IDisseminationSystemTarget>(
                            Constants.DisseminationSystemTargetType, SiloAddress.FromParsableString(command.Peer!));
                    var response = await peer.ExchangeAntiEntropy(new()
                    {
                        Sender = local,
                        SupportedNamespaces = [ns.Name],
                    }, deadline.Token);
                    return Program.Snapshot(services) with
                    {
                        ProbeError = response.UnsupportedNamespaces.Contains(ns.Name) ? "namespace-unsupported" : null,
                    };
                }
                catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
                {
                    return Program.Snapshot(services) with
                    {
                        ProbeError = $"{exception.GetType().FullName}: {exception.Message}",
                        ProbeTimedOut = deadline.IsCancellationRequested || exception is TimeoutException,
                    };
                }
            }
            case "publish-membership":
                await ns.PublishAsync(dissemination, manager.CurrentSnapshot, cancellationToken);
                break;
            case "membership-history":
            {
                if (!services.GetRequiredService<GatedMembershipGossiper>().Suppressed)
                {
                    throw new InvalidOperationException("History injection requires legacy gossip isolation.");
                }

                var table = services.GetRequiredService<FileMembershipTable>();
                await table.FreezeReads(false, cancellationToken);
                for (var index = 0; index < command.Count; index++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var current = await table.ReadAllAsync(cancellationToken);
                    var row = current.Members.Single(row => row.Item1.SiloAddress.Equals(local));
                    row.Item1.HostName = $"retained-history-{current.Version.Version + 1}";
                    if (!await table.UpdateRowAsync(row.Item1, row.Item2, current.Version.Next(), cancellationToken))
                    {
                        throw new InvalidOperationException("Unexpected concurrent membership writer during isolated history test.");
                    }

                    await manager.Refresh(null, cancellationToken);
                    await ns.PublishAsync(dissemination, manager.CurrentSnapshot, cancellationToken);
                }

                await table.FreezeReads(true, cancellationToken);
                var repair = ns.CreateRepair(new(
                    DisseminationKey.Default, command.Version, null, 100, 1024 * 1024, 1024 * 1024));
                if (repair.Status != DisseminationRepairStatus.Produced || repair.Values.Length != 1)
                {
                    throw new InvalidOperationException($"Expected a production membership repair, got {repair.Status}.");
                }

                return Program.Snapshot(services) with { RepairFromVersion = repair.Values[0].FromVersion };
            }
            case "membership-heartbeat":
            {
                if (!services.GetRequiredService<GatedMembershipGossiper>().Suppressed
                    || !services.GetRequiredService<FileMembershipTable>().ReadsFrozen)
                {
                    throw new InvalidOperationException("Heartbeat injection requires storage and legacy gossip isolation.");
                }

                var current = manager.CurrentSnapshot;
                var entries = current.Entries.ToBuilder();
                var entry = FileMembershipTable.EntryData.From(entries[local]).ToEntry();
                entry.IAmAliveTime = entry.IAmAliveTime.AddHours(1);
                entries[local] = entry;
                await manager.ProcessGossipSnapshot(new(current.Version, entries.ToImmutable()), cancellationToken);
                // Deliberately no Publish: only same-version fingerprint anti-entropy can carry this heartbeat.
                return Program.Snapshot(services) with { HeartbeatTicks = entry.IAmAliveTime.Ticks };
            }
            default:
                throw new NotSupportedException(command.Operation);
        }

        return Program.Snapshot(services);
    }
}

// System targets use the normal incoming-call filter pipeline. This filter blocks only tree
// application; native ExchangeAntiEntropy requests/responses continue over the real connection.
internal sealed class DisseminationTreeGate : IIncomingGrainCallFilter
{
    private readonly InFlightCallGate _gate = new();

    public TreeGateSnapshot Snapshot => _gate.Snapshot;

    public Task BlockAndDrain(CancellationToken cancellationToken) => _gate.BlockAndDrain(cancellationToken);

    public void Open() => _gate.Open();

    public Task Invoke(IIncomingGrainCallContext context) =>
        context.InterfaceMethod.DeclaringType == typeof(IDisseminationSystemTarget)
        && context.InterfaceMethod.Name == nameof(IDisseminationSystemTarget.PushBroadcast)
            ? _gate.Invoke(context.Invoke)
            : context.Invoke();
}
#endif
