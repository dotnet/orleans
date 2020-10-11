using Orleans.Metadata;
using Orleans.Runtime;

namespace Orleans.Streams
{
    /// <summary>
    /// Common interface for component that map a StreamId to a GrainId
    /// </summary>
    public interface IStreamIdMapper
    {
        /// <summary>
        /// Get the corresponding GrainId from the StreamId
        /// </summary>
        IdSpan GetGrainKeyId(GrainBindings grainBindings, StreamId streamId);
    }
}
