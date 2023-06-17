using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Orleans.GrainDirectory;
using Orleans.Runtime;
using Orleans.Runtime.GrainDirectory;

namespace UnitTests.Directory
{
    internal class MockLocalGrainDirectory : ILocalGrainDirectory
    {
        private readonly TimeSpan singleOperationDelay;
        private readonly TimeSpan batchOperationDelay;

        public List<(GrainAddress activationAddress, UnregistrationCause cause)> UnregistrationReceived { get; private set; }

        public int UnregistrationCounter { get; private set; }

        public MockLocalGrainDirectory(TimeSpan singleOperationDelay, TimeSpan batchOperationDelay)
        {
            Reset();
            this.singleOperationDelay = singleOperationDelay;
            this.batchOperationDelay = batchOperationDelay;
        }

        public void Reset()
        {
            this.UnregistrationCounter = 0;
            this.UnregistrationReceived = new List<(GrainAddress activationAddress, UnregistrationCause cause)>();
        }

        public async Task UnregisterAsync(GrainAddress address, UnregistrationCause cause, int hopCount = 0)
        {
            this.UnregistrationCounter++;
            await Task.Delay(singleOperationDelay);
            this.UnregistrationReceived.Add((address, cause));
        }

        public async Task UnregisterManyAsync(List<GrainAddress> addresses, UnregistrationCause cause, int hopCount = 0)
        {
            this.UnregistrationCounter++;
            await Task.Delay(batchOperationDelay);
            foreach(var addr in addresses)
            {
                this.UnregistrationReceived.Add((addr, cause));
            }
        }

        #region Not Implemented
        public RemoteGrainDirectory RemoteGrainDirectory => throw new NotImplementedException();

        public RemoteGrainDirectory CacheValidator => throw new NotImplementedException();

        public Task DeleteGrainAsync(GrainId grainId, int hopCount = 0)
        {
            throw new NotImplementedException();
        }

        public GrainAddress GetLocalCacheData(GrainId grain)
        {
            throw new NotImplementedException();
        }

        public AddressAndTag GetLocalDirectoryData(GrainId grain)
        {
            throw new NotImplementedException();
        }

        public SiloAddress GetPrimaryForGrain(GrainId grain)
        {
            throw new NotImplementedException();
        }

        public void InvalidateCacheEntry(GrainAddress activation)
        {
            throw new NotImplementedException();
        }

        public bool IsSiloInCluster(SiloAddress silo)
        {
            throw new NotImplementedException();
        }

        public bool LocalLookup(GrainId grain, out AddressAndTag addresses)
        {
            throw new NotImplementedException();
        }

        public Task<AddressAndTag> LookupAsync(GrainId grainId, int hopCount = 0)
        {
            throw new NotImplementedException();
        }

        public Task<AddressAndTag> RegisterAsync(GrainAddress address, GrainAddress previousAddress, int hopCount = 0)
        {
            throw new NotImplementedException();
        }

        public void SetSiloRemovedCatalogCallback(Action<SiloAddress, SiloStatus> catalogOnSiloRemoved)
        {
            throw new NotImplementedException();
        }

        public void Start()
        {
            throw new NotImplementedException();
        }

        public void Stop()
        {
            throw new NotImplementedException();
        }

        public Task UnregisterAfterNonexistingActivation(GrainAddress address, SiloAddress origin)
        {
            throw new NotImplementedException();
        }

        public void CachePlacementDecision(GrainId grainId, SiloAddress siloAddress) => throw new NotImplementedException();

        public void InvalidateCacheEntry(GrainId grainId) => throw new NotImplementedException();
        public bool TryCachedLookup(GrainId grainId, out GrainAddress address) => throw new NotImplementedException();
        #endregion
    }
}
