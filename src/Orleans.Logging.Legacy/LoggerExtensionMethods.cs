using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orleans.Logging.Legacy;
using Orleans.Providers;
using Orleans.Runtime;
using System;
using System.Collections.Generic;
using System.Text;

namespace Orleans.Runtime
{
    [Obsolete(OrleansLoggingUtils.ObsoleteMessageStringForLegacyLoggingInfrastructure)]
    public static class LoggerExtensionMethods
    {
        /// <summary>
        /// Returns a logger object that this grain's code can use for tracing.
        /// </summary>
        /// <returns>Name of the logger to use.</returns>
        public static Logger GetLogger(this Grain grain, string loggerName)
        {
            if(grain.Runtime == null)
                throw new InvalidOperationException("Grain was created outside of the Orleans creation process and no runtime was specified.");
            var loggerFactory = grain.Runtime.ServiceProvider.GetRequiredService<ILoggerFactory>();
            return new LoggerWrapper(loggerName, loggerFactory);
        }
        private static object GrainLoggerKey = new object();
        /// <summary>
        /// Returns a logger object that this grain's code can use for tracing.
        /// The name of the logger will be derived from the grain class name.
        /// </summary>
        /// <returns>A logger for this grain.</returns>
        public static Logger GetLogger(this Grain grain)
        {
            var grainActivatinContext = grain.Data as IGrainActivationContext;
            if (grainActivatinContext == null)
            {
                return grain.GetLogger(grain.GetType().FullName);
            }
            else
            {
                if (!grainActivatinContext.Items.TryGetValue(GrainLoggerKey, out var grainLogger))
                {
                    grainLogger = grain.GetLogger(grain.GetType().FullName);
                    grainActivatinContext.Items[GrainLoggerKey] = grainLogger;
                }
                return grainLogger as Logger;
            }
        }

        /// <summary>
        /// Extension method GetLogger for IGrainRuntime
        /// </summary>
        public static Logger GetLogger(this IGrainRuntime runtime, string loggerName)
        {
            return new LoggerWrapper(loggerName, runtime.ServiceProvider.GetRequiredService<ILoggerFactory>());
        }


        /// <summary>
        /// Provides a logger to be used by the provider. 
        /// </summary>
        /// <param name="loggerName">Name of the logger being requested.</param>
        /// <returns>Object reference to the requested logger.</returns>
        /// <seealso cref="LoggerType"/>
        public static Logger GetLogger(this IProviderRuntime runtime, string loggerName)
        {
            return new LoggerWrapper(loggerName, runtime.ServiceProvider.GetRequiredService<ILoggerFactory>());
        }



        /// <summary>
        /// Provides logging facility for applications.
        /// </summary>
        /// <exception cref="InvalidOperationException">Thrown if Orleans runtime is not correctly initialized before this call.</exception>
        public static Logger Logger(this IClusterClient client)
        {
            var loggerFactory = client.ServiceProvider?.GetRequiredService<ILoggerFactory>();
            if (loggerFactory == null)
                throw new InvalidOperationException("Client has been disposed or haven't finished initialization");
            return new LoggerWrapper("Application", loggerFactory);
        }
    }
}
