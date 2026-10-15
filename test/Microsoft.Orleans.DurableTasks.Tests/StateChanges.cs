#nullable enable
using System;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Orleans.DurableTasks.Diagnostics;

namespace Microsoft.Orleans.DurableTasks.Tests;

/// <summary>
/// Waits for test conditions at the transitions which can satisfy them, instead of polling.
/// </summary>
/// <remarks>
/// Every <see cref="DurableTaskEvents"/> event, and every change which a test fake reports through <see cref="Notify"/>,
/// wakes the waits, which then evaluate their conditions again. A runtime raises its events synchronously when it stages a
/// transition, so a condition over runtime state holds by the time its wait wakes. A wait which times out names its
/// condition.
/// </remarks>
internal static class StateChanges
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(10);
    private static readonly IDisposable Subscription;
    private static TaskCompletionSource _next = Create();

    static StateChanges() => Subscription = DurableTaskEvents.AllEvents.Subscribe(new Observer());

    /// <summary>Wakes the waits, so they evaluate their conditions again.</summary>
    public static void Notify() => Interlocked.Exchange(ref _next, Create()).TrySetResult();

    /// <summary>Waits until <paramref name="condition"/> holds.</summary>
    /// <exception cref="TimeoutException">The condition didn't hold within the timeout.</exception>
    public static async Task WaitUntilAsync(
        Func<bool> condition,
        TimeSpan? timeout = null,
        [CallerArgumentExpression(nameof(condition))] string? description = null)
    {
        GC.KeepAlive(Subscription);
        using var expiry = CancellationTokenSource.CreateLinkedTokenSource(Xunit.TestContext.Current.CancellationToken);
        expiry.CancelAfter(timeout ?? DefaultTimeout);
        while (true)
        {
            // A change which follows this read completes the captured signal, so no change is missed.
            var changed = Volatile.Read(ref _next).Task;
            if (condition())
            {
                return;
            }

            try
            {
                await changed.WaitAsync(expiry.Token);
            }
            catch (OperationCanceledException) when (!Xunit.TestContext.Current.CancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException($"The condition '{description}' didn't hold within {timeout ?? DefaultTimeout}.");
            }
        }
    }

    private static TaskCompletionSource Create() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class Observer : IObserver<DurableTaskEvents.DurableTaskEvent>
    {
        public void OnNext(DurableTaskEvents.DurableTaskEvent value) => Notify();

        public void OnCompleted() { }

        public void OnError(Exception error) { }
    }
}
