using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Orleans.GrainDirectory;

#nullable enable
namespace Orleans.Runtime.GrainDirectory;

/// <summary>
/// An <see cref="IRemoteGrainDirectory"/> implementation that delegates to <see cref="DistributedGrainDirectory"/>.
/// </summary>
/// <remarks>
/// <para>
/// This system target enables rolling upgrades from the legacy <see cref="LocalGrainDirectory"/> to the new
/// <see cref="DistributedGrainDirectory"/>. When an old silo using <see cref="LocalGrainDirectory"/> sends
/// a directory request to a new silo using <see cref="DistributedGrainDirectory"/>, the request is received
/// by this system target and forwarded to the <see cref="DistributedGrainDirectory"/>.
/// </para>
/// <para>
/// Unlike the previous <c>DelegatingGrainDirectoryPartition</c> approach, this implementation is fully async
/// and does not require blocking IO, since <see cref="IRemoteGrainDirectory"/> is an async interface.
/// </para>
/// <para>
/// This class is registered as an <see cref="ILifecycleParticipant{ISiloLifecycle}"/> and registers itself
/// in the activation directory during the <see cref="ServiceLifecycleStage.RuntimeInitialize"/> stage.
/// </para>
/// </remarks>
internal partial class DelegatingRemoteGrainDirectory : SystemTarget, IRemoteGrainDirectory, ILifecycleParticipant<ISiloLifecycle>
{
    private readonly DistributedGrainDirectory _directory;
    private readonly SystemTargetShared _shared;
    private readonly ILogger _logger;

    public DelegatingRemoteGrainDirectory(
        DistributedGrainDirectory directory,
        GrainType grainType,
        SystemTargetShared shared) : base(grainType, shared)
    {
        _directory = directory;
        _shared = shared;
        _logger = shared.LoggerFactory.CreateLogger<DelegatingRemoteGrainDirectory>();
    }

    void ILifecycleParticipant<ISiloLifecycle>.Participate(ISiloLifecycle lifecycle)
    {
        lifecycle.Subscribe(
            nameof(DelegatingRemoteGrainDirectory),
            ServiceLifecycleStage.RuntimeInitialize,
            OnRuntimeInitialize);
    }

    private Task OnRuntimeInitialize(CancellationToken cancellationToken)
    {
        // Register this system target in the activation directory so it can receive messages.
        _shared.ActivationDirectory.RecordNewTarget(this);
        LogDebugRegistered(GrainId);
        return Task.CompletedTask;
    }

    public async Task<AddressAndTag> RegisterAsync(GrainAddress address, int hopCount = 0)
    {
        LogRegisterAsync(address, hopCount);
        var result = await _directory.Register(address);
        return ToAddressAndTag(result);
    }

    public async Task<AddressAndTag> RegisterAsync(GrainAddress address, GrainAddress? previousAddress, int hopCount = 0)
    {
        LogRegisterAsyncWithPrevious(address, previousAddress, hopCount);
        var result = await _directory.Register(address, previousAddress);
        return ToAddressAndTag(result);
    }

    public async Task<AddressAndTag> LookupAsync(GrainId grainId, int hopCount = 0)
    {
        LogLookupAsync(grainId, hopCount);
        var result = await _directory.Lookup(grainId);
        return ToAddressAndTag(result);
    }

    public async Task UnregisterAsync(GrainAddress address, UnregistrationCause cause, int hopCount = 0)
    {
        LogUnregisterAsync(address, cause, hopCount);
        await _directory.Unregister(address);
    }

    public async Task UnregisterManyAsync(List<GrainAddress> addresses, UnregistrationCause cause, int hopCount = 0)
    {
        LogUnregisterManyAsync(addresses.Count, cause, hopCount);
        foreach (var address in addresses)
        {
            await _directory.Unregister(address);
        }
    }

    public async Task DeleteGrainAsync(GrainId grainId, int hopCount = 0)
    {
        LogDeleteGrainAsync(grainId, hopCount);
        // Look up the grain first to get the full address, then unregister it
        var address = await _directory.Lookup(grainId);
        if (address is not null)
        {
            await _directory.Unregister(address);
        }
    }

    public async Task RegisterMany(List<GrainAddress> addresses)
    {
        LogRegisterMany(addresses.Count);
        foreach (var address in addresses)
        {
            await _directory.Register(address);
        }
    }

