using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Orleans.GrainDirectory;

namespace Orleans.Runtime.GrainDirectory
{
    /// <summary>
    /// The registrar for the Cluster-Local Registration Strategy.
    /// </summary>
    internal class ClusterLocalRegistrar : IGrainRegistrar
    {
        private GrainDirectoryPartition DirectoryPartition;

        public ClusterLocalRegistrar(GrainDirectoryPartition partition)
        {
            DirectoryPartition = partition;
        }

        public bool IsSynchronous { get { return true; } }

        public virtual AddressAndTag Register(ActivationAddress address, bool singleActivation)
        {
            if (singleActivation)
            {
                var result = DirectoryPartition.AddSingleActivation(address.Grain, address.Activation, address.Silo);
                return result;
            }
            else
            {
                var tag = DirectoryPartition.AddActivation(address.Grain, address.Activation, address.Silo);
                return new AddressAndTag() { Address = address, VersionTag = tag };
            }
        }
  
        public virtual void Unregister(ActivationAddress address, bool force)
        {
            DirectoryPartition.RemoveActivation(address.Grain, address.Activation, force);
        }

        public virtual void Delete(GrainId gid)
        {
            DirectoryPartition.RemoveGrain(gid);
        }


        public virtual Task<AddressAndTag> RegisterAsync(ActivationAddress address, bool singleActivation)
        {
            throw new InvalidOperationException();
        }

        public virtual Task UnregisterAsync(ActivationAddress address, bool force)
        {
            throw new InvalidOperationException();
        }

        public virtual Task DeleteAsync(GrainId gid)
        {
            throw new InvalidOperationException();
        }
    }
}
