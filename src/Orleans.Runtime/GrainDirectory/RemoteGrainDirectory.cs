using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Orleans.GrainDirectory;

namespace Orleans.Runtime.GrainDirectory
{
    internal class RemoteGrainDirectory : SystemTarget, IRemoteDhtGrainDirectory
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

        public Task<List<(GrainId GrainId, int VersionTag, ActivationAddress ActivationAddress)>> LookUpMany(List<(GrainId GrainId, int VersionTag)> grainAndETagList)
        {
            router.CacheValidationsReceived.Increment();
            if (logger.IsEnabled(LogLevel.Trace)) logger.Trace("LookUpMany for {0} entries", grainAndETagList.Count);

            var result = new List<(GrainId, int, ActivationAddress)>();

            foreach (var query in grainAndETagList)
            {
                if (partition.TryLookup(query.GrainId, out var lookupResult) && lookupResult.Address != null)
                {
                    ActivationAddress address;
                    if (lookupResult.VersionTag == query.VersionTag)
                    {
                        // If the query's VersionTag matches the current registration's VersionTag, do not return the ActivationAddress.
                        address = null;
                    }
                    else
                    {
                        address = lookupResult.Address;
                    }

                    result.Add((query.GrainId, lookupResult.VersionTag, address));
                }
                else
                {
                    result.Add((query.GrainId, AddressAndTag.NO_ETAG, null));
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
