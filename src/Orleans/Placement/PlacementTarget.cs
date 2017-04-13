using Orleans.Core;

namespace Orleans.Runtime.Placement
{
    public struct PlacementTarget
    {
        public IGrainIdentity GrainIdentity { get; }

        public int InterfaceId { get; }

        public ushort InterfaceVersion { get; }

        public bool IsClient => GrainIdentity.IsClient;

        public PlacementTarget(IGrainIdentity grainIdentity, int interfaceId, ushort interfaceVersion)
        {
            GrainIdentity = grainIdentity;
            InterfaceId = interfaceId;
            InterfaceVersion = interfaceVersion;
        }
    }
}
