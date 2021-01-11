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

        public List<(ActivationAddress activationAddress, UnregistrationCause cause)> UnregistrationReceived { get; private set; }
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
            this.UnregistrationReceived = new List<(ActivationAddress activationAddress, UnregistrationCause cause)>();
        }

        public async Task UnregisterAsync(ActivationAddress address, UnregistrationCause cause, int hopCount = 0)
        {
            this.UnregistrationCounter++;
            await Task.Delay(singleOperationDelay);
            this.UnregistrationReceived.Add((address, cause));
        }

        public async Task UnregisterManyAsync(List<ActivationAddress> addresses, UnregistrationCause cause, int hopCount = 0)
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

        public List<ActivationAddress> GetLocalCacheData(GrainId grain)
        {
            throw new NotImplementedException();
        }

        public AddressesAndTag GetLocalDirectoryData(GrainId grain)
        {
            throw new NotImplementedException();
        }

        public SiloAddress GetPrimaryForGrain(GrainId grain)
        {
            throw new NotImplementedException();
        }

        public void InvalidateCacheEntry(ActivationAddress activation, bool invalidateDirectoryAlso = false)
        {
            throw new NotImplementedException();
        }

        public bool IsSiloInCluster(SiloAddress silo)
        {
            throw new NotImplementedException();
        }

        public bool LocalLookup(GrainId grain, out AddressesAndTag addresses)
        {
            throw new NotImplementedException();
        }

        public Task<AddressesAndTag> LookupAsync(GrainId grainId, int hopCount = 0)
        {
            throw new NotImplementedException();
        }

        public Task<AddressAndTag> RegisterAsync(ActivationAddress address, bool singleActivation, int hopCount = 0)
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

        public Task UnregisterAfterNonexistingActivation(ActivationAddress address, SiloAddress origin)
        {
            throw new NotImplementedException();
        }
        #endregion
    }
}
