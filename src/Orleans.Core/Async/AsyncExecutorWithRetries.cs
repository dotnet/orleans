using System;
using System.Runtime.ExceptionServices;
using System.Threading.Tasks;
using Orleans.Runtime;

namespace Orleans.Internal
{
    /// <summary>
    /// This class is a convenient utility class to execute a certain asynchronous function with retries,
    /// allowing to specify custom retry filters and policies.
    /// </summary>
    public static class AsyncExecutorWithRetries
    {
        /// <summary>
        /// Constant used to request an infinite number of retries.
        /// </summary>
        public static readonly int INFINITE_RETRIES = -1;

        /// <summary>
        /// Execute a given function a number of times, based on retry configuration parameters.
        /// </summary>
        /// <param name="action">
        /// The action to be executed.
        /// </param>
        /// <param name="maxNumErrorTries">
        /// The maximum number of retries.
        /// </param>
        /// <param name="retryExceptionFilter">
        /// The retry exception filter.
        /// </param>
        /// <param name="maxExecutionTime">
        /// The maximum execution time.
        /// </param>
        /// <param name="onErrorBackOff">
        /// The backoff provider.
        /// </param>
        /// <returns>
        /// A <see cref="Task"/> representing the operation.
        /// </returns>
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
        /// <param name="function">
        /// The delegate to be executed.
        /// </param>
        /// <param name="maxNumErrorTries">
        /// The maximum number of retries.
        /// </param>
        /// <param name="retryExceptionFilter">
        /// The retry exception filter.
        /// </param>
        /// <param name="maxExecutionTime">
        /// The maximum execution time.
        /// </param>
        /// <param name="onErrorBackOff">
        /// The backoff provider.
        /// </param>
        /// <returns>
        /// The value returned from the successful invocation of the provided function.
        /// </returns>
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
        /// Execute a given <paramref name="function"/> a number of times, based on retry configuration parameters.
        /// </summary>
        /// <typeparam name="T">
        /// The underlying return type of <paramref name="function"/>.
        /// </typeparam>
        /// <param name="function">
        /// Function to execute
        /// </param>
        /// <param name="maxNumSuccessTries">
        /// Maximal number of successful execution attempts. <see cref="ExecuteWithRetries"/> will try to re-execute the given <paramref name="function"/> again if directed so by <paramref name="retryValueFilter"/> .
        /// Set to <c>-1</c> for unlimited number of success retries, until <paramref name="retryValueFilter"/> is satisfied. Set to <c>0</c> for only one success attempt, which will cause <paramref name="retryValueFilter"/> to be
        /// ignored and the given <paramref name="function"/> executed only once until first success.
        /// </param>
        /// <param name="maxNumErrorTries">
        /// Maximal number of execution attempts due to errors. Set to -1 for unlimited number of error retries, until <paramref name="retryExceptionFilter"/> is satisfied.
        /// </param>
        /// <param name="retryValueFilter">
        /// Filter <paramref name="function"/> to indicate if successful execution should be retried. Set to <see langword="null"/> to disable successful retries.
        /// </param>
        /// <param name="retryExceptionFilter">
        /// Filter <paramref name="function"/> to indicate if error execution should be retried. Set to <see langword="null"/> to disable error retries.
        /// </param>
        /// <param name="maxExecutionTime">
        /// The maximal execution time of the <see cref="ExecuteWithRetries"/> function.
        /// </param>
        /// <param name="onSuccessBackOff">
        /// The backoff provider object, which determines how much to wait between success retries.
        /// </param>
        /// <param name="onErrorBackOff">
        /// The backoff provider object, which determines how much to wait between error retries
        /// </param>
        /// <returns>
        /// The value returned from the successful invocation of <paramref name="function"/>.
        /// </returns>
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
                maxNumSuccessTries,
                maxNumErrorTries,
                maxExecutionTime,
                DateTime.UtcNow,
                retryValueFilter,
                retryExceptionFilter,
                onSuccessBackOff,
                onErrorBackOff);
        }

        /// <summary>
        /// Execute a given <paramref name="function"/> a number of times, based on retry configuration parameters.
        /// </summary>
        /// <typeparam name="T">
        /// The underlying return type of <paramref name="function"/>.
        /// </typeparam>
        /// <param name="function">
        /// Function to execute.
        /// </param>
        /// <param name="maxNumSuccessTries">
        /// Maximal number of successful execution attempts. <see cref="ExecuteWithRetries"/> will try to re-execute the given <paramref name="function"/> again if directed so by <paramref name="retryValueFilter"/> .
        /// Set to <c>-1</c> for unlimited number of success retries, until <paramref name="retryValueFilter"/> is satisfied. Set to <c>0</c> for only one success attempt, which will cause <paramref name="retryValueFilter"/> to be
        /// ignored and the given <paramref name="function"/> executed only once until first success.
        /// </param>
        /// <param name="maxNumErrorTries">
        /// Maximal number of execution attempts due to errors. Set to -1 for unlimited number of error retries, until <paramref name="retryExceptionFilter"/> is satisfied.
        /// </param>
        /// <param name="maxExecutionTime">
        /// The maximal execution time of the <see cref="ExecuteWithRetries"/> function.
        /// </param>
        /// <param name="startExecutionTime">
        /// The time at which execution was started.
        /// </param>
        /// <param name="retryValueFilter">
        /// Filter <paramref name="function"/> to indicate if successful execution should be retried. Set to <see langword="null"/> to disable successful retries.
        /// </param>
        /// <param name="retryExceptionFilter">
        /// Filter <paramref name="function"/> to indicate if error execution should be retried. Set to <see langword="null"/> to disable error retries.
        /// </param>
        /// <param name="onSuccessBackOff">
        /// The backoff provider object, which determines how much to wait between success retries.
        /// </param>
        /// <param name="onErrorBackOff">
        /// The backoff provider object, which determines how much to wait between error retries
        /// </param>
        /// <returns>
        /// The value returned from the successful invocation of <paramref name="function"/>.
        /// </returns>
        private static async Task<T> ExecuteWithRetriesHelper<T>(
            Func<int, Task<T>> function,
            int maxNumSuccessTries,
            int maxNumErrorTries,
            TimeSpan maxExecutionTime,
            DateTime startExecutionTime,
            Func<T, int, bool> retryValueFilter = null,
            Func<Exception, int, bool> retryExceptionFilter = null,
            IBackoffProvider onSuccessBackOff = null,
            IBackoffProvider onErrorBackOff = null)
        {
            T result = default(T);
            ExceptionDispatchInfo lastExceptionInfo = null;
            bool retry;
            var callCounter = 0;

            do
            {
                retry = false;

                if (maxExecutionTime != Constants.INFINITE_TIMESPAN && maxExecutionTime != default(TimeSpan))
                {
                    DateTime now = DateTime.UtcNow;
                    if (now - startExecutionTime > maxExecutionTime)
                    {
                        if (lastExceptionInfo == null)
                        {
                            throw new TimeoutException(
                                $"ExecuteWithRetries has exceeded its max execution time of {maxExecutionTime}. Now is {LogFormatter.PrintDate(now)}, started at {LogFormatter.PrintDate(startExecutionTime)}, passed {now - startExecutionTime}");
                        }

                        lastExceptionInfo.Throw();
                    }
                }

                int counter = callCounter;

                try
                {
                    callCounter++;
                    result = await function(counter);
                    lastExceptionInfo = null;

                    if (callCounter < maxNumSuccessTries || maxNumSuccessTries == INFINITE_RETRIES) // -1 for infinite retries
                    {
                        if (retryValueFilter != null)
                            retry = retryValueFilter(result, counter);
                    }

                    if (retry)
                    {
                        TimeSpan? delay = onSuccessBackOff?.Next(counter);

                        if (delay.HasValue)
                        {
                            await Task.Delay(delay.Value);
                        }
                    }
                }
                catch (Exception exc)
                {
                    retry = false;

                    if (callCounter < maxNumErrorTries || maxNumErrorTries == INFINITE_RETRIES)
                    {
                        if (retryExceptionFilter != null)
                            retry = retryExceptionFilter(exc, counter);
                    }

                    if (!retry)
                    {
                        throw;
                    }

                    lastExceptionInfo = ExceptionDispatchInfo.Capture(exc);

                    TimeSpan? delay = onErrorBackOff?.Next(counter);

                    if (delay.HasValue)
                    {
                        await Task.Delay(delay.Value);
                    }
                }
            } while (retry);

            return result;
        }
    }

    /// <summary>
    /// Functionality for determining how long to wait between successive operation attempts.
    /// </summary>
    public interface IBackoffProvider
    {
        /// <summary>
        /// Returns the amount of time to wait before attempting a subsequent operation.
        /// </summary>
        /// <param name="attempt">The number of operation attempts which have been made.</param>
        /// <returns>The amount of time to wait before attempting a subsequent operation.</returns>
        TimeSpan Next(int attempt);
    }

    /// <summary>
    /// A fixed-duration backoff implementation, which always returns the configured delay.
    /// </summary>
    public class FixedBackoff : IBackoffProvider
    {
        private readonly TimeSpan fixedDelay;

        /// <summary>
        /// Initializes a new instance of the <see cref="FixedBackoff"/> class.
        /// </summary>
        /// <param name="delay">
        /// The fixed delay between attempts.
        /// </param>
        public FixedBackoff(TimeSpan delay)
        {
            fixedDelay = delay;
        }

        /// <inheritdoc/>
        public TimeSpan Next(int attempt)
        {
            return fixedDelay;
        }
    }

    /// <summary>
    /// An exponential backoff implementation, which initially returns the minimum delay it is configured
    /// with and exponentially increases its delay by two raised to the power of the attempt number until
    /// the maximum backoff delay is reached.
    /// </summary>
    internal class ExponentialBackoff : IBackoffProvider
    {
        private readonly TimeSpan minDelay;
        private readonly TimeSpan maxDelay;
        private readonly TimeSpan step;

        /// <summary>
        /// Initializes a new instance of the <see cref="ExponentialBackoff"/> class.
        /// </summary>
        /// <param name="minDelay">
        /// The minimum delay.
        /// </param>
        /// <param name="maxDelay">
        /// The maximum delay.
        /// </param>
        /// <param name="step">
        /// The step, which is multiplied by two raised to the power of the attempt number and added to the minimum delay to compute the delay for each iteration.
        /// </param>
        /// <exception cref="ArgumentOutOfRangeException">
        /// One or more argument values are outside out of their valid range.
        /// </exception>
        public ExponentialBackoff(TimeSpan minDelay, TimeSpan maxDelay, TimeSpan step)
        {
            if (minDelay <= TimeSpan.Zero) throw new ArgumentOutOfRangeException("minDelay", minDelay, "ExponentialBackoff min delay must be a positive number.");
            if (maxDelay <= TimeSpan.Zero) throw new ArgumentOutOfRangeException("maxDelay", maxDelay, "ExponentialBackoff max delay must be a positive number.");
            if (step <= TimeSpan.Zero) throw new ArgumentOutOfRangeException("step", step, "ExponentialBackoff step must be a positive number.");
            if (minDelay >= maxDelay) throw new ArgumentOutOfRangeException("minDelay", minDelay, "ExponentialBackoff min delay must be greater than max delay.");

            this.minDelay = minDelay;
            this.maxDelay = maxDelay;
            this.step = step;
        }

        /// <inheritdoc/>
        public TimeSpan Next(int attempt)
        {
            TimeSpan currMax;
            try
            {
                long multiple = checked(1 << attempt);
                currMax = minDelay + step.Multiply(multiple); // may throw OverflowException
                if (currMax <= TimeSpan.Zero)
                {
                    throw new OverflowException();
                }
            }
            catch (OverflowException)
            {
                currMax = maxDelay;
            }
            currMax = StandardExtensions.Min(currMax, maxDelay);

            if (minDelay >= currMax) throw new ArgumentOutOfRangeException($"minDelay {minDelay}, currMax = {currMax}");
            return ThreadSafeRandom.NextTimeSpan(minDelay, currMax);
        }
    }
}