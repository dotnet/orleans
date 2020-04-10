using System.Collections.Generic;

namespace Orleans.Runtime.Placement
{
    public struct PlacementTarget
    {
        public GrainId GrainIdentity { get; }

        public int InterfaceId { get; }

        public ushort InterfaceVersion { get; }

        public bool IsClient => GrainIdentity.IsClient();

        public Dictionary<string, object> RequestContextData { get; }

        public PlacementTarget(GrainId grainIdentity, Dictionary<string, object> requestContextData, int interfaceId, ushort interfaceVersion)
        {
            this.GrainIdentity = grainIdentity;
            this.InterfaceId = interfaceId;
            this.InterfaceVersion = interfaceVersion;
            this.RequestContextData = requestContextData;
        }
    }
}
