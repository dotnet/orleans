using System;
using System.Configuration;
using System.Threading.Tasks;

using Orleans.Runtime;

namespace Orleans
{
    using System.Runtime.ExceptionServices;

    /// <summary>
    /// This class a convinent utiliity class to execute a certain asyncronous function with retires, 
    /// allowing to specify custom retry filters and policies.
    /// </summary>
    internal static class AsyncExecutorWithRetries
    {
        public static readonly int INFINITE_RETRIES = -1;
        private static readonly Func<Exception, int, bool> retryAllExceptionsFilter = (Exception exc, int i) => true;

        /// <summary>
        /// Execute a given function a number of times, based on retry configuration parameters.
        /// </summary>
        public static Task ExecuteWithRetries(
            Func<int, Task> action,
            int maxNumErrorTries,
            Func<Exception, int, bool> retryExceptionFilter,
            TimeSpan maxExecutionTime,
            IBackoffProvider onErrorBackOff)
        {
            Func<int, Task<bool>> function = async (int i) => { await action(i); return true; };
            return ExecuteWithRetriesHelper<bool>(
                function,
                0,
                0,
                maxNumErrorTries,
                maxExecutionTime,
                DateTime.UtcNow,
                null,
                retryExceptionFilter,
                null,
                onErrorBackOff);
        }

        /// <summary>
        /// Execute a given function a number of times, based on retry configuration parameters.
        /// </summary>
        public static Task<T> ExecuteWithRetries<T>(
            Func<int, Task<T>> function,
            int maxNumErrorTries,
            Func<Exception, int, bool> retryExceptionFilter,
            TimeSpan maxExecutionTime,
            IBackoffProvider onErrorBackOff)
        {
            return ExecuteWithRetries<T>(
                function,
                0,
                maxNumErrorTries,
                null,
                retryExceptionFilter,
                maxExecutionTime,
                null,
                onErrorBackOff);
        }

        /// <summary>
        /// Execute a given function a number of times, based on retry configuration parameters.
        /// </summary>
        /// <param name="function">Function to execute</param>
        /// <param name="maxNumSuccessTries">Maximal number of successful execution attempts. 
        /// ExecuteWithRetries will try to re-execute the given function again if directed so by retryValueFilter.
        /// Set to -1 for unlimited number of success retries, until retryValueFilter is satisfied.
        /// Set to 0 for only one success attempt, which will cause retryValueFilter to be ignored and the given function executed only once until first success.</param>
        /// <param name="maxNumErrorTries">Maximal number of execution attempts due to errors.
        /// Set to -1 for unlimited number of error retries, until retryExceptionFilter is satisfied.</param>
        /// <param name="retryValueFilter">Filter function to indicate if successful execution should be retied.
        /// Set to null to disable successful retries.</param>
        /// <param name="retryExceptionFilter">Filter function to indicate if error execution should be retied.
        /// Set to null to disable error retries.</param>
        /// <param name="maxExecutionTime">The maximal execution time of the ExecuteWithRetries function.</param>
        /// <param name="onSuccessBackOff">The backoff provider object, which determines how much to wait between success retries.</param>
        /// <param name="onErrorBackOff">The backoff provider object, which determines how much to wait between error retries</param>
        /// <returns></returns>
        public static Task<T> ExecuteWithRetries<T>(
            Func<int, Task<T>> function,
            int maxNumSuccessTries,
            int maxNumErrorTries,
            Func<T, int, bool> retryValueFilter,
            Func<Exception, int, bool> retryExceptionFilter,
            TimeSpan maxExecutionTime = default(TimeSpan),
            IBackoffProvider onSuccessBackOff = null,
            IBackoffProvider onErrorBackOff = null)
        {
            return ExecuteWithRetriesHelper<T>(
                function,
                0,
                maxNumSuccessTries,
                maxNumErrorTries,
                maxExecutionTime,
                DateTime.UtcNow,
                retryValueFilter,
                retryExceptionFilter,
                onSuccessBackOff,
                onErrorBackOff);
        }

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Design", "CA1031:DoNotCatchGeneralExceptionTypes")]
        private static async Task<T> ExecuteWithRetriesHelper<T>(
            Func<int, Task<T>> function,
            int callCounter,
            int maxNumSuccessTries,
            int maxNumErrorTries,
            TimeSpan maxExecutionTime,
            DateTime startExecutionTime,
            Func<T, int, bool> retryValueFilter = null,
            Func<Exception, int, bool> retryExceptionFilter = null,
            IBackoffProvider onSuccessBackOff = null,
            IBackoffProvider onErrorBackOff = null)
        {
            if (maxExecutionTime != Constants.INFINITE_TIMESPAN && maxExecutionTime != default(TimeSpan))
            {
                DateTime now = DateTime.UtcNow;
                if (now - startExecutionTime > maxExecutionTime)
                {
                    Exception timeoutException = new TimeoutException(String.Format("ExecuteWithRetries has exceeded its max execution time of {0}. Now is {1}, started at {2}, passed {3}",
                            maxExecutionTime, TraceLogger.PrintDate(now), TraceLogger.PrintDate(startExecutionTime), now - startExecutionTime));
                    throw timeoutException;
                }
            }
            T result = default(T);
            int counter = callCounter;
            Exception exception = null;
            try
            {
                callCounter++;
                result = await function(counter);

                bool retry = false;
                if (callCounter < maxNumSuccessTries || maxNumSuccessTries == INFINITE_RETRIES) // -1 for infinite retries
                {
                    if (retryValueFilter != null)
                        retry = retryValueFilter(result, counter);
                }
                if (retry)
                {
                    if (onSuccessBackOff == null)
                    {
                        return await ExecuteWithRetriesHelper(function, callCounter, maxNumSuccessTries, maxNumErrorTries, maxExecutionTime, startExecutionTime, retryValueFilter, retryExceptionFilter, onSuccessBackOff, onErrorBackOff);
                    }
                    else
                    {
                        TimeSpan delay = onSuccessBackOff.Next(counter);
                        await Task.Delay(delay);
                        return await ExecuteWithRetriesHelper(function, callCounter, maxNumSuccessTries, maxNumErrorTries, maxExecutionTime, startExecutionTime, retryValueFilter, retryExceptionFilter, onSuccessBackOff, onErrorBackOff);
                    }
                }
                return result;
            }
            catch (Exception exc)
            {
                exception = exc;
            }

            if (exception != null)
            {
                bool retry = false;
                if (callCounter < maxNumErrorTries || maxNumErrorTries == INFINITE_RETRIES)
                {
                    if (retryExceptionFilter != null)
                        retry = retryExceptionFilter(exception, counter);
                }
                if (retry)
                {
                    if (onErrorBackOff == null)
                    {
                        return await ExecuteWithRetriesHelper(function, callCounter, maxNumSuccessTries, maxNumErrorTries, maxExecutionTime, startExecutionTime, retryValueFilter, retryExceptionFilter, onSuccessBackOff, onErrorBackOff);
                    }
                    else
                    {
                        TimeSpan delay = onErrorBackOff.Next(counter);
                        await Task.Delay(delay);
                        return await ExecuteWithRetriesHelper(function, callCounter, maxNumSuccessTries, maxNumErrorTries, maxExecutionTime, startExecutionTime, retryValueFilter, retryExceptionFilter, onSuccessBackOff, onErrorBackOff);
                    }
                }

                ExceptionDispatchInfo.Capture(exception).Throw();
            }
            return result; // this return value is just for the compiler to supress "not all control paths return a value".
        }
    }

