using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Orleans.Runtime;

namespace Orleans.GrainDirectory
{

    /// <summary>
    /// Recursive distributed operations on grain directories.
    /// Each operation may forward the request to a remote owner, increasing the hopCount.
    /// 
    /// The methods here can be called remotely (where extended by IRemoteGrainDirectory) or
    /// locally (where extended by ILocalGrainDirectory)
    /// </summary>
    interface IDhtGrainDirectory
    {
        /// <summary>
        /// Record a new grain activation by adding it to the directory.
        /// <para>This method must be called from a scheduler thread.</para>
        /// </summary>
        /// <param name="address">The address of the new activation.</param>
        /// <param name="singleActivation">If true, use single-activation mode: 
        /// If there is already an activation registered for this grain, then the new activation will
        /// not be registered and the address of the existing activation will be returned.
        /// Otherwise, the passed-in address will be returned.</param>
        /// <param name="hopCount">Counts recursion depth across silos</param>
        /// <returns>The registered address and the version associated with this directory mapping.</returns>
        Task<AddressAndTag> RegisterAsync(ActivationAddress address, bool singleActivation, int hopCount = 0);

        /// <summary>
        /// Removes the record for an existing activation from the directory service.
        /// This is used when an activation is being deleted.
        /// <para>This method must be called from a scheduler thread.</para>
        /// </summary>
        /// <param name="address">The address of the activation to remove.</param>
        /// <param name="hopCount">Counts recursion depth across silos</param>
        /// <param name="cause">The reason for unregistration</param>
        /// <returns>An acknowledgement that the unregistration has completed.</returns>
        Task UnregisterAsync(ActivationAddress address, UnregistrationCause cause, int hopCount = 0);

        /// <summary>
        /// Unregister a batch of addresses at once
        /// <para>This method must be called from a scheduler thread.</para>
        /// </summary>
        /// <param name="addresses"></param>
        /// <param name="hopCount">Counts recursion depth across silos</param>
        /// <param name="cause">The reason for unregistration</param>
        /// <returns>An acknowledgement that the unregistration has completed.</returns>
        Task UnregisterManyAsync(List<ActivationAddress> addresses, UnregistrationCause cause, int hopCount = 0);

        /// <summary>
        /// Removes all directory information about a grain.
        /// <para>This method must be called from a scheduler thread.</para>
        /// </summary>
        /// <param name="grainId">The ID of the grain.</param>
        /// <param name="hopCount">Counts recursion depth across silos</param>
        /// <returns>An acknowledgement that the deletion has completed.
        /// It is safe to ignore this result.</returns>
        Task DeleteGrainAsync(GrainId grainId, int hopCount = 0);

        /// <summary>
        /// Fetches complete directory information for a grain.
        /// If there is no local information, then this method will query the appropriate remote directory node.
        /// <para>This method must be called from a scheduler thread.</para>
        /// </summary>
        /// <param name="grainId">The ID of the grain to look up.</param>
        /// <param name="hopCount">Counts recursion depth across silos</param>
        /// <returns>A list of all known activations of the grain, and the e-tag.</returns>
        Task<AddressesAndTag> LookupAsync(GrainId grainId, int hopCount = 0);
    }


    [Serializable]
    internal struct AddressAndTag
    {
        public ActivationAddress Address;
        public int VersionTag;
    }
    

    [Serializable]
    internal struct AddressesAndTag 
    {
        public List<ActivationAddress> Addresses;
        public int VersionTag;
    }

    /// <summary>
    /// Indicates the reason for removing activations from the directory.
    /// This influences the conditions that are applied when determining whether or not to remove an entry.
    /// </summary>
    public enum UnregistrationCause : byte
    {
        /// <summary>
        /// Remove the directory entry forcefully, without any conditions
        /// </summary>
        Force,

        /// <summary>
        /// Remove the directory entry only if it is not too fresh (to avoid races on new registrations)
        /// </summary>
        NonexistentActivation
    }
}
