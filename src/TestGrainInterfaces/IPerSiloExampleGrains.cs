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
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using Orleans;

namespace TestGrainInterfaces
{
    /// <summary>
    /// Grain interface for PerSilo Example Grain
    /// </summary>
    public interface IPartitionGrain : IGrainWithGuidKey
    {
        /// <summary> Start this partiton grain. </summary>
        Task<PartitionInfo> Start();

        /// <summary> Return the <c>PartitionInfo</c> for this partition. </summary>
        Task<PartitionInfo> GetPartitionInfo();
    }

    /// <summary>
    /// Manager for Partition Grains.
    /// By convention, only Id=0 will be used, to ensure only single copy exists within the cluster.
    /// </summary>
    public interface IPartitionManager : IGrainWithIntegerKey
    {
        Task<IList<IPartitionGrain>> GetPartitions();
        Task<IList<PartitionInfo>> GetPartitionInfos();
        Task RegisterPartition(PartitionInfo partitonInfo, IPartitionGrain partitionGrain);
        Task RemovePartition(PartitionInfo partitonInfo, IPartitionGrain partitionGrain);
        Task Broadcast(Func<IPartitionGrain, Task> asyncAction);
    }

    [Serializable]
    [DebuggerDisplay("PartitionInfo:{PartitionId}")]
    public class PartitionInfo
    {
        public Guid PartitionId { get; set; }
        public string SiloId { get; set; }

        public override string ToString()
        {
            return string.Format("PartitionInfo:PartitionId={0},SiloId={1}", PartitionId, SiloId);
        }
    }
}
