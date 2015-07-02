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
using System.Collections.Concurrent;
using System.Collections.Generic;
using Orleans.CodeGeneration;
using Orleans.Core;
using Orleans.Runtime;

namespace Orleans
{
    using System.Reflection;
    using System.Threading.Tasks;

    /// <summary>
    /// Factory for accessing grains.
    /// </summary>
    public class GrainFactory : IGrainFactory
    {
        /// <summary>
        /// The collection of <see cref="IGrainObserver"/> <c>CreateObjectReference</c> delegates.
        /// </summary>
        private readonly ConcurrentDictionary<TypeInfo, Delegate> referenceCreators =
            new ConcurrentDictionary<TypeInfo, Delegate>();

        /// <summary>
        /// The collection of <see cref="IGrainObserver"/> <c>DeleteObjectReference</c> delegates.
        /// </summary>
        private readonly ConcurrentDictionary<TypeInfo, Delegate> referenceDestoyers =
            new ConcurrentDictionary<TypeInfo, Delegate>();

        // Make this internal so that client code is forced to access the IGrainFactory using the 
        // GrainClient (to make sure they don't forget to initialize the client).
        internal GrainFactory()
        {
        }

        /// <summary>
        /// Gets a reference to a grain.
        /// </summary>
        /// <typeparam name="TGrainInterface">The interface to get.</typeparam>
        /// <param name="primaryKey">The primary key of the grain.</param>
        /// <param name="grainClassNamePrefix">An optional class name prefix used to find the runtime type of the grain.</param>
        /// <returns></returns>
        public TGrainInterface GetGrain<TGrainInterface>(Guid primaryKey, string grainClassNamePrefix = null) where TGrainInterface : IGrainWithGuidKey
        {
            return Cast<TGrainInterface>(
                GrainFactoryBase.MakeGrainReference_FromType(
                    baseTypeCode => TypeCodeMapper.ComposeGrainId(baseTypeCode, primaryKey, typeof(TGrainInterface)),
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
        public TGrainInterface GetGrain<TGrainInterface>(long primaryKey, string grainClassNamePrefix = null) where TGrainInterface : IGrainWithIntegerKey
        {
            return Cast<TGrainInterface>(
                GrainFactoryBase.MakeGrainReference_FromType(
                    baseTypeCode => TypeCodeMapper.ComposeGrainId(baseTypeCode, primaryKey, typeof(TGrainInterface)),
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
        public TGrainInterface GetGrain<TGrainInterface>(string primaryKey, string grainClassNamePrefix = null) where TGrainInterface : IGrainWithStringKey
        {
            return Cast<TGrainInterface>(
                GrainFactoryBase.MakeGrainReference_FromType(
                    baseTypeCode => TypeCodeMapper.ComposeGrainId(baseTypeCode, primaryKey, typeof(TGrainInterface)),
                    typeof(TGrainInterface),
                    grainClassNamePrefix));
        }

        /// <summary>
        /// Gets a reference to a grain.
        /// </summary>
        /// <typeparam name="TGrainInterface">The interface to get.</typeparam>
        /// <param name="primaryKey">The primary key of the grain.</param>
        /// <param name="keyExtension">The key extention of the grain.</param>
        /// <param name="grainClassNamePrefix">An optional class name prefix used to find the runtime type of the grain.</param>
        /// <returns></returns>
        public TGrainInterface GetGrain<TGrainInterface>(Guid primaryKey, string keyExtension, string grainClassNamePrefix = null)
            where TGrainInterface : IGrainWithGuidCompoundKey
        {
            GrainFactoryBase.DisallowNullOrWhiteSpaceKeyExtensions(keyExtension);

            return Cast<TGrainInterface>(
                GrainFactoryBase.MakeGrainReference_FromType(
                    baseTypeCode => TypeCodeMapper.ComposeGrainId(baseTypeCode, primaryKey, typeof(TGrainInterface), keyExtension),
                    typeof(TGrainInterface),
                    grainClassNamePrefix));
        }

        /// <summary>
        /// Gets a reference to a grain.
        /// </summary>
        /// <typeparam name="TGrainInterface">The interface to get.</typeparam>
        /// <param name="primaryKey">The primary key of the grain.</param>
        /// <param name="keyExtension">The key extention of the grain.</param>
        /// <param name="grainClassNamePrefix">An optional class name prefix used to find the runtime type of the grain.</param>
        /// <returns></returns>
        public TGrainInterface GetGrain<TGrainInterface>(long primaryKey, string keyExtension, string grainClassNamePrefix = null)
            where TGrainInterface : IGrainWithIntegerCompoundKey
        {
            GrainFactoryBase.DisallowNullOrWhiteSpaceKeyExtensions(keyExtension);

            return Cast<TGrainInterface>(
                GrainFactoryBase.MakeGrainReference_FromType(
                    baseTypeCode => TypeCodeMapper.ComposeGrainId(baseTypeCode, primaryKey, typeof(TGrainInterface), keyExtension),
                    typeof(TGrainInterface),
                    grainClassNamePrefix));
        }

        /// <summary>
        /// Creates a reference to the provided <paramref name="obj"/>.
        /// </summary>
        /// <typeparam name="TGrainObserverInterface">
        /// The specific <see cref="IGrainObserver"/> type of <paramref name="obj"/>.
        /// </typeparam>
        /// <param name="obj">The object to create a reference to.</param>
        /// <returns>The reference to <paramref name="obj"/>.</returns>
        public Task<TGrainObserverInterface> CreateObjectReference<TGrainObserverInterface>(IGrainObserver obj)
            where TGrainObserverInterface : IGrainObserver
        {
            return CreateObjectReferenceImpl<TGrainObserverInterface>(obj);
        }

        internal Task<TGrainObserverInterface> CreateObjectReference<TGrainObserverInterface>(IAddressable obj)
                where TGrainObserverInterface : IAddressable
        {
            return CreateObjectReferenceImpl<TGrainObserverInterface>(obj);
        }

        private Task<TGrainObserverInterface> CreateObjectReferenceImpl<TGrainObserverInterface>(object obj)
        {
            var interfaceTypeInfo = typeof(TGrainObserverInterface).GetTypeInfo();
            if (!interfaceTypeInfo.IsInterface)
            {
                throw new ArgumentException(
                    string.Format(
                        "The provided type parameter must be an interface. '{0}' is not an interface.",
                        interfaceTypeInfo.FullName));
            }

            if (!interfaceTypeInfo.IsInstanceOfType(obj))
            {
                throw new ArgumentException(
                    string.Format("The provided object must implement '{0}'.", interfaceTypeInfo.FullName),
                    "obj");
            }

            Delegate creator;

            if (!referenceCreators.TryGetValue(interfaceTypeInfo, out creator))
            {
                creator = referenceCreators.GetOrAdd(interfaceTypeInfo, MakeCreateObjectReferenceDelegate);
            }

            var resultTask = ((Func<TGrainObserverInterface, Task<TGrainObserverInterface>>)creator)((TGrainObserverInterface)obj);
            return resultTask;
        }

        /// <summary>
        /// Deletes the provided object reference.
        /// </summary>
        /// <typeparam name="TGrainObserverInterface">
        /// The specific <see cref="IGrainObserver"/> type of <paramref name="obj"/>.
        /// </typeparam>
        /// <param name="obj">The reference being deleted.</param>
        /// <returns>A <see cref="Task"/> representing the work performed.</returns>
        public Task DeleteObjectReference<TGrainObserverInterface>(
            IGrainObserver obj) where TGrainObserverInterface : IGrainObserver
        {
            var interfaceTypeInfo = typeof(TGrainObserverInterface).GetTypeInfo();
            if (!interfaceTypeInfo.IsInterface)
            {
                throw new ArgumentException(
                    string.Format(
                        "The provided type parameter must be an interface. '{0}' is not an interface.",
                        interfaceTypeInfo.FullName));
            }

            if (!interfaceTypeInfo.IsInstanceOfType(obj))
            {
                throw new ArgumentException(
                    string.Format("The provided object must implement '{0}'.", interfaceTypeInfo.FullName),
                    "obj");
            }

            Delegate destroyer;

            if (!referenceDestoyers.TryGetValue(interfaceTypeInfo, out destroyer))
            {
                destroyer = referenceDestoyers.GetOrAdd(interfaceTypeInfo, MakeDeleteObjectReferenceDelegate);
            }

            return ((Func<TGrainObserverInterface, Task>)destroyer)((TGrainObserverInterface)obj);
        }

        #region IGrainObserver Methods
        private static Delegate MakeCreateObjectReferenceDelegate(TypeInfo interfaceTypeInfo)
        {
            var delegateType = typeof(Func<,>).MakeGenericType(interfaceTypeInfo, typeof(Task<>).MakeGenericType(interfaceTypeInfo));
            return MakeFactoryDelegate(interfaceTypeInfo, "CreateObjectReference", delegateType);
        }

        private static Delegate MakeDeleteObjectReferenceDelegate(TypeInfo interfaceTypeInfo)
        {
            var delegateType = typeof(Func<,>).MakeGenericType(interfaceTypeInfo, typeof(Task));
            return MakeFactoryDelegate(interfaceTypeInfo, "DeleteObjectReference", delegateType);
        }
        #endregion

        #region Interface Casting
        private readonly ConcurrentDictionary<TypeInfo, Func<IAddressable, object>> casters
            = new ConcurrentDictionary<TypeInfo, Func<IAddressable, object>>();

        internal TGrainInterface Cast<TGrainInterface>(IAddressable grain)
        {
            var interfaceTypeInfo = typeof(TGrainInterface).GetTypeInfo();
            Func<IAddressable, object> caster;

            if (!casters.TryGetValue(interfaceTypeInfo, out caster))
            {
                caster = casters.GetOrAdd(interfaceTypeInfo, MakeCaster);
            }

            return (TGrainInterface)caster(grain);
        }

        private static Func<IAddressable, object> MakeCaster(TypeInfo interfaceTypeInfo)
        {
            var delegateType = typeof(Func<IAddressable, object>);
            return (Func<IAddressable, object>)MakeFactoryDelegate(interfaceTypeInfo, "Cast", delegateType);
        }
        #endregion

        #region SystemTargets

        private readonly Dictionary<GrainId, Dictionary<SiloAddress, ISystemTarget>> typedSystemTargetReferenceCache =
                    new Dictionary<GrainId, Dictionary<SiloAddress, ISystemTarget>>();

        internal TGrainInterface GetSystemTarget<TGrainInterface>(GrainId grainId, SiloAddress destination)
            where TGrainInterface : ISystemTarget
        {
            Dictionary<SiloAddress, ISystemTarget> cache;

            lock (typedSystemTargetReferenceCache)
            {
                if (typedSystemTargetReferenceCache.ContainsKey(grainId))
                    cache = typedSystemTargetReferenceCache[grainId];
                else
                {
                    cache = new Dictionary<SiloAddress, ISystemTarget>();
                    typedSystemTargetReferenceCache[grainId] = cache;
                }
            }
            lock (cache)
            {
                if (cache.ContainsKey(destination))
                    return (TGrainInterface)cache[destination];

                var reference = Cast<TGrainInterface>(GrainReference.FromGrainId(grainId, null, destination));
                cache[destination] = reference;
                return reference;
            }
        }

        #endregion

        #region Utility functions
        /// <summary>
        /// Creates a delegate for calling into the static method named <paramref name="methodName"/> on the generated
        /// factory for <paramref name="interfaceTypeInfo"/>.
        /// </summary>
        /// <param name="interfaceTypeInfo">The interface type.</param>
        /// <param name="methodName">The name of the static factory method.</param>
        /// <param name="delegateType">The type of delegate to create.</param>
        /// <returns>The created delegate.</returns>
        private static Delegate MakeFactoryDelegate(TypeInfo interfaceTypeInfo, string methodName, Type delegateType)
        {
            var grainFactoryName = TypeUtils.GetSimpleTypeName(interfaceTypeInfo, t => false);
            if (interfaceTypeInfo.IsInterface && grainFactoryName.Length > 1 && grainFactoryName[0] == 'I'
                && char.IsUpper(grainFactoryName[1]))
            {
                grainFactoryName = grainFactoryName.Substring(1);
            }

            grainFactoryName = grainFactoryName + "Factory";

            if (interfaceTypeInfo.IsGenericType)
            {
                grainFactoryName = grainFactoryName + "`" + interfaceTypeInfo.GetGenericArguments().Length;
            }

            // expect grain reference to be generated into same namespace that interface is declared within
            if (!string.IsNullOrEmpty(interfaceTypeInfo.Namespace))
            {
                grainFactoryName = interfaceTypeInfo.Namespace + "." + grainFactoryName;
            }

            var grainFactoryType = interfaceTypeInfo.Assembly.GetType(grainFactoryName);
            if (grainFactoryType == null)
            {
                throw new InvalidOperationException(
                    string.Format("Cannot find generated factory type for interface '{0}'", interfaceTypeInfo));
            }

            if (interfaceTypeInfo.IsGenericType)
            {
                grainFactoryType = grainFactoryType.MakeGenericType(interfaceTypeInfo.GetGenericArguments());
            }

            var method = grainFactoryType.GetMethod(
                methodName,
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic
                | System.Reflection.BindingFlags.Public);
            if (method == null)
            {
                throw new InvalidOperationException(
                    string.Format("Cannot find '{0}' method for interface '{1}'", methodName, interfaceTypeInfo));
            }

            return method.CreateDelegate(delegateType);
        }
        #endregion
    }
}