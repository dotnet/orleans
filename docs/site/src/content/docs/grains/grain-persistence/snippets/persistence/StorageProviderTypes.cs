using Orleans.Runtime;
using Orleans.Storage;

namespace Orleans.Docs.Snippets.Persistence;

// <grain_storage_interface>
/// <summary>
/// Interface to be implemented for a storage able to read and write Orleans grain state data.
/// </summary>
public interface ICustomGrainStorage
{
    /// <summary>Read data function for this storage instance.</summary>
    /// <param name="stateName">Name of the state for this grain</param>
    /// <param name="grainId">Grain ID</param>
    /// <param name="grainState">State data object to be populated for this grain.</param>
    /// <typeparam name="T">The grain state type.</typeparam>
    /// <returns>Completion promise for the Read operation on the specified grain.</returns>
    Task ReadStateAsync<T>(
        string stateName, GrainId grainId, IGrainState<T> grainState);

    /// <summary>Write data function for this storage instance.</summary>
    /// <param name="stateName">Name of the state for this grain</param>
    /// <param name="grainId">Grain ID</param>
    /// <param name="grainState">State data object to be written for this grain.</param>
    /// <typeparam name="T">The grain state type.</typeparam>
    /// <returns>Completion promise for the Write operation on the specified grain.</returns>
    Task WriteStateAsync<T>(
        string stateName, GrainId grainId, IGrainState<T> grainState);

    /// <summary>Delete / Clear data function for this storage instance.</summary>
    /// <param name="stateName">Name of the state for this grain</param>
    /// <param name="grainId">Grain ID</param>
    /// <param name="grainState">Copy of last-known state data object for this grain.</param>
    /// <typeparam name="T">The grain state type.</typeparam>
    /// <returns>Completion promise for the Delete operation on the specified grain.</returns>
    Task ClearStateAsync<T>(
        string stateName, GrainId grainId, IGrainState<T> grainState);
}
// </grain_storage_interface>

// <inconsistent_state_exception>
public class InconsistentStateException : OrleansException
{
    public InconsistentStateException(
    string message,
    string storedEtag,
    string currentEtag,
    Exception storageException)
        : base(message, storageException)
    {
        StoredEtag = storedEtag;
        CurrentEtag = currentEtag;
    }

    public InconsistentStateException(
        string storedEtag,
        string currentEtag,
        Exception storageException)
        : this(storageException.Message, storedEtag, currentEtag, storageException)
    {
    }

    /// <summary>The Etag value currently held in persistent storage.</summary>
    public string StoredEtag { get; }

    /// <summary>The Etag value currently held in memory, and attempting to be updated.</summary>
    public string CurrentEtag { get; }
}
// </inconsistent_state_exception>

// <grain_base_methods>
public abstract class GrainBaseExample<TState>
{
    protected virtual Task ReadStateAsync() { return Task.CompletedTask; }
    protected virtual Task WriteStateAsync() { return Task.CompletedTask; }
    protected virtual Task ClearStateAsync() { return Task.CompletedTask; }
}
// </grain_base_methods>
