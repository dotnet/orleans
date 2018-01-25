using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Orleans.Runtime
{
    internal class SiloOptionsLogger : OptionsLogger, ILifecycleParticipant<ISiloLifecycle>
    {
        private int SiloOptionLoggerLifeCycleRing = int.MinValue;
        public SiloOptionsLogger(ILogger<SiloOptionsLogger> logger, IServiceProvider services)
            : base(logger, services)
        {
        }

        public void Participate(ISiloLifecycle lifecycle)
        {
            lifecycle.Subscribe(SiloOptionLoggerLifeCycleRing, this.OnStart);
        }

        public Task OnStart(CancellationToken token)
        {
            this.LogOptions();
            return Task.CompletedTask;
        }
    }
}
