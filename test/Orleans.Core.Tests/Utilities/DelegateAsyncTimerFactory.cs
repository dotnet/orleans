using Orleans.Runtime;

namespace NonSilo.Tests.Utilities
{
    internal class DelegateAsyncTimerFactory : IAsyncTimerFactory
    {
        public DelegateAsyncTimerFactory(Func<TimeSpan, string, IAsyncTimer> create)
        {
            this.CreateDelegate = create;
        }

        public Func<TimeSpan, string, IAsyncTimer> CreateDelegate { get; set; }

        public IAsyncTimer Create(TimeSpan period, string name) => this.CreateDelegate(period, name);
    }
}
