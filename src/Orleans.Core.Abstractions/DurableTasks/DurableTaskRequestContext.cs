#nullable enable
using System;
using System.Collections.Generic;
using System.Distributed.DurableTasks;
using System.Linq;
using Orleans.Runtime;
using Orleans.Serialization;
using Orleans.Serialization.Cloning;

namespace Orleans.DurableTasks;

[GenerateSerializer]
[Alias("DurableTaskRequestContext")]
public class DurableTaskRequestContext
{
    internal const int MaxEntryCount = 32;
    internal const int MaxKeyLength = 256;
    internal const int MaxSerializedValueLength = 64 * 1024;
    internal const int MaxSerializedTotalLength = 256 * 1024;
    private const string DurableJobTurnIsolationKey = "Orleans.DurableJobs.TurnIsolation";

    [Id(0)]
    public GrainId CallerId { get; set; }

    [Id(1)]
    public GrainId TargetId { get; set; }

    /// <summary>
    /// Gets or sets the scheduling-time Orleans request context values, serialized using the configured Orleans serializer.
    /// </summary>
    /// <remarks>
    /// At most 32 entries are retained. Keys are limited to 256 characters, each serialized value to 64 KiB,
    /// and all serialized values together to 256 KiB. Scheduling fails instead of silently dropping entries
    /// when an application value cannot be serialized or a limit is exceeded. Orleans framework-reserved
    /// values, including call-chain reentrancy and ping markers, are intentionally excluded because they are
    /// scoped to the original request and must not be replayed after recovery. Restoration retains the current
    /// activation's turn-isolation marker while replacing application entries with the persisted snapshot.
    /// </remarks>
    [Id(2)]
    public Dictionary<string, byte[]>? Values { get; set; }

    internal static Dictionary<string, byte[]>? CaptureRequestContext(Serializer serializer)
    {
        var entries = RequestContext.Entries
            .Where(static entry => !IsReservedKey(entry.Key))
            .ToArray();
        if (entries.Length == 0)
        {
            return null;
        }

        if (entries.Length > MaxEntryCount)
        {
            throw new InvalidOperationException($"The durable task request context exceeds the limit of {MaxEntryCount} entries.");
        }

        var totalLength = 0;
        var result = new Dictionary<string, byte[]>(entries.Length, StringComparer.Ordinal);
        foreach (var (key, value) in entries)
        {
            ValidateKey(key);
            byte[] serializedValue;
            try
            {
                serializedValue = serializer.SerializeToArray<object?>(value);
            }
            catch (Exception exception)
            {
                throw new InvalidOperationException(
                    $"Request context value '{key}' could not be serialized for durable execution.",
                    exception);
            }

            if (serializedValue.Length > MaxSerializedValueLength)
            {
                throw new InvalidOperationException(
                    $"Serialized request context value '{key}' exceeds the {MaxSerializedValueLength}-byte limit.");
            }

            totalLength = checked(totalLength + serializedValue.Length);
            if (totalLength > MaxSerializedTotalLength)
            {
                throw new InvalidOperationException(
                    $"The serialized durable task request context exceeds the {MaxSerializedTotalLength}-byte total limit.");
            }

            result.Add(key, serializedValue);
        }

        return result;
    }

    internal IDisposable RestoreRequestContext(Serializer serializer)
    {
        Validate();
        var previous = RequestContext.Entries.ToArray();
        RequestContext.Clear();
        try
        {
            foreach (var (key, value) in previous)
            {
                if (string.Equals(key, DurableJobTurnIsolationKey, StringComparison.Ordinal))
                {
                    RequestContext.Set(key, value);
                    break;
                }
            }

            if (Values is not null)
            {
                foreach (var (key, serializedValue) in Values)
                {
                    if (IsReservedKey(key))
                    {
                        continue;
                    }

                    var value = serializer.Deserialize<object>(serializedValue)
                        ?? throw new InvalidOperationException($"Request context value '{key}' deserialized to null.");
                    RequestContext.Set(key, value);
                }
            }
        }
        catch
        {
            Restore(previous);
            throw;
        }

        return new RequestContextScope(previous);
    }

    internal void Validate()
    {
        if (Values is not { } values)
        {
            return;
        }

        if (values.Count > MaxEntryCount)
        {
            throw new InvalidOperationException($"The durable task request context exceeds the limit of {MaxEntryCount} entries.");
        }

        var totalLength = 0;
        foreach (var (key, value) in values)
        {
            ValidateKey(key);
            ArgumentNullException.ThrowIfNull(value);
            if (value.Length > MaxSerializedValueLength)
            {
                throw new InvalidOperationException(
                    $"Serialized request context value '{key}' exceeds the {MaxSerializedValueLength}-byte limit.");
            }

            totalLength = checked(totalLength + value.Length);
            if (totalLength > MaxSerializedTotalLength)
            {
                throw new InvalidOperationException(
                    $"The serialized durable task request context exceeds the {MaxSerializedTotalLength}-byte total limit.");
            }
        }
    }

    internal bool HasEquivalentApplicationValues(DurableTaskRequestContext other)
    {
        ArgumentNullException.ThrowIfNull(other);
        Validate();
        other.Validate();

        var left = Values?.Where(static pair => !IsReservedKey(pair.Key)).ToArray() ?? [];
        var right = other.Values?.Where(static pair => !IsReservedKey(pair.Key)).ToArray() ?? [];
        if (left.Length != right.Length)
        {
            return false;
        }

        foreach (var (key, value) in left)
        {
            if (other.Values is null
                || !other.Values.TryGetValue(key, out var otherValue)
                || !value.AsSpan().SequenceEqual(otherValue))
            {
                return false;
            }
        }

        return true;
    }

    private static void ValidateKey(string key)
    {
        if (string.IsNullOrEmpty(key) || key.Length > MaxKeyLength)
        {
            throw new InvalidOperationException(
                $"Durable task request context keys must contain between 1 and {MaxKeyLength} characters.");
        }
    }

    private static bool IsReservedKey(string key) =>
        string.Equals(key, RequestContext.CALL_CHAIN_REENTRANCY_HEADER, StringComparison.Ordinal)
        || string.Equals(key, RequestContext.PING_APPLICATION_HEADER, StringComparison.Ordinal)
        || string.Equals(key, DurableJobTurnIsolationKey, StringComparison.Ordinal);

    private static void Restore(KeyValuePair<string, object>[] values)
    {
        RequestContext.Clear();
        foreach (var (key, value) in values)
        {
            RequestContext.Set(key, value);
        }
    }

    private sealed class RequestContextScope(KeyValuePair<string, object>[] previous) : IDisposable
    {
        public void Dispose() => Restore(previous);
    }
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
