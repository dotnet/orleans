using System.Collections.Generic;

namespace Orleans.Runtime.Placement
{
    public struct PlacementTarget
    {
        public GrainId GrainIdentity { get; }

        public GrainInterfaceId InterfaceId { get; }

        public ushort InterfaceVersion { get; }

        public Dictionary<string, object> RequestContextData { get; }

        public PlacementTarget(GrainId grainIdentity, Dictionary<string, object> requestContextData, GrainInterfaceId interfaceId, ushort interfaceVersion)
        {
            this.GrainIdentity = grainIdentity;
            this.InterfaceId = interfaceId;
            this.InterfaceVersion = interfaceVersion;
            this.RequestContextData = requestContextData;
        }
    }
}
