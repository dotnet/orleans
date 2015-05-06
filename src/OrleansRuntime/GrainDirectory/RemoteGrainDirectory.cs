/*
Project Orleans Cloud Service SDK ver. 1.0
 
Copyright (c) Microsoft Corporation
 
All rights reserved.
 
MIT License

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and 
associated documentation files (the ""Software""), to deal in the Software without restriction,
including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense,
and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so,
subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED *AS IS*, WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO
THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS
OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT,
TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
*/

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;


namespace Orleans.Runtime.GrainDirectory
{
    internal class RemoteGrainDirectory : SystemTarget, IRemoteGrainDirectory
    {
        private readonly LocalGrainDirectory router;
        private readonly GrainDirectoryPartition partition;
        private readonly TraceLogger logger;

        private static readonly TimeSpan RETRY_DELAY = TimeSpan.FromSeconds(5); // Pause 5 seconds between forwards to let the membership directory settle down

        internal RemoteGrainDirectory(LocalGrainDirectory r, GrainId id)
            : base(id, r.MyAddress)
        {
            router = r;
            partition = r.DirectoryPartition;
            logger = TraceLogger.GetLogger("Orleans.GrainDirectory.CacheValidator", TraceLogger.LoggerType.Runtime);
        }

        public async Task<int> Register(ActivationAddress address, int retries)
        {
            router.RegistrationsRemoteReceived.Increment();
            // validate that this grain should be stored in our partition
            SiloAddress owner = router.CalculateTargetSilo(address.Grain);
            if (owner == null)
            {
                // We don't know about any other silos, and we're stopping, so throw
                throw new InvalidOperationException("Grain directory is stopping");
            }
            
            if (router.MyAddress.Equals(owner))
            {
                router.RegistrationsLocal.Increment();
                return partition.AddActivation(address.Grain, address.Activation, address.Silo);
            }

            if (retries > 0)
            {
                if (logger.IsVerbose2) logger.Verbose2("Retry " + retries + " RemoteGrainDirectory.Register for address=" + address + " at Owner=" + owner);
                PrepareForRetry(retries);

                await Task.Delay(RETRY_DELAY);
                
                SiloAddress o = router.CalculateTargetSilo(address.Grain);
                if (o == null)
                {
                    // We don't know about any other silos, and we're stopping, so throw
                    throw new InvalidOperationException("Grain directory is stopping");
                }
                if (router.MyAddress.Equals(o))
                {
                    router.RegistrationsLocal.Increment();
                    return partition.AddActivation(address.Grain, address.Activation, address.Silo);
                }
                router.RegistrationsRemoteSent.Increment();
                return await GetDirectoryReference(o).Register(address, retries - 1);
            }
            
            throw new OrleansException("Silo " + router.MyAddress + " is not the owner of the grain " + address.Grain + " Owner=" + owner);
        }

        public Task RegisterMany(List<ActivationAddress> addresses, int retries)
        {
            return Task.WhenAll(addresses.Select(addr => Register(addr, retries)));
        }

