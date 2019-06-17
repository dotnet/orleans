using Orleans.Runtime;
using System;

namespace NonSilo.Tests.Utilities
{
    internal class DelegateAsyncTimerFactory : IAsyncTimerFactory
    {
        private readonly Func<TimeSpan, string, IAsyncTimer> create;

        public DelegateAsyncTimerFactory(Func<TimeSpan, string, IAsyncTimer> create)
        {
            this.create = create;
        }

        public IAsyncTimer Create(TimeSpan period, string name) => this.create(period, name);
    }
}
