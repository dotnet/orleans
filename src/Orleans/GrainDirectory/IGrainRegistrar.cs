using System.Threading.Tasks;
using Orleans.Runtime;
using System.Collections.Generic;

namespace Orleans.GrainDirectory
{
    /// <summary>
    /// A grain registrar takes responsibility of coordinating the registration of a grains,
    /// possibly involving multiple clusters. 
    /// The grain registrar is called only on the silo that is the owner for that grain.
    /// </summary>
    interface IGrainRegistrar
    {

        /// <summary>
        /// Indicates whether this registrar can be called synchronously
        /// </summary>
        /// <returns>true if synchronous methods should be used, false if asynchronous methods should be used</returns>
        bool IsSynchronous { get; }

        /// <summary>
        /// Registers a new activation with the directory service, synchronously.
        /// <para>This method must be called from a scheduler thread.</para>
        /// </summary>
        /// <param name="address">The address of the activation to register.</param>
        /// <param name="singleActivation">If true, use single-activation registration</param>
        /// <returns>The address registered for the grain's single activation.</returns>
        AddressAndTag Register(ActivationAddress address, bool singleActivation);

        /// <summary>
        /// Registers a new activation with the directory service, asynchronously.
        /// <para>This method must be called from a scheduler thread.</para>
        /// </summary>
        /// <param name="address">The address of the activation to register.</param>
        /// <param name="singleActivation">If true, use single-activation registration</param>
        /// <returns>The address registered for the grain's single activation.</returns>
        Task<AddressAndTag> RegisterAsync(ActivationAddress address, bool singleActivation);

        /// <summary>
        /// Removes the given activation registration, synchronously.
        /// <para>This method must be called from a scheduler thread.</para>
        /// </summary>
        /// <param name="address">the activation to unregister</param>
        /// <param name="cause">The reason for unregistration</param>
        void Unregister(ActivationAddress address, UnregistrationCause cause);


        /// <summary>
        /// Invalidates registrations that are caches, i.e. point to activations in other clusters
        /// </summary>
        /// <param name="address">the remote activation to remove from the cache</param>
        void InvalidateCache(ActivationAddress address);


        /// <summary>
        /// Removes the given activation registrations, asynchronously.
        /// <para>This method must be called from a scheduler thread.</para>
        /// </summary>
        /// <param name="addresses">the activations to unregister</param>
        /// <param name="cause">The reason for unregistration</param>
        Task UnregisterAsync(List<ActivationAddress> addresses, UnregistrationCause cause);

        /// <summary>
        /// Deletes all registrations for a grain, synchronously
        /// </summary>
        /// <param name="gid">The id of the grain</param>
        void Delete(GrainId gid);

        /// <summary>
        /// Deletes all registrations for a grain, asynchronously
        /// </summary>
        /// <param name="gid">The id of the grain</param>
        Task DeleteAsync(GrainId gid);
    }
}
