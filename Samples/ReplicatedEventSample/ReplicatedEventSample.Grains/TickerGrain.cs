using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using ReplicatedEventSample.Interfaces;

using Orleans;
using Orleans.MultiCluster;

namespace ReplicatedEventSample.Grains
{

    /// <summary>
    ///  These grains records interesting things that happen in events
    ///  there is one of these per cluster
    ///  it receives updates from the event grains in the same cluster
    /// </summary>
    [OneInstancePerCluster]
    public class TickerGrain : Grain, ITickerGrain
    {

        private string last_thing_that_happened;
        private DateTime timestamp;

        public Task SomethingHappened(string what)
        {
            if (what == null)
                throw new ArgumentNullException("what");

            last_thing_that_happened = what;
            timestamp = DateTime.UtcNow;

            return TaskDone.Done;
        }      

        public Task<string> GetTickerLine()
        {
         
            if (last_thing_that_happened == null 
                || timestamp + TimeSpan.FromSeconds(30) < DateTime.UtcNow)
                return Task.FromResult("no news right now");

            else
                return Task.FromResult(last_thing_that_happened);
        }
    }
}
