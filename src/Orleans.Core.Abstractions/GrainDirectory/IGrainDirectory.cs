using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;

namespace Orleans.GrainDirectory
{
    /// <summary>
    /// Interface for grain directory implementations
    /// </summary>
    public interface IGrainDirectory
    {
        /// <summary>
        /// Register a <see cref="GrainAddress"/> entry in the directory.
        /// Only one <see cref="GrainAddress"/> per <see cref="GrainAddress.GrainId"/> can be registered. If there is already an
        /// existing entry, the directory will not override it.
        /// </summary>
        /// <param name="address">The <see cref="GrainAddress"/> to register</param>
        /// <returns>The <see cref="GrainAddress"/> that is effectively registered in the directory.</returns>
        Task<GrainAddress> Register(GrainAddress address);

        /// <summary>
        /// Unregister a <see cref="GrainAddress"/> entry in the directory.
        /// </summary>
        /// <param name="address">The <see cref="GrainAddress"/> to unregister</param>
        Task Unregister(GrainAddress address);

        /// <summary>
        /// Unregister a batch of <see cref="GrainAddress"/> entries in the directory.
        /// </summary>
        /// <param name="addresses">The grains to unregister</param>
        Task UnregisterMany(List<GrainAddress> addresses);

        /// <summary>
        /// Lookup for a <see cref="GrainAddress"/> for a given Grain ID.
        /// </summary>
        /// <param name="grainId">The Grain ID to lookup</param>
        /// <returns>The <see cref="GrainAddress"/> entry found in the directory, if any</returns>
        Task<GrainAddress> Lookup(string grainId);
    }

    /// <summary>
    /// Represents an entry in a <see cref="IGrainDirectory"/>
    /// </summary>
    public class GrainAddress
    {
        /// <summary>
        /// Address of the silo where the grain activation lives
        /// </summary>
        public string SiloAddress { get; set; }

        /// <summary>
        /// Identifier of the Grain
        /// </summary>
        public string GrainId { get; set; }

        /// <summary>
        /// Id of the specific Grain activation
        /// </summary>
        public string ActivationId { get; set; }
    }
}
