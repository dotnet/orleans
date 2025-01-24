using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

#nullable enable
namespace Orleans.Runtime.MembershipService.SiloMetadata;

public class SiloMetadataGrainService : GrainService, ISiloMetadataGrainService
{
    private readonly SiloMetadata _siloMetadata;

    public SiloMetadataGrainService(IOptions<SiloMetadata> siloMetadata) : base()
    {
        _siloMetadata = siloMetadata.Value;
    }

    public SiloMetadataGrainService(IOptions<SiloMetadata> siloMetadata, GrainId grainId, Silo silo, ILoggerFactory loggerFactory) : base(grainId, silo, loggerFactory)
    {
        _siloMetadata = siloMetadata.Value;
    }

    public Task<SiloMetadata> GetSiloMetadata() => Task.FromResult(_siloMetadata);
}