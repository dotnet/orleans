#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;
using Orleans.GrainDirectory;
using Orleans.Runtime;

namespace Orleans.TestingHost.InProcess;
internal sealed class InProcessGrainDirectory(Func<SiloAddress, SiloStatus> getSiloStatus) : IGrainDirectory
{
    private readonly ConcurrentDictionary<GrainId, GrainAddress> _entries = [];

    public Task<GrainAddress?> Lookup(GrainId grainId)
    {
        if (_entries.TryGetValue(grainId, out var result) && !IsSiloDead(result))
        {
            return Task.FromResult<GrainAddress?>(result); 
        }

        return Task.FromResult<GrainAddress?>(null);
    }

    public Task<GrainAddress?> Register(GrainAddress address, GrainAddress? previousAddress)
    {
        ArgumentNullException.ThrowIfNull(address);

        var result = _entries.AddOrUpdate(
            address.GrainId,
            static (grainId, state) => state.Address,
            static (grainId, existing, state) =>
            {
                if (existing is null || state.PreviousAddress is { } prev && existing.Matches(prev) || state.Self.IsSiloDead(existing))
                {
                    return state.Address;
                }

                return existing;
            },
            (Self: this, Address: address, PreviousAddress: previousAddress));

        if (result is null || IsSiloDead(result))
        {
            return Task.FromResult<GrainAddress?>(null);
        }

        return Task.FromResult<GrainAddress?>(result);
    }

    public Task<GrainAddress?> Register(GrainAddress address) => Register(address, null);

    public Task Unregister(GrainAddress address)
    {
        if (!((IDictionary<GrainId, GrainAddress>)_entries).Remove(KeyValuePair.Create(address.GrainId, address)))
        {
            if (_entries.TryGetValue(address.GrainId, out var existing) && (existing.Matches(address) || IsSiloDead(existing)))
            {
                ((IDictionary<GrainId, GrainAddress>)_entries).Remove(KeyValuePair.Create(existing.GrainId, existing));
            }
        }

        return Task.CompletedTask;
    }

    public Task UnregisterSilos(List<SiloAddress> siloAddresses)
    {
        foreach (var entry in _entries)
        {
            foreach (var silo in siloAddresses)
            {
                if (silo.Equals(entry.Value.SiloAddress))
                {
                    ((IDictionary<GrainId, GrainAddress>)_entries).Remove(entry);
                }
            }
        }

        return Task.CompletedTask;
    }

    private bool IsSiloDead(GrainAddress existing) => existing.SiloAddress is not { } address || getSiloStatus(address) is SiloStatus.Dead or SiloStatus.None;
}
