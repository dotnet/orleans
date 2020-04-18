using System;
using Orleans.CodeGeneration;
using Orleans.Metadata;

namespace Orleans.Runtime.Metadata
{
    internal class LegacyGrainTypeResolver : IGrainTypeProvider
    {
        public bool TryGetGrainType(Type grainClass, out GrainType grainType)
        {
            if (LegacyGrainId.IsLegacyGrainType(grainClass))
            {
                grainType = LegacyGrainId.GetGrainId(GrainInterfaceUtils.GetGrainClassTypeCode(grainClass), Guid.Empty).ToGrainId().Type;
                return true;
            }

            grainType = default;
            return false;
        }
    }
}
