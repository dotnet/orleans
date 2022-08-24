using Orleans.Metadata;
using Orleans.Runtime;

namespace Orleans.BroadcastChannel
{
    /// <summary>
    /// Common interface for component that map a <see cref="ChannelId"/> to a <see cref="GrainId.Key"/>
    /// </summary>
    public interface IChannelIdMapper
    {
        /// <summary>
        /// Get the <see cref="GrainId.Key" /> which maps to the provided <see cref="ChannelId" />
        /// </summary>
        /// <param name="grainBindings">The grain bindings.</param>
        /// <param name="streamId">The stream identifier.</param>
        /// <returns>The <see cref="GrainId.Key"/> component.</returns>
        IdSpan GetGrainKeyId(GrainBindings grainBindings, ChannelId streamId);
    }
}
