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
﻿using Orleans.CodeGeneration;
﻿using Orleans.Runtime;

namespace Orleans
{
    using System.Threading.Tasks;

    /// <summary>
    /// Factory for accessing grains.
    /// </summary>
    public static class GrainFactory
    {
        /// <summary>
        /// The collection of <see cref="IGrainObserver"/> <c>CreateObjectReference</c> delegates.
        /// </summary>
        private static readonly ConcurrentDictionary<Type, Delegate> referenceCreators =
            new ConcurrentDictionary<Type, Delegate>();

        /// <summary>
        /// The collection of <see cref="IGrainObserver"/> <c>DeleteObjectReference</c> delegates.
        /// </summary>
        private static readonly ConcurrentDictionary<Type, Delegate> referenceDestoyers =
            new ConcurrentDictionary<Type, Delegate>();

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
        public static TGrainInterface GetGrain<TGrainInterface>(long primaryKey, string grainClassNamePrefix = null)
            where TGrainInterface : IGrainWithIntegerKey
        {
            return Cast<TGrainInterface>(
                _MakeGrainReference(
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
        public static TGrainInterface GetGrain<TGrainInterface>(string primaryKey, string grainClassNamePrefix = null)
            where TGrainInterface : IGrainWithStringKey
        {
            return Cast<TGrainInterface>(
                _MakeGrainReference(
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
        public static TGrainInterface GetGrain<TGrainInterface>(Guid primaryKey, string keyExtension, string grainClassNamePrefix = null)
            where TGrainInterface : IGrainWithGuidCompoundKey
        {
            GrainFactoryBase.DisallowNullOrWhiteSpaceKeyExtensions(keyExtension);

            return Cast<TGrainInterface>(
                _MakeGrainReference(
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
        public static TGrainInterface GetGrain<TGrainInterface>(long primaryKey, string keyExtension, string grainClassNamePrefix = null)
            where TGrainInterface : IGrainWithGuidCompoundKey
        {
            GrainFactoryBase.DisallowNullOrWhiteSpaceKeyExtensions(keyExtension);

            return Cast<TGrainInterface>(
                _MakeGrainReference(
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
        public static Task<TGrainObserverInterface> CreateObjectReference<TGrainObserverInterface>(IGrainObserver obj)
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
        public static Task DeleteObjectReference<TGrainObserverInterface>(
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
            GrainId grainId = getGrainId(TypeCodeMapper.GetImplementationTypeCode(interfaceType, grainClassNamePrefix));
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
        #endregion
    }
}