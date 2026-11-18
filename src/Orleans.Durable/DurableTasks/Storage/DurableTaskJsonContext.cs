using System.Text.Json.Serialization;
using Orleans.DurableTasks.Runtime;
using Orleans.Messaging;

namespace Orleans.DurableTasks.Storage;

/// <summary>
/// Source-generated JSON metadata for the task state and pending invocations which the adapter stores in JSON journals,
/// and for the task addresses which grains store in their own journaled state.
/// </summary>
[JsonSerializable(typeof(DurableTaskState))]
[JsonSerializable(typeof(PendingInvocation))]
[JsonSerializable(typeof(DurableTaskAddress))]
[JsonSerializable(typeof(MessageId))]
[JsonSerializable(typeof(DateTimeOffset))]
[JsonSerializable(typeof(byte[]))]
internal sealed partial class DurableTaskJsonContext : JsonSerializerContext;
