namespace Orleans.DurableTasks.Analyzers.Tests;

/// <summary>
/// Declares the durable task programming model, grain state, and signals by the metadata names which the analyzers match:
/// <c>DurableTask.Current</c> returning <c>DurableTaskContext</c> with its cancellation members, <c>DurableTask.Once</c>,
/// <c>IsReplaying</c>, <c>ReplaySafe()</c>, the journaled state types, and grains.
/// </summary>
internal static class DurableTaskStubs
{
    public const string Source = """
        #nullable enable
        using System;
        using System.Collections.Generic;
        using System.Runtime.CompilerServices;
        using System.Threading;
        using System.Threading.Tasks;
        using Microsoft.Extensions.Logging;

        namespace Orleans.DurableTasks
        {
            [AsyncMethodBuilder(typeof(DurableTaskMethodBuilder))]
            public abstract class DurableTask
            {
                public static DurableTaskContext Current => null!;
                public static DurableTask Delay(TimeSpan duration) => null!;
                public static DurableTask DelayUntil(DateTimeOffset dueTime) => null!;
                public static DurableTask<T> FromResult<T>(T value) => null!;
                public static DurableTask Run(Action<CancellationToken> action) => null!;
                public static DurableTask<T> Run<T>(Func<CancellationToken, T> function) => null!;
                public static DurableTask Run(Func<CancellationToken, Task> function) => null!;
                public static DurableTask<T> Run<T>(Func<CancellationToken, Task<T>> function) => null!;
                public static DurableTask Run(Func<Task> function) => null!;
                public static DurableTask<T> Run<T>(Func<Task<T>> function) => null!;
                public static T Once<T>(string name, Func<T> effect) => effect();
                public static void Once(string name, Action effect) => effect();
                public static T Once<T>(Func<T> effect) => effect();
                public static void Once(Action effect) => effect();
                public static int GetVersion(string name, int minSupported, int maxSupported) => maxSupported;
                public static DurableTask<T[]> WhenAll<T>(IEnumerable<DurableTask<T>> inputs) => null!;
                public static DurableTask WhenAll(IEnumerable<DurableTask> inputs) => null!;
                public static DurableTask<int> WhenAny(IEnumerable<DurableTask> inputs) => null!;
                public static IAsyncEnumerable<ScheduledTask> WhenEach(IEnumerable<DurableTask> inputs) => null!;
                public static DurableTask<(int Index, T Result)> Race<T>(IReadOnlyList<DurableTask<T>> inputs) => null!;
            }

            [AsyncMethodBuilder(typeof(DurableTaskMethodBuilder<>))]
            public abstract class DurableTask<TResult> : DurableTask
            {
            }

            public abstract class DurableTaskContext
            {
                public abstract DateTimeOffset UtcNow { get; }
                public bool IsTaskCancellationRequested => false;
                public string? TaskCancellationReason => null;
                public CancellationToken CancellationToken => default;
                public CancellationToken AttemptCancellationToken => default;
                public virtual bool IsReplaying => false;
            }

            public enum DurableTaskAttemptCancellationReason
            {
                ActivationStopping,
                TaskAborted,
                TaskDeleted,
            }

            public sealed class DurableTaskAttemptCanceledException : OperationCanceledException
            {
                public DurableTaskAttemptCancellationReason Reason { get; }
            }

            public readonly struct ConfiguredDurableTask
            {
                public DurableTaskAwaiter GetAwaiter() => default;
                public ConfiguredDurableTask WithId(string name) => default;
                public ConfiguredDurableTask Detached() => default;
                public ConfiguredDurableTask Shielded() => default;
                public Task<ScheduledTask> ScheduleAsync(CancellationToken cancellationToken = default) => null!;
                public ScheduledTask Post() => null!;
            }

            public readonly struct ConfiguredDurableTask<TResult>
            {
                public DurableTaskAwaiter<TResult> GetAwaiter() => default;
                public ConfiguredDurableTask<TResult> WithId(string name) => default;
                public ConfiguredDurableTask<TResult> Detached() => default;
                public ConfiguredDurableTask<TResult> Shielded() => default;
                public Task<ScheduledTask<TResult>> ScheduleAsync(CancellationToken cancellationToken = default) => null!;
                public ScheduledTask<TResult> Post() => null!;
            }

            public readonly struct DurableTaskAwaiter : ICriticalNotifyCompletion
            {
                public bool IsCompleted => true;
                public void GetResult() { }
                public void OnCompleted(Action continuation) { }
                public void UnsafeOnCompleted(Action continuation) { }
            }

            public readonly struct DurableTaskAwaiter<TResult> : ICriticalNotifyCompletion
            {
                public bool IsCompleted => true;
                public TResult GetResult() => default!;
                public void OnCompleted(Action continuation) { }
                public void UnsafeOnCompleted(Action continuation) { }
            }

            public static class DurableTaskExtensions
            {
                public static DurableTaskAwaiter GetAwaiter(this DurableTask task) => default;
                public static DurableTaskAwaiter<TResult> GetAwaiter<TResult>(this DurableTask<TResult> task) => default;
                public static ConfiguredDurableTask WithId(this DurableTask task, string name) => default;
                public static ConfiguredDurableTask<TResult> WithId<TResult>(this DurableTask<TResult> task, string name) => default;
                public static ConfiguredDurableTask Detached(this DurableTask task) => default;
                public static ConfiguredDurableTask<TResult> Detached<TResult>(this DurableTask<TResult> task) => default;
                public static ConfiguredDurableTask Shielded(this DurableTask task) => default;
                public static ConfiguredDurableTask<TResult> Shielded<TResult>(this DurableTask<TResult> task) => default;
                public static Task<ScheduledTask> ScheduleAsync(this DurableTask task, CancellationToken cancellationToken = default) => null!;
                public static ScheduledTask Post(this DurableTask task) => null!;
                public static ScheduledTask<TResult> Post<TResult>(this DurableTask<TResult> task) => null!;
                public static DurableTask<TResult> WaitAsync<TResult>(this DurableTask<TResult> task, TimeSpan timeout) => null!;
            }

            public abstract class ScheduledTask
            {
            }

            public abstract class ScheduledTask<TResult> : ScheduledTask
            {
                public Task<TResult> WaitUntilAsync(DateTimeOffset dueTime, CancellationToken cancellationToken = default) => null!;
            }

            public sealed class DurableSignal<T>
            {
                public DurableSignal(string name) { }
                public DurableSignal<T> For(string suffix) => this;
                public DurableTask<T> WaitAsync() => null!;
            }

            public sealed class DurableVersion
            {
                public DurableVersion(string name, int minSupported, int maxSupported) { }
                public int Get() => 0;
                public bool IsAtLeast(int version) => true;
            }

            public static class DurableTaskLoggerExtensions
            {
                public static ILogger ReplaySafe(this ILogger logger) => logger;
            }

            public struct DurableTaskMethodBuilder
            {
                public static DurableTaskMethodBuilder Create() => default;
                public DurableTask Task => null!;
                public void Start<TStateMachine>(ref TStateMachine stateMachine) where TStateMachine : IAsyncStateMachine { }
                public void SetStateMachine(IAsyncStateMachine stateMachine) { }
                public void SetResult() { }
                public void SetException(Exception exception) { }
                public void AwaitOnCompleted<TAwaiter, TStateMachine>(ref TAwaiter awaiter, ref TStateMachine stateMachine) where TAwaiter : INotifyCompletion where TStateMachine : IAsyncStateMachine { }
                public void AwaitUnsafeOnCompleted<TAwaiter, TStateMachine>(ref TAwaiter awaiter, ref TStateMachine stateMachine) where TAwaiter : ICriticalNotifyCompletion where TStateMachine : IAsyncStateMachine { }
            }

            public struct DurableTaskMethodBuilder<TResult>
            {
                public static DurableTaskMethodBuilder<TResult> Create() => default;
                public DurableTask<TResult> Task => null!;
                public void Start<TStateMachine>(ref TStateMachine stateMachine) where TStateMachine : IAsyncStateMachine { }
                public void SetStateMachine(IAsyncStateMachine stateMachine) { }
                public void SetResult(TResult result) { }
                public void SetException(Exception exception) { }
                public void AwaitOnCompleted<TAwaiter, TStateMachine>(ref TAwaiter awaiter, ref TStateMachine stateMachine) where TAwaiter : INotifyCompletion where TStateMachine : IAsyncStateMachine { }
                public void AwaitUnsafeOnCompleted<TAwaiter, TStateMachine>(ref TAwaiter awaiter, ref TStateMachine stateMachine) where TAwaiter : ICriticalNotifyCompletion where TStateMachine : IAsyncStateMachine { }
            }
        }

        namespace Orleans
        {
            public interface IGrainBase
            {
            }

            public abstract class Grain : IGrainBase
            {
            }
        }

        namespace Orleans.Journaling
        {
            public interface IDurableValue<T>
            {
                T? Value { get; set; }
            }

            public interface IDurableDictionary<TKey, TValue> : IDictionary<TKey, TValue>
            {
            }

            public interface IDurableList<T> : IList<T>
            {
                void AddRange(IEnumerable<T> collection);
            }

            public interface IDurableQueue<T> : IReadOnlyCollection<T>
            {
                void Enqueue(T item);

                T Dequeue();
            }

            public interface IDurableSet<T> : ISet<T>
            {
            }

            public interface IDurableTaskCompletionSource<T>
            {
                bool TrySetResult(T value);
            }

            public interface IDurableLog<T> : IReadOnlyCollection<T>
            {
                long Append(T item);

                void TrimBefore(long sequence);

                void Clear();
            }
        }

        namespace Orleans.Runtime
        {
            public interface IPersistentState<TState>
            {
                TState State { get; set; }

                Task WriteStateAsync();
            }
        }
        """;
}
