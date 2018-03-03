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
            lifecycle.Subscribe<ClientOptionsLogger>(ClientOptionLoggerLifeCycleRing, this.OnStart);
        }

        public Task OnStart(CancellationToken token)
        {
            this.LogOptions();
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// Base class for client and silo default options loggers.
    /// </summary>
    public abstract class OptionsLogger 
    {
        private ILogger logger;
        private IServiceProvider services;
        protected OptionsLogger(ILogger logger, IServiceProvider services)
        {
            this.logger = logger;
            this.services = services;
        }

        /// <summary>
        /// Log all options with registered formatters
        /// </summary>
        public void LogOptions()
        {
            this.LogOptions(services.GetServices<IOptionFormatter>());
        }

        /// <summary>
        /// Log options using a set of formatters
        /// </summary>
        /// <param name="formatters"></param>
        public void LogOptions(IEnumerable<IOptionFormatter> formatters)
        {
            foreach (var optionFormatter in formatters.OrderBy(f => f.Name))
            {
                this.LogOption(optionFormatter);
            }
        }

        /// <summary>
        /// Log an options using a formatter
        /// </summary>
        /// <param name="formatter"></param>
        public void LogOption(IOptionFormatter formatter)
        {
            try
            {
                var stringBuiler = new StringBuilder();
                stringBuiler.AppendLine($"Configuration {formatter.Name}: ");
                foreach (var setting in formatter.Format())
                {
                    stringBuiler.AppendLine($"{setting}");
                }
                this.logger.LogInformation(stringBuiler.ToString());
            } catch(Exception ex)
            {
                this.logger.LogError(ex, $"An error occured while logging options {formatter.Name}", formatter.Name);
                throw;
            }
        }
    }
}
