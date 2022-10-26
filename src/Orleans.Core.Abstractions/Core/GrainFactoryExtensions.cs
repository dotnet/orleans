using System;
using Orleans.Runtime;

namespace Orleans
{
    /// <summary>
    /// Extensions for <see cref="IGrainFactory"/>.
    /// </summary>
    public static class GrainFactoryExtensions
    {
        /// <summary>
        /// Gets a reference to a grain.
        /// </summary>
        /// <typeparam name="TGrainInterface">The interface type.</typeparam>
        /// <param name="primaryKey">The primary key of the grain.</param>
        /// <param name="grainClassNamePrefix">An optional class name prefix used to find the runtime type of the grain.</param>
        /// <returns>A reference to the specified grain.</returns>
        public static TGrainInterface GetGrain<TGrainInterface>(this IGrainFactory grainFactory, Guid primaryKey, string grainClassNamePrefix = null) where TGrainInterface : IGrainWithGuidKey
        {
            var grainKey = GrainIdKeyExtensions.CreateGuidKey(primaryKey);
            return (TGrainInterface)grainFactory.GetGrain(typeof(TGrainInterface), grainKey, grainClassNamePrefix: grainClassNamePrefix);
        }

        /// <summary>
        /// Gets a reference to a grain.
        /// </summary>
        /// <typeparam name="TGrainInterface">The interface type.</typeparam>
        /// <param name="primaryKey">The primary key of the grain.</param>
        /// <param name="grainClassNamePrefix">An optional class name prefix used to find the runtime type of the grain.</param>
        /// <returns>A reference to the specified grain.</returns>
        public static TGrainInterface GetGrain<TGrainInterface>(this IGrainFactory grainFactory, long primaryKey, string grainClassNamePrefix = null) where TGrainInterface : IGrainWithIntegerKey
        {
            var grainKey = GrainIdKeyExtensions.CreateIntegerKey(primaryKey);
            return (TGrainInterface)grainFactory.GetGrain(typeof(TGrainInterface), grainKey, grainClassNamePrefix: grainClassNamePrefix);
        }

        /// <summary>
        /// Gets a reference to a grain.
        /// </summary>
        /// <typeparam name="TGrainInterface">The interface type.</typeparam>
        /// <param name="primaryKey">The primary key of the grain.</param>
        /// <param name="grainClassNamePrefix">An optional class name prefix used to find the runtime type of the grain.</param>
        /// <returns>A reference to the specified grain.</returns>
        public static TGrainInterface GetGrain<TGrainInterface>(this IGrainFactory grainFactory, string primaryKey, string grainClassNamePrefix = null)
            where TGrainInterface : IGrainWithStringKey
        {
            var grainKey = IdSpan.Create(primaryKey);
            return (TGrainInterface)grainFactory.GetGrain(typeof(TGrainInterface), grainKey, grainClassNamePrefix: grainClassNamePrefix);
        }

        /// <summary>
        /// Gets a reference to a grain.
        /// </summary>
        /// <typeparam name="TGrainInterface">The interface type.</typeparam>
        /// <param name="primaryKey">The primary key of the grain.</param>
        /// <param name="keyExtension">The key extension of the grain.</param>
        /// <param name="grainClassNamePrefix">An optional class name prefix used to find the runtime type of the grain.</param>
        /// <returns>A reference to the specified grain.</returns>
        public static TGrainInterface GetGrain<TGrainInterface>(this IGrainFactory grainFactory, Guid primaryKey, string keyExtension, string grainClassNamePrefix = null)
            where TGrainInterface : IGrainWithGuidCompoundKey
        {
            ValidateGrainKeyExtension(keyExtension);

            var grainKey = GrainIdKeyExtensions.CreateGuidKey(primaryKey, keyExtension);
            return (TGrainInterface)grainFactory.GetGrain(typeof(TGrainInterface), grainKey, grainClassNamePrefix: grainClassNamePrefix);
        }

        /// <summary>
        /// Gets a reference to a grain.
        /// </summary>
        /// <typeparam name="TGrainInterface">The interface type.</typeparam>
        /// <param name="primaryKey">The primary key of the grain.</param>
        /// <param name="keyExtension">The key extension of the grain.</param>
        /// <param name="grainClassNamePrefix">An optional class name prefix used to find the runtime type of the grain.</param>
        /// <returns>A reference to the specified grain.</returns>
        public static TGrainInterface GetGrain<TGrainInterface>(this IGrainFactory grainFactory, long primaryKey, string keyExtension, string grainClassNamePrefix = null)
            where TGrainInterface : IGrainWithIntegerCompoundKey
        {
            ValidateGrainKeyExtension(keyExtension);

            var grainKey = GrainIdKeyExtensions.CreateIntegerKey(primaryKey, keyExtension);
            return (TGrainInterface)grainFactory.GetGrain(typeof(TGrainInterface), grainKey, grainClassNamePrefix: grainClassNamePrefix);
        }

