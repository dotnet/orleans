using System;

namespace Orleans.DurableTasks.Runtime;

/// <summary>
/// Describes how an attempt diverged from its execution's record: it requested another operation under a recorded
/// identifier, or a recorded outcome no longer decodes.
/// </summary>
/// <remarks>
/// The attempt's context stalls the attempt with <see cref="DurableTaskAttemptCancellationReason.Diverged"/> and this
/// exception as the inner exception of the <see cref="DurableTaskAttemptCanceledException"/> which the operation throws.
/// </remarks>
internal sealed class DurableTaskDivergenceException(string message, Exception? innerException = null)
    : InvalidOperationException(message, innerException);
