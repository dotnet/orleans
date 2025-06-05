using System;
using System.Threading.Tasks;
using Orleans.Runtime;

namespace Orleans
{
    public static class GrainFactoryExtensions
    {
        /// <summary>
        /// Validates the provided grain key extension.
        /// </summary>
        /// <param name="keyExt">The grain key extension.</param>
        /// <exception cref="ArgumentNullException">The key is <see langword="null"/>.</exception>
        /// <exception cref="ArgumentException">The key is empty or contains only whitespace.</exception>
        private static void ValidateGrainKeyExtension(string keyExt)
        {
            if (!string.IsNullOrWhiteSpace(keyExt)) return;

            ArgumentNullException.ThrowIfNull(keyExt);

            throw new ArgumentException("Key extension is empty or white space.", nameof(keyExt));
        }

        /// <summary>
        /// Gets a reference to a grain.
        /// </summary>
        /// <typeparam name="TGrainInterface">The interface type.</typeparam>
        /// <param name="primaryKey">The primary key of the grain.</param>
        /// <param name="grainClassNamePrefix">An optional class name prefix used to find the runtime type of the grain.</param>
        /// <returns>A reference to the specified grain.</returns>
        public static TGrainInterface GetGrain<TGrainInterface>(this IGrainFactory grainFactory, Guid primaryKey, string grainClassNamePrefix = null) where TGrainInterface : IGrainWithGuidKey
        {
            ArgumentNullException.ThrowIfNull(grainFactory);
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
            ArgumentNullException.ThrowIfNull(grainFactory);
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
            ArgumentNullException.ThrowIfNull(grainFactory);
            ArgumentNullException.ThrowIfNullOrWhiteSpace(primaryKey);
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
            ArgumentNullException.ThrowIfNull(grainFactory);
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
            ArgumentNullException.ThrowIfNull(grainFactory);
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
            ArgumentNullException.ThrowIfNull(grainFactory);
            var grainKey = GrainIdKeyExtensions.CreateGuidKey(grainPrimaryKey);
            return grainFactory.GetGrain(grainInterfaceType, grainKey, grainClassNamePrefix: null);
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
            ArgumentNullException.ThrowIfNull(grainFactory);
            var grainKey = GrainIdKeyExtensions.CreateIntegerKey(grainPrimaryKey);
            return grainFactory.GetGrain(grainInterfaceType, grainKey, grainClassNamePrefix: null);
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
            ArgumentNullException.ThrowIfNull(grainFactory);
            ArgumentNullException.ThrowIfNullOrWhiteSpace(grainPrimaryKey);
            var grainKey = IdSpan.Create(grainPrimaryKey);
            return grainFactory.GetGrain(grainInterfaceType, grainKey, grainClassNamePrefix: null);
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
            ArgumentNullException.ThrowIfNull(grainFactory);
            var grainKey = GrainIdKeyExtensions.CreateGuidKey(grainPrimaryKey, keyExtension);
            return grainFactory.GetGrain(grainInterfaceType, grainKey, grainClassNamePrefix: null);
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
            ArgumentNullException.ThrowIfNull(grainFactory);
            var grainKey = GrainIdKeyExtensions.CreateIntegerKey(grainPrimaryKey, keyExtension);
            return grainFactory.GetGrain(grainInterfaceType, grainKey, grainClassNamePrefix: null);
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
        /// <param name="grainClassNamePrefix">An optional class name prefix used to find the runtime type of the grain.</param>
        /// <returns>
        /// A reference to the grain which is the primary implementation of the provided interface type and has the provided primary key.
        /// </returns>
        /// <summary>
        /// Gets a grain reference which implements the specified grain interface type and has the specified grain key, without specifying the grain type directly.
        /// </summary>
        /// <remarks>
        /// This method infers the most appropriate <see cref="GrainId.Type"/> value based on the <paramref name="grainInterfaceType"/> argument and optional <paramref name="grainClassNamePrefix"/> argument.
        /// </remarks>
        /// <returns>A grain reference which implements the provided interface.</returns>
        public static IGrain GetGrain(this IGrainFactory grainFactory, Type grainInterfaceType, IdSpan grainPrimaryKey, string grainClassNamePrefix = null)
        {
            var interfaceType = grainFactory.GetGrainInterfaceType(grainInterfaceType);

            GrainType grainType;
            if (!string.IsNullOrWhiteSpace(grainClassNamePrefix))
            {
                grainType = grainFactory.GetGrainType(interfaceType, grainClassNamePrefix);
            }
            else
            {
                grainType = grainFactory.GetGrainType(interfaceType);
            }

            var grainId = GrainId.Create(grainType, grainPrimaryKey);
            var grain = grainFactory.CreateGrainReference(grainId, interfaceType);
            return (IGrain)grain;
        }

        /// <summary>
        /// Returns a reference to the specified grain which implements the specified interface.
        /// </summary>
        /// <param name="grainId">
        /// The grain id.
        /// </param>
        /// <typeparam name="TGrainInterface">
        /// The grain interface type which the returned grain reference must implement.
        /// </typeparam>
        /// <returns>
        /// A reference to the specified grain which implements the specified interface.
        /// </returns>
        public static TGrainInterface GetGrain<TGrainInterface>(this IGrainFactory grainFactory, GrainId grainId) where TGrainInterface : IAddressable
        {
            ArgumentNullException.ThrowIfNull(grainFactory);

            var grainInterfaceType = grainFactory.GetGrainInterfaceType(typeof(TGrainInterface));
            return (TGrainInterface)grainFactory.CreateGrainReference(grainId, grainInterfaceType);
        }

        /// <summary>
        /// Returns an untyped reference for the provided grain id.
        /// </summary>
        /// <param name="grainId">
        /// The grain id.
        /// </param>
        /// <returns>
        /// An untyped reference for the provided grain id.
        /// </returns>
        public static IAddressable GetGrain(this IGrainFactory grainFactory, GrainId grainId)
        {
            ArgumentNullException.ThrowIfNull(grainFactory);
            return grainFactory.GetGrain(grainId, default);
        }

        /// <summary>
        /// Returns a reference for the provided grain id which implements the specified interface type.
        /// </summary>
        /// <param name="grainId">
        /// The grain id.
        /// </param>
        /// <param name="interfaceType">
        /// The interface type which the returned grain reference must implement.
        /// </param>
        /// <returns>
        /// A reference for the provided grain id which implements the specified interface type.
        /// </returns>
        public static IAddressable GetGrain(this IGrainFactory grainFactory, GrainId grainId, GrainInterfaceType interfaceType)
        {
            ArgumentNullException.ThrowIfNull(grainFactory);
            return grainFactory.CreateGrainReference(grainId, interfaceType);
        }
    }

