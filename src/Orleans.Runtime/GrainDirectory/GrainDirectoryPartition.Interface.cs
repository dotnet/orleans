using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

#nullable enable

namespace Orleans.Runtime.GrainDirectory;

internal sealed partial class GrainDirectoryPartition
{
    async ValueTask<DirectoryResult<GrainAddress>> IGrainDirectoryPartition.RegisterAsync(MembershipVersion version, GrainAddress address, GrainAddress? currentRegistration)
    {
        ArgumentNullException.ThrowIfNull(address);
        LogRegisterAsync(version, address, currentRegistration);

        // Ensure that the current membership version is new enough.
        await WaitForRange(address.GrainId, version);
        if (!IsOwner(CurrentView, address.GrainId))
        {
            return DirectoryResult.RefreshRequired<GrainAddress>(CurrentView.Version);
        }

        DebugAssertOwnership(address.GrainId);
        return DirectoryResult.FromResult(RegisterCore(address, currentRegistration), version);
    }

    async ValueTask<DirectoryResult<GrainAddress?>> IGrainDirectoryPartition.LookupAsync(MembershipVersion version, GrainId grainId)
    {
        LogLookupAsync(version, grainId);

        // Ensure we can serve the request.
        await WaitForRange(grainId, version);
        if (!IsOwner(CurrentView, grainId))
        {
            return DirectoryResult.RefreshRequired<GrainAddress?>(CurrentView.Version);
        }

        return DirectoryResult.FromResult(LookupCore(grainId), version);
    }

    async ValueTask<DirectoryResult<bool>> IGrainDirectoryPartition.DeregisterAsync(MembershipVersion version, GrainAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        LogDeregisterAsync(version, address);

        await WaitForRange(address.GrainId, version);
        if (!IsOwner(CurrentView, address.GrainId))
        {
            return DirectoryResult.RefreshRequired<bool>(CurrentView.Version);
        }

        DebugAssertOwnership(address.GrainId);
        return DirectoryResult.FromResult(DeregisterCore(address), version);
    }

    private bool DeregisterCore(GrainAddress address)
    {
        if (_directory.TryGetValue(address.GrainId, out var existing) && (existing.Matches(address) || IsSiloDead(existing)))
        {
            return _directory.Remove(address.GrainId);
        }

        return false;
    }

    internal GrainAddress? LookupCore(GrainId grainId)
    {
        if (_directory.TryGetValue(grainId, out var existing) && !IsSiloDead(existing))
        {
            return existing;
        }

        return null;
    }

    private GrainAddress RegisterCore(GrainAddress newAddress, GrainAddress? existingAddress)
    {
        ref var existing = ref CollectionsMarshal.GetValueRefOrAddDefault(_directory, newAddress.GrainId, out _);

        if (existing is null || existing.Matches(existingAddress) || IsSiloDead(existing))
        {
            if (newAddress.MembershipVersion != CurrentView.Version)
            {
                // Set the membership version to match the view number in which it was registered.
                newAddress = new()
                {
                    GrainId = newAddress.GrainId,
                    SiloAddress = newAddress.SiloAddress,
                    ActivationId = newAddress.ActivationId,
                    MembershipVersion = CurrentView.Version
                };
            }

            existing = newAddress;
        }

        return existing;
    }

    private bool IsSiloDead(GrainAddress existing) => _owner.ClusterMembershipSnapshot.GetSiloStatus(existing.SiloAddress) == SiloStatus.Dead;

    [LoggerMessage(
        Level = LogLevel.Trace,
        Message = "RegisterAsync('{Version}', '{Address}', '{ExistingAddress}')"
    )]
    private partial void LogRegisterAsync(MembershipVersion version, GrainAddress address, GrainAddress? existingAddress);

    [LoggerMessage(
        Level = LogLevel.Trace,
        Message = "LookupAsync('{Version}', '{GrainId}')"
    )]
    private partial void LogLookupAsync(MembershipVersion version, GrainId grainId);

    [LoggerMessage(
        Level = LogLevel.Trace,
        Message = "DeregisterAsync('{Version}', '{Address}')"
    )]
    private partial void LogDeregisterAsync(MembershipVersion version, GrainAddress address);
}
