using System;
using System.Threading.Tasks;
using Orleans.GrainDirectory;
using System.Collections.Generic;

namespace Orleans.Runtime.GrainDirectory
{
    /// <summary>
    /// The registrar for the Cluster-Local Registration Strategy.
    /// TODO Merge back logic in LocalGrainDirectory
    /// </summary>
    internal class ClusterLocalRegistrar
    {
        private readonly GrainDirectoryPartition directoryPartition;

        public ClusterLocalRegistrar(GrainDirectoryPartition directoryPartition)
        {
            this.directoryPartition = directoryPartition;
        }

        public bool IsSynchronous { get { return true; } }

        public AddressAndTag Register(ActivationAddress address, bool singleActivation)
        {
            if (singleActivation)
            {
                var result = directoryPartition.AddSingleActivation(address.Grain, address.Activation, address.Silo, GrainDirectoryEntryStatus.ClusterLocal);
                return result;
            }
            else
            {
                var tag = directoryPartition.AddActivation(address.Grain, address.Activation, address.Silo);
                return new AddressAndTag() { Address = address, VersionTag = tag };
            }
        }
  
        public void Unregister(ActivationAddress address, UnregistrationCause cause)
        {
            directoryPartition.RemoveActivation(address.Grain, address.Activation, cause);
        }

        public void Delete(GrainId gid)
        {
            directoryPartition.RemoveGrain(gid);
        }
    }
}
