using System.Collections.Generic;

namespace Orleans.Messaging;

/// <summary>Contains a command identity, payload, subject, and optional application headers.</summary>
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
    public required HierarchicalKey MessageId { get; init; }

    /// <summary>Gets the required application payload bytes, including an empty payload.</summary>
    [Id(1)]
    public required byte[] Payload { get; init; }

    /// <summary>Gets the optional, nonempty ordinal application protocol subject.</summary>
    [Id(2)]
    public string? Subject { get; init; }

    /// <summary>Gets optional application-defined named byte-array headers.</summary>
    /// <remarks>Use an ordinal dictionary for case-sensitive header names. Keys are nonempty and arrays are non-null.</remarks>
    [Id(3)]
    public Dictionary<string, byte[]>? Headers { get; init; }
}
