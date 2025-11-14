using System.Diagnostics;
using System.Runtime.InteropServices;
using Orleans.Core.Internal;

namespace ActivationRepartitioning.Frontend.Data;

public class ClusterDiagnosticsService(IGrainFactory grainFactory)
{
    private readonly Dictionary<SiloAddress, int> _hostKeys = [];
    private readonly Dictionary<SiloAddress, HostDetails> _hostDetails = [];
    private readonly Dictionary<GrainId, GrainDetails> _grainDetails = []; // Grain to host id
    private readonly Dictionary<Key, ulong> _edges = [];
    private readonly IManagementGrain _managementGrain = grainFactory.GetGrain<IManagementGrain>(0);
    private readonly record struct GrainDetails(int GrainKey, int HostKey);
    private readonly record struct HostDetails(int HostKey, int ActivationCount);
    private int _version;

    public async ValueTask<CallGraph> GetGrainCallFrequencies()
    {
        var loaderGrain = grainFactory.GetGrain<ILoaderGrain>("root");
        var loaderGrainType = loaderGrain.GetGrainId().Type;
        var resetCount = await loaderGrain.GetResetCount();
        if (resetCount > _version)
        {
            _version = resetCount;
            await ResetAsync();
        }

        lock (this)
        {
            _edges.Clear();
        }

        var maxEdgeValue = 0;
        var maxActivationCount = 0;

        var silos = (await _managementGrain.GetHosts(onlyActive: true)).Keys.Order();
        foreach (var silo in silos)
        {
            var hostKey = GetHostVertex(silo);
            var activationCount = 0;
            foreach (var activation in await _managementGrain.GetDetailedGrainStatistics(hostsIds: [silo]))
            {
                if (activation.GrainId.Type.Equals(loaderGrainType)) continue;
                if (activation.GrainId.IsSystemTarget()) continue;

                lock (this)
                {
                    var details = GetGrainVertex(activation.GrainId, hostKey);
                    _grainDetails[activation.GrainId] = new(details.GrainKey, hostKey);
                    ++activationCount;
                }
            }

            lock (this)
            {
                maxActivationCount = Math.Max(maxActivationCount, activationCount);
                _hostDetails[silo] = new(hostKey, activationCount);
            }
        }

        foreach (var edge in await _managementGrain.GetGrainCallFrequencies())
        {
            if (edge.TargetGrain.IsSystemTarget() || edge.SourceGrain.IsSystemTarget()) continue;
            lock (this)
            {
                var sourceHostId = GetHostVertex(edge.SourceHost);
                var targetHostId = GetHostVertex(edge.TargetHost);
                var sourceVertex = GetGrainVertex(edge.SourceGrain, sourceHostId);
                var targetVertex = GetGrainVertex(edge.TargetGrain, targetHostId);
                maxEdgeValue = Math.Max(maxEdgeValue, (int)edge.CallCount);
                UpdateEdge(new(sourceVertex.GrainKey, targetVertex.GrainKey), edge.CallCount);
            }
        }

        lock (this)
        {
            var grainIds = new List<GraphNode>(_grainDetails.Count);
            CollectionsMarshal.SetCount(grainIds, _grainDetails.Count);
            foreach ((var grainId, var (grainKey, hostKey)) in _grainDetails)
            {
                grainIds[grainKey] = new(grainId.ToString(), grainId.Key.ToString()!, hostKey, 1.0);
            }

            var hostIds = new List<HostNode>(_hostKeys.Count);
            CollectionsMarshal.SetCount(hostIds, _hostKeys.Count);
            foreach ((var hostId, var key) in _hostKeys)
            {
                if (_hostDetails.TryGetValue(hostId, out var details))
                {
                    hostIds[key] = new(hostId.ToString(), details.ActivationCount);
                }
            }

            var edges = new List<GraphEdge>();

            foreach (var edge in _edges)
            {
                edges.Add(new(edge.Key.Source, edge.Key.Target, edge.Value));
            }

            return new(grainIds, hostIds, edges, maxEdgeValue, maxActivationCount);
        }
    }

    internal async ValueTask ResetAsync()
    {
        var fanoutType = grainFactory.GetGrain<IFanOutGrain>(0, "0").GetGrainId().Type;
        foreach (var activation in await _managementGrain.GetDetailedGrainStatistics())
        {
            if (!activation.GrainId.Type.Equals(fanoutType)) continue;
            await grainFactory.GetGrain<IGrainManagementExtension>(activation.GrainId).DeactivateOnIdle();
        }

        Reset();
    }

    internal void Reset()
    {
        lock (this)
        {
            _hostKeys.Clear();
            _hostDetails.Clear();
            _grainDetails.Clear();
            _edges.Clear();
        }
    }

    private GrainDetails GetGrainVertex(GrainId grainId, int hostKey)
    {
        lock (this)
        {
            ref var key = ref CollectionsMarshal.GetValueRefOrAddDefault(_grainDetails, grainId, out var exists);
            if (!exists)
            {
                key = new(_grainDetails.Count - 1, hostKey);
            }

            return key;
        }
    }

    private int GetHostVertex(SiloAddress silo)
    {
        lock (this)
        {
            ref var key = ref CollectionsMarshal.GetValueRefOrAddDefault(_hostKeys, silo, out var exists);
            if (!exists)
            {
                key = _hostKeys.Count - 1;
            }

            return key;
        }
    }

    private void UpdateEdge(Key key, ulong increment)
    {
        lock (this)
        {
            ref var count = ref CollectionsMarshal.GetValueRefOrAddDefault(_edges, key, out var exists);
            count += increment;
        }
    }
}

public record class CallGraph(List<GraphNode> GrainIds, List<HostNode> HostIds, List<GraphEdge> Edges, int MaxEdgeValue, int MaxActivationCount);

public record struct HostNode(string Name, int ActivationCount);
public record struct GraphNode(string Name, string Key, int Host, double Weight);
public record struct Key(int Source, int Target);
public record struct GraphEdge(int Source, int Target, double Weight);
