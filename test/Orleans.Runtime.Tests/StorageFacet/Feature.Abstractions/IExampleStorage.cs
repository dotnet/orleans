namespace Tester.StorageFacet.Abstractions
{
    /// <summary>
    /// Primary storage feature interface.
    ///  This is the actual functionality the users need.
    /// </summary>
    /// <typeparam name="TState"></typeparam>
    public interface IExampleStorage<TState>
    {
        TState State { get; set; }

        Task Save();

        // Test calls - used to verify facet wiring works
        string Name { get; }
        string GetExtendedInfo();
    }

    /// <summary>
    /// Feature configuration information which application layer can provide to the
    ///  feature per instance (by grain type if using attributes).
    /// </summary>
    public interface IExampleStorageConfig
    {
        string StateName { get; }
    }

    /// <summary>
    /// Feature configuration utility class
    /// </summary>
    public class ExampleStorageConfig : IExampleStorageConfig
    {
        public ExampleStorageConfig(string stateName)
        {
            this.StateName = stateName;
        }

        public string StateName { get; }
    }

    /// <summary>
    /// Creates a storage feature from a configuration
    /// </summary>
    public interface IExampleStorageFactory
    {
        IExampleStorage<TState> Create<TState>(IExampleStorageConfig config);
    }

    /// <summary>
    /// Creates a storage feature by name from a configuration
    /// </summary>
    public interface INamedExampleStorageFactory
    {
        IExampleStorage<TState> Create<TState>(string name, IExampleStorageConfig config);
    }
}
