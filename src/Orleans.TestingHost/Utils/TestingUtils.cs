using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Orleans.TestingHost.Logging;

namespace Orleans.TestingHost.Utils
{
    /// <summary> Collection of test utilities </summary>
    public static class TestingUtils
    {
        private static long uniquifier = Stopwatch.GetTimestamp();

        /// <summary>
        /// Configure <paramref name="builder" /> with a <see cref="FileLoggerProvider" /> which logs to <paramref name="filePath" />
        /// by default;
        /// </summary>
        /// <param name="builder">The builder.</param>
        /// <param name="filePath">The file path.</param>
        public static void ConfigureDefaultLoggingBuilder(ILoggingBuilder builder, string filePath)
        {
            builder.AddFile(filePath);
        }

        /// <summary>
        /// Create trace file name for a specific node or client in a specific deployment
        /// </summary>
        /// <param name="nodeName">Name of the node.</param>
        /// <param name="clusterId">The cluster identifier.</param>
        /// <returns>The new trace file name.</returns>
        public static string CreateTraceFileName(string nodeName, string clusterId)
        {
            const string traceFileFolder = "logs";

            if (!Directory.Exists(traceFileFolder))
            {
                Directory.CreateDirectory(traceFileFolder);
            }

            var traceFileName = Path.Combine(traceFileFolder, $"{clusterId}_{Interlocked.Increment(ref uniquifier):X}_{nodeName}.log");

            return traceFileName;
        }

        /// <summary>
        /// Create the default logger factory, which would configure logger factory with a <see cref="FileLoggerProvider" /> that writes logs to <paramref name="filePath" /> and console.
        /// by default;
        /// </summary>
        /// <param name="filePath">The file path.</param>
        /// <returns>ILoggerFactory.</returns>
        public static ILoggerFactory CreateDefaultLoggerFactory(string filePath)
        {
            return CreateDefaultLoggerFactory(filePath, new LoggerFilterOptions());
        }

        /// <summary>
        /// Create the default logger factory, which would configure logger factory with a <see cref="FileLoggerProvider"/> that writes logs to <paramref name="filePath"/> and console.
        /// by default;
        /// </summary>
        /// <param name="filePath">the logger file path</param>
        /// <param name="filters">log filters you want to configure your logging with</param>
        /// <returns></returns>
        public static ILoggerFactory CreateDefaultLoggerFactory(string filePath, LoggerFilterOptions filters)
        {
            var factory = new LoggerFactory(new List<ILoggerProvider>(), filters);
            factory.AddProvider(new FileLoggerProvider(filePath));
            return factory;
        }

        /// <summary>Run the predicate until it succeeds or times out.</summary>
        /// <param name="predicate">The predicate to run</param>
        /// <param name="timeout">The timeout value</param>
        /// <param name="delayOnFail">The time to delay next call upon failure</param>
        /// <returns>A task representing the operation.</returns>
        /// <exception cref="TimeoutException">The predicate did not succeed before the timeout elapsed.</exception>
        public static Task WaitUntilAsync(Func<bool,Task<bool>> predicate, TimeSpan timeout, TimeSpan? delayOnFail = null)
            => WaitUntilAsync(predicate, timeout, delayOnFail, TimeProvider.System);

        internal static Task WaitUntilAsync(
            Func<bool, Task<bool>> predicate,
            TimeSpan timeout,
            TimeSpan? delayOnFail,
            TimeProvider timeProvider)
        {
            ArgumentNullException.ThrowIfNull(predicate);
            ArgumentNullException.ThrowIfNull(timeProvider);

            var predicateName = $"{predicate.Method.DeclaringType?.FullName ?? "<unknown>"}.{predicate.Method.Name}";
            return WaitUntilAsync((lastTry, _) => predicate(lastTry), timeout, delayOnFail, default, timeProvider, predicateName);
        }

        /// <summary>Run the predicate until it succeeds or times out.</summary>
        /// <param name="predicate">
        /// The predicate to run. The first argument indicates the final attempt, allowing the predicate to throw a detailed assertion failure.
        /// The token is cancelled when the deadline expires or cancellation is requested.
        /// </param>
        /// <param name="timeout">The timeout value.</param>
        /// <param name="delayOnFail">The delay before retrying after an unsuccessful attempt.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <param name="predicateExpression">The expression supplied for <paramref name="predicate"/>.</param>
        /// <returns>A task representing the operation.</returns>
        /// <exception cref="TimeoutException">The predicate did not succeed before the timeout elapsed.</exception>
        public static Task WaitUntilAsync(
            Func<bool, CancellationToken, Task<bool>> predicate,
            TimeSpan timeout,
            TimeSpan? delayOnFail = null,
            CancellationToken cancellationToken = default,
            [CallerArgumentExpression(nameof(predicate))] string? predicateExpression = null)
            => WaitUntilAsync(predicate, timeout, delayOnFail, cancellationToken, TimeProvider.System, predicateExpression);