        /// <summary>
        /// Registers a new activation, in single activation mode, with the directory service.
        /// If there is already an activation registered for this grain, then the new activation will
        /// not be registered and the address of the existing activation will be returned.
        /// Otherwise, the passed-in address will be returned.
        /// <para>This method must be called from a scheduler thread.</para>
        /// </summary>
        /// <param name="address">The address of the potential new activation.</param>
        /// <param name="retries"></param>
        /// <returns>The address registered for the grain's single activation.</returns>
        public async Task<Tuple<ActivationAddress, int>> RegisterSingleActivation(ActivationAddress address, int retries)
        {
            router.RegistrationsSingleActRemoteReceived.Increment();
            if (logger.IsVerbose2) logger.Verbose2("Trying to register activation for grain. GrainId: {0}. ActivationId: {1}.", address.Grain, address.Activation);

            // validate that this grain should be stored in our partition
            SiloAddress owner = router.CalculateTargetSilo(address.Grain);
            if (owner == null)
            {
                // We don't know about any other silos, and we're stopping, so throw
                throw new InvalidOperationException("Grain directory is stopping");
            }

            if (router.MyAddress.Equals(owner))
            {
                router.RegistrationsSingleActLocal.Increment();
                return partition.AddSingleActivation(address.Grain, address.Activation, address.Silo);
            }

            if (retries <= 0)
                throw new OrleansException("Silo " + router.MyAddress + " is not the owner of the grain " +
                                                address.Grain + " Owner=" + owner);

            if (logger.IsVerbose2) logger.Verbose2("Retry {0} RemoteGrainDirectory.RegisterSingleActivation for address={1} at Owner={2}", retries, address, owner);
            PrepareForRetry(retries);

            await Task.Delay(RETRY_DELAY);

            SiloAddress o = router.CalculateTargetSilo(address.Grain);
            if (o == null)
            {
                // We don't know about any other silos, and we're stopping, so throw
                throw new InvalidOperationException("Grain directory is stopping");
            }
            if (o.Equals(router.MyAddress))
            {
                router.RegistrationsSingleActLocal.Increment();
                return partition.AddSingleActivation(address.Grain, address.Activation, address.Silo);
            }
            router.RegistrationsSingleActRemoteSent.Increment();
            return await GetDirectoryReference(o).RegisterSingleActivation(address, retries - 1);
        }

        /// <summary>
        /// Registers multiple new activations, in single activation mode, with the directory service.
        /// If there is already an activation registered for any of the grains, then the corresponding new activation will
        /// not be registered and the address of the existing activation will be returned.
        /// Otherwise, the passed-in address will be returned.
        /// <para>This method must be called from a scheduler thread.</para>
        /// </summary>
        /// <param name="addresses"></param>
        /// <param name="retries">Number of retries to execute the method in case the virtual ring (servers) changes.</param>
        /// <returns></returns>
        public Task RegisterManySingleActivation(List<ActivationAddress> addresses, int retries)
        {
            // validate that this request arrived correctly
            //logger.Assert(ErrorCode.Runtime_Error_100140, silo.Matches(router.MyAddress), "destination address != my address");
            //var result = new List<ActivationAddress>();
            if (logger.IsVerbose2) logger.Verbose2("RegisterManySingleActivation Count={0}", addresses.Count);

            if (addresses.Count == 0)
                return TaskDone.Done;
            
            var done = addresses.Select(addr => RegisterSingleActivation(addr, retries));
            return Task.WhenAll(done);
        }

        public async Task Unregister(ActivationAddress address, bool force, int retries)
        {
            router.UnregistrationsRemoteReceived.Increment();
            // validate that this grain should be stored in our partition
            SiloAddress owner = router.CalculateTargetSilo(address.Grain);
            if (owner == null)
            {
                // We don't know about any other silos, and we're stopping, so throw
                throw new InvalidOperationException("Grain directory is stopping");
            }

            if (owner.Equals(router.MyAddress))
            {
                router.UnregistrationsLocal.Increment();
                partition.RemoveActivation(address.Grain, address.Activation, force);
                return;
            }
            
            if (retries > 0)
            {
                if (logger.IsVerbose2) logger.Verbose2("Retry {0} RemoteGrainDirectory.Unregister for address={1} at Owner={2}", retries, address, owner);
                PrepareForRetry(retries);

                await Task.Delay(RETRY_DELAY);

                SiloAddress o = router.CalculateTargetSilo(address.Grain);
                if (o == null)
                {
                    // We don't know about any other silos, and we're stopping, so throw
                    throw new InvalidOperationException("Grain directory is stopping");
                }
                if (o.Equals(router.MyAddress))
                {
                    router.UnregistrationsLocal.Increment();
                    partition.RemoveActivation(address.Grain, address.Activation, force);
                    return;
                }
                router.UnregistrationsRemoteSent.Increment();
                await GetDirectoryReference(o).Unregister(address, force, retries - 1);
            }
            else
            {
                throw new OrleansException("Silo " + router.MyAddress + " is not the owner of the grain " + address.Grain + " Owner=" + owner);
            }
        }

