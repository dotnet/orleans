using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Orleans.Runtime.Scheduler;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Orleans.Hosting;
using Orleans.Internal;

namespace Orleans.Runtime
{
    public static class SiloUnobservedExceptionsHandlerServiceProviderExtensions
    {
        internal static void InitializeSiloUnobservedExceptionsHandler(this IServiceProvider services)
        {
            //resolve handler from DI to initialize it
            _ = services.GetService<SiloUnobservedExceptionsHandler>();
        }

        /// <summary>
        /// Configure silo with unobserved exception handler
        /// </summary>
        public static ISiloBuilder UseSiloUnobservedExceptionsHandler(this ISiloBuilder siloBuilder)
        {
            siloBuilder.ConfigureServices(services => services.TryAddSingleton<SiloUnobservedExceptionsHandler>());
            return siloBuilder;
        }

        /// <summary>
        /// Configure silo with unobserved exception handler
        /// </summary>
        public static ISiloHostBuilder UseSiloUnobservedExceptionsHandler(this ISiloHostBuilder siloBuilder)
        {
            siloBuilder.ConfigureServices(services => services.TryAddSingleton<SiloUnobservedExceptionsHandler>());
            return siloBuilder;
        }
    }

    internal class SiloUnobservedExceptionsHandler : IDisposable
    {
        private readonly ILogger logger;

        public SiloUnobservedExceptionsHandler(ILogger<SiloUnobservedExceptionsHandler> logger)
        {
            this.logger = logger;
            AppDomain.CurrentDomain.UnhandledException += this.DomainUnobservedExceptionHandler;
            TaskScheduler.UnobservedTaskException += InternalUnobservedTaskExceptionHandler;
        }

        private void InternalUnobservedTaskExceptionHandler(object sender, UnobservedTaskExceptionEventArgs e)
        {
            var aggrException = e.Exception;
            var baseException = aggrException.GetBaseException();
            var context = (sender as Task)?.AsyncState;

            try
            {
                this.logger.LogError((int)ErrorCode.Runtime_Error_100104, baseException, "Silo caught an unobserved exception thrown from context {Context}: {Exception}", context, baseException);
            }
            finally
            {
                if (e.Observed)
                {
                    logger.Info(ErrorCode.Runtime_Error_100311, "Silo caught an unobserved exception which was successfully observed and recovered from. BaseException = {0}. Exception = {1}",
                            baseException.Message, LogFormatter.PrintException(aggrException));
                }
                else
                {
                    var errorStr = String.Format("Silo Caught an UnobservedTaskException event sent by {0}. Exception = {1}",
                            OrleansTaskExtentions.ToString((Task)sender), LogFormatter.PrintException(aggrException));
                    logger.Error(ErrorCode.Runtime_Error_100005, errorStr);
                    logger.Error(ErrorCode.Runtime_Error_100006, "Exception remained UnObserved!!! The subsequent behavior depends on the ThrowUnobservedTaskExceptions setting in app config and .NET version.");
                }
            }
        }

        private void DomainUnobservedExceptionHandler(object context, UnhandledExceptionEventArgs args)
        {
            var exception = (Exception)args.ExceptionObject;
            logger.LogError((int)ErrorCode.Runtime_Error_100324, exception, "Silo caught an unobserved exception thrown from context {Context}: {Exception}", context, exception);
        }

        public void Dispose()
        {
            TaskScheduler.UnobservedTaskException -= InternalUnobservedTaskExceptionHandler;
            AppDomain.CurrentDomain.UnhandledException -= this.DomainUnobservedExceptionHandler;
        }
    }
}
