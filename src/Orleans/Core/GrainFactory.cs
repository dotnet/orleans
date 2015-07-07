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
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Threading.Tasks;
using Orleans.CodeGeneration;
using Orleans.Runtime;
using GrainInterfaceData = Orleans.CodeGeneration.GrainInterfaceData;

namespace Orleans
{
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
        /// The collection of <see cref="IGrainMethodInvoker"/>s for their corresponding grain interface type.
        /// </summary>
        private readonly ConcurrentDictionary<Type, IGrainMethodInvoker> invokers =
            new ConcurrentDictionary<Type, IGrainMethodInvoker>();

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

        private async Task<TGrainObserverInterface> CreateObjectReferenceImpl<TGrainObserverInterface>(IAddressable obj)
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

            IGrainMethodInvoker invoker;
            if (!this.invokers.TryGetValue(interfaceType, out invoker))
            {
                invoker = MakeInvoker(interfaceType);
                if (invoker != null)
                {
                    this.invokers.TryAdd(interfaceType, invoker);
                }
            }

            if (invoker != null)
            {
                return Cast<TGrainObserverInterface>(await GrainReference.CreateObjectReference(obj, invoker));
            }

            if (!referenceCreators.TryGetValue(interfaceType, out creator))
            {
                creator = referenceCreators.GetOrAdd(interfaceType, MakeCreateObjectReferenceDelegate);
            }

