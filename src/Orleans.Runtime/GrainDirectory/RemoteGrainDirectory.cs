using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Orleans.GrainDirectory;

#nullable enable
namespace Orleans.Runtime.GrainDirectory
{
    internal sealed class RemoteGrainDirectory : SystemTarget, IRemoteGrainDirectory
    {
        private readonly LocalGrainDirectory router;
        private readonly GrainDirectoryPartition partition;
        private readonly ILogger logger;

        internal RemoteGrainDirectory(LocalGrainDirectory r, GrainType grainType, ILoggerFactory loggerFactory)
            : base(grainType, r.MyAddress, loggerFactory)
        {
            router = r;
            partition = r.DirectoryPartition;
            logger = loggerFactory.CreateLogger($"{typeof(RemoteGrainDirectory).FullName}.CacheValidator");
        }

        public Task<AddressAndTag> RegisterAsync(GrainAddress address, GrainAddress? previousAddress, int hopCount)
        {
            DirectoryInstruments.RegistrationsSingleActRemoteReceived.Add(1);

            return router.RegisterAsync(address, previousAddress, hopCount);
        }

        public Task RegisterMany(List<GrainAddress> addresses)
        {
            if (addresses == null || addresses.Count == 0)
            {
                throw new ArgumentException("Addresses cannot be an empty list or null");
            }

            // validate that this request arrived correctly
            //logger.Assert(ErrorCode.Runtime_Error_100140, silo.Matches(router.MyAddress), "destination address != my address");

            if (logger.IsEnabled(LogLevel.Trace)) logger.LogTrace("RegisterMany Count={Count}", addresses.Count);


            return Task.WhenAll(addresses.Select(addr => router.RegisterAsync(addr, previousAddress: null, 1)));
        }

        public Task UnregisterAsync(GrainAddress address, UnregistrationCause cause, int hopCount)
        {
            return router.UnregisterAsync(address, cause, hopCount);
        }

        public Task UnregisterManyAsync(List<GrainAddress> addresses, UnregistrationCause cause, int hopCount)
        {
            return router.UnregisterManyAsync(addresses, cause, hopCount);
        }

        public Task DeleteGrainAsync(GrainId grainId, int hopCount)
        {
            return router.DeleteGrainAsync(grainId, hopCount);
        }

        public Task<AddressAndTag> LookupAsync(GrainId grainId, int hopCount)
        {
            return router.LookupAsync(grainId, hopCount);
        }

        public Task<List<AddressAndTag>> LookUpMany(List<(GrainId GrainId, int Version)> grainAndETagList)
        {
            DirectoryInstruments.ValidationsCacheReceived.Add(1);
            if (logger.IsEnabled(LogLevel.Trace)) logger.LogTrace("LookUpMany for {Count} entries", grainAndETagList.Count);

            var result = new List<AddressAndTag>();

            foreach (var tuple in grainAndETagList)
            {
                int curGen = partition.GetGrainETag(tuple.GrainId);
                if (curGen == tuple.Version || curGen == GrainInfo.NO_ETAG)
                {
                    // the grain entry either does not exist in the local partition (curGen = -1) or has not been updated

                    result.Add(new(GrainAddress.GetAddress(null, tuple.GrainId, default), curGen));
                }
                else
                {
                    // the grain entry has been updated -- fetch and return its current version
                    var lookupResult = partition.LookUpActivation(tuple.GrainId);
                    // validate that the entry is still in the directory (i.e., it was not removed concurrently)
                    if (lookupResult.Address != null)
                    {
                        result.Add(lookupResult);
                    }
                    else
                    {
                        result.Add(new(GrainAddress.GetAddress(null, tuple.GrainId, default), GrainInfo.NO_ETAG));
                    }
                }
            }

            return Task.FromResult(result);
        }

        public Task AcceptSplitPartition(List<GrainAddress> singleActivations)
        {
            router.HandoffManager.AcceptExistingRegistrations(singleActivations);
            return Task.CompletedTask;
        }

        public Task<AddressAndTag> RegisterAsync(GrainAddress address, int hopCount = 0) => router.RegisterAsync(address, hopCount);
    }
}
