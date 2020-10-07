using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Orleans.CodeGeneration;
using Orleans.Runtime;
using Orleans.Runtime.ReminderService;
using Orleans.Services;

namespace Orleans
{
    internal class SiloLoggingHelper : IGrainIdLoggingHelper, ILifecycleParticipant<ISiloLifecycle>
    {
        private readonly IRuntimeClient runtimeClient;
        private readonly Dictionary<int, string> grainServicesNames = new Dictionary<int, string>();

        public SiloLoggingHelper(IRuntimeClient runtimeClient)
        {
            this.runtimeClient = runtimeClient;
        }

        public string GetGrainTypeName(int typeCode) => this.runtimeClient.GrainTypeResolver.GetGrainTypeName(typeCode);

        public string GetSystemTargetName(GrainId grainId)
        {
            // "SystemTarget" can be either a real SystemTarget, or a GrainService
            var name = Constants.SystemTargetName(grainId);
            if (!string.IsNullOrEmpty(name))
            {
                // It was a real SystemTarget
                return name;
            }
            else
            {
                // It should be a grain service
                if (this.grainServicesNames.TryGetValue(grainId.TypeCode, out name))
                    return name;
            }

            return null;
        }

        public void RegisterGrainService(IGrainService service)
        {
            var typeCode = ((ISystemTargetBase)service).GrainId.TypeCode;
            var name = service.GetType().FullName;
            this.grainServicesNames.Add(typeCode, name);
        }

        public void Participate(ISiloLifecycle lifecycle)
        {
            Task Setup(CancellationToken ct)
            {
                GrainId.GrainTypeNameMapper = this;
                return Task.CompletedTask;
            }

            lifecycle.Subscribe<SiloLoggingHelper>(ServiceLifecycleStage.BecomeActive, Setup);
        }
    }
}
