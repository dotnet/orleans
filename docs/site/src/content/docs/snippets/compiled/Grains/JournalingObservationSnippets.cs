using System.Text;
using Orleans;
using Orleans.Journaling;

#pragma warning disable ORLEANSEXP005

namespace Documentation.Grains.Journaling.Observation;

public interface IResponseGenerator
{
    IAsyncEnumerable<string> GenerateAsync(string prompt, CancellationToken cancellationToken);
}

// <conversation_grain_interface>
public interface IConversationGrain : IGrainWithStringKey
{
    ValueTask<long> Ask(string question, CancellationToken cancellationToken);

    // Committed history after the entry with sequence number 'after'.
    IAsyncEnumerable<LogEvent<string>> WatchAsync(long after, CancellationToken cancellationToken = default);

    // Committed history, staged entries, and streaming drafts.
    IAsyncEnumerable<LogEvent<string>> WatchLiveAsync(
        long committedSequence, LogDraftId? draftId, CancellationToken cancellationToken = default);

    IAsyncEnumerable<ValueChange<string>> WatchStatusAsync(CancellationToken cancellationToken = default);
}
// </conversation_grain_interface>

// <conversation_grain>
public sealed class ConversationGrain(
    IDurableStateManager stateManager,
    IResponseGenerator generator) : Grain, IConversationGrain
{
    private readonly IDurableLog<string> _messages = stateManager.GetOrAddLog<string>("messages");
    private readonly IDurableValue<string> _status = stateManager.GetOrAddValue<string>("status");

    public async ValueTask<long> Ask(string question, CancellationToken cancellationToken)
    {
        _messages.Append(question);
        _status.Value = "responding";
        await stateManager.WriteStateAsync(cancellationToken);

        // Speculative readers receive each token while the response is generated.
        using var draft = _messages.BeginDraft();
        var response = new StringBuilder();
        try
        {
            await foreach (var token in generator.GenerateAsync(question, cancellationToken))
            {
                draft.Write(token);
                response.Append(token);
            }
        }
        catch (Exception exception)
        {
            draft.Abandon($"The response failed: {exception.Message}");
            _status.Value = "failed";
            await stateManager.WriteStateAsync(CancellationToken.None);
            throw;
        }

        var sequence = draft.Commit(response.ToString());
        _status.Value = "idle";
        await stateManager.WriteStateAsync(cancellationToken);
        return sequence;
    }

    public IAsyncEnumerable<LogEvent<string>> WatchAsync(long after, CancellationToken cancellationToken = default) =>
        _messages.ReadAsync(after + 1, cancellationToken: cancellationToken);

    public IAsyncEnumerable<LogEvent<string>> WatchLiveAsync(
        long committedSequence, LogDraftId? draftId, CancellationToken cancellationToken = default) =>
        _messages.ReadAsync(committedSequence, DurableWatchMode.Speculative, draftId, cancellationToken);

    public IAsyncEnumerable<ValueChange<string>> WatchStatusAsync(CancellationToken cancellationToken = default) =>
        _status.WatchAsync(cancellationToken: cancellationToken);
}
// </conversation_grain>

public static class ConversationClient
{
    // <conversation_client>
    public static async Task RenderAsync(IConversationGrain conversation, CancellationToken cancellationToken)
    {
        var committedSequence = 0L;
        var drafts = new Dictionary<LogDraftId, StringBuilder>();
        await foreach (var logEvent in conversation.WatchLiveAsync(committedSequence, draftId: null, cancellationToken))
        {
            switch (logEvent.Kind)
            {
                case LogEventKind.Entry:
                    Console.WriteLine($"[{logEvent.Sequence}] {logEvent.Item} ({logEvent.Status})");
                    break;
                case LogEventKind.Watermark:
                    // Resume from here after a disconnection.
                    committedSequence = logEvent.Sequence;
                    break;
                case LogEventKind.Reset:
                    Console.WriteLine($"History restarts at {logEvent.Sequence}.");
                    break;
                case LogEventKind.DraftItem:
                    var draft = logEvent.Sequence == 0
                        ? drafts[logEvent.DraftId!.Value] = new StringBuilder()
                        : drafts[logEvent.DraftId!.Value];
                    draft.Append(logEvent.Item);
                    break;
                case LogEventKind.DraftCommitted:
                case LogEventKind.DraftAbandoned:
                    drafts.Remove(logEvent.DraftId!.Value);
                    break;
            }
        }
    }
    // </conversation_client>
}

public static class StateDiagnostics
{
    // <state_discovery>
    public static IEnumerable<string> DescribeState(IJournaledStateManager manager)
    {
        foreach (var (name, stateMachine) in manager.GetStateMachines())
        {
            yield return $"{name}: {stateMachine.GetType().Name}";
        }
    }
    // </state_discovery>
}
