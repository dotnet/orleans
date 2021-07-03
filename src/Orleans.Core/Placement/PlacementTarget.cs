using System.Collections.Generic;

namespace Orleans.Runtime.Placement
{
    public readonly struct PlacementTarget
    {
        public GrainId GrainIdentity { get; }

        public GrainInterfaceType InterfaceType { get; }

        public ushort InterfaceVersion { get; }

        public Dictionary<string, object> RequestContextData { get; }

        public PlacementTarget(GrainId grainIdentity, Dictionary<string, object> requestContextData, GrainInterfaceType interfaceType, ushort interfaceVersion)
        {
            this.GrainIdentity = grainIdentity;
            this.InterfaceType = interfaceType;
            this.InterfaceVersion = interfaceVersion;
            this.RequestContextData = requestContextData;
        }
    }
}
