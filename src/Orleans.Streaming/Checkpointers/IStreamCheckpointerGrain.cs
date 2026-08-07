using System.Threading;
using System.Threading.Tasks;
using Orleans.Metadata;

namespace Orleans.Streams
{
    /// <summary>
    /// Stores a persistent stream queue checkpoint.
    /// </summary>
    [DefaultGrainType("stream.checkpoint")]
    public interface IStreamCheckpointerGrain : IGrainWithStringKey
    {
        /// <summary>
        /// Loads the checkpoint.
        /// </summary>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The checkpoint.</returns>
        ValueTask<string> Load(CancellationToken cancellationToken);

        /// <summary>
        /// Updates the checkpoint if the persisted value matches the expected value.
        /// </summary>
        /// <param name="offset">The offset.</param>
        /// <param name="expectedCheckpoint">The expected persisted checkpoint.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The persisted checkpoint after the update attempt.</returns>
        ValueTask<string> Update(
            string offset,
            string expectedCheckpoint,
            CancellationToken cancellationToken);
    }

    internal interface IConfiguredStreamCheckpointerGrain : IStreamCheckpointerGrain
    {
    }
}
