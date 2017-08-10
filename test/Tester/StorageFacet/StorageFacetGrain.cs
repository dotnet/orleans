using Orleans;
using System.Threading.Tasks;
// Note that for a feature exposed to a grain as a facet, only it's abstractions should be necessary.
using Tester.StorageFacet.Abstractions;

namespace Tester
{
    public interface IStorageFacetGrain : IGrainWithIntegerKey
    {
        Task<string[]> GetNames();
        Task<string[]> GetExtendedInfo();
    }

    public class StorageFacetGrain : Grain, IStorageFacetGrain
    {
        private readonly IStorageFeature<string> first;
        private readonly IStorageFeature<string> second;

        public StorageFacetGrain(
            [StorageFeature("Blob", "FirstState")] IStorageFeature<string> first,
            [StorageFeature("Table")] IStorageFeature<string> second)
        {
            this.first = first;
            this.second = second;
        }

        public Task<string[]> GetNames()
        {
            return Task.FromResult(new[] { this.first.Name, this.second.Name });
        }

        public Task<string[]> GetExtendedInfo()
        {
            return Task.FromResult(new[] { this.first.GetExtendedInfo(), this.second.GetExtendedInfo() });
        }
    }

    public interface IStorageFactoryGrain : IStorageFacetGrain
    {
    }
    public class StorageFactoryGrain : Grain, IStorageFactoryGrain
    {
        private readonly IStorageFeature<string> first;
        private readonly IStorageFeature<string> second;

        public StorageFactoryGrain(
            INamedStorageFeatureFactory namedStorageFeatureFactory)
        {
            this.first = namedStorageFeatureFactory.Create<string>("Blob", new StorageFeatureConfig("FirstState"));
            this.second = namedStorageFeatureFactory.Create<string>("Table", new StorageFeatureConfig("second")); ;
        }

        public Task<string[]> GetNames()
        {
            return Task.FromResult(new[] { this.first.Name, this.second.Name });
        }

        public Task<string[]> GetExtendedInfo()
        {
            return Task.FromResult(new[] { this.first.GetExtendedInfo(), this.second.GetExtendedInfo() });
        }
    }

    public interface IStorageDefaultFactoryGrain : IStorageFacetGrain
    {
    }

    public class StorageDefaultFactoryGrain : Grain, IStorageDefaultFactoryGrain
    {
        private readonly IStorageFeature<string> first;
        private readonly IStorageFeature<string> second;

        public StorageDefaultFactoryGrain(
            IStorageFeatureFactory StorageFeatureFactory)
        {
            this.first = StorageFeatureFactory.Create<string>(new StorageFeatureConfig("FirstState"));
            this.second = StorageFeatureFactory.Create<string>(new StorageFeatureConfig("second"));
        }

        public Task<string[]> GetNames()
        {
            return Task.FromResult(new[] { this.first.Name, this.second.Name });
        }

        public Task<string[]> GetExtendedInfo()
        {
            return Task.FromResult(new[] { this.first.GetExtendedInfo(), this.second.GetExtendedInfo() });
        }
    }

    public interface IStorageDefaultFacetGrain : IStorageFacetGrain
    {
    }

    public class StorageDefaultFacetGrain : Grain, IStorageDefaultFacetGrain
    {
        private readonly IStorageFeature<string> first;
        private readonly IStorageFeature<string> second;

        public StorageDefaultFacetGrain(
            [StorageFeature(stateName: "FirstState")] IStorageFeature<string> first,
            [StorageFeature] IStorageFeature<string> second)
        {
            this.first = first;
            this.second = second;
        }

        public Task<string[]> GetNames()
        {
            return Task.FromResult(new[] { this.first.Name, this.second.Name });
        }

        public Task<string[]> GetExtendedInfo()
        {
            return Task.FromResult(new[] { this.first.GetExtendedInfo(), this.second.GetExtendedInfo() });
        }
    }
}
