using Orleans.Runtime;

namespace TestGrainInterfaces
{
    public interface IGeneratedEventReporterGrain : IGrainWithGuidKey
    {
        Task ReportResult(Guid streamGuid, string streamProvider, string streamNamespace, int count);

        Task<IDictionary<Guid, int>> GetReport(string streamProvider, string streamNamespace, CancellationToken cancellationToken = default);

        Task Reset(CancellationToken cancellationToken = default);

        Task<bool> IsLocatedOnSilo(SiloAddress siloAddress);
    }
}
