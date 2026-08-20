#nullable enable
using System;
using System.Collections.Generic;
using Orleans.DurableTasks;
using Orleans.Runtime;
using Orleans.Serialization.Cloning;

namespace Orleans.DurableTasks.Protocol;

[GenerateSerializer]
[Alias("DurableTaskRequestContext")]
public class DurableTaskRequestContext
{
    [Id(0)]
    public GrainId CallerId { get; set; }

    [Id(1)]
    public GrainId TargetId { get; set; }

    // TODO: Use a specialized collection type which allows for late materialization when deserialized.
    [Id(2)]
    public Dictionary<string, byte[]>? Values { get; set; }

    [Id(3)]
    public bool SupportsDurableCompletion { get; set; }
}

[RegisterConverter, RegisterCopier]
internal sealed class DurableTaskPopulator : IConverter<DurableTask, DurableTaskSurrogate>, IPopulator<DurableTask, DurableTaskSurrogate>, IBaseCopier<DurableTask>
{
    public void DeepCopy(DurableTask input, DurableTask output, CopyContext context)
    {
        EnsureRequest(input);
        EnsureRequest(output);
    }

    public void Populate(in DurableTaskSurrogate surrogate, DurableTask value)
    {
        EnsureRequest(value);
    }

    DurableTask IConverter<DurableTask, DurableTaskSurrogate>.ConvertFromSurrogate(in DurableTaskSurrogate surrogate)
        => throw GetUnsupportedSerializationException();

    DurableTaskSurrogate IConverter<DurableTask, DurableTaskSurrogate>.ConvertToSurrogate(in DurableTask value)
    {
        EnsureRequest(value);
        return default;
    }

    private static void EnsureRequest(DurableTask value)
    {
        if (value is not IDurableTaskRequest)
        {
            throw GetUnsupportedSerializationException();
        }
    }

    private static NotSupportedException GetUnsupportedSerializationException() =>
        new("DurableTask values cannot be serialized directly. Only generated durable grain-call requests are serializable.");
}

[RegisterConverter, RegisterCopier]
internal sealed class DurableTaskPopulator<T> : IConverter<DurableTask<T>, DurableTaskSurrogate>, IPopulator<DurableTask<T>, DurableTaskSurrogate>, IBaseCopier<DurableTask<T>>
{
    public void DeepCopy(DurableTask<T> input, DurableTask<T> output, CopyContext context)
    {
        EnsureRequest(input);
        EnsureRequest(output);
    }

    public void Populate(in DurableTaskSurrogate surrogate, DurableTask<T> value)
    {
        EnsureRequest(value);
    }

    DurableTask<T> IConverter<DurableTask<T>, DurableTaskSurrogate>.ConvertFromSurrogate(in DurableTaskSurrogate surrogate)
        => throw GetUnsupportedSerializationException();

    DurableTaskSurrogate IConverter<DurableTask<T>, DurableTaskSurrogate>.ConvertToSurrogate(in DurableTask<T> value)
    {
        EnsureRequest(value);
        return default;
    }

    private static void EnsureRequest(DurableTask<T> value)
    {
        if (value is not IDurableTaskRequest)
        {
            throw GetUnsupportedSerializationException();
        }
    }

    private static NotSupportedException GetUnsupportedSerializationException() =>
        new("DurableTask<T> values cannot be serialized directly. Only generated durable grain-call requests are serializable.");
}

[GenerateSerializer, Immutable]
internal readonly struct DurableTaskSurrogate
{
}

[RegisterConverter]
internal sealed class TaskIdConverter : IConverter<TaskId, TaskIdSurrogate>
{
    public TaskId ConvertFromSurrogate(in TaskIdSurrogate surrogate)
    {
        ArgumentNullException.ThrowIfNullOrEmpty(surrogate.Value);
        return TaskId.Parse(surrogate.Value, provider: null);
    }

    public TaskIdSurrogate ConvertToSurrogate(in TaskId value)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(value, default);
        return new(value.ToString());
    }
}

[GenerateSerializer, Immutable]
internal readonly struct TaskIdSurrogate(string value)
{
    [Id(0)]
    public string Value { get; } = value;
}
