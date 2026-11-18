using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Orleans.DurableJobs;

[JsonSerializable(typeof(DurableJob))]
[JsonSerializable(typeof(DurableJobShardJournalRecord))]
[JsonSerializable(typeof(DurableJobShardRemoveOperation))]
[JsonSerializable(typeof(DurableJobShardRetryOperation))]
[JsonSerializable(typeof(DurableJobShardScheduleOperation))]
[JsonSerializable(typeof(DurableJobShardSnapshot))]
[JsonSerializable(typeof(DurableJobShardSnapshotEntry))]
[JsonSerializable(typeof(Dictionary<string, string>))]
[JsonSerializable(typeof(DateTime))]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(uint))]
[JsonSerializable(typeof(ulong))]
internal sealed partial class DurableJobsJsonContext : JsonSerializerContext;
