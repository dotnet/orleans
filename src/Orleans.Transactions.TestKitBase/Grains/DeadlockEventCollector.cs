using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;
using Orleans.Concurrency;

namespace Orleans.Transactions.TestKit.Base.Grains
{
    public class DeadlockEventCollector : Grain, IDeadlockEventCollector
    {
        private readonly List<DeadlockEvent> events = new List<DeadlockEvent>();

        public Task ReportEvent(DeadlockEvent @event)
        {
            this.events.Add(@event);
            return Task.CompletedTask;
        }

        public Task<IList<DeadlockEvent>> GetEvents() => Task.FromResult<IList<DeadlockEvent>>(this.events.ToArray());

        public Task Clear()
        {
            this.events.Clear();
            return Task.CompletedTask;
        }
    }
}