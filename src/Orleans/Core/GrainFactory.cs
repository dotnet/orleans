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
using System.Collections.Concurrent;

using Orleans.Runtime;

namespace Orleans
{
    

    /// <summary>
    /// Factory for accessing grains.
    /// </summary>
    public static class GrainFactory
    {
        /// <summary>
        /// Gets a reference to a grain.
        /// </summary>
        /// <typeparam name="TGrainInterface">The interface to get.</typeparam>
        /// <param name="primaryKey">The primary key of the grain.</param>
        /// <param name="grainClassNamePrefix">An optional class name prefix used to find the runtime type of the grain.</param>
        /// <returns></returns>
        public static TGrainInterface GetGrain<TGrainInterface>(Guid primaryKey, string grainClassNamePrefix = null)
            where TGrainInterface : IGrainWithGuidKey
        {
            return Cast<TGrainInterface>(
                _MakeGrainReference(
                    baseTypeCode => ComposeGrainId(baseTypeCode, primaryKey, typeof(TGrainInterface)),
                    typeof(TGrainInterface),
                    grainClassNamePrefix));
        }

        /// <summary>
        /// Gets a reference to a grain.
        /// </summary>
        /// <typeparam name="TGrainInterface">The interface to get.</typeparam>
        /// <param name="primaryKey">The primary key of the grain.</param>
        /// <param name="grainClassNamePrefix">An optional class name prefix used to find the runtime type of the grain.</param>
        /// <returns></returns>
        public static TGrainInterface GetGrain<TGrainInterface>(long primaryKey, string grainClassNamePrefix = null)
            where TGrainInterface : IGrainWithIntegerKey
        {
            return Cast<TGrainInterface>(
                _MakeGrainReference(
                    baseTypeCode => ComposeGrainId(baseTypeCode, primaryKey, typeof(TGrainInterface)),
                    typeof(TGrainInterface),
                    grainClassNamePrefix));
        }

        /// <summary>
        /// Gets a reference to a grain.
        /// </summary>
        /// <typeparam name="TGrainInterface">The interface to get.</typeparam>
        /// <param name="primaryKey">The primary key of the grain.</param>
        /// <param name="grainClassNamePrefix">An optional class name prefix used to find the runtime type of the grain.</param>
        /// <returns></returns>
        public static TGrainInterface GetGrain<TGrainInterface>(string primaryKey, string grainClassPrefixName = null)
            where TGrainInterface : IGrainWithStringKey
        {
            return Cast<TGrainInterface>(
                _MakeGrainReference(
                    baseTypeCode => ComposeGrainId(baseTypeCode, primaryKey, typeof(TGrainInterface)),
                    typeof(TGrainInterface)));
        }

        private static IAddressable _MakeGrainReference(
            Func<int, GrainId> getGrainId,
            Type interfaceType,
            string grainClassNamePrefix = null)
        {
            CheckRuntimeEnvironmentSetup();
            if (!CodeGeneration.GrainInterfaceData.IsGrainType(interfaceType))
            {
                throw new ArgumentException("Cannot fabricate grain-reference for non-grain type: " + interfaceType.FullName);
            }
            GrainId grainId = getGrainId(GetImplementationTypeCode(interfaceType, grainClassNamePrefix));
            return GrainReference.FromGrainId(grainId, interfaceType.IsGenericType ? interfaceType.UnderlyingSystemType.FullName : null);
        }

        #region Interface Casting
        private static readonly ConcurrentDictionary<Type, Func<IAddressable, object>> casters
            = new ConcurrentDictionary<Type, Func<IAddressable, object>>();

        internal static TGrainInterface Cast<TGrainInterface>(IAddressable grain)
        {
            var interfaceType = typeof(TGrainInterface);
            Func<IAddressable, object> caster;

            if (!casters.TryGetValue(interfaceType, out caster))
            {
                caster = casters.GetOrAdd(interfaceType, CreateCaster);
            }

            return (TGrainInterface)caster(grain);
        }