        public async Task UnregisterMany(List<ActivationAddress> addresses, int retries)
        {
            router.UnregistrationsManyRemoteReceived.Increment();
            var retry = new Dictionary<SiloAddress, List<ActivationAddress>>();
            foreach (var address in addresses)
            {
                SiloAddress owner = router.CalculateTargetSilo(address.Grain);
                if (owner == null)
                {
                    // We don't know about any other silos, and we're stopping, so throw
                    throw new InvalidOperationException("Grain directory is stopping");
                }

                if (owner.Equals(router.MyAddress))
                {
                    router.UnregistrationsLocal.Increment();
                    partition.RemoveActivation(address.Grain, address.Activation, true);
                }
                else
                {
                    List<ActivationAddress> list;
                    if (retry.TryGetValue(owner, out list))
                        list.Add(address);
                    else
                        retry[owner] = new List<ActivationAddress> {address};
                }
            }

            if (retry.Count == 0) return;
            if (retries <= 0)
                throw new OrleansException("Silo " + router.MyAddress + " is not the owner of grains" + 
                            Utils.DictionaryToString(retry, null, " "));
            
            PrepareForRetry(retries);
            await Task.Delay(RETRY_DELAY);
            await Task.WhenAll( retry.Select(p =>
                    {
                        router.UnregistrationsManyRemoteSent.Increment();
                        return GetDirectoryReference(p.Key).UnregisterMany(p.Value, retries - 1);
                    }));
        }

        public async Task DeleteGrain(GrainId grain, int retries)
        {
            // validate that this grain should be stored in our partition
            SiloAddress owner = router.CalculateTargetSilo(grain);
            if (owner == null)
            {
                // We don't know about any other silos, and we're stopping, so throw
                throw new InvalidOperationException("Grain directory is stopping");
            }
            
            if (owner.Equals(router.MyAddress))
            {
                partition.RemoveGrain(grain);
                return;
            }
            
            if (retries > 0)
            {
                if (logger.IsVerbose2) logger.Verbose2("Retry {0} RemoteGrainDirectory.DeleteGrain for Grain={1} at Owner={2}", retries, grain, owner);
                PrepareForRetry(retries);

                await Task.Delay(RETRY_DELAY);

                SiloAddress o = router.CalculateTargetSilo(grain);
                    if (o == null)
                    {
                        // We don't know about any other silos, and we're stopping, so throw
                        throw new InvalidOperationException("Grain directory is stopping");
                    }
                    if (o.Equals(router.MyAddress))
                    {
                        partition.RemoveGrain(grain);
                        return;
                    }
                    await GetDirectoryReference(o).DeleteGrain(grain, retries - 1);
            }
            else
            {
                throw new OrleansException("Silo " + router.MyAddress + " is not the owner of the grain " + grain + " Owner=" + owner);
            }
        }

