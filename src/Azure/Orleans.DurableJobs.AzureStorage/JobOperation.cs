using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using Orleans.Runtime;

namespace Orleans.DurableJobs.AzureStorage;

/// <summary>
/// Represents an operation to be performed on a durable job.
/// </summary>
internal struct JobOperation
{
    /// <summary>
    /// The type of operation to perform.
    /// </summary>
    public enum OperationType
    {
        Add,
        Remove,
        Retry,
    }

    /// <summary>
    /// Gets or sets the type of operation.
    /// </summary>
    public OperationType Type { get; init; }

    /// <summary>
    /// Gets or sets the job identifier.
    /// </summary>
    public string Id { get; init; }

    /// <summary>
    /// Gets or sets the job name (only used for Add operations).
    /// </summary>
    public string? Name { get; init; }

    /// <summary>
    /// Gets or sets the due time (used for Add and Retry operations).
    /// </summary>
    public DateTimeOffset? DueTime { get; init; }

    /// <summary>
    /// Gets or sets the target grain ID (only used for Add operations).
    /// </summary>
    public GrainId? TargetGrainId { get; init; }

    /// <summary>
    /// Gets or sets the job metadata (only used for Add operations).
    /// </summary>
    public IReadOnlyDictionary<string, string>? Metadata { get; init; }

    /// <summary>
    /// Creates an Add operation for scheduling a new job.
    /// </summary>
    /// <param name="id">The job identifier.</param>
    /// <param name="name">The job name.</param>
    /// <param name="dueTime">The job due time.</param>
    /// <param name="targetGrainId">The target grain ID.</param>
    /// <param name="metadata">The job metadata.</param>
    /// <returns>A new JobOperation for adding a job.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="id"/> or <paramref name="name"/> is null or empty.</exception>
    public static JobOperation CreateAddOperation(string id, string name, DateTimeOffset dueTime, GrainId targetGrainId, IReadOnlyDictionary<string, string>? metadata)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        ArgumentException.ThrowIfNullOrEmpty(name);

        return new() { Type = OperationType.Add, Id = id, Name = name, DueTime = dueTime, TargetGrainId = targetGrainId, Metadata = metadata };
    }

    /// <summary>
    /// Creates a Remove operation for canceling a job.
    /// </summary>
    /// <param name="id">The job identifier.</param>
    /// <returns>A new JobOperation for removing a job.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="id"/> is null or empty.</exception>
    public static JobOperation CreateRemoveOperation(string id)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);

        return new() { Type = OperationType.Remove, Id = id };
    }

    /// <summary>
    /// Creates a Retry operation for rescheduling a job.
    /// </summary>
    /// <param name="id">The job identifier.</param>
    /// <param name="dueTime">The new due time.</param>
    /// <returns>A new JobOperation for retrying a job.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="id"/> is null or empty.</exception>
    public static JobOperation CreateRetryOperation(string id, DateTimeOffset dueTime)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);

        return new() { Type = OperationType.Retry, Id = id, DueTime = dueTime };
    }
}

/// <summary>
/// JSON serialization context for JobOperation with compile-time source generation.
/// </summary>
[JsonSerializable(typeof(JobOperation))]
[JsonSourceGenerationOptions(
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = false)]
internal partial class JobOperationJsonContext : JsonSerializerContext
{
}