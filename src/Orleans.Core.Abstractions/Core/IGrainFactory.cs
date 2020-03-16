using System;
using System.Threading.Tasks;
using Orleans.Runtime;

namespace Orleans
{
    public interface IGrainFactory
    {
        /// <summary>
        /// Gets a reference to a grain.
        /// </summary>
        /// <typeparam name="TGrainInterface">The interface to get.</typeparam>
        /// <param name="primaryKey">The primary key of the grain.</param>
        /// <param name="grainClassNamePrefix">An optional class name prefix used to find the runtime type of the grain.</param>
        /// <returns>A reference to the specified grain.</returns>
        TGrainInterface GetGrain<TGrainInterface>(Guid primaryKey, string grainClassNamePrefix = null) where TGrainInterface : IGrainWithGuidKey;

        /// <summary>
        /// Gets a reference to a grain.
        /// </summary>
        /// <typeparam name="TGrainInterface">The interface to get.</typeparam>
        /// <param name="primaryKey">The primary key of the grain.</param>
        /// <param name="grainClassNamePrefix">An optional class name prefix used to find the runtime type of the grain.</param>
        /// <returns>A reference to the specified grain.</returns>
        TGrainInterface GetGrain<TGrainInterface>(long primaryKey, string grainClassNamePrefix = null) where TGrainInterface : IGrainWithIntegerKey;

        /// <summary>
        /// Gets a reference to a grain.
        /// </summary>
        /// <typeparam name="TGrainInterface">The interface to get.</typeparam>
        /// <param name="primaryKey">The primary key of the grain.</param>
        /// <param name="grainClassNamePrefix">An optional class name prefix used to find the runtime type of the grain.</param>
        /// <returns>A reference to the specified grain.</returns>
        TGrainInterface GetGrain<TGrainInterface>(string primaryKey, string grainClassNamePrefix = null) where TGrainInterface : IGrainWithStringKey;

        /// <summary>
        /// Gets a reference to a grain.
        /// </summary>
        /// <typeparam name="TGrainInterface">The interface to get.</typeparam>
        /// <param name="primaryKey">The primary key of the grain.</param>
        /// <param name="keyExtension">The key extension of the grain.</param>
        /// <param name="grainClassNamePrefix">An optional class name prefix used to find the runtime type of the grain.</param>
        /// <returns>A reference to the specified grain.</returns>
        TGrainInterface GetGrain<TGrainInterface>(Guid primaryKey, string keyExtension, string grainClassNamePrefix = null) where TGrainInterface : IGrainWithGuidCompoundKey;

        /// <summary>
        /// Gets a reference to a grain.
        /// </summary>
        /// <typeparam name="TGrainInterface">The interface to get.</typeparam>
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
        Task<TGrainObserverInterface> CreateObjectReference<TGrainObserverInterface>(IGrainObserver obj) where TGrainObserverInterface : IGrainObserver;

        /// <summary>
        /// Deletes the provided object reference.
        /// </summary>
        /// <typeparam name="TGrainObserverInterface">
        /// The specific <see cref="IGrainObserver"/> type of <paramref name="obj"/>.
        /// </typeparam>
        /// <param name="obj">The reference being deleted.</param>
        /// <returns>A <see cref="Task"/> representing the work performed.</returns>
        Task DeleteObjectReference<TGrainObserverInterface>(IGrainObserver obj) where TGrainObserverInterface : IGrainObserver;

        /// <summary>
        /// Binds the provided grain reference to this instance.
        /// </summary>
        /// <param name="grain">The grain reference.</param>
        void BindGrainReference(IAddressable grain);

        /// <summary>
        /// A GetGrain overload that returns the runtime type of the grain interface and returns the grain cast to
        /// TGrainInterface.
        /// 
        /// The main use-case is when you want to get a grain whose type is unknown at compile time (e.g. generic type parameters).
        /// </summary>
        /// <typeparam name="TGrainInterface">The output type of the grain</typeparam>
        /// <param name="grainPrimaryKey">the primary key of the grain</param>
        /// <param name="grainInterfaceType">the runtime type of the grain interface</param>
        /// <returns>the requested grain with the given grainID and grainInterfaceType</returns>
        TGrainInterface GetGrain<TGrainInterface>(Type grainInterfaceType, Guid grainPrimaryKey)
            where TGrainInterface : IGrain;

        /// <summary>
        /// A GetGrain overload that returns the runtime type of the grain interface and returns the grain cast to
        /// TGrainInterface.
        /// 
        /// The main use-case is when you want to get a grain whose type is unknown at compile time (e.g. generic type parameters).
        /// </summary>
        /// <typeparam name="TGrainInterface">The output type of the grain</typeparam>
        /// <param name="grainPrimaryKey">the primary key of the grain</param>
        /// <param name="grainInterfaceType">the runtime type of the grain interface</param>
        /// <returns>the requested grain with the given grainID and grainInterfaceType</returns>
        TGrainInterface GetGrain<TGrainInterface>(Type grainInterfaceType, long grainPrimaryKey)
            where TGrainInterface : IGrain;

        /// <summary>
        /// A GetGrain overload that returns the runtime type of the grain interface and returns the grain cast to
        /// TGrainInterface.
        /// 
        /// The main use-case is when you want to get a grain whose type is unknown at compile time (e.g. generic type parameters).
        /// </summary>
        /// <typeparam name="TGrainInterface">The output type of the grain</typeparam>
        /// <param name="grainPrimaryKey">the primary key of the grain</param>
        /// <param name="grainInterfaceType">the runtime type of the grain interface</param>
        /// <returns>the requested grain with the given grainID and grainInterfaceType</returns>
        TGrainInterface GetGrain<TGrainInterface>(Type grainInterfaceType, string grainPrimaryKey)
            where TGrainInterface : IGrain;

