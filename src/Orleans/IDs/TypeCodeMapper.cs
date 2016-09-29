using System;

namespace Orleans.Runtime
{
    /// <summary>
    /// Type Code Mapping functions.
    /// </summary>
    internal static class TypeCodeMapper
    {
        internal static GrainClassData GetImplementation(Type interfaceType, string grainClassNamePrefix = null)
        {
            GrainClassData implementation;
            IGrainTypeResolver grainTypeResolver = RuntimeClient.Current.GrainTypeResolver;
            if (!grainTypeResolver.TryGetGrainClassData(interfaceType, out implementation, grainClassNamePrefix))
            {
                var loadedAssemblies = grainTypeResolver.GetLoadedGrainAssemblies();
                throw new ArgumentException(
                    String.Format("Cannot find an implementation class for grain interface: {0}{2}. Make sure the grain assembly was correctly deployed and loaded in the silo.{1}",
                                  interfaceType,
                                  String.IsNullOrEmpty(loadedAssemblies) ? String.Empty : String.Format(" Loaded grain assemblies: {0}", loadedAssemblies),
                                  String.IsNullOrEmpty(grainClassNamePrefix) ? String.Empty : ", grainClassNamePrefix=" + grainClassNamePrefix));
            }
            return implementation;
        }

        internal static GrainClassData GetImplementation(int interfaceId, string grainClassNamePrefix = null)
        {
            GrainClassData implementation;
            var grainTypeResolver = RuntimeClient.Current.GrainTypeResolver;
            if (grainTypeResolver.TryGetGrainClassData(interfaceId, out implementation, grainClassNamePrefix)) return implementation;

            var loadedAssemblies = grainTypeResolver.GetLoadedGrainAssemblies();
            throw new ArgumentException(
                String.Format("Cannot find an implementation class for grain interface: {0}{2}. Make sure the grain assembly was correctly deployed and loaded in the silo.{1}",
                    interfaceId,
                    String.IsNullOrEmpty(loadedAssemblies) ? String.Empty : String.Format(" Loaded grain assemblies: {0}", loadedAssemblies),
                    String.IsNullOrEmpty(grainClassNamePrefix) ? String.Empty : ", grainClassNamePrefix=" + grainClassNamePrefix));
        }

        internal static GrainClassData GetImplementation(string grainImplementationClassName)
        {
            GrainClassData implementation;
            var grainTypeResolver = RuntimeClient.Current.GrainTypeResolver;
            if (!grainTypeResolver.TryGetGrainClassData(grainImplementationClassName, out implementation))
                throw new ArgumentException(String.Format("Cannot find an implementation grain class: {0}. Make sure the grain assembly was correctly deployed and loaded in the silo.", grainImplementationClassName));

            return implementation;
        }

        internal static GrainId ComposeGrainId(GrainClassData implementation, Guid primaryKey, Type interfaceType, string keyExt = null)
        {
            return GrainId.GetGrainId(implementation.GetTypeCode(interfaceType), primaryKey, keyExt);
        }

        internal static GrainId ComposeGrainId(GrainClassData implementation, long primaryKey, Type interfaceType, string keyExt = null)
        {
            return GrainId.GetGrainId(implementation.GetTypeCode(interfaceType), primaryKey, keyExt);
        }

        internal static GrainId ComposeGrainId(GrainClassData implementation, string primaryKey, Type interfaceType)
        {
            return GrainId.GetGrainId(implementation.GetTypeCode(interfaceType), primaryKey);
        }

    }
}