    // Allow multiple implementations of the backoff algorithm.
    // For instance, ConstantBackoff variation that always waits for a fixed timespan, 
    // or a RateLimitingBackoff that keeps makes sure that some minimum time period occurs between calls to some API 
    // (especially useful if you use the same instance for multiple potentially simultaneous calls to ExecuteWithRetries).
    // Implementations should be imutable.
    // If mutable state is needed, extend the next function to pass the state from the caller.
    // example: TimeSpan Next(int attempt, object state, out object newState);
    internal interface IBackoffProvider
    {
        TimeSpan Next(int attempt);
    }

    internal class FixedBackoff : IBackoffProvider
    {
        private readonly TimeSpan fixedDelay;

        public FixedBackoff(TimeSpan delay)
        {
            fixedDelay = delay;
        }

        public TimeSpan Next(int attempt)
        {
            return fixedDelay;
        }
    }

    internal class ExponentialBackoff : IBackoffProvider
    {
        private readonly TimeSpan minDelay;
        private readonly TimeSpan maxDelay;
        private readonly TimeSpan step;
        private readonly SafeRandom random;

        public ExponentialBackoff(TimeSpan minDelay, TimeSpan maxDelay, TimeSpan step)
        {
            if (minDelay <= TimeSpan.Zero) throw new ArgumentOutOfRangeException("minDelay", minDelay, "ExponentialBackoff min delay must be a positive number.");
            if (maxDelay <= TimeSpan.Zero) throw new ArgumentOutOfRangeException("maxDelay", maxDelay, "ExponentialBackoff max delay must be a positive number.");
            if (step <= TimeSpan.Zero) throw new ArgumentOutOfRangeException("step", step, "ExponentialBackoff step must be a positive number.");
            if (minDelay >= maxDelay) throw new ArgumentOutOfRangeException("minDelay", minDelay, "ExponentialBackoff min delay must be greater than max delay.");

            this.minDelay = minDelay;
            this.maxDelay = maxDelay;
            this.step = step;
            this.random = new SafeRandom();
        }

        public TimeSpan Next(int attempt)
        {
            TimeSpan currMax;
            try
            {
                long multiple = checked(1 << attempt);
                currMax = minDelay + step.Multiply(multiple); // may throw OverflowException
                if (currMax <= TimeSpan.Zero)
                    throw new OverflowException();
            }
            catch (OverflowException)
            {
                currMax = maxDelay;
            }
            currMax = StandardExtensions.Min(currMax, maxDelay);

            if (minDelay >= currMax) throw new ArgumentOutOfRangeException(String.Format("minDelay {0}, currMax = {1}", minDelay, currMax));
            return random.NextTimeSpan(minDelay, currMax);
        }
    }
}
