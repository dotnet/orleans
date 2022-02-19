
namespace Orleans.Runtime
{
    /// <summary>
    /// Configuration for persistent state.
    /// </summary>
    /// <seealso cref="IPersistentState{TState}"/>
    public interface IPersistentStateConfiguration
    {
        /// <summary>
        /// Gets the name of the state.
        /// </summary>
        /// <value>The name of the state.</value>
        string StateName { get; }

        /// <summary>
        /// Gets the name of the storage provider.
        /// </summary>
        /// <value>The name of the storage provider.</value>
        string StorageName { get; }
    }
}
