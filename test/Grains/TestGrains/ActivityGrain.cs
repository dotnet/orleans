using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Orleans;
using UnitTests.GrainInterfaces;

namespace UnitTests.Grains
{
    public class ActivityGrain : Grain, IActivityGrain
    {
        public Task<ActivityData> GetActivityId()
        {
            var activity = Activity.Current;
            if (activity == null)
            {
                return null;
            }

            var result = new ActivityData() { Id = activity.Id, TraceState = activity.TraceStateString };

            return Task.FromResult(result);
        }
    }
}
