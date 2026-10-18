using System.Text;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.DependencyInjection;
using Orleans;
using Orleans.Hosting;
using Orleans.Journaling;
using Orleans.Journaling.Json;
using Orleans.TestingHost;

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

    // Ends the conversation. Every watch ends after the final entry.
    ValueTask End(CancellationToken cancellationToken);

    // Committed history after the entry with sequence number 'after'.
    IAsyncEnumerable<LogEvent<string>> WatchAsync(long after, CancellationToken cancellationToken = default);

    // Committed history and streaming drafts.
    IAsyncEnumerable<LogEvent<string>> WatchLiveAsync(
        long nextSequence, LogDraftId? draftId, CancellationToken cancellationToken = default);

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

        // Readers which include drafts receive each token while the response is generated.
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

        // The committed entry replaces the draft's tokens.
        var sequence = draft.Commit(response.ToString());
        _status.Value = "idle";
        await stateManager.WriteStateAsync(cancellationToken);
        return sequence;
    }

    public async ValueTask End(CancellationToken cancellationToken)
    {
        _messages.Seal();
        _status.Value = "ended";
        await stateManager.WriteStateAsync(cancellationToken);
    }

    public IAsyncEnumerable<LogEvent<string>> WatchAsync(long after, CancellationToken cancellationToken = default) =>
        _messages.ReadAsync(after + 1, cancellationToken: cancellationToken);

    public IAsyncEnumerable<LogEvent<string>> WatchLiveAsync(
        long nextSequence, LogDraftId? draftId, CancellationToken cancellationToken = default) =>
        _messages.ReadAsync(nextSequence, DurableWatchMode.Committed | DurableWatchMode.Drafts, draftId, cancellationToken);

    public IAsyncEnumerable<ValueChange<string>> WatchStatusAsync(CancellationToken cancellationToken = default) =>
        _status.WatchAsync(cancellationToken: cancellationToken);
}
// </conversation_grain>

public static class ConversationClient
{
    // <conversation_client>
    public static async Task RenderAsync(IConversationGrain conversation, CancellationToken cancellationToken)
    {
        // Every entry is durable, so the position after the latest entry is a valid resume point.
        var nextSequence = 0L;
        var history = new SortedList<long, string>();
        var drafts = new Dictionary<LogDraftId, StringBuilder>();
        await foreach (var logEvent in conversation.WatchLiveAsync(nextSequence, draftId: null, cancellationToken))
        {
            switch (logEvent.Kind)
            {
                case LogEventKind.Entry:
                    history[logEvent.Sequence] = logEvent.Item!;
                    nextSequence = logEvent.Sequence + 1;
                    Console.WriteLine($"[{logEvent.Sequence}] {logEvent.Item}");
                    break;
                case LogEventKind.CaughtUp:
                    Console.WriteLine($"Loaded {history.Count} messages.");
                    break;
                case LogEventKind.Trimmed:
                    // Older entries were pruned. Later entries remain valid.
                    while (history.Count > 0 && history.Keys[0] < logEvent.Sequence)
                    {
                        history.RemoveAt(0);
                    }

                    nextSequence = Math.Max(nextSequence, logEvent.Sequence);
                    break;
                case LogEventKind.Reset:
                    // The history was replaced, for example by a compacted summary.
                    history.Clear();
                    nextSequence = logEvent.Sequence;
                    break;
                case LogEventKind.DraftItem:
                    var draft = logEvent.Sequence == 0
                        ? drafts[logEvent.DraftId!.Value] = new StringBuilder()
                        : drafts[logEvent.DraftId!.Value];
                    draft.Append(logEvent.Item);
                    break;
                case LogEventKind.DraftCommitted:
                    // The draft's tokens are superseded by the entries in [Sequence, Sequence + Count).
                    drafts.Remove(logEvent.DraftId!.Value);
                    break;
                case LogEventKind.DraftAbandoned:
                    drafts.Remove(logEvent.DraftId!.Value);
                    Console.WriteLine($"Response abandoned ({logEvent.AbandonReason}): {logEvent.Message}");
                    break;
                case LogEventKind.Sealed:
                    Console.WriteLine("The conversation ended.");
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

public sealed class FixedResponseGenerator(params string[] tokens) : IResponseGenerator
{
    public async IAsyncEnumerable<string> GenerateAsync(string prompt, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        foreach (var token in tokens)
        {
            await Task.Yield();
            yield return token;
        }
    }
}

public static class ConversationGrainTests
{
    // <journaling_test_cluster>
    public static async Task AskCommitsTheResponseAndSurvivesReactivation()
    {
        var builder = new InProcessTestClusterBuilder(initialSilosCount: 1);
        builder.ConfigureSilo((options, siloBuilder) =>
        {
            // Real journaled state managers, with journals held in silo memory. Reflection-based JSON metadata
            // suits tests; production silos register source-generated metadata.
            siloBuilder.AddJournaling()
                .UseJsonJournalFormat(new DefaultJsonTypeInfoResolver())
                .AddVolatileJournalStorage();
            siloBuilder.Services.AddSingleton<IResponseGenerator>(new FixedResponseGenerator("Hello", ", world"));
        });

        await using var cluster = builder.Build();
        await cluster.DeployAsync();
        var conversation = cluster.Client.GetGrain<IConversationGrain>("test");
        var sequence = await conversation.Ask("Hi", CancellationToken.None);

        // A new activation recovers the conversation from the journal.
        await cluster.DeactivateAsync(conversation);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var history = new List<string>();
        await foreach (var logEvent in conversation.WatchAsync(after: -1, timeout.Token))
        {
            if (logEvent.Kind is LogEventKind.Entry)
            {
                history.Add(logEvent.Item!);
            }
            else if (logEvent.Kind is LogEventKind.CaughtUp)
            {
                break;
            }
        }

        if (sequence != 1 || !history.SequenceEqual(["Hi", "Hello, world"]))
        {
            throw new InvalidOperationException("The conversation did not record the committed response.");
        }
    }
    // </journaling_test_cluster>
}
