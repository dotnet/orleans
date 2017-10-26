using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Orleans
{
    using Orleans.Runtime;

    internal class UnobservedExceptionsHandler : IDisposable
    {
        private readonly ILogger logger;
        private UnobservedExceptionDelegate unobservedExceptionHandler;

        internal delegate void UnobservedExceptionDelegate(ISchedulingContext context, Exception exception);
        
        public UnobservedExceptionsHandler(ILogger<UnobservedExceptionsHandler> logger)
        {
            this.logger = logger;
            TaskScheduler.UnobservedTaskException += InternalUnobservedTaskExceptionHandler;
        }


        internal bool TrySetUnobservedExceptionHandler(UnobservedExceptionDelegate handler)
        {
            if (handler == null) throw new ArgumentNullException(nameof(handler));
            if (unobservedExceptionHandler != null)
            {
                return false;
            }
            unobservedExceptionHandler = handler;

            return true;
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
                if (unobservedExceptionHandler != null)
                {
                    unobservedExceptionHandler(context, baseException);
                }
            }
            finally
            {
                if (e.Observed)
                {
                    logger.Info(ErrorCode.Runtime_Error_100311, "UnobservedExceptionsHandlerClass caught an UnobservedTaskException which was successfully observed and recovered from. BaseException = {0}. Exception = {1}",
                            baseException.Message, LogFormatter.PrintException(aggrException));
                }
                else
                {
                    var errorStr = String.Format("UnobservedExceptionsHandlerClass Caught an UnobservedTaskException event sent by {0}. Exception = {1}",
                            OrleansTaskExtentions.ToString((Task)sender), LogFormatter.PrintException(aggrException));
                    logger.Error(ErrorCode.Runtime_Error_100005, errorStr);
                    logger.Error(ErrorCode.Runtime_Error_100006, "Exception remained UnObserved!!! The subsequent behavior depends on the ThrowUnobservedTaskExceptions setting in app config and .NET version.");
                }
            }
        }

        public void Dispose()
        {
            TaskScheduler.UnobservedTaskException -= InternalUnobservedTaskExceptionHandler;
            unobservedExceptionHandler = null;
        }
    }
}
