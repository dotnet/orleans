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

    public class ActivityData
    {
        public string Id { get; set; }

        public string TraceState { get; set; }

        public List<KeyValuePair<string, string>> Baggage { get; set; }
    }
}
