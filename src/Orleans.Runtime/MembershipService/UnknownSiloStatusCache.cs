using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Orleans.Caching;

namespace Orleans.Runtime.MembershipService;

/// <summary>
/// Conservatively classifies silos which are absent from cluster membership.
/// </summary>
internal sealed partial class UnknownSiloStatusCache
{
    private const int CacheCapacity = 1_024;
    private readonly ConcurrentLruCache<SiloAddress, byte> _deadSilos = new(CacheCapacity);
    private readonly IMembershipManager _membershipManager;
    private readonly ILogger _logger;

    public UnknownSiloStatusCache(IMembershipManager membershipManager, ILogger<UnknownSiloStatusCache> logger)
    {
        _membershipManager = membershipManager;
        _logger = logger;
    }

    public async ValueTask<Dictionary<SiloAddress, SiloStatus>> GetSiloStatuses(
        ClusterMembershipSnapshot snapshot,
        IEnumerable<SiloAddress> siloAddresses)
    {
        var result = new Dictionary<SiloAddress, SiloStatus>();
        List<SiloAddress>? unknownSilos = null;
        foreach (var siloAddress in siloAddresses.Distinct())
        {
            var status = snapshot.GetSiloStatus(siloAddress);
            if (status != SiloStatus.None)
            {
                _deadSilos.TryRemove(siloAddress);
                result.Add(siloAddress, status);
            }
            else if (_deadSilos.TryGet(siloAddress, out _))
            {
                result.Add(siloAddress, SiloStatus.Dead);
            }
            else
            {
                unknownSilos ??= [];
                unknownSilos.Add(siloAddress);
            }
        }

        if (unknownSilos is null)
        {
            return result;
        }

        try
        {
            // The first call can join a refresh which began before the silos were observed as unknown.
            // The second call starts after that work completed and therefore establishes the causal barrier.
            await _membershipManager.Refresh(targetVersion: null, CancellationToken.None);
            await _membershipManager.Refresh(targetVersion: null, CancellationToken.None);

            var refreshedSnapshot = _membershipManager.CurrentSnapshot;
            foreach (var siloAddress in unknownSilos)
            {
                var status = refreshedSnapshot.GetSiloStatus(siloAddress);
                if (status == SiloStatus.None)
                {
                    status = SiloStatus.Dead;
                    _deadSilos.AddOrUpdate(siloAddress, 0);
                }

                result.Add(siloAddress, status);
            }
        }
        catch (Exception exception)
        {
            LogWarningUnableToValidateUnknownSilos(_logger, exception);
            foreach (var siloAddress in unknownSilos)
            {
                result.Add(siloAddress, SiloStatus.None);
            }
        }

        return result;
    }

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Unable to validate unknown silos against cluster membership"
    )]
    private static partial void LogWarningUnableToValidateUnknownSilos(
        ILogger logger,
        Exception exception);
}
