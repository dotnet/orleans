using System;
using System.Threading.Tasks;
using Orleans.Runtime;

namespace Orleans
{
    /// <summary>
    /// Functionality for creating references to grains.
    /// </summary>
    public interface IGrainFactory
    {
        /// <summary>
        /// Gets a reference to a grain.
        /// </summary>
        /// <typeparam name="TGrainInterface">The interface type.</typeparam>
        /// <param name="primaryKey">The primary key of the grain.</param>
        /// <param name="grainClassNamePrefix">An optional class name prefix used to find the runtime type of the grain.</param>
        /// <returns>A reference to the specified grain.</returns>
        TGrainInterface GetGrain<TGrainInterface>(Guid primaryKey, string grainClassNamePrefix = null) where TGrainInterface : IGrainWithGuidKey;

        /// <summary>
        /// Gets a reference to a grain.
        /// </summary>
        /// <typeparam name="TGrainInterface">The interface type.</typeparam>
        /// <param name="primaryKey">The primary key of the grain.</param>
        /// <param name="grainClassNamePrefix">An optional class name prefix used to find the runtime type of the grain.</param>
        /// <returns>A reference to the specified grain.</returns>
        TGrainInterface GetGrain<TGrainInterface>(long primaryKey, string grainClassNamePrefix = null) where TGrainInterface : IGrainWithIntegerKey;

        /// <summary>
        /// Gets a reference to a grain.
        /// </summary>
        /// <typeparam name="TGrainInterface">The interface type.</typeparam>
        /// <param name="primaryKey">The primary key of the grain.</param>
        /// <param name="grainClassNamePrefix">An optional class name prefix used to find the runtime type of the grain.</param>
        /// <returns>A reference to the specified grain.</returns>
        TGrainInterface GetGrain<TGrainInterface>(string primaryKey, string grainClassNamePrefix = null) where TGrainInterface : IGrainWithStringKey;

        /// <summary>
        /// Gets a reference to a grain.
        /// </summary>
        /// <typeparam name="TGrainInterface">The interface type.</typeparam>
        /// <param name="primaryKey">The primary key of the grain.</param>
        /// <param name="keyExtension">The key extension of the grain.</param>
        /// <param name="grainClassNamePrefix">An optional class name prefix used to find the runtime type of the grain.</param>
        /// <returns>A reference to the specified grain.</returns>
        TGrainInterface GetGrain<TGrainInterface>(Guid primaryKey, string keyExtension, string grainClassNamePrefix = null) where TGrainInterface : IGrainWithGuidCompoundKey;

        /// <summary>
        /// Gets a reference to a grain.
        /// </summary>
        /// <typeparam name="TGrainInterface">The interface type.</typeparam>
        /// <param name="primaryKey">The primary key of the grain.</param>
        /// <param name="keyExtension">The key extension of the grain.</param>
        /// <param name="grainClassNamePrefix">An optional class name prefix used to find the runtime type of the grain.</param>
        /// <returns>A reference to the specified grain.</returns>
        TGrainInterface GetGrain<TGrainInterface>(long primaryKey, string keyExtension, string grainClassNamePrefix = null) where TGrainInterface : IGrainWithIntegerCompoundKey;

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
        IGrain GetGrain(Type grainInterfaceType, Guid grainPrimaryKey);

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
        IGrain GetGrain(Type grainInterfaceType, long grainPrimaryKey);

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
        IGrain GetGrain(Type grainInterfaceType, string grainPrimaryKey);

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
        IGrain GetGrain(Type grainInterfaceType, Guid grainPrimaryKey, string keyExtension);

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
        IGrain GetGrain(Type grainInterfaceType, long grainPrimaryKey, string keyExtension);

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
        TGrainInterface GetGrain<TGrainInterface>(GrainId grainId) where TGrainInterface : IAddressable;

        /// <summary>
        /// Returns an untyped reference for the provided grain id.
        /// </summary>
        /// <param name="grainId">
        /// The grain id.
        /// </param>
        /// <returns>
        /// An untyped reference for the provided grain id.
        /// </returns>
        IAddressable GetGrain(GrainId grainId);

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
        IAddressable GetGrain(GrainId grainId, GrainInterfaceType interfaceType);

        /// <summary>
        /// Returns the unique <see cref="GrainInterfaceType"/> for the specified grain interface <paramref name="interfaceType"/>.
        /// </summary>
        /// <param name="interfaceType">The grain interface type to retrieve the identifier for.</param>
        /// <returns>
        /// The <see cref="GrainInterfaceType"/> that uniquely identifies the specified grain interface type.
        /// </returns>
        GrainInterfaceType GetGrainInterfaceType(Type interfaceType);

        /// <summary>
        /// Returns the grain type for the specified grain interface type and optional class name prefix.
        /// </summary>
        /// <param name="grainInterfaceType">The grain interface type.</param>
        /// <param name="grainClassNamePrefix">An optional class name prefix used to find the runtime type of the grain.</param>
        /// <returns>
        /// The <see cref="GrainType"/> corresponding to the specified interface type and class name prefix.
        /// </returns>
        GrainType GetGrainType(GrainInterfaceType grainInterfaceType, string grainClassNamePrefix = null);
    }
}
