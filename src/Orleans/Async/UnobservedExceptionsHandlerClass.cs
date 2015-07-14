/*
Project Orleans Cloud Service SDK ver. 1.0
 
Copyright (c) Microsoft Corporation
 
All rights reserved.
 
MIT License

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and 
associated documentation files (the ""Software""), to deal in the Software without restriction,
including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense,
and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so,
subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED *AS IS*, WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO
THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS
OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT,
TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
*/

using System;
using System.Threading.Tasks;

namespace Orleans
{
    using Orleans.Runtime;

    internal class UnobservedExceptionsHandlerClass
    {
        private static readonly Object lockObject = new Object();
        private static readonly TraceLogger logger = TraceLogger.GetLogger("UnobservedExceptionHandler");
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
                            baseException.Message, TraceLogger.PrintException(aggrException));
                }
                else
                {
                    var errorStr = String.Format("UnobservedExceptionsHandlerClass Caught an UnobservedTaskException event sent by {0}. Exception = {1}",
                            OrleansTaskExtentions.ToString((Task)sender), TraceLogger.PrintException(aggrException));
                    logger.Error(ErrorCode.Runtime_Error_100005, errorStr);
                    logger.Error(ErrorCode.Runtime_Error_100006, "Exception remained UnObserved!!! The subsequent behaivour depends on the ThrowUnobservedTaskExceptions setting in app config and .NET version.");
                }
            }
        }
    }
}
