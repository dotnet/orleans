using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Orleans.GrainDirectory;

namespace Orleans.Runtime.GrainDirectory
{
    internal class RemoteGrainDirectory : SystemTarget, IRemoteGrainDirectory
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

        public async Task<AddressAndTag> RegisterAsync(ActivationAddress address, int hopCount)
        {
            router.RegistrationsSingleActRemoteReceived.Increment();
            
            return await router.RegisterAsync(address, hopCount);
        }

        public Task RegisterMany(List<ActivationAddress> addresses)
        {
            if (addresses == null || addresses.Count == 0)
                throw new ArgumentException("addresses cannot be an empty list or null");

            // validate that this request arrived correctly
            //logger.Assert(ErrorCode.Runtime_Error_100140, silo.Matches(router.MyAddress), "destination address != my address");

            if (logger.IsEnabled(LogLevel.Trace)) logger.Trace("RegisterMany Count={0}", addresses.Count);


            return Task.WhenAll(addresses.Select(addr => router.RegisterAsync(addr, 1)));
        }

        public Task UnregisterAsync(ActivationAddress address, UnregistrationCause cause, int hopCount)
        {
            return router.UnregisterAsync(address, cause, hopCount);
        }

        public Task UnregisterManyAsync(List<ActivationAddress> addresses, UnregistrationCause cause, int hopCount)
        {
            return router.UnregisterManyAsync(addresses, cause, hopCount);
        }

        public  Task DeleteGrainAsync(GrainId grainId, int hopCount)
        {
            return router.DeleteGrainAsync(grainId, hopCount);
        }

        public Task<AddressAndTag> LookupAsync(GrainId grainId, int hopCount)
        {
            return router.LookupAsync(grainId, hopCount);
        }

        public Task<List<AddressAndTag>> LookUpMany(List<(GrainId GrainId, int Version)> grainAndETagList)
        {
            router.CacheValidationsReceived.Increment();
            if (logger.IsEnabled(LogLevel.Trace)) logger.Trace("LookUpMany for {0} entries", grainAndETagList.Count);

            var result = new List<AddressAndTag>();

            foreach (var tuple in grainAndETagList)
            {
                int curGen = partition.GetGrainETag(tuple.GrainId);
                if (curGen == tuple.Version || curGen == GrainInfo.NO_ETAG)
                {
                    // the grain entry either does not exist in the local partition (curGen = -1) or has not been updated

                    result.Add(new AddressAndTag { Address = ActivationAddress.GetAddress(null, tuple.GrainId, default), VersionTag = curGen });
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
                        result.Add(new AddressAndTag { Address = ActivationAddress.GetAddress(null, tuple.GrainId, default), VersionTag = GrainInfo.NO_ETAG });
                    }
                }
            }

            return Task.FromResult(result);
        }

        public Task RemoveHandoffPartition(SiloAddress source)
        {
            router.HandoffManager.RemoveHandoffPartition(source);
            return Task.CompletedTask;
        }

        public Task AcceptSplitPartition(List<ActivationAddress> singleActivations)
        {
            router.HandoffManager.AcceptExistingRegistrations(singleActivations);
            return Task.CompletedTask;
        }
    }
}
