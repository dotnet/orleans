using Microsoft.Owin.Hosting;
using Orleans.Runtime.Scheduler;
using System;

namespace Orleans.Runtime
{
    internal class HttpApi
    {
        readonly IGrainFactory grainFactory;
        readonly OrleansTaskScheduler taskScheduler;
        readonly ISchedulingContext schedulingContext;
        readonly HttpApiConfiguration configuration;
        readonly Logger logger;

        public HttpApi(IGrainFactory grainFactory, OrleansTaskScheduler taskScheduler, ISchedulingContext schedulingContext, HttpApiConfiguration configuration, Logger logger)
        {
            if (null == grainFactory) throw new ArgumentNullException(nameof(grainFactory));
            if (null == taskScheduler) throw new ArgumentNullException(nameof(taskScheduler));
            if (null == schedulingContext) throw new ArgumentNullException(nameof(schedulingContext));
            if (null == logger) throw new ArgumentNullException(nameof(logger));


            this.grainFactory = grainFactory;
            this.taskScheduler = taskScheduler;
            this.schedulingContext = schedulingContext;
            this.configuration = configuration;
            this.logger = logger;
        }

        /// <summary>
        /// Start an HTTP endpoint
        /// </summary>
        /// <returns>The HTTP listener, can be null</returns>
        public IDisposable Start()
        {
            if (!this.configuration.Enable) return null;

            try
            {
                var router = new Router();
                new GrainController(router, this.taskScheduler, this.grainFactory, this.schedulingContext);

                var options = new StartOptions
                {
                    ServerFactory = "Nowin",
                    Port = configuration.Port
                };

                var webServer = WebApp.Start(options, app => new WebServer(router, configuration.Username, configuration.Password).Configure(app));
                
                if (this.logger.IsVerbose) this.logger.Verbose($"HTTP API listening on {options.Port}");
                return webServer;
            }
            catch (Exception ex)
            {
                logger.Error(
                    ErrorCode.SiloHttpServerFailedToStart, 
                    string.Format("Failed to start HTTP server on port {0}", configuration.Port), 
                    ex);

                return null;
            }
            
        }

    }
}
