using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Orleans;

namespace TestGrainInterfaces
{
    public interface IGeneratedEventReporterGrain : IGrainWithGuidKey
    {
        Task ReportResult(Guid streamGuid, string streamProvider, string streamNamespace, int count);

        Task<IDictionary<Guid,int>> GetReport(string streamProvider, string streamNamespace);

        Task Reset();
    }
}