            return await ((Func<TGrainObserverInterface, Task<TGrainObserverInterface>>)creator)((TGrainObserverInterface)obj);
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
            return GrainReference.DeleteObjectReference(obj);
        }

        private static IGrainMethodInvoker MakeInvoker(Type interfaceType)
        {
            CodeGeneratorManager.GenerateAndCacheCodeForAssembly(interfaceType.Assembly);
            var genericInterfaceType = interfaceType.IsConstructedGenericType
                                           ? interfaceType.GetGenericTypeDefinition()
                                           : interfaceType;

            // Try to find the correct IGrainMethodInvoker type for this interface.
            var invokerType = TypeUtils.GetTypes(
                _ =>
                {
                    var attr = _.GetCustomAttribute<MethodInvokerAttribute>(false);
                    return attr != null && attr.GrainType == genericInterfaceType;
                }).FirstOrDefault();
            if (invokerType == null)
            {
                return null;
            }

            if (interfaceType.IsConstructedGenericType)
            {
                invokerType = invokerType.MakeGenericType(interfaceType.GenericTypeArguments);
            }

            return (IGrainMethodInvoker)Activator.CreateInstance(invokerType);
        }

        #region IGrainObserver Methods
        private static Delegate MakeCreateObjectReferenceDelegate(Type interfaceType)
        {
            var delegateType = typeof(Func<,>).MakeGenericType(interfaceType, typeof(Task<>).MakeGenericType(interfaceType));
            return MakeFactoryDelegate(interfaceType, "CreateObjectReference", delegateType);
        }
        #endregion

        #region Interface Casting
        private readonly ConcurrentDictionary<Type, Func<IAddressable, object>> casters
            = new ConcurrentDictionary<Type, Func<IAddressable, object>>();

        internal TGrainInterface Cast<TGrainInterface>(IAddressable grain)
        {
            var interfaceType = typeof(TGrainInterface);
            return (TGrainInterface)this.Cast(grain, interfaceType);
        }

        internal object Cast(IAddressable grain, Type interfaceType)
        {
            Func<IAddressable, object> caster;
            if (!this.casters.TryGetValue(interfaceType, out caster))
            {
                caster = this.casters.GetOrAdd(interfaceType, MakeCaster);
            }

            return caster(grain);
        }

        private static Func<IAddressable, object> MakeCaster(Type interfaceType)
        {
            CodeGeneratorManager.GenerateAndCacheCodeForAssembly(interfaceType.Assembly);
            var genericInterfaceType = interfaceType.IsConstructedGenericType
                                           ? interfaceType.GetGenericTypeDefinition()
                                           : interfaceType;

            // Try to find the correct GrainReference type for this interface.
            var grainReferenceType = TypeUtils.GetTypes(
                _ =>
                {
                    var attr = _.GetCustomAttribute<GrainReferenceAttribute>(false);
                    return attr != null && attr.GrainType == genericInterfaceType;
                }).FirstOrDefault();
            if (grainReferenceType == null)
            {
                // Fall back to finding a static GrainFactory delegate.
                var staticCaster =
                    (Func<IAddressable, object>)
                    MakeFactoryDelegate(interfaceType, "Cast", typeof(Func<IAddressable, object>));
                if (staticCaster == null)
                {
                    throw new InvalidOperationException(
                        string.Format(
                            "Cannot find generated factory type or reference type for interface '{0}'",
                            interfaceType));
                }

                return staticCaster;
            }

            if (interfaceType.IsConstructedGenericType)
            {
                grainReferenceType = grainReferenceType.MakeGenericType(interfaceType.GenericTypeArguments);
            }

            // Get the grain reference constructor.
            var constructor =
                grainReferenceType.GetConstructors(BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance)
                    .Where(
                        _ =>
                        {
                            var parameters = _.GetParameters();
                            return parameters.Length == 1 && parameters[0].ParameterType == typeof(GrainReference);
                        }).FirstOrDefault();

            if (constructor == null)
            {
                throw new InvalidOperationException(
                    string.Format(
                        "Cannot find suitable constructor on generated reference type for interface '{0}'",
                        interfaceType));
            }

            // Construct an expression to construct a new instance of this grain reference when given another grain
            // reference.
            var createLambdaParameter = Expression.Parameter(typeof(GrainReference), "gr");
            var createLambda =
                Expression.Lambda<Func<GrainReference, IAddressable>>(
                    Expression.New(constructor, createLambdaParameter),
                    createLambdaParameter);
            var grainRefParameter = Expression.Parameter(typeof(IAddressable), "grainRef");
            var body =
                Expression.Call(
                    TypeUtils.Method(() => GrainReference.CastInternal(default(Type), null, default(IAddressable), 0)),
                    Expression.Constant(interfaceType),
                    createLambda,
                    grainRefParameter,
                    Expression.Constant(GrainInterfaceData.GetGrainInterfaceId(interfaceType)));

            // Compile and return the reference casting lambda.
            var lambda = Expression.Lambda<Func<IAddressable, object>>(body, grainRefParameter);
            return lambda.Compile();
        }
        #endregion

        #region SystemTargets

        private readonly Dictionary<Tuple<GrainId,Type>, Dictionary<SiloAddress, ISystemTarget>> typedSystemTargetReferenceCache =
                    new Dictionary<Tuple<GrainId, Type>, Dictionary<SiloAddress, ISystemTarget>>();

        internal TGrainInterface GetSystemTarget<TGrainInterface>(GrainId grainId, SiloAddress destination)
            where TGrainInterface : ISystemTarget
        {
            Dictionary<SiloAddress, ISystemTarget> cache;
            Tuple<GrainId, Type> key = Tuple.Create(grainId, typeof(TGrainInterface));

            lock (typedSystemTargetReferenceCache)
            {
                if (typedSystemTargetReferenceCache.ContainsKey(key))
                    cache = typedSystemTargetReferenceCache[key];
                else
                {
                    cache = new Dictionary<SiloAddress, ISystemTarget>();
                    typedSystemTargetReferenceCache[key] = cache;
                }
            }

            ISystemTarget reference;
            lock (cache)
            {
                if (cache.ContainsKey(destination))
                {
                    reference = cache[destination];
                }
                else
                {
                    reference = Cast<TGrainInterface>(GrainReference.FromGrainId(grainId, null, destination));
                    cache[destination] = reference; // Store for next time
                }
            }
            return (TGrainInterface) reference;
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