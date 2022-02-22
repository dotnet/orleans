using System.Collections.Generic;

namespace Orleans.Runtime.Placement
{
    /// <summary>
    /// Describes a placement target, which is a grain as well as context regarding the request which is triggering grain placement.
    /// </summary>
    public readonly struct PlacementTarget
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="PlacementTarget"/> struct.
        /// </summary>
        /// <param name="grainIdentity">The grain being targeted.</param>
        /// <param name="requestContextData">The <see cref="RequestContext"/> dictionary for the request which triggered placement.</param>
        /// <param name="interfaceType">The interface being requested.</param>
        /// <param name="interfaceVersion">The interface version being requested.</param>
        public PlacementTarget(GrainId grainIdentity, Dictionary<string, object> requestContextData, GrainInterfaceType interfaceType, ushort interfaceVersion)
        {
            this.GrainIdentity = grainIdentity;
            this.InterfaceType = interfaceType;
            this.InterfaceVersion = interfaceVersion;
            this.RequestContextData = requestContextData;
        }

        /// <summary>
        /// Gets the grain being targeted.
        /// </summary>
        public GrainId GrainIdentity { get; }

        /// <summary>
        /// Gets the interface type of the interface which is being called on the grain which triggered this placement request.
        /// </summary>
        public GrainInterfaceType InterfaceType { get; }

        /// <summary>
        /// Gets the interface version being requested.
        /// </summary>
        public ushort InterfaceVersion { get; }

        /// <summary>
        /// Gets the <see cref="RequestContext"/> dictionary for the request which triggered placement.
        /// </summary>
        public Dictionary<string, object> RequestContextData { get; }
    }
}
