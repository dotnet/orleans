using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace Orleans.Transactions.TestKit;

internal static class TransactionRecoveryFailureObservation
{
    internal enum OutcomeKind
    {
        FailureObserved,
        StoppedWithoutFailure,
        AttemptTimedOut,
    }

    internal sealed record Outcome<TFailure, TProducerResult>(
        OutcomeKind Kind,
        TFailure? Failure,
        TProducerResult ProducerResult,
        bool ProducerSettled,
        TimeSpan Elapsed,
        TimeSpan DrainElapsed)
        where TFailure : class;

    public static async Task ObserveAsync(Task task, Action<Exception, long> onFailure)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            onFailure(exception, Stopwatch.GetTimestamp());
        }
    }

    public static bool IsPremature(long observedAt, long shutdownRequestedAt) => observedAt < shutdownRequestedAt;

    internal static async Task<Outcome<TFailure, TProducerResult>> DetectAsync<TFailure, TProducerResult>(
        Task<TProducerResult> producer,
        Task<TFailure> firstFailure,
        CancellationTokenSource stopProducing,
        TimeSpan responseWindow,
        TimeSpan schedulingMargin)
        where TFailure : class
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(responseWindow, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThan(schedulingMargin, TimeSpan.Zero);

        var startedAt = Stopwatch.GetTimestamp();
        var responseDeadline = GetDeadline(startedAt, responseWindow);
        var drainDeadline = GetDeadline(responseDeadline, schedulingMargin);

        await WaitUntilAsync(firstFailure, producer, responseDeadline);
        if (firstFailure.IsCompleted)
        {
            return await CompleteFailureAsync(
                producer,
                firstFailure,
                stopProducing,
                startedAt,
                drainDeadline);
        }

        if (producer.IsCompleted)
        {
            var producerResult = await producer.ConfigureAwait(false);
            if (firstFailure.IsCompleted)
            {
                return await CompleteFailureAsync(
                    producer,
                    firstFailure,
                    stopProducing,
                    startedAt,
                    drainDeadline);
            }

            return new(
                OutcomeKind.StoppedWithoutFailure,
                Failure: null,
                producerResult,
                ProducerSettled: true,
                Stopwatch.GetElapsedTime(startedAt),
                DrainElapsed: TimeSpan.Zero);
        }

        stopProducing.Cancel();
        var drainStartedAt = Stopwatch.GetTimestamp();
        await WaitUntilAsync(firstFailure, producer, drainDeadline);

        if (firstFailure.IsCompleted)
        {
            return await CompleteFailureAsync(
                producer,
                firstFailure,
                stopProducing,
                startedAt,
                drainDeadline,
                drainStartedAt);
        }

        if (producer.IsCompleted)
        {
            var producerResult = await producer.ConfigureAwait(false);
            if (firstFailure.IsCompleted)
            {
                return await CompleteFailureAsync(
                    producer,
                    firstFailure,
                    stopProducing,
                    startedAt,
                    drainDeadline,
                    drainStartedAt);
            }

            return new(
                OutcomeKind.StoppedWithoutFailure,
                Failure: null,
                producerResult,
                ProducerSettled: true,
                Stopwatch.GetElapsedTime(startedAt),
                Stopwatch.GetElapsedTime(drainStartedAt));
        }

        producer.Ignore();
        return new(
            OutcomeKind.AttemptTimedOut,
            Failure: null,
            ProducerResult: default!,
            ProducerSettled: false,
            Stopwatch.GetElapsedTime(startedAt),
            Stopwatch.GetElapsedTime(drainStartedAt));
    }

    private static async Task<Outcome<TFailure, TProducerResult>> CompleteFailureAsync<TFailure, TProducerResult>(
        Task<TProducerResult> producer,
        Task<TFailure> firstFailure,
        CancellationTokenSource stopProducing,
        long startedAt,
        long drainDeadline,
        long? drainStartedAt = null)
        where TFailure : class
    {
        stopProducing.Cancel();
        var failure = await firstFailure.ConfigureAwait(false);
        var drainStarted = drainStartedAt ?? Stopwatch.GetTimestamp();
        if (!producer.IsCompleted)
        {
            await WaitUntilAsync(producer, drainDeadline);
        }

        if (producer.IsCompleted)
        {
            return new(
                OutcomeKind.FailureObserved,
                failure,
                await producer.ConfigureAwait(false),
                ProducerSettled: true,
                Stopwatch.GetElapsedTime(startedAt),
                Stopwatch.GetElapsedTime(drainStarted));
        }

        producer.Ignore();
        return new(
            OutcomeKind.FailureObserved,
            failure,
            ProducerResult: default!,
            ProducerSettled: false,
            Stopwatch.GetElapsedTime(startedAt),
            Stopwatch.GetElapsedTime(drainStarted));
    }

    private static async Task WaitUntilAsync(Task first, long deadline)
    {
        if (first.IsCompleted)
        {
            return;
        }

        var now = Stopwatch.GetTimestamp();
        if (now < deadline)
        {
            await Task.WhenAny(first, Task.Delay(Stopwatch.GetElapsedTime(now, deadline))).ConfigureAwait(false);
        }
    }

    private static async Task WaitUntilAsync(Task first, Task second, long deadline)
    {
        if (first.IsCompleted || second.IsCompleted)
        {
            return;
        }

        var now = Stopwatch.GetTimestamp();
        if (now < deadline)
        {
            await Task.WhenAny(first, second, Task.Delay(Stopwatch.GetElapsedTime(now, deadline))).ConfigureAwait(false);
        }
    }

    private static long GetDeadline(long startedAt, TimeSpan duration)
        => checked(startedAt + (long)(duration.TotalSeconds * Stopwatch.Frequency));
}
