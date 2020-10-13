using System.Collections.Generic;
using Orleans.ApplicationParts;
using Orleans.Runtime;
using Orleans.Services;

namespace Orleans
{
    internal class SiloLoggingHelper : BaseLoggingHelper<ISiloLifecycle>
    {
        private readonly Dictionary<int, string> grainServicesNames = new Dictionary<int, string>();

        public SiloLoggingHelper(IRuntimeClient runtimeClient, IApplicationPartManager applicationPartManager)
            : base(runtimeClient, applicationPartManager)
        {
        }

        public override string GetSystemTargetName(GrainId grainId)
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
    }
}
