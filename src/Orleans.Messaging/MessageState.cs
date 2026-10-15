using System;

namespace Orleans.Messaging;

[GenerateSerializer, Alias("Orleans.Messaging.InboxMessageState")]
internal sealed class InboxMessageState
{
    [Id(0)]
    public int AttemptCount { get; set; }

    [Id(1)]
    public DateTimeOffset? NextAttemptAt { get; set; }

    [Id(2)]
    public string? LastError { get; set; }

    /// <summary>Gets or sets when a failing message's retries end, <c>InboxRetryTimeout</c> after its first failure.</summary>
    [Id(3)]
    public DateTimeOffset? ExpirationTime { get; set; }
}

[GenerateSerializer, Alias("Orleans.Messaging.InboxDeadLetter")]
internal sealed class InboxDeadLetter
{
    [Id(0)]
    public required InboxMessage Message { get; init; }

    [Id(1)]
    public required DateTimeOffset DeadLetteredAt { get; init; }

    [Id(2)]
    public required string Reason { get; init; }

    [Id(3)]
    public int AttemptCount { get; init; }
}
