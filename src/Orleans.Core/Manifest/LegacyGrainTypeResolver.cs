using System;
using Orleans.CodeGeneration;
using Orleans.Runtime;

namespace Orleans.Metadata
{
    internal class LegacyGrainTypeResolver : IGrainTypeProvider
    {
        public bool TryGetGrainType(Type grainClass, out GrainType grainType)
        {
            if (!LegacyGrainId.IsLegacyGrainType(grainClass))
            {
                grainType = default;
                return false;
            }

            Type canonicalGrainClass;
            if (grainClass.IsConstructedGenericType)
            {
                canonicalGrainClass = grainClass.GetGenericTypeDefinition();
            }
            else
            {
                canonicalGrainClass = grainClass;
            }

            var isKeyExt = LegacyGrainId.IsLegacyKeyExtGrainType(canonicalGrainClass);
            var typeCode = GrainInterfaceUtils.GetGrainClassTypeCode(canonicalGrainClass);
            grainType = LegacyGrainId.GetGrainType(typeCode, isKeyExt);

            if (grainClass.IsGenericType)
            {
                grainType = GrainType.Create($"{grainType}`{canonicalGrainClass.GetGenericArguments().Length}");
            }

            return true;
        }
    }
}
