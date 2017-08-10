using System.Threading.Tasks;

namespace Tester.StorageFacet.Abstractions
{
    /// <summary>
    /// Primary storage feature interface.  
    ///  This is the actual functionality the users need.
    /// </summary>
    /// <typeparam name="TState"></typeparam>
    public interface IStorageFeature<TState>
    {
        string Name { get; }

        TState State { get; set; }

        Task SaveAsync();

        string GetExtendedInfo();
    }

    /// <summary>
    /// Feature configuration information which application layer can provide to the
    ///  feature per instance (by grain type if using attributes).
    /// </summary>
    public interface IStorageFeatureConfig
    {
        string StateName { get; }
    }

    /// <summary>
    /// Feature configuration utility class
    /// </summary>
    public class StorageFeatureConfig : IStorageFeatureConfig
    {
        public StorageFeatureConfig(string stateName)
        {
            this.StateName = stateName;
        }

        public string StateName { get; }
    }

    /// <summary>
    /// Creates a storage feature from a configuration
    /// </summary>
    public interface IStorageFeatureFactory
    {
        IStorageFeature<TState> Create<TState>(IStorageFeatureConfig config);
    }

    /// <summary>
    /// Creates a storage feature by name from a configuration
    /// </summary>
    public interface INamedStorageFeatureFactory
    {
        IStorageFeature<TState> Create<TState>(string name, IStorageFeatureConfig config);
    }
}
