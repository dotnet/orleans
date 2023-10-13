using System.Collections.Specialized;
using Microsoft.Extensions.Logging;
using UnitTests.GrainInterfaces;

namespace UnitTests.Grains
{
    public class ExternalTypeGrain : Grain, IExternalTypeGrain
    {
        private readonly ILogger<ExternalTypeGrain> logger;

        public ExternalTypeGrain(ILogger<ExternalTypeGrain> logger)
        {
            this.logger = logger;
        }

        public Task GetAbstractModel(IEnumerable<NameObjectCollectionBase> list)
        {
            this.logger.LogDebug("GetAbstractModel: Success");
            return Task.CompletedTask;
        }

        public Task<EnumClass> GetEnumModel()
        {
            return Task.FromResult( new EnumClass() { EnumsList = new List<DateTimeKind>() { DateTimeKind.Local } });
        }
    }
}
