using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Orleans.GrainDirectory;

#nullable enable
namespace Orleans.Runtime.GrainDirectory;

internal sealed partial class ReplicatedGrainDirectory(
    GrainDirectoryReplica localReplica,
    ILogger<ReplicatedGrainDirectory> logger
    /*,
    IServiceProvider serviceProvider*/) : IGrainDirectory
{
    public async Task<GrainAddress?> Lookup(GrainId grainId) => await InvokeAsync(
        grainId,
        static (replica, version, grainId) => replica.LookupAsync(version, grainId),
        grainId);

    public async Task<GrainAddress?> Register(GrainAddress address) => await InvokeAsync(
        address.GrainId,
        static (replica, version, address) => replica.RegisterAsync(version, address, null),
        address);

    public async Task Unregister(GrainAddress address) => await InvokeAsync(
        address.GrainId,
        static (replica, version, address) => replica.UnregisterAsync(version, address),
        address);

    public async Task<GrainAddress?> Register(GrainAddress address, GrainAddress? previousAddress) => await InvokeAsync(
        address.GrainId,
        static (replica, version, state) => replica.RegisterAsync(version, state.Address, state.PreviousAddress),
        (Address: address, PreviousAddress: previousAddress));

    public Task UnregisterSilos(List<SiloAddress> siloAddresses) => Task.CompletedTask;

    private async Task<TResult> InvokeAsync<TState, TResult>(
        GrainId grainId,
        Func<IGrainDirectoryReplica, MembershipVersion, TState, ValueTask<DirectoryResult<TResult>>> func,
        TState state,
        [CallerArgumentExpression(nameof(func))] string operation = "")
    {
        DirectoryResult<TResult> invokeResult;
        var view = localReplica.CurrentView;
        while (true)
        {
            if (!view.TryGetOwnerIndex(grainId, out var ownerIndex))
            {
                if (view.Members.Length == 0 && view.Version.Value > 0)
                {
                    return default!;
                }

                view = await localReplica.RefreshViewAsync(new(view.Version.Value + 1));
                continue;
            }

            var owner = view.Members[ownerIndex];
            if (logger.IsEnabled(LogLevel.Trace))
            {
                logger.LogTrace("Invoking '{Operation}' on '{Owner}' for grain '{GrainId}'.", operation, owner, grainId);
            }

            var replica = localReplica.GetReplica(owner);
            invokeResult = await func(replica, view.Version, state);

            if (invokeResult.TryGetResult(view.Version, out var result))
            {
                if (logger.IsEnabled(LogLevel.Trace))
                {
                    logger.LogInformation("Invoked '{Operation}' on '{Owner}' for grain '{GrainId}' and received result '{Result}'.", operation, owner, grainId, result);
                }

                return result;
            }
            else
            {
                // Sync with the remote replica.
                view = await localReplica.RefreshViewAsync(invokeResult.Version);
            }
        }
    }
}
