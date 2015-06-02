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
    using System.Threading.Tasks;

    /// <summary>
    /// Factory for accessing grains.
    /// </summary>
    public class GrainFactory : IGrainFactory
    {
        /// <summary>
        /// The collection of <see cref="IGrainObserver"/> <c>CreateObjectReference</c> delegates.
        /// </summary>
        private readonly ConcurrentDictionary<Type, Delegate> referenceCreators =
            new ConcurrentDictionary<Type, Delegate>();

        /// <summary>
        /// The collection of <see cref="IGrainObserver"/> <c>DeleteObjectReference</c> delegates.
        /// </summary>
        private readonly ConcurrentDictionary<Type, Delegate> referenceDestoyers =
            new ConcurrentDictionary<Type, Delegate>();

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
            var interfaceType = typeof(TGrainObserverInterface);
            if (!interfaceType.IsInterface)
            {
                throw new ArgumentException(
                    string.Format(
                        "The provided type parameter must be an interface. '{0}' is not an interface.",
                        interfaceType.FullName));
            }

            if (!interfaceType.IsInstanceOfType(obj))
            {
                throw new ArgumentException(
                    string.Format("The provided object must implement '{0}'.", interfaceType.FullName),
                    "obj");
            }

            Delegate creator;

            if (!referenceCreators.TryGetValue(interfaceType, out creator))
            {
                creator = referenceCreators.GetOrAdd(interfaceType, MakeCreateObjectReferenceDelegate);
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
            var interfaceType = typeof(TGrainObserverInterface);
            if (!interfaceType.IsInterface)
            {
                throw new ArgumentException(
                    string.Format(
                        "The provided type parameter must be an interface. '{0}' is not an interface.",
                        interfaceType.FullName));
            }

            if (!interfaceType.IsInstanceOfType(obj))
            {
                throw new ArgumentException(
                    string.Format("The provided object must implement '{0}'.", interfaceType.FullName),
                    "obj");
            }

            Delegate destroyer;

            if (!referenceDestoyers.TryGetValue(interfaceType, out destroyer))
            {
                destroyer = referenceDestoyers.GetOrAdd(interfaceType, MakeDeleteObjectReferenceDelegate);
            }

            return ((Func<TGrainObserverInterface, Task>)destroyer)((TGrainObserverInterface)obj);
        }

        #region IGrainObserver Methods
        private static Delegate MakeCreateObjectReferenceDelegate(Type interfaceType)
        {
            var delegateType = typeof(Func<,>).MakeGenericType(interfaceType, typeof(Task<>).MakeGenericType(interfaceType));
            return MakeFactoryDelegate(interfaceType, "CreateObjectReference", delegateType);
        }

        private static Delegate MakeDeleteObjectReferenceDelegate(Type interfaceType)
        {
            var delegateType = typeof(Func<,>).MakeGenericType(interfaceType, typeof(Task));
            return MakeFactoryDelegate(interfaceType, "DeleteObjectReference", delegateType);
        }
        #endregion

        #region Interface Casting
        private static readonly ConcurrentDictionary<Type, Func<IAddressable, object>> casters
            = new ConcurrentDictionary<Type, Func<IAddressable, object>>();

        internal static TGrainInterface Cast<TGrainInterface>(IAddressable grain)
        {
            var interfaceType = typeof(TGrainInterface);
            Func<IAddressable, object> caster;

            if (!casters.TryGetValue(interfaceType, out caster))
            {
                caster = casters.GetOrAdd(interfaceType, MakeCaster);
            }

            return (TGrainInterface)caster(grain);
        }

        private static Func<IAddressable, object> MakeCaster(Type interfaceType)
        {
            var delegateType = typeof(Func<IAddressable, object>);
            return (Func<IAddressable, object>)MakeFactoryDelegate(interfaceType, "Cast", delegateType);
        }
        #endregion

        #region SystemTargets

        private static readonly Dictionary<GrainId, Dictionary<SiloAddress, ISystemTarget>> typedSystemTargetReferenceCache =
                    new Dictionary<GrainId, Dictionary<SiloAddress, ISystemTarget>>();

        internal static TGrainInterface GetSystemTarget<TGrainInterface>(GrainId grainId, SiloAddress destination)
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
        /// factory for <paramref name="interfaceType"/>.
        /// </summary>
        /// <param name="interfaceType">The interface type.</param>
        /// <param name="methodName">The name of the static factory method.</param>
        /// <param name="delegateType">The type of delegate to create.</param>
        /// <returns>The created delegate.</returns>
        private static Delegate MakeFactoryDelegate(Type interfaceType, string methodName, Type delegateType)
        {
            var grainFactoryName = TypeUtils.GetSimpleTypeName(interfaceType, t => false);
            if (interfaceType.IsInterface && grainFactoryName.Length > 1 && grainFactoryName[0] == 'I'
                && char.IsUpper(grainFactoryName[1]))
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
                throw new InvalidOperationException(
                    string.Format("Cannot find generated factory type for interface '{0}'", interfaceType));
            }

            if (interfaceType.IsGenericType)
            {
                grainFactoryType = grainFactoryType.MakeGenericType(interfaceType.GetGenericArguments());
            }

            var method = grainFactoryType.GetMethod(
                methodName,
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic
                | System.Reflection.BindingFlags.Public);
            if (method == null)
            {
                throw new InvalidOperationException(
                    string.Format("Cannot find '{0}' method for interface '{1}'", methodName, interfaceType));
            }

            return method.CreateDelegate(delegateType);
        }
        #endregion
    }
}