        /// <summary>
        /// Returns a reference to the grain which is the primary implementation of the provided interface type and has the provided primary key.
        /// </summary>
        /// <param name="grainInterfaceType">
        /// The grain interface type which the returned grain reference must implement.
        /// </param>
        /// <param name="grainPrimaryKey">
        /// The primary key of the grain
        /// </param>
        /// <returns>
        /// A reference to the grain which is the primary implementation of the provided interface type and has the provided primary key.
        /// </returns>
        public static IGrain GetGrain(this IGrainFactory grainFactory, Type grainInterfaceType, Guid grainPrimaryKey)
        {
            var grainKey = GrainIdKeyExtensions.CreateGuidKey(grainPrimaryKey);
            return (IGrain)grainFactory.GetGrain(grainInterfaceType, grainKey, grainClassNamePrefix: null);
        }

        /// <summary>
        /// Returns a reference to the grain which is the primary implementation of the provided interface type and has the provided primary key.
        /// </summary>
        /// <param name="grainInterfaceType">
        /// The grain interface type which the returned grain reference must implement.
        /// </param>
        /// <param name="grainPrimaryKey">
        /// The primary key of the grain
        /// </param>
        /// <returns>
        /// A reference to the grain which is the primary implementation of the provided interface type and has the provided primary key.
        /// </returns>
        public static IGrain GetGrain(this IGrainFactory grainFactory, Type grainInterfaceType, long grainPrimaryKey)
        {
            var grainKey = GrainIdKeyExtensions.CreateIntegerKey(grainPrimaryKey);
            return (IGrain)grainFactory.GetGrain(grainInterfaceType, grainKey, grainClassNamePrefix: null);
        }

        /// <summary>
        /// Returns a reference to the grain which is the primary implementation of the provided interface type and has the provided primary key.
        /// </summary>
        /// <param name="grainInterfaceType">
        /// The grain interface type which the returned grain reference must implement.
        /// </param>
        /// <param name="grainPrimaryKey">
        /// The primary key of the grain
        /// </param>
        /// <returns>
        /// A reference to the grain which is the primary implementation of the provided interface type and has the provided primary key.
        /// </returns>
        public static IGrain GetGrain(this IGrainFactory grainFactory, Type grainInterfaceType, string grainPrimaryKey)
        {
            var grainKey = IdSpan.Create(grainPrimaryKey);
            return (IGrain)grainFactory.GetGrain(grainInterfaceType, grainKey, grainClassNamePrefix: null);
        }

        /// <summary>
        /// Returns a reference to the grain which is the primary implementation of the provided interface type and has the provided primary key.
        /// </summary>
        /// <param name="grainInterfaceType">
        /// The grain interface type which the returned grain reference must implement.
        /// </param>
        /// <param name="grainPrimaryKey">
        /// The primary key of the grain
        /// </param>
        /// <param name="keyExtension">
        /// The grain key extension component.
        /// </param>
        /// <returns>
        /// A reference to the grain which is the primary implementation of the provided interface type and has the provided primary key.
        /// </returns>
        public static IGrain GetGrain(this IGrainFactory grainFactory, Type grainInterfaceType, Guid grainPrimaryKey, string keyExtension)
        {
            var grainKey = GrainIdKeyExtensions.CreateGuidKey(grainPrimaryKey, keyExtension);
            return (IGrain)grainFactory.GetGrain(grainInterfaceType, grainKey, grainClassNamePrefix: null);
        }

        /// <summary>
        /// Returns a reference to the grain which is the primary implementation of the provided interface type and has the provided primary key.
        /// </summary>
        /// <param name="grainInterfaceType">
        /// The grain interface type which the returned grain reference must implement.
        /// </param>
        /// <param name="grainPrimaryKey">
        /// The primary key of the grain
        /// </param>
        /// <param name="keyExtension">
        /// The grain key extension component.
        /// </param>
        /// <returns>
        /// A reference to the grain which is the primary implementation of the provided interface type and has the provided primary key.
        /// </returns>
        public static IGrain GetGrain(this IGrainFactory grainFactory, Type grainInterfaceType, long grainPrimaryKey, string keyExtension)
        {
            var grainKey = GrainIdKeyExtensions.CreateIntegerKey(grainPrimaryKey, keyExtension);
            return (IGrain)grainFactory.GetGrain(grainInterfaceType, grainKey, grainClassNamePrefix: null);
        }

        /// <summary>
        /// Validates the provided grain key extension.
        /// </summary>
        /// <param name="keyExt">The grain key extension.</param>
        /// <exception cref="ArgumentNullException">The key is <see langword="null"/>.</exception>
        /// <exception cref="ArgumentException">The key is empty or contains only whitespace.</exception>
        private static void ValidateGrainKeyExtension(string keyExt)
        {
            if (!string.IsNullOrWhiteSpace(keyExt)) return;

            if (null == keyExt)
            {
                throw new ArgumentNullException(nameof(keyExt)); 
            }
            
            throw new ArgumentException("Key extension is empty or white space.", nameof(keyExt));
        }
    }
}