using System.Threading;

namespace Orleans.Journaling;

/// <summary>
/// Records whether one asynchronous flow staged a change in a journaled state manager.
/// </summary>
/// <remarks>
/// A scope covers the flow of the async method which begins it, including that method's callees and continuations, and
/// ends when the method returns. Other flows, such as interleaved grain requests, stage changes outside the scope. The
/// manager captures, writes, and recovers state in its own flow, so those operations stage nothing in any scope.
/// </remarks>
internal sealed class StagedChangeScope
{
    private static readonly AsyncLocal<StagedChangeScope?> Current = new();
    private readonly JournalBufferWriter _writer;
    private bool _staged;

    private StagedChangeScope(JournalBufferWriter writer) => _writer = writer;

    /// <summary>Gets whether the flow staged a change.</summary>
    internal bool HasStagedChanges => _staged;

    internal static StagedChangeScope Begin(JournalBufferWriter writer) => Current.Value = new(writer);

    internal static void OnStaged(JournalBufferWriter writer)
    {
        if (Current.Value is { } scope && ReferenceEquals(scope._writer, writer)) scope._staged = true;
    }
}