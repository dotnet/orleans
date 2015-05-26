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
using Orleans.Runtime;

namespace Orleans.CodeGeneration
{
    /// <summary>
    /// Abstract base class for all grain proxy factory classes.
    /// </summary>
    /// <remarks>
    /// These methods are used from generated code.
    /// </remarks>
    public static class GrainFactoryBase
    {
        /// <summary>
        /// Fabricate a grain reference for a grain with the specified Int64 primary key
        /// </summary>
        /// <param name="grainInterfaceType">Grain type</param>
        /// <param name="interfaceId">Type code value for this grain type</param>
        /// <param name="primaryKey">Primary key for the grain</param>
        /// <param name="grainClassNamePrefix">Prefix or full name of the grain class to disambiguate multiple implementations.</param>
        /// <returns><c>GrainReference</c> for connecting to the grain with the specified primary key</returns>
        /// <exception cref="System.ArgumentException">If called for a grain type that is not a valid grain type.</exception>
        public static IAddressable MakeGrainReferenceInternal(
            Type grainInterfaceType,
            int interfaceId,
            long primaryKey,
            string grainClassNamePrefix = null)
        {
            return
                MakeGrainReference(
                    implementation => TypeCodeMapper.ComposeGrainId(implementation, primaryKey, grainInterfaceType),
                    grainInterfaceType,
                    interfaceId,
                    grainClassNamePrefix);
        }

        /// <summary>
        /// Fabricate a grain reference for a grain with the specified Guid primary key
        /// </summary>
        /// <param name="grainInterfaceType">Grain type</param>
        /// <param name="interfaceId">Type code value for this self-managed grain type</param>
        /// <param name="primaryKey">Primary key for the grain</param>
        /// <param name="grainClassNamePrefix">Prefix or full name of the grain class to disambiguate multiple implementations.</param>
        /// <returns><c>GrainReference</c> for connecting to the self-managed grain with the specified primary key</returns>
        /// <exception cref="System.ArgumentException">If called for a grain type that is not a valid grain type.</exception>
        public static IAddressable MakeGrainReferenceInternal(
            Type grainInterfaceType,
            int interfaceId,
            Guid primaryKey,
            string grainClassNamePrefix = null)
        {
            return
                MakeGrainReference(
                    implementation => TypeCodeMapper.ComposeGrainId(implementation, primaryKey, grainInterfaceType),
                    grainInterfaceType,
                    interfaceId,
                    grainClassNamePrefix);
        }

        /// <summary>
        /// Fabricate a grain reference for a grain with the specified Guid primary key
        /// </summary>
        /// <param name="grainInterfaceType">Grain type</param>
        /// <param name="interfaceId">Type code value for this self-managed grain type</param>
        /// <param name="primaryKey">Primary key for the grain</param>
        /// <param name="grainClassNamePrefix">Prefix or full name of the grain class to disambiguate multiple implementations.</param>
        /// <returns><c>GrainReference</c> for connecting to the self-managed grain with the specified primary key</returns>
        /// <exception cref="System.ArgumentException">If called for a grain type that is not a valid grain type.</exception>
        public static IAddressable MakeGrainReferenceInternal(
            Type grainInterfaceType,
            int interfaceId,
            string primaryKey,
            string grainClassNamePrefix = null)
        {
            return
                MakeGrainReference(
                    implementation => TypeCodeMapper.ComposeGrainId(implementation, primaryKey, grainInterfaceType),
                    grainInterfaceType,
                    interfaceId,
                    grainClassNamePrefix);
        }

        /// <summary>
        /// Fabricate a grain reference for an extended-key grain with the specified Guid primary key
        /// </summary>
        /// <param name="grainInterfaceType">Grain type</param>
        /// <param name="interfaceId">Type code value for this grain type</param>
        /// <param name="primaryKey">Primary key for the grain</param>
        /// <param name="keyExt">Extended key for the grain</param>
        /// <param name="grainClassNamePrefix">Prefix or full name of the grain class to disambiguate multiple implementations.</param>
        /// <returns><c>GrainReference</c> for connecting to the grain with the specified primary key</returns>
        /// <exception cref="System.ArgumentException">If called for a grain type that is not a valid grain type.</exception>
        public static IAddressable MakeKeyExtendedGrainReferenceInternal(
            Type grainInterfaceType,
            int interfaceId,
            Guid primaryKey,
            string keyExt,
            string grainClassNamePrefix = null)
        {
            DisallowNullOrWhiteSpaceKeyExtensions(keyExt);

            return
                MakeGrainReference(
                    implementation => TypeCodeMapper.ComposeGrainId(implementation, primaryKey, grainInterfaceType, keyExt),
                    grainInterfaceType,
                    interfaceId,
                    grainClassNamePrefix);
        }

