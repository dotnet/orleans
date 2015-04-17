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

﻿using System;

namespace Orleans.Runtime
{
    /// <summary>
    /// Type Code Mapping functions.
    /// </summary>
    internal static class TypeCodeMapper
    {
        internal static int GetImplementationTypeCode(Type interfaceType, string grainClassNamePrefix = null)
        {
            int typeCode;
            IGrainTypeResolver grainTypeResolver = RuntimeClient.Current.GrainTypeResolver;
            if (!grainTypeResolver.TryGetGrainTypeCode(interfaceType, out typeCode, grainClassNamePrefix))
            {
                var loadedAssemblies = grainTypeResolver.GetLoadedGrainAssemblies();
                throw new ArgumentException(
                    String.Format("Cannot find a type code for an implementation class for grain interface: {0}{2}. Make sure the grain assembly was correctly deployed and loaded in the silo.{1}",
                                  interfaceType,
                                  String.IsNullOrEmpty(loadedAssemblies) ? String.Empty : String.Format(" Loaded grain assemblies: {0}", loadedAssemblies),
                                  String.IsNullOrEmpty(grainClassNamePrefix) ? String.Empty : ", grainClassNamePrefix=" + grainClassNamePrefix));
            }
            return typeCode;
        }

        internal static int GetImplementationTypeCode(int interfaceId, string grainClassNamePrefix = null)
        {
            int typeCode;
            var grainTypeResolver = RuntimeClient.Current.GrainTypeResolver;
            if (grainTypeResolver.TryGetGrainTypeCode(interfaceId, out typeCode, grainClassNamePrefix)) return typeCode;

            var loadedAssemblies = grainTypeResolver.GetLoadedGrainAssemblies();
            throw new ArgumentException(
                String.Format("Cannot find a type code for an implementation class for grain interface: {0}{2}. Make sure the grain assembly was correctly deployed and loaded in the silo.{1}",
                    interfaceId,
                    String.IsNullOrEmpty(loadedAssemblies) ? String.Empty : String.Format(" Loaded grain assemblies: {0}", loadedAssemblies),
                    String.IsNullOrEmpty(grainClassNamePrefix) ? String.Empty : ", grainClassNamePrefix=" + grainClassNamePrefix));
        }

        internal static int GetImplementationTypeCode(string grainImplementationClassName)
        {
            int typeCode;
            var grainTypeResolver = RuntimeClient.Current.GrainTypeResolver;
            if (!grainTypeResolver.TryGetGrainTypeCode(grainImplementationClassName, out typeCode))
                throw new ArgumentException(String.Format("Cannot find a type code for an implementation grain class: {0}. Make sure the grain assembly was correctly deployed and loaded in the silo.", grainImplementationClassName));

            return typeCode;
        }

        internal static GrainId ComposeGrainId(int baseTypeCode, Guid primaryKey, Type interfaceType, string keyExt = null)
        {
            return GrainId.GetGrainId(ComposeGenericTypeCode(interfaceType, baseTypeCode), 
                primaryKey, keyExt);
        }

        internal static GrainId ComposeGrainId(int baseTypeCode, long primaryKey, Type interfaceType, string keyExt = null)
        {
            return GrainId.GetGrainId(ComposeGenericTypeCode(interfaceType, baseTypeCode),
                primaryKey, keyExt);
        }

        internal static GrainId ComposeGrainId(int baseTypeCode, string primaryKey, Type interfaceType)
        {
            return GrainId.GetGrainId(ComposeGenericTypeCode(interfaceType, baseTypeCode), primaryKey);
        }

        internal static long ComposeGenericTypeCode(Type interfaceType, int baseTypeCode)
        {
            if (!interfaceType.IsGenericType)
                return baseTypeCode;

            string args = TypeUtils.GetGenericTypeArgs(interfaceType.GetGenericArguments(), t => true);
            int hash = Utils.CalculateIdHash(args);
            return (((long)(hash & 0x00FFFFFF)) << 32) + baseTypeCode;
        }
    }
}
