using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Orleans
{ 
    internal class ClientOptionsLogger : OptionsLogger, ILifecycleParticipant<IClusterClientLifecycle>
    {
        private int ClientOptionLoggerLifeCycleRing = int.MinValue;
        public ClientOptionsLogger(ILogger<ClientOptionsLogger> logger, IServiceProvider services)
            : base(logger, services)
        {
        }

        public void Participate(IClusterClientLifecycle lifecycle)
        {
            lifecycle.Subscribe(ClientOptionLoggerLifeCycleRing, this.OnStart);
        }

        public Task OnStart(CancellationToken token)
        {
            this.LogOptions();
            return Task.CompletedTask;
        }
    }

    public abstract class OptionsLogger 
    {
        private ILogger logger;
        private IServiceProvider services;
        protected OptionsLogger(ILogger logger, IServiceProvider services)
        {
            this.logger = logger;
            this.services = services;
        }
        public void LogOptions()
        {
            var optionFormatters = services.GetServices<IOptionFormatter>();
            foreach (var optionFormatter in optionFormatters)
            {
                LogOption(optionFormatter);
            }
        }

        private void LogOption(IOptionFormatter formatter)
        {
            var stringBuiler = new StringBuilder();
            stringBuiler.AppendLine($"Configuration {formatter.Name}: ");
            foreach (var setting in formatter.Format())
            {
                stringBuiler.AppendLine($"{setting}");
            }
            this.logger.LogInformation(stringBuiler.ToString());
        }
    }
}
