namespace Orleans.DurableTasks;

public static partial class DurableTaskExtensions
{
    /// <inheritdoc cref="DurableTask.WhenAll(IEnumerable{DurableTask})"/>
    public static DurableTask WhenAll(this IEnumerable<DurableTask> tasks) => DurableTask.WhenAll(tasks);

    /// <inheritdoc cref="DurableTask.WhenAll{TResult}(IEnumerable{DurableTask{TResult}})"/>
    public static DurableTask<TResult[]> WhenAll<TResult>(this IEnumerable<DurableTask<TResult>> tasks) => DurableTask.WhenAll(tasks);

    /// <inheritdoc cref="DurableTask.WhenAny(IEnumerable{DurableTask})"/>
    public static DurableTask<int> WhenAny(this IEnumerable<DurableTask> tasks) => DurableTask.WhenAny(tasks);

    /// <inheritdoc cref="DurableTask.WhenAny{TResult}(IEnumerable{DurableTask{TResult}})"/>
    public static DurableTask<(int Index, TResult Result)> WhenAny<TResult>(this IEnumerable<DurableTask<TResult>> tasks) => DurableTask.WhenAny(tasks);

    /// <inheritdoc cref="DurableTask.Race(IEnumerable{DurableTask})"/>
    public static DurableTask<int> Race(this IEnumerable<DurableTask> tasks) => DurableTask.Race(tasks);

    /// <inheritdoc cref="DurableTask.Race{TResult}(IEnumerable{DurableTask{TResult}})"/>
    public static DurableTask<(int Index, TResult Result)> Race<TResult>(this IEnumerable<DurableTask<TResult>> tasks) => DurableTask.Race(tasks);

    /// <inheritdoc cref="DurableTask.WhenAllCompleted(IEnumerable{DurableTask})"/>
    public static DurableTask<ScheduledTask[]> WhenAllCompleted(this IEnumerable<DurableTask> tasks) => DurableTask.WhenAllCompleted(tasks);

    /// <inheritdoc cref="DurableTask.WhenAllCompleted{TResult}(IEnumerable{DurableTask{TResult}})"/>
    public static DurableTask<ScheduledTask<TResult>[]> WhenAllCompleted<TResult>(this IEnumerable<DurableTask<TResult>> tasks) => DurableTask.WhenAllCompleted(tasks);

    /// <inheritdoc cref="DurableTask.ScheduleAll(IEnumerable{DurableTask})"/>
    public static DurableTask<ScheduledTask[]> ScheduleAll(this IEnumerable<DurableTask> tasks) => DurableTask.ScheduleAll(tasks);

    /// <inheritdoc cref="DurableTask.ScheduleAll{TResult}(IEnumerable{DurableTask{TResult}})"/>
    public static DurableTask<ScheduledTask<TResult>[]> ScheduleAll<TResult>(this IEnumerable<DurableTask<TResult>> tasks) => DurableTask.ScheduleAll(tasks);

    /// <inheritdoc cref="DurableTask.WhenEach(IEnumerable{DurableTask})"/>
    public static IAsyncEnumerable<ScheduledTask> WhenEach(this IEnumerable<DurableTask> tasks) => DurableTask.WhenEach(tasks);

    /// <inheritdoc cref="DurableTask.WhenEach{TResult}(IEnumerable{DurableTask{TResult}})"/>
    public static IAsyncEnumerable<ScheduledTask<TResult>> WhenEach<TResult>(this IEnumerable<DurableTask<TResult>> tasks) => DurableTask.WhenEach(tasks);

    /// <inheritdoc cref="ScheduledTask.WhenAll(IReadOnlyList{ScheduledTask}, CancellationToken)"/>
    public static Task WhenAll(this IReadOnlyList<ScheduledTask> tasks, CancellationToken cancellationToken = default)
        => ScheduledTask.WhenAll(tasks, cancellationToken);

    /// <inheritdoc cref="ScheduledTask.WhenAll{TResult}(IReadOnlyList{ScheduledTask{TResult}}, CancellationToken)"/>
    public static Task WhenAll<TResult>(this IReadOnlyList<ScheduledTask<TResult>> tasks, CancellationToken cancellationToken = default)
        => ScheduledTask.WhenAll(tasks, cancellationToken);

    /// <inheritdoc cref="ScheduledTask.WhenAny(IReadOnlyList{ScheduledTask}, CancellationToken)"/>
    public static Task<ScheduledTask> WhenAny(this IReadOnlyList<ScheduledTask> tasks, CancellationToken cancellationToken = default)
        => ScheduledTask.WhenAny(tasks, cancellationToken);

    /// <inheritdoc cref="ScheduledTask.WhenAny{TResult}(IReadOnlyList{ScheduledTask{TResult}}, CancellationToken)"/>
    public static Task<ScheduledTask<TResult>> WhenAny<TResult>(this IReadOnlyList<ScheduledTask<TResult>> tasks, CancellationToken cancellationToken = default)
        => ScheduledTask.WhenAny(tasks, cancellationToken);

    /// <inheritdoc cref="ScheduledTask.Race(IReadOnlyList{ScheduledTask}, CancellationToken)"/>
    public static Task<ScheduledTask> Race(this IReadOnlyList<ScheduledTask> tasks, CancellationToken cancellationToken = default)
        => ScheduledTask.Race(tasks, cancellationToken);

    /// <inheritdoc cref="ScheduledTask.Race{TResult}(IReadOnlyList{ScheduledTask{TResult}}, CancellationToken)"/>
    public static Task<ScheduledTask<TResult>> Race<TResult>(this IReadOnlyList<ScheduledTask<TResult>> tasks, CancellationToken cancellationToken = default)
        => ScheduledTask.Race(tasks, cancellationToken);

    /// <inheritdoc cref="ScheduledTask.WhenAllCompleted(IReadOnlyList{ScheduledTask}, CancellationToken)"/>
    public static Task<ScheduledTask[]> WhenAllCompleted(this IReadOnlyList<ScheduledTask> tasks, CancellationToken cancellationToken = default)
        => ScheduledTask.WhenAllCompleted(tasks, cancellationToken);

    /// <inheritdoc cref="ScheduledTask.WhenAllCompleted{TResult}(IReadOnlyList{ScheduledTask{TResult}}, CancellationToken)"/>
    public static Task<ScheduledTask<TResult>[]> WhenAllCompleted<TResult>(this IReadOnlyList<ScheduledTask<TResult>> tasks, CancellationToken cancellationToken = default)
        => ScheduledTask.WhenAllCompleted(tasks, cancellationToken);

    /// <inheritdoc cref="ScheduledTask.WhenEach(IReadOnlyList{ScheduledTask})"/>
    public static IAsyncEnumerable<ScheduledTask> WhenEach(this IReadOnlyList<ScheduledTask> tasks) => ScheduledTask.WhenEach(tasks);

    /// <inheritdoc cref="ScheduledTask.WhenEach{TResult}(IReadOnlyList{ScheduledTask{TResult}})"/>
    public static IAsyncEnumerable<ScheduledTask<TResult>> WhenEach<TResult>(this IReadOnlyList<ScheduledTask<TResult>> tasks) => ScheduledTask.WhenEach(tasks);
}