    public async Task<List<AddressAndTag>> LookUpMany(List<(GrainId GrainId, int Version)> grainAndETagList)
    {
        LogLookUpMany(grainAndETagList.Count);
        var result = new List<AddressAndTag>(grainAndETagList.Count);
        foreach (var (grainId, version) in grainAndETagList)
        {
            var address = await _directory.Lookup(grainId);
            var tag = address?.GetHashCode() ?? GrainInfo.NO_ETAG;
            
            // If the version matches, return empty address (no update needed)
            if (tag == version)
            {
                result.Add(new AddressAndTag(GrainAddress.GetAddress(null, grainId, default), tag));
            }
            else
            {
                result.Add(new AddressAndTag(address, tag));
            }
        }
        return result;
    }

    public async Task AcceptSplitPartition(List<GrainAddress> singleActivations)
    {
        // During rolling upgrade, an old silo may try to hand off partition data to this silo.
        // We accept these registrations by registering them in the DistributedGrainDirectory.
        LogAcceptSplitPartition(singleActivations.Count);
        foreach (var address in singleActivations)
        {
            await _directory.Register(address);
        }
    }

    private static AddressAndTag ToAddressAndTag(GrainAddress? address)
    {
        return new AddressAndTag(address, address?.GetHashCode() ?? 0);
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Registered DelegatingRemoteGrainDirectory system target: {GrainId}")]
    private partial void LogDebugRegistered(GrainId grainId);

    [LoggerMessage(Level = LogLevel.Trace, Message = "RegisterAsync: address={Address}, hopCount={HopCount}")]
    private partial void LogRegisterAsync(GrainAddress address, int hopCount);

    [LoggerMessage(Level = LogLevel.Trace, Message = "RegisterAsync: address={Address}, previousAddress={PreviousAddress}, hopCount={HopCount}")]
    private partial void LogRegisterAsyncWithPrevious(GrainAddress address, GrainAddress? previousAddress, int hopCount);

    [LoggerMessage(Level = LogLevel.Trace, Message = "LookupAsync: grainId={GrainId}, hopCount={HopCount}")]
    private partial void LogLookupAsync(GrainId grainId, int hopCount);

    [LoggerMessage(Level = LogLevel.Trace, Message = "UnregisterAsync: address={Address}, cause={Cause}, hopCount={HopCount}")]
    private partial void LogUnregisterAsync(GrainAddress address, UnregistrationCause cause, int hopCount);

    [LoggerMessage(Level = LogLevel.Trace, Message = "UnregisterManyAsync: count={Count}, cause={Cause}, hopCount={HopCount}")]
    private partial void LogUnregisterManyAsync(int count, UnregistrationCause cause, int hopCount);

    [LoggerMessage(Level = LogLevel.Trace, Message = "DeleteGrainAsync: grainId={GrainId}, hopCount={HopCount}")]
    private partial void LogDeleteGrainAsync(GrainId grainId, int hopCount);

    [LoggerMessage(Level = LogLevel.Trace, Message = "RegisterMany: count={Count}")]
    private partial void LogRegisterMany(int count);

    [LoggerMessage(Level = LogLevel.Trace, Message = "LookUpMany: count={Count}")]
    private partial void LogLookUpMany(int count);

    [LoggerMessage(Level = LogLevel.Debug, Message = "AcceptSplitPartition: accepting {Count} entries from old silo during rolling upgrade")]
    private partial void LogAcceptSplitPartition(int count);
}

/// <summary>
/// The <see cref="IRemoteGrainDirectory"/> system target that handles directory service requests from old silos.
/// </summary>
internal sealed class DelegatingDirectoryService : DelegatingRemoteGrainDirectory
{
    public DelegatingDirectoryService(DistributedGrainDirectory directory, SystemTargetShared shared)
        : base(directory, Constants.DirectoryServiceType, shared)
    {
    }
}

/// <summary>
/// The <see cref="IRemoteGrainDirectory"/> system target that handles cache validation requests from old silos.
/// </summary>
internal sealed class DelegatingCacheValidator : DelegatingRemoteGrainDirectory
{
    public DelegatingCacheValidator(DistributedGrainDirectory directory, SystemTargetShared shared)
        : base(directory, Constants.DirectoryCacheValidatorType, shared)
    {
    }
}
