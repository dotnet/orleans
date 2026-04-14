using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Orleans.GrainDirectory;

#nullable enable
namespace Orleans.Runtime.GrainDirectory;

/// <summary>
/// A system target that implements <see cref="IRemoteGrainDirectory"/> by delegating to <see cref="DistributedGrainDirectory"/>.
/// This enables silos running the old <see cref="LocalGrainDirectory"/> to forward directory requests to silos running the
/// new <see cref="DistributedGrainDirectory"/> during a rolling upgrade.
/// </summary>
internal sealed class DistributedRemoteGrainDirectory : SystemTarget, IRemoteGrainDirectory
{
    private readonly DistributedGrainDirectory _directory;
    private readonly DirectoryMembershipService _membershipService;

    private DistributedRemoteGrainDirectory(
        DistributedGrainDirectory directory,
        DirectoryMembershipService membershipService,
        GrainType grainType,
        SystemTargetShared shared)
        : base(grainType, shared)
    {
        _directory = directory;
        _membershipService = membershipService;
        shared.ActivationDirectory.RecordNewTarget(this);
    }

    /// <summary>
    /// Creates the pair of system targets that replace <see cref="RemoteGrainDirectory"/> when
    /// <see cref="DistributedGrainDirectory"/> is active: one for <see cref="Constants.DirectoryServiceType"/>
    /// and one for <see cref="Constants.DirectoryCacheValidatorType"/>.
    /// </summary>
    internal static (DistributedRemoteGrainDirectory DirectoryService, DistributedRemoteGrainDirectory CacheValidator)
        Create(DistributedGrainDirectory directory, DirectoryMembershipService membershipService, SystemTargetShared shared)
    {
        var directoryService = new DistributedRemoteGrainDirectory(directory, membershipService, Constants.DirectoryServiceType, shared);
        var cacheValidator = new DistributedRemoteGrainDirectory(directory, membershipService, Constants.DirectoryCacheValidatorType, shared);
        return (directoryService, cacheValidator);
    }

    /// <summary>
    /// Ensures the directory has an initialized membership view before processing requests.
    /// Without this, calls arriving before the directory processes its first membership update
    /// would block indefinitely in <see cref="DistributedGrainDirectory"/>'s internal retry loop.
    /// </summary>
    private async Task EnsureDirectoryInitializedAsync(CancellationToken cancellationToken)
    {
        if (_membershipService.CurrentView.Version == MembershipVersion.MinValue)
        {
            await _membershipService.RefreshViewAsync(new MembershipVersion(1), cancellationToken);
        }
    }

    private CancellationTokenSource CreateTimeoutCts() => new(TimeSpan.FromSeconds(30));

    public async Task<AddressAndTag> RegisterAsync(GrainAddress address, int hopCount)
    {
        using var cts = CreateTimeoutCts();
        await EnsureDirectoryInitializedAsync(cts.Token);
        var result = await _directory.RegisterAsync(address, null, cts.Token);
        return new(result, 0);
    }

    public async Task<AddressAndTag> RegisterAsync(GrainAddress address, GrainAddress? previousAddress, int hopCount)
    {
        using var cts = CreateTimeoutCts();
        await EnsureDirectoryInitializedAsync(cts.Token);
        var result = await _directory.RegisterAsync(address, previousAddress, cts.Token);
        return new(result, 0);
    }

    public async Task<AddressAndTag> LookupAsync(GrainId grainId, int hopCount)
    {
        using var cts = CreateTimeoutCts();
        await EnsureDirectoryInitializedAsync(cts.Token);
        var result = await _directory.LookupAsync(grainId, cts.Token);
        return new(result, 0);
    }

    public async Task UnregisterAsync(GrainAddress address, UnregistrationCause cause, int hopCount)
    {
        using var cts = CreateTimeoutCts();
        await EnsureDirectoryInitializedAsync(cts.Token);
        await _directory.UnregisterAsync(address, cts.Token);
    }

    public async Task UnregisterManyAsync(List<GrainAddress> addresses, UnregistrationCause cause, int hopCount)
    {
        using var cts = CreateTimeoutCts();
        await EnsureDirectoryInitializedAsync(cts.Token);
        foreach (var address in addresses)
        {
            await _directory.UnregisterAsync(address, cts.Token);
        }
    }

    public async Task DeleteGrainAsync(GrainId grainId, int hopCount)
    {
        using var cts = CreateTimeoutCts();
        await EnsureDirectoryInitializedAsync(cts.Token);
        var existing = await _directory.LookupAsync(grainId, cts.Token);
        if (existing is not null)
        {
            await _directory.UnregisterAsync(existing, cts.Token);
        }
    }

    public async Task RegisterMany(List<GrainAddress> addresses)
    {
        using var cts = CreateTimeoutCts();
        await EnsureDirectoryInitializedAsync(cts.Token);
        foreach (var address in addresses)
        {
            await _directory.RegisterAsync(address, null, cts.Token);
        }
    }

    public async Task<List<AddressAndTag>> LookUpMany(List<(GrainId GrainId, int Version)> grainAndETagList)
    {
        using var cts = CreateTimeoutCts();
        await EnsureDirectoryInitializedAsync(cts.Token);
        var result = new List<AddressAndTag>(grainAndETagList.Count);
        foreach (var (grainId, _) in grainAndETagList)
        {
            var address = await _directory.LookupAsync(grainId, cts.Token);
            result.Add(new(address, 0));
        }

        return result;
    }

    public async Task AcceptSplitPartition(List<GrainAddress> singleActivations)
    {
        using var cts = CreateTimeoutCts();
        await EnsureDirectoryInitializedAsync(cts.Token);
        foreach (var address in singleActivations)
        {
            await _directory.RegisterAsync(address, null, cts.Token);
        }
    }
}
