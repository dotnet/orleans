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
        private int ClientOptionLoggerLifeCycleRing = 1000;
        public ClientOptionsLogger(ILogger<ClientOptionsLogger> logger, IServiceProvider services)
            : base(logger, services)
        {
        }

        public void Participate(IClusterClientLifecycle lifecycle)
        {
            lifecycle.Subscribe(ClientOptionLoggerLifeCycleRing, this.OnRuntimeInitializeStart);
        }

        public Task OnRuntimeInitializeStart(CancellationToken token)
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
            this.logger.LogInformation("Starting with following configuration:");
            var optionFormatters = services.GetServices<IOptionFormatter>();
            foreach (var formatterGroup in optionFormatters
                .GroupBy(optionFormatter => optionFormatter.Category)
                .OrderBy(group => group.Key))
            {
                int spaceNum = 0;
                //log out category if there's one
                if (!string.IsNullOrEmpty(formatterGroup.Key))
                {
                    this.logger.LogInformation($"{CreateNSpace(spaceNum)}{formatterGroup.Key}:");
                    spaceNum += 2;
                }

                foreach (var optionFormatter in formatterGroup)
                {
                    LogOption(optionFormatter, spaceNum);
                }

            }
        }

        private void LogOption(IOptionFormatter formatter, int spaces)
        {
            this.logger.LogInformation($"{CreateNSpace(spaces)}{formatter.Name}:");
            foreach (var setting in formatter.Format())
            {
                this.logger.LogInformation($"{CreateNSpace(spaces + 2)}{setting}");
            }
        }

        private string CreateNSpace(int n)
        {
            var builder = new StringBuilder();
            while (n-- > 0)
            {
                builder.Append(" ");
            }
            return builder.ToString();
        }
    }
}
