using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace Orleans.Runtime.GrainDirectory;

internal sealed partial class GrainDirectoryPartition
{
    async ValueTask<DirectoryResult<GrainAddress>> IGrainDirectoryPartition.RegisterAsync(MembershipVersion version, GrainAddress address, GrainAddress? currentRegistration)
    {
        ArgumentNullException.ThrowIfNull(address);
        LogRegisterAsync(version, address, currentRegistration);

        var currentView = await WaitForOwnershipViewAsync(address.GrainId, version);
        if (!IsOwner(currentView, address.GrainId))
        {
            return DirectoryResult.RefreshRequired<GrainAddress>(currentView.Version);
        }

        DebugAssertOwnership(currentView, address.GrainId);

        if (_leaseHoldDuration > TimeSpan.Zero)
        {
            var utcNow = _timeProvider.GetUtcNow().UtcDateTime;
            var rangeHash = address.GrainId.GetUniformHashCode();

            // Range lease holds
            for (var i = _rangeLeaseHolds.Count - 1; i >= 0; i--)
            {
                var (lockedRange, expiration) = _rangeLeaseHolds[i];

                if (utcNow >= expiration)
                {
                    // We use this opportunity to cleanup this expired range lease hold.
                    _rangeLeaseHolds.RemoveAt(i);
                    continue;
                }

                // If it is still active, does it block this request?
                if (lockedRange.Contains(rangeHash))
                {
                    return DirectoryResult.RetryAfter<GrainAddress>(expiration - utcNow);
                }
            }

            // Grain lease holds
            if (_directory.TryGetValue(address.GrainId, out var existingActivation))
            {
                if (_siloLeaseHolds.TryGetValue(existingActivation.SiloAddress!, out var expiration) && utcNow < expiration)
                {
                    // This grain belongs to this partition, and the activation is sitting on a silo that has an active lease hold.
                    // We need to check if the request includes the previous activation id, and if it does it's a valid update/override,
                    // otherwise it's a new activation trying to "steal" the id while the lease is active, so we reject it!
                    if (currentRegistration is null || !existingActivation.Matches(currentRegistration))
                    {
                        return DirectoryResult.RetryAfter<GrainAddress>(expiration - utcNow);
                    }
                }
            }
        }

        return DirectoryResult.FromResult(RegisterCore(address, currentRegistration, currentView.Version), version);
    }

    async ValueTask<DirectoryResult<GrainAddress?>> IGrainDirectoryPartition.LookupAsync(MembershipVersion version, GrainId grainId)
    {
        LogLookupAsync(version, grainId);

        var currentView = await WaitForOwnershipViewAsync(grainId, version);
        if (!IsOwner(currentView, grainId))
        {
            return DirectoryResult.RefreshRequired<GrainAddress?>(currentView.Version);
        }

        return DirectoryResult.FromResult(LookupCore(grainId), version);
    }

    async ValueTask<DirectoryResult<bool>> IGrainDirectoryPartition.DeregisterAsync(MembershipVersion version, GrainAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        LogDeregisterAsync(version, address);

        var currentView = await WaitForOwnershipViewAsync(address.GrainId, version);
        if (!IsOwner(currentView, address.GrainId))
        {
            return DirectoryResult.RefreshRequired<bool>(currentView.Version);
        }

        DebugAssertOwnership(currentView, address.GrainId);
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

    private async ValueTask<DirectoryMembershipSnapshot> WaitForOwnershipViewAsync(GrainId grainId, MembershipVersion version)
    {
        while (true)
        {
            // Requests which arrive with a stale membership version must still wait for any in-flight ownership
            // transition in the current view before deciding whether this partition can serve them.
            var currentView = CurrentView;
            var waitVersion = currentView.Version > version ? currentView.Version : version;
            await WaitForRange(grainId, waitVersion);
            if (ReferenceEquals(currentView, CurrentView))
            {
                return currentView;
            }
        }
    }

    private GrainAddress RegisterCore(GrainAddress newAddress, GrainAddress? existingAddress, MembershipVersion currentVersion)
    {
        ref var existing = ref CollectionsMarshal.GetValueRefOrAddDefault(_directory, newAddress.GrainId, out _);

        if (existing is null || existing.Matches(existingAddress) || IsSiloDead(existing))
        {
            if (newAddress.MembershipVersion != currentVersion)
            {
                // Set the membership version to match the view number in which it was registered.
                newAddress = new()
                {
                    GrainId = newAddress.GrainId,
                    SiloAddress = newAddress.SiloAddress,
                    ActivationId = newAddress.ActivationId,
                    MembershipVersion = currentVersion
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
