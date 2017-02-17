using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Orleans.Runtime.Placement
{
    internal class PlacementTarget
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
