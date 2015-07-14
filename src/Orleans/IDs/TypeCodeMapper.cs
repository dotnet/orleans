/*
Project Orleans Cloud Service SDK ver. 1.0
 
Copyright (c) Microsoft Corporation
 
All rights reserved.
 
MIT License

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and 
associated documentation files (the ""Software""), to deal in the Software without restriction,
including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense,
and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so,
subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED *AS IS*, WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO
THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS
OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT,
TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
*/

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
