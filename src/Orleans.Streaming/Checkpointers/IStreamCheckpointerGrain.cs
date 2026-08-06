using System.Threading;
using System.Threading.Tasks;
using Orleans.Metadata;

namespace Orleans.Streams
{
    /// <summary>
    /// Stores a persistent stream queue checkpoint.
    /// </summary>
    [DefaultGrainType("streamcheckpointergrain")]
    public interface IStreamCheckpointerGrain : IGrainWithStringKey
    {
        /// <summary>
        /// Loads the checkpoint.
        /// </summary>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The checkpoint.</returns>
        ValueTask<string> Load(CancellationToken cancellationToken);

        /// <summary>
        /// Updates the checkpoint.
        /// </summary>
        /// <param name="offset">The offset.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        ValueTask Update(string offset, CancellationToken cancellationToken);
    }

    internal interface IConfiguredStreamCheckpointerGrain : IStreamCheckpointerGrain
    {
    }
}
