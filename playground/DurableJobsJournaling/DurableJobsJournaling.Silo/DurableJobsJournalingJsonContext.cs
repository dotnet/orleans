using System.Text.Json.Serialization;
using DurableJobsJournaling.Abstractions;

namespace DurableJobsJournaling.Silo;

[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(ulong))]
[JsonSerializable(typeof(DateTime))]
[JsonSerializable(typeof(DateTimeOffset))]
[JsonSerializable(typeof(LoadSettings))]
[JsonSerializable(typeof(WorkflowStartRequest))]
[JsonSerializable(typeof(WorkflowState))]
[JsonSerializable(typeof(WorkflowStageRecord))]
[JsonSerializable(typeof(ChildTaskResult))]
[JsonSerializable(typeof(WorkflowResult))]
internal sealed partial class DurableJobsJournalingJsonContext : JsonSerializerContext;
