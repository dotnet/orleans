using System.Threading;
using System.Threading.Tasks;

namespace Orleans.Streams;

/// <summary>
/// Stores a persistent stream checkpoint using conditional updates.
/// </summary>
public interface IStreamCheckpointStore
{
    /// <summary>
    /// Loads the current checkpoint and its version.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The current checkpoint state.</returns>
    ValueTask<StreamCheckpointStoreState> Load(CancellationToken cancellationToken);

    /// <summary>
    /// Updates the checkpoint if <paramref name="expectedVersion"/> matches the persisted version.
    /// </summary>
    /// <param name="checkpoint">The checkpoint to persist.</param>
    /// <param name="expectedVersion">The expected persisted version.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>
    /// The persisted state after the update attempt. If the expected version did not match,
    /// the returned state contains the conflicting persisted checkpoint and version.
    /// </returns>
    ValueTask<StreamCheckpointStoreState> Update(
        string checkpoint,
        string expectedVersion,
        CancellationToken cancellationToken);
}
