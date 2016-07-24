using System;
using System.Threading.Tasks;

using Orleans.Runtime.Providers;

namespace Orleans.Runtime
{
    internal class TypeManager : SystemTarget, ITypeManager
    {
        private readonly GrainTypeManager grainTypeManager;

        internal TypeManager(SiloAddress myAddr, GrainTypeManager grainTypeManager)
            : base(Constants.TypeManagerId, myAddr)
        {
            this.grainTypeManager = grainTypeManager;
        }


        public Task<IGrainTypeResolver> GetTypeCodeMap(SiloAddress silo)
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