        /// <summary>
        /// A GetGrain overload that returns the runtime type of the grain interface and returns the grain cast to
        /// TGrainInterface.
        /// 
        /// The main use-case is when you want to get a grain whose type is unknown at compile time (e.g. generic type parameters).
        /// </summary>
        /// <typeparam name="TGrainInterface">The output type of the grain</typeparam>
        /// <param name="grainPrimaryKey">the primary key of the grain</param>
        /// <param name="keyExtension">The key extension of the grain.</param>
        /// <param name="grainInterfaceType">the runtime type of the grain interface</param>
        /// <returns>the requested grain with the given grainID and grainInterfaceType</returns>
        TGrainInterface GetGrain<TGrainInterface>(Type grainInterfaceType, Guid grainPrimaryKey, string keyExtension)
            where TGrainInterface : IGrain;

        /// <summary>
        /// A GetGrain overload that returns the runtime type of the grain interface and returns the grain cast to
        /// TGrainInterface.
        /// 
        /// The main use-case is when you want to get a grain whose type is unknown at compile time (e.g. generic type parameters).
        /// </summary>
        /// <typeparam name="TGrainInterface">The output type of the grain</typeparam>
        /// <param name="grainPrimaryKey">the primary key of the grain</param>
        /// <param name="keyExtension">The key extension of the grain.</param>
        /// <param name="grainInterfaceType">the runtime type of the grain interface</param>
        /// <returns>the requested grain with the given grainID and grainInterfaceType</returns>
        TGrainInterface GetGrain<TGrainInterface>(Type grainInterfaceType, long grainPrimaryKey, string keyExtension)
            where TGrainInterface : IGrain;

        /// <summary>
        /// A GetGrain overload that returns the runtime type of the grain interface and returns the grain cast to
        /// <see paramref="TGrainInterface"/>. It is the caller's responsibility to ensure <see paramref="TGrainInterface"/>
        /// extends IGrain, as there is no compile-time checking for this overload.
        /// 
        /// The main use-case is when you want to get a grain whose type is unknown at compile time.
        /// </summary>
        /// <param name="grainPrimaryKey">the primary key of the grain</param>
        /// <param name="grainInterfaceType">the runtime type of the grain interface</param>
        /// <returns></returns>
        IGrain GetGrain(Type grainInterfaceType, Guid grainPrimaryKey);

        /// <summary>
        /// A GetGrain overload that returns the runtime type of the grain interface and returns the grain cast to
        /// <see paramref="TGrainInterface"/>. It is the caller's responsibility to ensure <see paramref="TGrainInterface"/>
        /// extends IGrain, as there is no compile-time checking for this overload.
        /// 
        /// The main use-case is when you want to get a grain whose type is unknown at compile time.
        /// </summary>
        /// <param name="grainPrimaryKey">the primary key of the grain</param>
        /// <param name="grainInterfaceType">the runtime type of the grain interface</param>
        /// <returns></returns>
        IGrain GetGrain(Type grainInterfaceType, long grainPrimaryKey);

        /// <summary>
        /// A GetGrain overload that returns the runtime type of the grain interface and returns the grain cast to
        /// <see paramref="TGrainInterface"/>. It is the caller's responsibility to ensure <see paramref="TGrainInterface"/>
        /// extends IGrain, as there is no compile-time checking for this overload.
        /// 
        /// The main use-case is when you want to get a grain whose type is unknown at compile time.
        /// </summary>
        /// <param name="grainPrimaryKey">the primary key of the grain</param>
        /// <param name="grainInterfaceType">the runtime type of the grain interface</param>
        /// <returns></returns>
        IGrain GetGrain(Type grainInterfaceType, string grainPrimaryKey);

        /// <summary>
        /// A GetGrain overload that returns the runtime type of the grain interface and returns the grain cast to
        /// <see paramref="TGrainInterface"/>. It is the caller's responsibility to ensure <see paramref="TGrainInterface"/>
        /// extends IGrain, as there is no compile-time checking for this overload.
        /// 
        /// The main use-case is when you want to get a grain whose type is unknown at compile time.
        /// </summary>
        /// <param name="grainPrimaryKey">the primary key of the grain</param>
        /// <param name="keyExtension">The key extension of the grain.</param>
        /// <param name="grainInterfaceType">the runtime type of the grain interface</param>
        /// <returns></returns>
        IGrain GetGrain(Type grainInterfaceType, Guid grainPrimaryKey, string keyExtension);

        /// <summary>
        /// A GetGrain overload that returns the runtime type of the grain interface and returns the grain cast to
        /// <see paramref="TGrainInterface"/>. It is the caller's responsibility to ensure <see paramref="TGrainInterface"/>
        /// extends IGrain, as there is no compile-time checking for this overload.
        /// 
        /// The main use-case is when you want to get a grain whose type is unknown at compile time.
        /// </summary>
        /// <param name="grainPrimaryKey">the primary key of the grain</param>
        /// <param name="keyExtension">The key extension of the grain.</param>
        /// <param name="grainInterfaceType">the runtime type of the grain interface</param>
        /// <returns></returns>
        IGrain GetGrain(Type grainInterfaceType, long grainPrimaryKey, string keyExtension);

        TGrainInterface GetGrain<TGrainInterface>(GrainId grainId) where TGrainInterface : IAddressable;
    }
}