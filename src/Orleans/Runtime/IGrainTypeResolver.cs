using System;

namespace Orleans.Runtime
{
    internal interface IGrainTypeResolver
    {
        bool TryGetGrainClassData(Type grainInterfaceType, out GrainClassData implementation, string grainClassNamePrefix);
        bool TryGetGrainClassData(int grainInterfaceId, out GrainClassData implementation, string grainClassNamePrefix);
        bool TryGetGrainClassData(string grainImplementationClassName, out GrainClassData implementation);
        bool IsUnordered(int grainTypeCode);
        string GetLoadedGrainAssemblies();
    }
}