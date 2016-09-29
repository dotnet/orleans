using System;
using System.Threading.Tasks;

namespace Orleans
{
    using Orleans.Runtime;

    internal class UnobservedExceptionsHandlerClass
    {
        private static readonly Object lockObject = new Object();
        private static readonly Logger logger = LogManager.GetLogger("UnobservedExceptionHandler");
        private static UnobservedExceptionDelegate unobservedExceptionHandler;
        private static readonly bool alreadySubscribedToTplEvent = false;

        internal delegate void UnobservedExceptionDelegate(ISchedulingContext context, Exception exception);
        
        static UnobservedExceptionsHandlerClass()
        {
            lock (lockObject)
            {
                if (!alreadySubscribedToTplEvent)
                {
                    TaskScheduler.UnobservedTaskException += InternalUnobservedTaskExceptionHandler;
                    alreadySubscribedToTplEvent = true;
                }
            }
        }

        internal static void SetUnobservedExceptionHandler(UnobservedExceptionDelegate handler)
        {
            lock (lockObject)
            {
                if (unobservedExceptionHandler != null && handler != null)
                {
                    throw new InvalidOperationException("Calling SetUnobservedExceptionHandler the second time.");
                }
                unobservedExceptionHandler = handler;
            }
        }

        internal static void ResetUnobservedExceptionHandler()
        {
            lock (lockObject)
            {
                unobservedExceptionHandler = null;
            }
        }

        private static void InternalUnobservedTaskExceptionHandler(object sender, UnobservedTaskExceptionEventArgs e)
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
    }
}
