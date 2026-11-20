using System.Runtime.CompilerServices;

namespace Orleans.DurableTasks.Abstractions.Tests;

/// <summary>
/// Calls the host members of <see cref="DurableTaskContext"/>, which hosts reach through derivation, for tests which act as
/// a context's host.
/// </summary>
internal static class DurableTaskContextHostExtensions
{
    public static void RequestTaskCancellation(this DurableTaskContext context, string? reason) => RequestTaskCancellationCore(context, reason);

    public static void CancelAttempt(this DurableTaskContext context, DurableTaskAttemptCancellationReason reason) => CancelAttemptCore(context, reason);

    public static bool IsAttemptCanceled(this DurableTaskContext context) => GetIsAttemptCanceled(context);

    public static DurableTaskAttemptCancellationReason? AttemptCancellationReason(this DurableTaskContext context) => GetAttemptCancellationReason(context);

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "RequestTaskCancellation")]
    private static extern void RequestTaskCancellationCore(DurableTaskContext context, string? reason);

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "CancelAttempt")]
    private static extern void CancelAttemptCore(DurableTaskContext context, DurableTaskAttemptCancellationReason reason);

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_IsAttemptCanceled")]
    private static extern bool GetIsAttemptCanceled(DurableTaskContext context);

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_AttemptCancellationReason")]
    private static extern DurableTaskAttemptCancellationReason? GetAttemptCancellationReason(DurableTaskContext context);
}
