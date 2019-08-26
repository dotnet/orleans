using System.Threading.Tasks;
using Orleans.Runtime;
using System;

namespace NonSilo.Tests.Utilities
{
    internal class DelegateAsyncTimer : IAsyncTimer
    {
        private readonly Func<TimeSpan?, Task<bool>> nextTick;

        public DelegateAsyncTimer(Func<TimeSpan?, Task<bool>> nextTick)
        {
            this.nextTick = nextTick;
        }

        public int DisposedCounter { get; private set; }

        public Task<bool> NextTick(TimeSpan? overrideDelay = null) => this.nextTick(overrideDelay);

        public bool CheckHealth(DateTime lastCheckTime) => true;

        public void Dispose() => ++this.DisposedCounter;
    }
}