        /// <summary>
        /// Fabricate a grain reference for an extended-key grain with the specified Int64 primary key
        /// </summary>
        /// <param name="grainInterfaceType">Grain type</param>
        /// <param name="interfaceId">Type code value for this grain type</param>
        /// <param name="primaryKey">Primary key for the grain</param>
        /// <param name="keyExt">Extended key for the grain</param>
        /// <param name="grainClassNamePrefix">Prefix or full name of the grain class to disambiguate multiple implementations.</param>
        /// <returns><c>GrainReference</c> for connecting to the grain with the specified primary key</returns>
        /// <exception cref="System.ArgumentException">If called for a grain type that is not a valid grain type.</exception>
        public static IAddressable MakeKeyExtendedGrainReferenceInternal(
            Type grainInterfaceType,
            int interfaceId,
            long primaryKey,
            string keyExt,
            string grainClassNamePrefix = null)
        {
            DisallowNullOrWhiteSpaceKeyExtensions(keyExt);

            return
                MakeGrainReference(
                    implementation => TypeCodeMapper.ComposeGrainId(implementation, primaryKey, grainInterfaceType, keyExt),
                    grainInterfaceType,
                    interfaceId,
                    grainClassNamePrefix);
        }

        internal static IAddressable MakeGrainReference_FromType(
            Func<GrainClassData, GrainId> getGrainId,
            Type interfaceType,
            string grainClassNamePrefix = null)
        {
            CheckRuntimeEnvironmentSetup();
            if (!GrainInterfaceData.IsGrainType(interfaceType))
            {
                throw new ArgumentException("Cannot fabricate grain-reference for non-grain type: " + interfaceType.FullName);
            }
            var implementation = TypeCodeMapper.GetImplementation(interfaceType, grainClassNamePrefix);
            GrainId grainId = getGrainId(implementation);
            return GrainReference.FromGrainId(grainId, interfaceType.IsGenericType ? interfaceType.UnderlyingSystemType.FullName : null);
        }

        internal static IAddressable MakeGrainReference(
            Func<GrainClassData, GrainId> getGrainId,
            Type grainType,
            int interfaceId,
            string grainClassNamePrefix = null)
        {
            CheckRuntimeEnvironmentSetup();
            if (!GrainInterfaceData.IsGrainType(grainType))
            {
                throw new ArgumentException("Cannot fabricate grain-reference for non-grain type: " + grainType.FullName);
            }
            var implementation = TypeCodeMapper.GetImplementation(interfaceId, grainClassNamePrefix);
            GrainId grainId = getGrainId(implementation);
            return GrainReference.FromGrainId(grainId, 
                grainType.IsGenericType ? grainType.UnderlyingSystemType.FullName : null);
        }

        /// <summary>
        /// Check that a grain observer parameter is of the correct underlying concrent type -- either extending from <c>GrainRefereence</c> or <c>Grain</c>
        /// </summary>
        /// <param name="grainObserver">Grain observer parameter to be checked.</param>
        /// <exception cref="ArgumentNullException">If grainObserver is <c>null</c></exception>
        /// <exception cref="NotSupportedException">If grainObserver class is not an appropriate underlying concrete type.</exception>
        public static void CheckGrainObserverParamInternal(IGrainObserver grainObserver)
        {
            if (grainObserver == null)
            {
                throw new ArgumentNullException("grainObserver", "IGrainObserver parameters cannot be null");
            }
            if (grainObserver is GrainReference || grainObserver is Grain)
            {
                // OK
            }
            else
            {
                string errMsg = string.Format("IGrainObserver parameters must be GrainReference or Grain and cannot be type {0}. Did you forget to CreateObjectReference?", grainObserver.GetType());
                throw new NotSupportedException(errMsg);
            }
        }

        #region Utility functions

        /// <summary>
        /// Check the current runtime environment has been setup and initialized correctly.
        /// Throws InvalidOperationException if current runtime environment is not initialized.
        /// </summary>
        private static void CheckRuntimeEnvironmentSetup()
        {
            if (RuntimeClient.Current == null)
            {
                const string msg = "Orleans runtime environment is not set up (RuntimeClient.Current==null). If you are running on the client, perhaps you are missing a call to Client.Initialize(...) ? " +
                                   "If you are running on the silo, perhaps you are trying to send a message or create a grain reference not within Orleans thread or from within grain constructor?";
                throw new InvalidOperationException(msg);
            }
        }

        internal static void DisallowNullOrWhiteSpaceKeyExtensions(string keyExt)
        {
            if (!string.IsNullOrWhiteSpace(keyExt)) return;

            if (null == keyExt)
            {
                throw new ArgumentNullException("keyExt"); 
            }
            
            throw new ArgumentException("Key extension is empty or white space.", "keyExt");
        }

        #endregion
    }
}
