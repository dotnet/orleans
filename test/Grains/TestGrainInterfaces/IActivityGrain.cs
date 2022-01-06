using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using Orleans;

namespace UnitTests.GrainInterfaces
{
    public interface IActivityGrain : IGrainWithIntegerKey
    {
        Task<ActivityData> GetActivityId();
    }

    [GenerateSerializer]
    public class ActivityData
    {
        [Id(0)]
        public string Id { get; set; }

        [Id(1)]
        public string TraceState { get; set; }

        [Id(2)]
        public List<KeyValuePair<string, string>> Baggage { get; set; }
    }
}
