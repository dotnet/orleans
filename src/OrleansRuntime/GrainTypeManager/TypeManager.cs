using System;
using System.Threading.Tasks;
using Orleans.Runtime.Providers;

namespace Orleans.Runtime
{
    internal class TypeManager : SystemTarget, IClusterTypeManager, ISiloTypeManager
    {
        private readonly GrainTypeManager grainTypeManager;

        internal TypeManager(SiloAddress myAddr, GrainTypeManager grainTypeManager)
            : base(Constants.TypeManagerId, myAddr)
        {
            this.grainTypeManager = grainTypeManager;
        }


        public Task<IGrainTypeResolver> GetClusterTypeCodeMap()
        {
            return Task.FromResult<IGrainTypeResolver>(grainTypeManager.ClusterGrainInterfaceMap);
        }

        public Task<GrainInterfaceMap> GetSiloTypeCodeMap()
        {
            return Task.FromResult(grainTypeManager.GetTypeCodeMap());
        }

        public Task<Streams.ImplicitStreamSubscriberTable> GetImplicitStreamSubscriberTable(SiloAddress silo)
        {
            Streams.ImplicitStreamSubscriberTable table = SiloProviderRuntime.Instance.ImplicitStreamSubscriberTable;
            if (null == table)
            {
                throw new InvalidOperationException("the implicit stream subscriber table is not initialized");
            }
            return Task.FromResult(table);
        }
    }
}


