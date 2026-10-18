using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Orleans.Messaging;

/// <summary>Contains a command identity, payload, subject, optional application headers, and an optional delivery time.</summary>
/// <remarks>
/// Message identity and payload are required; an empty payload is valid. Raw protocols can omit
/// subject and headers. Prepare the payload and headers before publishing a message and keep them
/// unchanged afterward. Struct assignment shares these objects; ordinary Orleans deep copying
/// and deserialization produce independent payload arrays, header dictionaries, and header value arrays.
/// </remarks>
[GenerateSerializer, Alias("Orleans.Messaging.Envelope")]
public readonly struct Envelope
{
    /// <summary>Gets the exact application-defined command identity within the receiving inbox.</summary>
    [Id(0)]
    public required MessageId MessageId { get; init; }

    /// <summary>Gets the required application payload bytes, including an empty payload.</summary>
    [Id(1)]
    public required byte[] Payload { get; init; }

    /// <summary>Gets the optional, nonempty ordinal application protocol subject.</summary>
    [Id(2)]
    public string? Subject { get; init; }

    /// <summary>Gets optional application-defined named byte-array headers.</summary>
    /// <remarks>Use an ordinal dictionary for case-sensitive header names. Keys are nonempty, well-formed UTF-16 strings, and arrays are non-null.</remarks>
    [Id(3)]
    public Dictionary<string, byte[]>? Headers { get; init; }

    /// <summary>Gets the optional earliest time at which the receiving inbox processes the message.</summary>
    /// <remarks>
    /// <para>
    /// The outbox delivers the message immediately. The receiving inbox accepts, deduplicates, and persists it
    /// as usual, then holds it until this time is reached on the Durable Jobs clock, and one durable wake-up
    /// resumes the grain when the message becomes due. <see langword="null"/> processes the message as soon as
    /// its acceptance is durable.
    /// </para>
    /// <para>
    /// The time is part of the command: reusing a pending message ID with a different value is a conflicting command.
    /// A message with a value does not count toward the inbox <c>MaxCapacity</c>, including after it becomes due.
    /// </para>
    /// </remarks>
    [Id(4)]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTimeOffset? DeliverAt { get; init; }
}