        internal static async Task WaitUntilAsync(
            Func<bool, CancellationToken, Task<bool>> predicate,
            TimeSpan timeout,
            TimeSpan? delayOnFail,
            CancellationToken cancellationToken,
            TimeProvider timeProvider,
            [CallerArgumentExpression(nameof(predicate))] string? predicateExpression = null)
        {
            ArgumentNullException.ThrowIfNull(predicate);
            ArgumentNullException.ThrowIfNull(timeProvider);

            if (!await WaitUntilSucceededAsync(
                predicate,
                timeout,
                delayOnFail,
                cancellationToken,
                invokeFinalAttempt: true,
                timeProvider))
            {
                throw new TimeoutException(
                    $"The condition evaluated by '{predicateExpression ?? "<unknown>"}' was not satisfied within {timeout} "
                    + $"using a retry delay of {delayOnFail ?? TimeSpan.FromSeconds(1)}. "
                    + "The predicate was not invoked again after the deadline.");
            }
        }

        /// <summary>Runs the predicate until it succeeds or the monotonic deadline expires.</summary>
        /// <param name="predicate">The predicate to run. The token is cancelled when the deadline expires or cancellation is requested.</param>
        /// <param name="timeout">The timeout value.</param>
        /// <param name="delayOnFail">The delay before retrying after an unsuccessful attempt.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns><see langword="true"/> if the predicate succeeded before the deadline; otherwise, <see langword="false"/>.</returns>
        public static async Task<bool> WaitUntilSucceededAsync(
            Func<CancellationToken, Task<bool>> predicate,
            TimeSpan timeout,
            TimeSpan? delayOnFail = null,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(predicate);
            return await WaitUntilSucceededAsync(predicate, timeout, delayOnFail, cancellationToken, TimeProvider.System);
        }

        internal static async Task<bool> WaitUntilSucceededAsync(
            Func<CancellationToken, Task<bool>> predicate,
            TimeSpan timeout,
            TimeSpan? delayOnFail,
            CancellationToken cancellationToken,
            TimeProvider timeProvider)
        {
            ArgumentNullException.ThrowIfNull(predicate);
            ArgumentNullException.ThrowIfNull(timeProvider);
            return await WaitUntilSucceededAsync(
                (_, token) => predicate(token),
                timeout,
                delayOnFail,
                cancellationToken,
                invokeFinalAttempt: false,
                timeProvider);
        }

        private static async Task<bool> WaitUntilSucceededAsync(
            Func<bool, CancellationToken, Task<bool>> predicate,
            TimeSpan timeout,
            TimeSpan? delayOnFail,
            CancellationToken cancellationToken,
            bool invokeFinalAttempt,
            TimeProvider timeProvider)
        {
            ArgumentNullException.ThrowIfNull(predicate);
            ArgumentOutOfRangeException.ThrowIfLessThan(timeout, TimeSpan.Zero);

            var retryDelay = delayOnFail ?? TimeSpan.FromSeconds(1);
            ArgumentOutOfRangeException.ThrowIfLessThan(retryDelay, TimeSpan.Zero);

            cancellationToken.ThrowIfCancellationRequested();
            var startedAt = timeProvider.GetTimestamp();
            using var deadlineCancellation = new CancellationTokenSource(timeout, timeProvider);
            using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadlineCancellation.Token);
            var finalAttempt = false;
            var finalAttemptWindow = retryDelay > TimeSpan.Zero ? retryDelay : TimeSpan.FromMilliseconds(10);

            while (timeProvider.GetElapsedTime(startedAt) < timeout)
            {
                if (deadlineCancellation.IsCancellationRequested || timeProvider.GetElapsedTime(startedAt) >= timeout)
                {
                    return false;
                }

                bool succeeded;
                try
                {
                    succeeded = await predicate(finalAttempt, linkedCancellation.Token);
                }
                catch (OperationCanceledException) when (deadlineCancellation.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                {
                    return false;
                }

                cancellationToken.ThrowIfCancellationRequested();
                var elapsed = timeProvider.GetElapsedTime(startedAt);
                if (elapsed >= timeout)
                {
                    return false;
                }

                if (succeeded)
                {
                    return true;
                }

                var remaining = timeout - elapsed;
                if (finalAttempt)
                {
                    try
                    {
                        await Task.Delay(remaining, timeProvider, linkedCancellation.Token);
                    }
                    catch (OperationCanceledException) when (deadlineCancellation.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                    {
                        return false;
                    }

                    return false;
                }

                if (invokeFinalAttempt && remaining <= finalAttemptWindow + retryDelay)
                {
                    finalAttempt = true;
                    continue;
                }

                var delay = retryDelay < remaining ? retryDelay : remaining;
                if (invokeFinalAttempt)
                {
                    var delayUntilFinalAttempt = remaining - finalAttemptWindow;
                    if (delayUntilFinalAttempt < delay)
                    {
                        delay = delayUntilFinalAttempt;
                    }
                }

                try
                {
                    await Task.Delay(delay, timeProvider, linkedCancellation.Token);
                }
                catch (OperationCanceledException) when (deadlineCancellation.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                {
                    return false;
                }

                if (delay >= remaining)
                {
                    return false;
                }
            }

            return false;
        }

        /// <summary>
        /// Multiply a timeout by a value
        /// </summary>
        /// <param name="time">The time.</param>
        /// <param name="value">The value.</param>
        /// <returns>The resulting time span value.</returns>
        public static TimeSpan Multiply(TimeSpan time, double value)
        {
            double ticksD = checked(time.Ticks * value);
            long ticks = checked((long)ticksD);
            return TimeSpan.FromTicks(ticks);
        }
    }
}
