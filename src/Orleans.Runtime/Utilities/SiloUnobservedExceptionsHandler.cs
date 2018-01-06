using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Orleans.Runtime.Scheduler;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Orleans.Hosting;

namespace Orleans.Runtime
{
    public static class SiloUnobservedExceptionsHandlerServiceProviderExtensions
    {
        internal static void InitializeSiloUnobservedExceptionsHandler(this IServiceProvider services)
        {
            //resolve handler from DI to initialize it
            var ignore = services.GetService<SiloUnobservedExceptionsHandler>();
        }

        /// <summary>
        /// Configure silo with unobserved exception handler
        /// </summary>
        /// <param name="services"></param>
        public static void UseSiloUnobservedExceptionsHandler(this ISiloHostBuilder siloBuilder)
        {
            siloBuilder.ConfigureServices(services => services.TryAddSingleton<SiloUnobservedExceptionsHandler>());
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
            var tplTask = (Task)sender;
            var contextObj = tplTask.AsyncState;
            var context = contextObj as ISchedulingContext;

            try
            {
                UnobservedExceptionHandler(context, baseException);
            }
            finally
            {
                if (e.Observed)
                {
                    logger.Info(ErrorCode.Runtime_Error_100311, "Silo caught an UnobservedTaskException which was successfully observed and recovered from. BaseException = {0}. Exception = {1}",
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

        private void UnobservedExceptionHandler(ISchedulingContext context, Exception exception)
        {
            var schedulingContext = context as SchedulingContext;
            if (schedulingContext == null)
            {
                if (context == null)
                    logger.Error(ErrorCode.Runtime_Error_100102, "Silo caught an UnobservedException with context==null.", exception);
                else
                    logger.Error(ErrorCode.Runtime_Error_100103, String.Format("Silo caught an UnobservedException with context of type different than OrleansContext. The type of the context is {0}. The context is {1}",
                        context.GetType(), context), exception);
            }
            else
            {
                logger.Error(ErrorCode.Runtime_Error_100104, String.Format("Silo caught an UnobservedException thrown by {0}.", schedulingContext.Activation), exception);
            }
        }

        private void DomainUnobservedExceptionHandler(object context, UnhandledExceptionEventArgs args)
        {
            var exception = (Exception)args.ExceptionObject;
            if (context is ISchedulingContext)
                UnobservedExceptionHandler(context as ISchedulingContext, exception);
            else
                logger.Error(ErrorCode.Runtime_Error_100324, String.Format("Called DomainUnobservedExceptionHandler with context {0}.", context), exception);
        }

        public void Dispose()
        {
            TaskScheduler.UnobservedTaskException -= InternalUnobservedTaskExceptionHandler;
            AppDomain.CurrentDomain.UnhandledException -= this.DomainUnobservedExceptionHandler;
        }
    }
}