        public async Task<Tuple<List<Tuple<SiloAddress, ActivationId>>, int>> LookUp(GrainId grain, int retries)
        {
            router.RemoteLookupsReceived.Increment();

            // validate that this grain should be stored in our partition
            SiloAddress owner = router.CalculateTargetSilo(grain, false);
            if (router.MyAddress.Equals(owner))
            {
                router.LocalDirectoryLookups.Increment();
                // It can happen that we cannot find the grain in our partition if there were 
                // some recent changes in the membership. Return empty list in such case (and not null) to avoid
                // NullReference exceptions in the code of invokers
                Tuple<List<Tuple<SiloAddress, ActivationId>>, int> res = partition.LookUpGrain(grain);
                if (res != null)
                {
                    router.LocalDirectorySuccesses.Increment();
                    return res;
                }

                return new Tuple<List<Tuple<SiloAddress, ActivationId>>, int>(new List<Tuple<SiloAddress, ActivationId>>(), GrainInfo.NO_ETAG);
            }

            if (retries <= 0)
                throw new OrleansException("Silo " + router.MyAddress + " is not the owner of the grain " + 
                    grain + " Owner=" + owner);

            if (logger.IsVerbose2) logger.Verbose2("Retry " + retries + " RemoteGrainDirectory.LookUp for Grain=" + grain + " at Owner=" + owner);
            
            PrepareForRetry(retries);
            await Task.Delay(RETRY_DELAY);

            SiloAddress o = router.CalculateTargetSilo(grain, false);
            if (router.MyAddress.Equals(o))
            {
                router.LocalDirectoryLookups.Increment();
                var res = partition.LookUpGrain(grain);
                if (res == null)
                    return new Tuple<List<Tuple<SiloAddress, ActivationId>>, int>(
                        new List<Tuple<SiloAddress, ActivationId>>(), GrainInfo.NO_ETAG);

                router.LocalDirectorySuccesses.Increment();
                return res;
            }
            router.RemoteLookupsSent.Increment();
            return await GetDirectoryReference(o).LookUp(grain, retries - 1);
        }

        public Task<List<Tuple<GrainId, int, List<Tuple<SiloAddress, ActivationId>>>>> LookUpMany(List<Tuple<GrainId, int>> grainAndETagList, int retries)
        {
            router.CacheValidationsReceived.Increment();
            if (logger.IsVerbose2) logger.Verbose2("LookUpMany for {0} entries", grainAndETagList.Count);

            var result = new List<Tuple<GrainId, int, List<Tuple<SiloAddress, ActivationId>>>>();

            foreach (Tuple<GrainId, int> tuple in grainAndETagList)
            {
                int curGen = partition.GetGrainETag(tuple.Item1);
                if (curGen == tuple.Item2 || curGen == GrainInfo.NO_ETAG)
                {
                    // the grain entry either does not exist in the local partition (curGen = -1) or has not been updated
                    result.Add(new Tuple<GrainId, int, List<Tuple<SiloAddress, ActivationId>>>(tuple.Item1, curGen, null));
                }
                else
                {
                    // the grain entry has been updated -- fetch and return its current version
                    Tuple<List<Tuple<SiloAddress, ActivationId>>, int> lookupResult = partition.LookUpGrain(tuple.Item1);
                    // validate that the entry is still in the directory (i.e., it was not removed concurrently)
                    if (lookupResult != null)
                    {
                        result.Add(new Tuple<GrainId, int, List<Tuple<SiloAddress, ActivationId>>>(tuple.Item1, lookupResult.Item2, lookupResult.Item1));
                    }
                    else
                    {
                        result.Add(new Tuple<GrainId, int, List<Tuple<SiloAddress, ActivationId>>>(tuple.Item1, GrainInfo.NO_ETAG, null));
                    }
                }
            }
            return Task.FromResult(result);
        }

        public Task AcceptHandoffPartition(SiloAddress source, Dictionary<GrainId, IGrainInfo> partition, bool isFullCopy)
        {
            router.HandoffManager.AcceptHandoffPartition(source, partition, isFullCopy);
            return TaskDone.Done;
        }

        public Task RemoveHandoffPartition(SiloAddress source)
        {
            router.HandoffManager.RemoveHandoffPartition(source);
            return TaskDone.Done;
        }
        
        /// <summary>
        /// This method is called before retrying to access the current owner of a grain, following
        /// a request that was sent to us, while we are not the owner of the given grain.
        /// This may happen if during the time the request was on its way, a ring has changed 
        /// (new servers came up / failed down).
        /// Here we might take some actions before the actual retrial is done.
        /// For example, we might back-off for some random time.
        /// </summary>
        /// <param name="retries"></param>
        protected void PrepareForRetry(int retries)
        {
            // For now, we do not do anything special ...
        }

        private IRemoteGrainDirectory GetDirectoryReference(SiloAddress target)
        {
            return RemoteGrainDirectoryFactory.GetSystemTarget(Constants.DirectoryServiceId, target);
        }
    }
}
