using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Orleans.Runtime;

namespace Orleans
{
    internal class ClientLoggingHelper : IGrainIdLoggingHelper, ILifecycleParticipant<IClusterClientLifecycle>
    {
        private readonly IRuntimeClient runtimeClient;

        public ClientLoggingHelper(IRuntimeClient runtimeClient)
        {
            this.runtimeClient = runtimeClient;
        }

        public string GetGrainTypeName(int typeCode) => this.runtimeClient.GrainTypeResolver.GetGrainTypeName(typeCode);

        public string GetSystemTargetName(GrainId grainId) => Constants.SystemTargetName(grainId);

        public void Participate(IClusterClientLifecycle lifecycle)
        {
            Task Setup(CancellationToken ct)
            {
                GrainId.GrainTypeNameMapper = this;
                return Task.CompletedTask;
            }

            lifecycle.Subscribe<ClientLoggingHelper>(ServiceLifecycleStage.BecomeActive, Setup);
        }
    }
}
