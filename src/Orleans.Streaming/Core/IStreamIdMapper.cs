using Orleans.Metadata;
using Orleans.Runtime;

namespace Orleans.Streams
{
    /// <summary>
    /// Common interface for component that map a <see cref="StreamId"/> to a <see cref="GrainId.Key"/>
    /// </summary>
    public interface IStreamIdMapper
    {
        /// <summary>
        /// Get the <see cref="GrainId.Key" /> which maps to the provided <see cref="StreamId" />
        /// </summary>
        /// <param name="grainBindings">The grain bindings.</param>
        /// <param name="streamId">The stream identifier.</param>
        /// <returns>The <see cref="GrainId.Key"/> component.</returns>
        IdSpan GetGrainKeyId(GrainBindings grainBindings, StreamId streamId);
    }
}
