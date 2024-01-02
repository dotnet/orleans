#nullable enable
using System;
using System.Collections.Generic;
using System.Distributed.DurableTasks;
using Orleans.Runtime;
using Orleans.Serialization.Cloning;

namespace Orleans.DurableTasks;

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
}

[RegisterConverter, RegisterCopier]
internal sealed class DurableTaskPopulator : IConverter<DurableTask, DurableTaskSurrogate>, IPopulator<DurableTask, DurableTaskSurrogate>, IBaseCopier<DurableTask>
{
    public void DeepCopy(DurableTask input, DurableTask output, CopyContext context)
    {
        // No-op
    }

    public void Populate(in DurableTaskSurrogate surrogate, DurableTask value)
    {
        // No-op
    }

    DurableTask IConverter<DurableTask, DurableTaskSurrogate>.ConvertFromSurrogate(in DurableTaskSurrogate surrogate)
    {
        // Populator will be used instead.
        throw new NotImplementedException();
    }

    DurableTaskSurrogate IConverter<DurableTask, DurableTaskSurrogate>.ConvertToSurrogate(in DurableTask value)
    {
        return default;
    }
}

[RegisterConverter, RegisterCopier]
internal sealed class DurableTaskPopulator<T> : IConverter<DurableTask<T>, DurableTaskSurrogate>, IPopulator<DurableTask<T>, DurableTaskSurrogate>, IBaseCopier<DurableTask<T>>
{
    public void DeepCopy(DurableTask<T> input, DurableTask<T> output, CopyContext context)
    {
        // No-op
    }

    public void Populate(in DurableTaskSurrogate surrogate, DurableTask<T> value)
    {
        // No-op
    }

    DurableTask<T> IConverter<DurableTask<T>, DurableTaskSurrogate>.ConvertFromSurrogate(in DurableTaskSurrogate surrogate)
    {
        // Populator will be used instead.
        throw new NotImplementedException();
    }

    DurableTaskSurrogate IConverter<DurableTask<T>, DurableTaskSurrogate>.ConvertToSurrogate(in DurableTask<T> value)
    {
        return default;
    }
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

