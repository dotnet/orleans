using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Orleans.Runtime
{
    internal class SiloOptionsLogger : OptionsLogger, ILifecycleParticipant<ISiloLifecycle>
    {
        public SiloOptionsLogger(ILogger<SiloOptionsLogger> logger, IServiceProvider services)
            : base(logger, services)
        {
        }

        public void Participate(ISiloLifecycle lifecycle)
        {
            lifecycle.Subscribe<SiloOptionsLogger>(ServiceLifecycleStage.First, OnStart);
        }

        public Task OnStart(CancellationToken token)
        {
            LogOptions();
            return Task.CompletedTask;
        }
    }
}
