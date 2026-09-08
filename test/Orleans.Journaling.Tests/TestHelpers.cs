namespace Orleans.Journaling.Tests;

/// <summary>
/// Shared test utilities for Orleans.Journaling.Tests.
/// Provides deterministic wait helpers that poll for conditions instead of using arbitrary delays.
/// </summary>
public static class TestHelpers
{
    /// <summary>
    /// Default timeout for polling operations.
    /// </summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Default polling interval between condition checks.
    /// </summary>
    public static readonly TimeSpan DefaultPollInterval = TimeSpan.FromMilliseconds(50);

    /// <summary>
    /// Polls until a condition becomes true, or throws a TimeoutException if the timeout is exceeded.
    /// This is a deterministic replacement for arbitrary Task.Delay calls.
    /// </summary>
    /// <param name="condition">A function that returns true when the condition is met.</param>
    /// <param name="timeout">Maximum time to wait for the condition. Defaults to 10 seconds.</param>
    /// <param name="pollInterval">Time between condition checks. Defaults to 50ms.</param>
    /// <param name="message">Message to include in the timeout exception.</param>
    /// <returns>A task that completes when the condition is met.</returns>
    /// <exception cref="TimeoutException">Thrown if the condition is not met within the timeout period.</exception>
    public static async Task WaitUntilAsync(
        Func<bool> condition,
        TimeSpan? timeout = null,
        TimeSpan? pollInterval = null,
        string? message = null)
    {
        timeout ??= DefaultTimeout;
        pollInterval ??= DefaultPollInterval;

        var deadline = DateTimeOffset.UtcNow + timeout.Value;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }
            await Task.Delay(pollInterval.Value);
        }

        throw new TimeoutException(message ?? $"Condition was not met within {timeout.Value.TotalSeconds} seconds.");
    }

    /// <summary>
    /// Polls until an async condition becomes true, or throws a TimeoutException if the timeout is exceeded.
    /// </summary>
    /// <param name="condition">An async function that returns true when the condition is met.</param>
    /// <param name="timeout">Maximum time to wait for the condition. Defaults to 10 seconds.</param>
    /// <param name="pollInterval">Time between condition checks. Defaults to 50ms.</param>
    /// <param name="message">Message to include in the timeout exception.</param>
    /// <returns>A task that completes when the condition is met.</returns>
    /// <exception cref="TimeoutException">Thrown if the condition is not met within the timeout period.</exception>
    public static async Task WaitUntilAsync(
        Func<Task<bool>> condition,
        TimeSpan? timeout = null,
        TimeSpan? pollInterval = null,
        string? message = null)
    {
        timeout ??= DefaultTimeout;
        pollInterval ??= DefaultPollInterval;

        var deadline = DateTimeOffset.UtcNow + timeout.Value;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (await condition())
            {
                return;
            }
            await Task.Delay(pollInterval.Value);
        }

        throw new TimeoutException(message ?? $"Condition was not met within {timeout.Value.TotalSeconds} seconds.");
    }

    /// <summary>
    /// Polls until a condition becomes true, returning false instead of throwing if the timeout is exceeded.
    /// </summary>
    /// <param name="condition">An async function that returns true when the condition is met.</param>
    /// <param name="timeout">Maximum time to wait for the condition. Defaults to 10 seconds.</param>
    /// <param name="pollInterval">Time between condition checks. Defaults to 50ms.</param>
    /// <returns>True if the condition was met, false if the timeout was exceeded.</returns>
    public static async Task<bool> TryWaitUntilAsync(
        Func<Task<bool>> condition,
        TimeSpan? timeout = null,
        TimeSpan? pollInterval = null)
    {
        timeout ??= DefaultTimeout;
        pollInterval ??= DefaultPollInterval;

        var deadline = DateTimeOffset.UtcNow + timeout.Value;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (await condition())
            {
                return true;
            }
            await Task.Delay(pollInterval.Value);
        }

        return false;
    }

    /// <summary>
    /// Polls until a value-returning async function produces a result that matches the expected value.
    /// Returns the final value regardless of whether the condition was met.
    /// </summary>
    /// <typeparam name="T">The type of value being polled.</typeparam>
    /// <param name="getValue">An async function that retrieves the current value.</param>
    /// <param name="expected">The expected value to wait for.</param>
    /// <param name="timeout">Maximum time to wait for the value. Defaults to 10 seconds.</param>
    /// <param name="pollInterval">Time between value checks. Defaults to 50ms.</param>
    /// <returns>The value when the condition is met or the timeout is reached.</returns>
    public static async Task<T> WaitForValueAsync<T>(
        Func<Task<T>> getValue,
        T expected,
        TimeSpan? timeout = null,
        TimeSpan? pollInterval = null)
        where T : IEquatable<T>
    {
        timeout ??= DefaultTimeout;
        pollInterval ??= DefaultPollInterval;

        var deadline = DateTimeOffset.UtcNow + timeout.Value;
        T value = default!;

        while (DateTimeOffset.UtcNow < deadline)
        {
            value = await getValue();
            if (EqualityComparer<T>.Default.Equals(value, expected))
            {
                return value!;
            }
            await Task.Delay(pollInterval.Value);
        }

        return value!;
    }

    /// <summary>
    /// Polls until a value-returning async function produces a non-null result.
    /// </summary>
    /// <typeparam name="T">The type of value being polled (must be a reference type).</typeparam>
    /// <param name="getValue">An async function that retrieves the current value.</param>
    /// <param name="timeout">Maximum time to wait for a non-null value. Defaults to 10 seconds.</param>
    /// <param name="pollInterval">Time between value checks. Defaults to 50ms.</param>
    /// <returns>The value when it becomes non-null.</returns>
    /// <exception cref="TimeoutException">Thrown if the value is still null when the timeout is exceeded.</exception>
    public static async Task<T> WaitForNonNullAsync<T>(
        Func<Task<T?>> getValue,
        TimeSpan? timeout = null,
        TimeSpan? pollInterval = null,
        string? message = null)
        where T : class
    {
        timeout ??= DefaultTimeout;
        pollInterval ??= DefaultPollInterval;

        var deadline = DateTimeOffset.UtcNow + timeout.Value;

        while (DateTimeOffset.UtcNow < deadline)
        {
            var value = await getValue();
            if (value is not null)
            {
                return value;
            }
            await Task.Delay(pollInterval.Value);
        }

        throw new TimeoutException(message ?? $"Value was still null after {timeout.Value.TotalSeconds} seconds.");
    }

    /// <summary>
    /// Polls until a numeric value reaches at least the specified minimum.
    /// </summary>
    /// <param name="getValue">An async function that retrieves the current value.</param>
    /// <param name="minimum">The minimum value to wait for.</param>
    /// <param name="timeout">Maximum time to wait. Defaults to 10 seconds.</param>
    /// <param name="pollInterval">Time between value checks. Defaults to 50ms.</param>
    /// <returns>The value when it reaches the minimum or the timeout is reached.</returns>
    public static async Task<int> WaitForMinimumAsync(
        Func<Task<int>> getValue,
        int minimum,
        TimeSpan? timeout = null,
        TimeSpan? pollInterval = null)
    {
        timeout ??= DefaultTimeout;
        pollInterval ??= DefaultPollInterval;

        var deadline = DateTimeOffset.UtcNow + timeout.Value;
        int value = 0;

        while (DateTimeOffset.UtcNow < deadline)
        {
            value = await getValue();
            if (value >= minimum)
            {
                return value;
            }
            await Task.Delay(pollInterval.Value);
        }

        return value;
    }

    /// <summary>
    /// Waits for a nullable value to have a value (not null for structs).
    /// </summary>
    /// <typeparam name="T">The underlying value type.</typeparam>
    /// <param name="getValue">An async function that retrieves the current nullable value.</param>
    /// <param name="timeout">Maximum time to wait. Defaults to 10 seconds.</param>
    /// <param name="pollInterval">Time between value checks. Defaults to 50ms.</param>
    /// <returns>The value when it becomes non-null.</returns>
    /// <exception cref="TimeoutException">Thrown if the value is still null when the timeout is exceeded.</exception>
    public static async Task<T?> WaitForNullableValueAsync<T>(
        Func<Task<T?>> getValue,
        TimeSpan? timeout = null,
        TimeSpan? pollInterval = null,
        string? message = null)
        where T : struct
    {
        timeout ??= DefaultTimeout;
        pollInterval ??= DefaultPollInterval;

        var deadline = DateTimeOffset.UtcNow + timeout.Value;

        while (DateTimeOffset.UtcNow < deadline)
        {
            var value = await getValue();
            if (value.HasValue)
            {
                return value;
            }
            await Task.Delay(pollInterval.Value);
        }

        throw new TimeoutException(message ?? $"Nullable value was still null after {timeout.Value.TotalSeconds} seconds.");
    }
}
