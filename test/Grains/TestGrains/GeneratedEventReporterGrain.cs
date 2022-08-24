using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Orleans;
using Orleans.Runtime;
using TestGrainInterfaces;

namespace TestGrains
{
    class GeneratedEventReporterGrain : Grain, IGeneratedEventReporterGrain
    {
        private ILogger logger;

        private Dictionary<Tuple<string, string>, Dictionary<Guid, int>> reports;

        public GeneratedEventReporterGrain(ILoggerFactory loggerFactory)
        {
            this.logger = loggerFactory.CreateLogger($"{this.GetType().Name}-{this.IdentityString}");
        }

        public override Task OnActivateAsync(CancellationToken cancellationToken)
        {
            logger.LogInformation("OnActivateAsync");

            reports = new Dictionary<Tuple<string, string>, Dictionary<Guid, int>>();
            return base.OnActivateAsync(cancellationToken);
        }

        public Task ReportResult(Guid streamGuid, string streamProvider, string streamNamespace, int count)
        {
            Dictionary<Guid, int> counts;
            Tuple<string, string> key = Tuple.Create(streamProvider, streamNamespace);
            if (!reports.TryGetValue(key, out counts))
            {
                counts = new Dictionary<Guid, int>();
                reports[key] = counts;
            }

            logger.LogInformation(
                "ReportResult. StreamProvider: {StreamProvider}, StreamNamespace: {StreamNamespace}, StreamGuid: {StreamGuid}, Count: {Count}",
                streamProvider,
                streamNamespace,
                streamGuid,
                count);
            counts[streamGuid] = count;
            return Task.CompletedTask;
        }

        public Task<IDictionary<Guid, int>> GetReport(string streamProvider, string streamNamespace)
        {
            Dictionary<Guid, int> counts;
            Tuple<string, string> key = Tuple.Create(streamProvider, streamNamespace);
            if (!reports.TryGetValue(key, out counts))
            {
                return Task.FromResult<IDictionary<Guid, int>>(new Dictionary<Guid, int>());
            }
            return Task.FromResult<IDictionary<Guid, int>>(counts);
        }

        public Task Reset()
        {
            reports = new Dictionary<Tuple<string, string>, Dictionary<Guid, int>>();
            return Task.CompletedTask;
        }

        public Task<bool> IsLocatedOnSilo(SiloAddress siloAddress)
        {
            return Task.FromResult(RuntimeIdentity == siloAddress.ToString());
        }
    }
}
