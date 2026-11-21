namespace Orleans.Durable.Messaging;

/// <summary>
/// Keeps every inbox and outbox of a silo which registers it at its recorded state, for the replay host of
/// <c>Microsoft.Orleans.Durable.Testing</c>.
/// </summary>
/// <remarks>
/// Recovery charges no interrupted inbox attempt, compacts no dead letters, and consumes no clean-stop marker. The pump
/// dispatches no inbox message and starts no outbox delivery, and the activation records no clean stop. A recovered
/// message whose due time has passed, including one with neither a delivery time nor a retry time and an outbox entry
/// which no attempt has delivered yet, stays pending without an attempt.
/// </remarks>
internal sealed class MessagingReplayHold;
