using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Orleans.Runtime.Diagnostics;

namespace Orleans.Runtime.GrainDirectory;

internal sealed partial class GrainDirectoryPartition
{
    async ValueTask<DirectoryResult<GrainAddress>> IGrainDirectoryPartition.RegisterAsync(
        MembershipVersion version,
        GrainAddress address,
        GrainAddress? currentRegistration,
        CancellationToken cancellationToken,
        bool allowPreviousVersion)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(address);
        LogRegisterAsync(version, address, currentRegistration);

        var currentView = await WaitForOwnershipViewAsync(address.GrainId, version, cancellationToken, allowPreviousVersion, address);
        if (!IsOwner(currentView, address.GrainId))
        {
            return DirectoryResult.RefreshRequired<GrainAddress>(currentView.Version);
        }

        DebugAssertOwnership(currentView, address.GrainId);

        if (_deadSiloLeaseDuration > TimeSpan.Zero)
        {
            var utcNow = _timeProvider.GetUtcNow();
            var rangeHash = address.GrainId.GetUniformHashCode();
            var isExistingRegistration = currentRegistration is not null && address.Matches(currentRegistration);

            // Range lease holds
            for (var i = _rangeLeaseHolds.Count - 1; !isExistingRegistration && i >= 0; i--)
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
                    var retryAfter = expiration - utcNow;
                    GrainDirectoryEvents.EmitRegistrationBlockedByRangeLease(_id, address.GrainId, lockedRange, expiration, retryAfter);
                    return DirectoryResult.RetryAfter<GrainAddress>(retryAfter);
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
                        var retryAfter = expiration - utcNow;
                        GrainDirectoryEvents.EmitRegistrationBlockedBySiloLease(_id, address.GrainId, existingActivation.SiloAddress!, expiration, retryAfter);
                        return DirectoryResult.RetryAfter<GrainAddress>(retryAfter);
                    }
                }
            }
        }

        var result = RegisterCore(address, currentRegistration, currentView.Version,
            allowPreviousVersion ? currentView.ClusterMembershipSnapshot : _owner.ClusterMembershipSnapshot);
        return DirectoryResult.FromResult(result, version);
    }

    async ValueTask<DirectoryResult<GrainAddress?>> IGrainDirectoryPartition.LookupAsync(
        MembershipVersion version,
        GrainId grainId,
        CancellationToken cancellationToken,
        bool allowPreviousVersion)
    {
        cancellationToken.ThrowIfCancellationRequested();
        LogLookupAsync(version, grainId);

        var currentView = await WaitForOwnershipViewAsync(grainId, version, cancellationToken, allowPreviousVersion);
        if (!IsOwner(currentView, grainId))
        {
            return DirectoryResult.RefreshRequired<GrainAddress?>(currentView.Version);
        }

        var result = LookupCore(grainId,
            allowPreviousVersion ? currentView.ClusterMembershipSnapshot : _owner.ClusterMembershipSnapshot);
        return DirectoryResult.FromResult(result, version);
    }

    async ValueTask<DirectoryResult<bool>> IGrainDirectoryPartition.DeregisterAsync(
        MembershipVersion version,
        GrainAddress address,
        CancellationToken cancellationToken,
        bool allowPreviousVersion)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(address);
        LogDeregisterAsync(version, address);

        var currentView = await WaitForOwnershipViewAsync(address.GrainId, version, cancellationToken, allowPreviousVersion, address);
        if (!IsOwner(currentView, address.GrainId))
        {
            return DirectoryResult.RefreshRequired<bool>(currentView.Version);
        }

        DebugAssertOwnership(currentView, address.GrainId);
        var result = DeregisterCore(address,
            allowPreviousVersion ? currentView.ClusterMembershipSnapshot : _owner.ClusterMembershipSnapshot);
        return DirectoryResult.FromResult(result, version);
    }

    private bool DeregisterCore(GrainAddress address, ClusterMembershipSnapshot membership)
    {
        if (!_directory.TryGetValue(address.GrainId, out var existing))
        {
            return false;
        }

        if (_deadSiloLeaseDuration > TimeSpan.Zero
            && existing.SiloAddress is { } siloAddress
            && _siloLeaseHolds.TryGetValue(siloAddress, out var expiration)
            && _timeProvider.GetUtcNow() < expiration)
        {
            return false;
        }

        if (existing.Matches(address) || IsSiloDead(existing, membership))
        {
            return _directory.Remove(address.GrainId);
        }

        return false;
    }

    internal GrainAddress? LookupCore(GrainId grainId) => LookupCore(grainId, _owner.ClusterMembershipSnapshot);

    private GrainAddress? LookupCore(GrainId grainId, ClusterMembershipSnapshot membership)
    {
        if (_directory.TryGetValue(grainId, out var existing) && !IsSiloDead(existing, membership))
        {
            return existing;
        }

        return null;
    }

    private async ValueTask<DirectoryMembershipSnapshot> WaitForOwnershipViewAsync(
        GrainId grainId,
        MembershipVersion version,
        CancellationToken cancellationToken,
        bool allowPreviousVersion = false,
        GrainAddress? activation = null)
    {
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, ShutdownToken);
        while (true)
        {
            // Requests which arrive with a stale membership version must still wait for any in-flight ownership
            // transition in the current view before deciding whether this partition can serve them.
            var currentView = CurrentView;
            // Routing proves ownership in the requested view; local ownership proves its immediate predecessor.
            // A larger gap could conceal an intervening owner.
            var canUsePreviousView = allowPreviousVersion
                && version.Value > long.MinValue
                && currentView.Version.Value == version.Value - 1
                && IsOwner(currentView, grainId)
                && (activation is null || activation.SiloAddress is { } host
                    && currentView.ClusterMembershipSnapshot.GetSiloStatus(host) == SiloStatus.Active);
            var requiredVersion = canUsePreviousView ? currentView.Version : version;
            var waitVersion = currentView.Version > requiredVersion ? currentView.Version : requiredVersion;
            if (allowPreviousVersion)
            {
                GrainDirectoryEvents.EmitPreviousViewAdmission(_id, _partitionIndex, grainId, version,
                    currentView.Version, currentView.Version < requiredVersion ? "refresh-required" : "range-gate");
            }

            await WaitForRange(grainId, waitVersion, linkedCts.Token);
            linkedCts.Token.ThrowIfCancellationRequested();
            if (ReferenceEquals(currentView, CurrentView))
            {
                if (allowPreviousVersion)
                {
                    GrainDirectoryEvents.EmitPreviousViewAdmission(_id, _partitionIndex, grainId, version,
                        currentView.Version, "admitted");
                }

                return currentView;
            }
        }
    }

    private GrainAddress RegisterCore(
        GrainAddress newAddress,
        GrainAddress? existingAddress,
        MembershipVersion currentVersion,
        ClusterMembershipSnapshot membership)
    {
        ref var existing = ref CollectionsMarshal.GetValueRefOrAddDefault(_directory, newAddress.GrainId, out _);

        if (existing is null || existing.Matches(existingAddress) || IsSiloDead(existing, membership))
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

    private static bool IsSiloDead(GrainAddress existing, ClusterMembershipSnapshot membership)
        => existing.SiloAddress is null || membership.GetSiloStatus(existing.SiloAddress, existing.MembershipVersion) == SiloStatus.Dead;

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
