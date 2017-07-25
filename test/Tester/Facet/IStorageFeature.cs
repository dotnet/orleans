using System.Threading.Tasks;

namespace Tester
{
    public interface IStorageFeatureConfig
    {
        string StorageProviderName { get; }
        string StateName { get; }
    }

    public interface IStorageFeature<TState>
    {
        string Name { get; }

        TState State { get; set; }

        Task SaveAsync();

        string GetExtendedInfo();
    }

    public interface IStorageFeatureFactory
    {
        object Create(IStorageFeatureConfig config);
    }

    public interface IStorageFeatureFactory<TState> : IStorageFeatureFactory
    {
    }
}
