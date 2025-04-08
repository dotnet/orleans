using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

#nullable enable
namespace Orleans.Runtime.MembershipService.SiloMetadata;

internal sealed class SiloMetadataSystemTarget : SystemTarget, ISiloMetadataSystemTarget, ILifecycleParticipant<ISiloLifecycle>
{
    private readonly SiloMetadata _siloMetadata;

    public SiloMetadataSystemTarget(
        IOptions<SiloMetadata> siloMetadata,
        SystemTargetShared shared) : base(Constants.SiloMetadataType, shared)
    {
        _siloMetadata = siloMetadata.Value;
        shared.ActivationDirectory.RecordNewTarget(this);
    }

    public Task<SiloMetadata> GetSiloMetadata() => Task.FromResult(_siloMetadata);
    void ILifecycleParticipant<ISiloLifecycle>.Participate(ISiloLifecycle lifecycle)
    {
        // We don't participate in any lifecycle stages: activating this instance is all that is necessary.
    }
}