    /// <summary>
    /// Functionality for creating references to grains.
    /// </summary>
    public interface IGrainFactory
    {
        /// <summary>
        /// Creates a reference to the provided <paramref name="obj"/>.
        /// </summary>
        /// <typeparam name="TGrainObserverInterface">
        /// The specific <see cref="IGrainObserver"/> type of <paramref name="obj"/>.
        /// </typeparam>
        /// <param name="obj">The object to create a reference to.</param>
        /// <returns>The reference to <paramref name="obj"/>.</returns>
        TGrainObserverInterface CreateObjectReference<TGrainObserverInterface>(IGrainObserver obj) where TGrainObserverInterface : IGrainObserver;

        /// <summary>
        /// Deletes the provided object reference.
        /// </summary>
        /// <typeparam name="TGrainObserverInterface">
        /// The specific <see cref="IGrainObserver"/> type of <paramref name="obj"/>.
        /// </typeparam>
        /// <param name="obj">The reference being deleted.</param>
        /// <returns>A <see cref="Task"/> representing the work performed.</returns>
        void DeleteObjectReference<TGrainObserverInterface>(IGrainObserver obj) where TGrainObserverInterface : IGrainObserver;

        /// <summary>
        /// Gets the unique <see cref="GrainInterfaceType"/> for the specified grain interface <paramref name="interfaceType"/>.
        /// </summary>
        /// <param name="interfaceType">The grain interface type to retrieve the identifier for.</param>
        /// <returns>
        /// The <see cref="GrainInterfaceType"/> that uniquely identifies the specified grain interface type.
        /// </returns>
        GrainInterfaceType GetGrainInterfaceType(Type interfaceType);

        /// <summary>
        /// Creates a grain reference for the specified <paramref name="grainId"/> and <paramref name="interfaceType"/>.
        /// </summary>
        /// <param name="grainId">The unique identifier of the grain.</param>
        /// <param name="interfaceType">The grain interface type that the returned reference must implement.</param>
        /// <returns>
        /// An <see cref="IAddressable"/> reference to the grain identified by <paramref name="grainId"/> and implementing <paramref name="interfaceType"/>.
        /// </returns>
        IAddressable CreateGrainReference(GrainId grainId, GrainInterfaceType interfaceType);

        /// <summary>
        /// Gets the grain type for the specified grain interface type and optional class name prefix.
        /// </summary>
        /// <param name="grainInterfaceType">The grain interface type.</param>
        /// <param name="grainClassNamePrefix">An optional class name prefix used to find the runtime type of the grain.</param>
        /// <returns>
        /// The <see cref="GrainType"/> corresponding to the specified interface type and class name prefix.
        /// </returns>
        GrainType GetGrainType(GrainInterfaceType grainInterfaceType, string grainClassNamePrefix = null);
    }
}