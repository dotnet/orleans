using System;
using Orleans.Serialization;

namespace Orleans.DurableMessaging;

[GenerateSerializer, Alias("Orleans.DurableMessaging.InboxMessageState")]
internal sealed class InboxMessageState
{
    [Id(0)]
    public int AttemptCount { get; set; }

    [Id(1)]
    public DateTimeOffset? NextAttemptAt { get; set; }

    [Id(2)]
    public string? LastError { get; set; }

}

[GenerateSerializer, Alias("Orleans.DurableMessaging.OutboxMessageState")]
internal sealed class OutboxMessageState
{
    [Id(0)]
    public int AttemptCount { get; set; }

    [Id(1)]
    public DateTimeOffset? NextAttemptAt { get; set; }

    [Id(2)]
    public string? LastError { get; set; }

    [Id(3)]
    public DateTimeOffset? EnqueuedAt { get; set; }
}

[GenerateSerializer, Alias("Orleans.DurableMessaging.InboxDeadLetter")]
internal sealed class InboxDeadLetter
{
    [Id(0)]
    public required DurableEnvelope Envelope { get; init; }

    [Id(1)]
    public required DateTimeOffset DeadLetteredAt { get; init; }

    [Id(2)]
    public required string Reason { get; init; }

    [Id(3)]
    public int AttemptCount { get; init; }
}

[GenerateSerializer, Alias("Orleans.DurableMessaging.OutboxDeadLetter")]
internal sealed class OutboxDeadLetter
{
    [Id(0)]
    public required DurableEnvelope Envelope { get; init; }

    [Id(1)]
    public required DateTimeOffset DeadLetteredAt { get; init; }

    [Id(2)]
    public required string Reason { get; init; }

    [Id(3)]
    public int AttemptCount { get; init; }
}
