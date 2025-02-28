using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

#nullable enable
namespace Orleans.Runtime.MembershipService.SiloMetadata;

internal sealed class SiloMetadataSystemTarget(
    IOptions<SiloMetadata> siloMetadata,
    ILocalSiloDetails localSiloDetails,
    ILoggerFactory loggerFactory,
    IServiceProvider serviceProvider)
    : SystemTarget(Constants.SiloMetadataType, localSiloDetails.SiloAddress, loggerFactory), ISiloMetadataSystemTarget, ILifecycleParticipant<ISiloLifecycle>
{
    private readonly SiloMetadata _siloMetadata = siloMetadata.Value;

    public Task<SiloMetadata> GetSiloMetadata() => Task.FromResult(_siloMetadata);

    void ILifecycleParticipant<ISiloLifecycle>.Participate(ISiloLifecycle lifecycle)
    {
        lifecycle.Subscribe(nameof(SiloMetadataSystemTarget), ServiceLifecycleStage.RuntimeInitialize, OnRuntimeInitializeStart, OnRuntimeInitializeStop);

        Task OnRuntimeInitializeStart(CancellationToken token)
        {
            serviceProvider.GetRequiredService<Catalog>().RegisterSystemTarget(this);
            return Task.CompletedTask;
        }

        Task OnRuntimeInitializeStop(CancellationToken token) => Task.CompletedTask;
    }
}