using System;
using System.Threading.Tasks;
using Orleans.GrainDirectory;

namespace Orleans.Runtime.GrainDirectory
{
    /// <summary>
    /// The registrar for the Cluster-Local Registration Strategy.
    /// </summary>
    internal class ClusterLocalRegistrar : IGrainRegistrar
    {
        private readonly GrainDirectoryPartition directoryPartition;

        public ClusterLocalRegistrar(GrainDirectoryPartition partition)
        {
            directoryPartition = partition;
        }

        public bool IsSynchronous { get { return true; } }

        public virtual AddressAndTag Register(ActivationAddress address, bool singleActivation)
        {
            if (singleActivation)
            {
                var result = directoryPartition.AddSingleActivation(address.Grain, address.Activation, address.Silo);
                return result;
            }
            else
            {
                var tag = directoryPartition.AddActivation(address.Grain, address.Activation, address.Silo);
                return new AddressAndTag() { Address = address, VersionTag = tag };
            }
        }
  
        public virtual void Unregister(ActivationAddress address, bool force)
        {
            directoryPartition.RemoveActivation(address.Grain, address.Activation, force);
        }

        public virtual void Delete(GrainId gid)
        {
            directoryPartition.RemoveGrain(gid);
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