        private static Func<IAddressable, object> CreateCaster(Type interfaceType)
        {
            var grainFactoryName = TypeUtils.GetSimpleTypeName(interfaceType, t => false);
            if (interfaceType.IsInterface && grainFactoryName.Length > 1 && grainFactoryName[0] == 'I' && Char.IsUpper(grainFactoryName[1]))
            {
                grainFactoryName = grainFactoryName.Substring(1);
            }

            grainFactoryName = grainFactoryName + "Factory";

            if (interfaceType.IsGenericType)
            {
                grainFactoryName = grainFactoryName + "`" + interfaceType.GetGenericArguments().Length;
            }

            // expect grain reference to be generated into same namespace that interface is declared within
            if (!string.IsNullOrEmpty(interfaceType.Namespace))
            {
                grainFactoryName = interfaceType.Namespace + "." + grainFactoryName;
            }

            var grainFactoryType = interfaceType.Assembly.GetType(grainFactoryName);
            if (grainFactoryType == null)
            {
                throw new InvalidOperationException(string.Format("Cannot find generated grain reference type for interface '{0}'", interfaceType));
            }

            if (interfaceType.IsGenericType)
            {
                grainFactoryType = grainFactoryType.MakeGenericType(interfaceType.GetGenericArguments());
            }

            var castMethod = grainFactoryType.GetMethod("Cast", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);
            if (castMethod == null)
            {
                throw new InvalidOperationException(string.Format("Cannot find grain reference cast method for interface '{0}'", interfaceType));
            }

            return (Func<IAddressable, object>)castMethod.CreateDelegate(typeof(Func<IAddressable, object>));
        }
        #endregion

        #region Utility functions
        /// <summary>
        /// Check the current runtime environment has been setup and initialized correctly.
        /// Throws InvalidOperationException if current runtime environment is not initialized.
        /// </summary>
        private static void CheckRuntimeEnvironmentSetup()
        {
            if (RuntimeClient.Current == null)
            {
                var msg = "Orleans runtime environment is not set up (RuntimeClient.Current==null). If you are running on the client, perhaps you are missing a call to Client.Initialize(...) ? " +
                            "If you are running on the silo, perhaps you are trying to send a message or create a grain reference not within Orleans thread or from within grain constructor?";
                throw new InvalidOperationException(msg);
            }
        }

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

        internal static int GetImplementationTypeCode(string grainImplementationClassName)
        {
            int typeCode;
            IGrainTypeResolver grainTypeResolver = RuntimeClient.Current.GrainTypeResolver;
            if (!grainTypeResolver.TryGetGrainTypeCode(grainImplementationClassName, out typeCode))
                throw new ArgumentException(String.Format("Cannot find a type code for an implementation grain class: {0}. Make sure the grain assembly was correctly deployed and loaded in the silo.", grainImplementationClassName));

            return typeCode;
        }

        private static GrainId ComposeGrainId(int baseTypeCode, Guid primaryKey, Type interfaceType)
        {
            return GrainId.GetGrainId(ComposeGenericTypeCode(interfaceType, baseTypeCode), primaryKey);
        }

        private static GrainId ComposeGrainId(int baseTypeCode, long primaryKey, Type interfaceType)
        {
            return GrainId.GetGrainId(ComposeGenericTypeCode(interfaceType, baseTypeCode), primaryKey);
        }

        private static GrainId ComposeGrainId(int baseTypeCode, string primaryKey, Type interfaceType)
        {
            return GrainId.GetGrainId(ComposeGenericTypeCode(interfaceType, baseTypeCode), primaryKey);
        }      

        private static long ComposeGenericTypeCode(Type interfaceType, int baseTypeCode)
        {
            if (!interfaceType.IsGenericType)
                return baseTypeCode;

            string args = TypeUtils.GetGenericTypeArgs(interfaceType.GetGenericArguments(), t => true);
            int hash = Utils.CalculateIdHash(args);
            return (((long)(hash & 0x00FFFFFF)) << 32) + baseTypeCode;
        }
        #endregion
    }
}