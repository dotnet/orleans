namespace Orleans.Runtime.Placement
{
    internal struct PlacementTarget
    {
        public GrainId GrainId { get; }

        public int InterfaceId { get; }

        public ushort InterfaceVersion { get; }

        public bool IsClient => GrainId.IsClient;

        public PlacementTarget(GrainId grainId, int interfaceId, ushort interfaceVersion)
        {
            GrainId = grainId;
            InterfaceId = interfaceId;
            InterfaceVersion = interfaceVersion;
        }
    }
}
