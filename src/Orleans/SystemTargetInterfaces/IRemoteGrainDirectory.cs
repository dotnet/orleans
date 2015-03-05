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
using System.Collections.Generic;
using System.Threading.Tasks;


namespace Orleans.Runtime
{
    internal interface IActivationInfo
    {
        SiloAddress SiloAddress { get; }
        DateTime TimeCreated { get; }
    }

    internal interface IGrainInfo
    {
        Dictionary<ActivationId, IActivationInfo> Instances { get; }
        int VersionTag { get; }
        bool SingleInstance { get; }
        bool AddActivation(ActivationId act, SiloAddress silo);
        ActivationAddress AddSingleActivation(GrainId grain, ActivationId act, SiloAddress silo);
        bool RemoveActivation(ActivationAddress addr);
        bool RemoveActivation(ActivationId act, bool force);
        bool Merge(GrainId grain, IGrainInfo other);
    }

    /// <summary>
    /// Per-silo system interface for managing the distributed, partitioned grain-silo-activation directory.
    /// </summary>
    internal interface IRemoteGrainDirectory : ISystemTarget
    {
        /// <summary>
        /// Record a new grain activation by adding it to the directory.
        /// </summary>
        /// <param name="address">The address of the new activation.</param>
        /// <param name="retries">Number of retries to execute the method in case the virtual ring (servers) changes.</param>
        /// <returns>The version associated with this directory mapping.</returns>
        Task<int> Register(ActivationAddress address, int retries = 0);

        /// <summary>
        /// Records a bunch of new grain activations.
        /// </summary>
        /// <param name="silo"></param>
        /// <param name="addresses"></param>
        /// <param name="retries">Number of retries to execute the method in case the virtual ring (servers) changes.</param>
        /// <returns></returns>
        Task RegisterMany(List<ActivationAddress> addresses, int retries = 0);

        /// <summary>
        /// Registers a new activation, in single activation mode, with the directory service.
        /// If there is already an activation registered for this grain, then the new activation will
        /// not be registered and the address of the existing activation will be returned.
        /// Otherwise, the passed-in address will be returned.
        /// <para>This method must be called from a scheduler thread.</para>
        /// </summary>
        /// <param name="silo"></param>
        /// <param name="address">The address of the potential new activation.</param>
        /// <param name="retries">Number of retries to execute the method in case the virtual ring (servers) changes.</param>
        /// <returns>The address registered for the grain's single activation and the version associated with it.</returns>
        Task<Tuple<ActivationAddress, int>> RegisterSingleActivation(ActivationAddress address, int retries = 0);

        /// <summary>
        /// Registers multiple new activations, in single activation mode, with the directory service.
        /// If there is already an activation registered for any of the grains, then the corresponding new activation will
        /// not be registered.
        /// <para>This method must be called from a scheduler thread.</para>
        /// </summary>
        /// <param name="silo"></param>
        /// <param name="addresses"></param>
        /// <param name="retries">Number of retries to execute the method in case the virtual ring (servers) changes.</param>
        /// <returns></returns>
        Task RegisterManySingleActivation(List<ActivationAddress> addresses, int retries = 0);

        /// <summary>
        /// Remove an activation from the directory.
        /// </summary>
        /// <param name="address">The address of the activation to unregister.</param>
        /// <param name="force">If true, then the entry is removed; if false, then the entry is removed only if it is
        /// sufficiently old.</param>
        /// <param name="retries">Number of retries to execute the method in case the virtual ring (servers) changes.</param>
        /// <returns>Success</returns>
        Task Unregister(ActivationAddress address, bool force, int retries = 0);

        /// <summary>
        /// Removes all directory information about a grain.
        /// </summary>
        /// <param name="grain">The ID of the grain to look up.</param>
        /// <param name="retries">Number of retries to execute the method in case the virtual ring (servers) changes.</param>
        /// <returns></returns>
        Task DeleteGrain(GrainId grain, int retries = 0);

        /// <summary>
        /// Fetch the list of the current activations for a grain along with the version number of the list.
        /// </summary>
        /// <param name="grain">The ID of the grain.</param>
        /// <param name="retries">Number of retries to execute the method in case the virtual ring (servers) changes.</param>
        /// <returns></returns>
        Task<Tuple<List<Tuple<SiloAddress, ActivationId>>, int>> LookUp(GrainId grain, int retries = 0);

        /// <summary>
        /// Fetch the updated information on the given list of grains.
        /// This method should be called only remotely to refresh directory caches.
        /// </summary>
        /// <param name="grainAndETagList">list of grains and generation (version) numbers. The latter denote the versions of 
        /// the lists of activations currently held by the invoker of this method.</param>
        /// <param name="retries">Number of retries to execute the method in case the virtual ring (servers) changes.</param>
        /// <returns>list of tuples holding a grain, generation number of the list of activations, and the list of activations. 
        /// If the generation number of the invoker matches the number of the destination, the list is null. If the destination does not
        /// hold the information on the grain, generation counter -1 is returned (and the list of activations is null)</returns>
        Task<List<Tuple<GrainId, int, List<Tuple<SiloAddress, ActivationId>>>>> LookUpMany(List<Tuple<GrainId, int>> grainAndETagList, int retries = 0);

        /// <summary>
        /// Handoffs the the directory partition from source silo to the destination silo.
        /// </summary>
        /// <param name="source">The address of the owner of the partition.</param>
        /// <param name="partition">The (full or partial) copy of the directory partition to be Haded off.</param>
        /// <param name="isFullCopy">Flag specifying whether it is a full copy of the directory partition (and thus any old copy should be just replaced) or the
        /// a delta copy (and thus the old copy should be updated by delta changes) </param>
        /// <returns></returns>
        Task AcceptHandoffPartition(SiloAddress source, Dictionary<GrainId, IGrainInfo> partition, bool isFullCopy);

        /// <summary>
        /// Removes the handed off directory partition from source silo on the destination silo.
        /// </summary>
        /// <param name="source">The address of the owner of the partition.</param>
        /// <returns></returns>
        Task RemoveHandoffPartition(SiloAddress source);

        /// <summary>
        /// Unregister a block of addresses at once
        /// </summary>
        /// <param name="activationAddresses"></param>
        /// <param name="retries"></param>
        /// <returns></returns>
        Task UnregisterMany(List<ActivationAddress> activationAddresses, int retries);
    }
}
