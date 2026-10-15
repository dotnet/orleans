using System.Buffers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Orleans.DurableJobs;
using Orleans.Hosting;
using Orleans.Journaling;
using Orleans.Messaging;

namespace Microsoft.Orleans.DurableTasks.IntegrationTests.FaultInjection;

/// <summary>An infrastructure failure which a test injected.</summary>
/// <remarks>
/// Its message starts with <see cref="MessagePrefix"/>, so a test can assert that no injected failure reaches application
/// code or a task's recorded result.
/// </remarks>
public sealed class InjectedFaultException(string message) : Exception(MessagePrefix + message)
{
    public const string MessagePrefix = "Injected fault: ";
}

/// <summary>Where a fault is injected.</summary>
public enum FaultPoint
{
    /// <summary>A journal write fails before it reaches storage.</summary>
    JournalWriteBeforeDurable,

    /// <summary>A journal write reaches storage, and its acknowledgement is lost: an ambiguous commit.</summary>
    JournalWriteAfterDurable,

    /// <summary>A journal read fails, which fails the activation which reads it.</summary>
    JournalRead,

    /// <summary>An outbox delivery call fails before it reaches the receiver.</summary>
    DeliveryThrows,

    /// <summary>An outbox delivery call reaches the receiver, which processes it, and its response is lost.</summary>
    DeliveryResponseLost,

    /// <summary>An outbox delivery call doesn't return until the test releases it, and then times out.</summary>
    DeliveryHangs,

    /// <summary>A sink registered ahead of the durable task sink throws when the inbox offers it a message.</summary>
    InboxSinkThrows,

    /// <summary>A handler registered ahead of the durable task handler throws for a message the inbox dispatches.</summary>
    InboxHandlerThrows,

    /// <summary>A Durable Jobs wake-up of the messaging pump fails before it reaches the grain.</summary>
    JobWakeupFails,

    /// <summary>An outbox delivery call returned or failed; counted only, to observe delivery attempts.</summary>
    DeliveryCompleted,

    /// <summary>
    /// The receiver admits a delivery call and holds it, before the inbox sees the message, until the test releases it.
    /// </summary>
    DeliveryHeldAtReceiver,
}

/// <summary>One armed fault: it fires on the <see cref="Occurrence"/>th operation which matches it, counting from zero.</summary>
public sealed class InjectedFault
{
    private readonly TaskCompletionSource _fired = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly List<(int Count, TaskCompletionSource Completion)> _matchWaiters = [];
    private int _matches;

    internal InjectedFault(FaultPoint point, GrainId target, string? subject, int occurrence)
    {
        Point = point;
        Target = target;
        TargetKey = target.ToString();
        Subject = subject;
        Occurrence = occurrence;
    }

    public FaultPoint Point { get; }

    /// <summary>Gets the grain whose journal, inbox, or wake-up the fault targets; for deliveries, the receiver.</summary>
    public GrainId Target { get; }

    /// <summary>Gets the message subject which the fault matches, or <see langword="null"/> for every message.</summary>
    public string? Subject { get; }

    /// <summary>Gets the matching operation on which the fault fires, or <see cref="Every"/>.</summary>
    public int Occurrence { get; }

    /// <summary>The occurrence of a fault which fires on every matching operation.</summary>
    public const int Every = -1;

    /// <summary>Gets the number of operations which matched the fault so far, including the one which fired it.</summary>
    public int Matches
    {
        get
        {
            lock (_matchWaiters)
            {
                return _matches;
            }
        }
    }

    public bool HasFired => _fired.Task.IsCompleted;

    public Task Fired => _fired.Task;

    /// <summary>Gets the target's grain ID string, which is also its journal ID.</summary>
    internal string TargetKey { get; }

    internal Task Released => _released.Task;

    /// <summary>Releases a <see cref="FaultPoint.DeliveryHangs"/> fault's call, which then times out.</summary>
    public void Release() => _released.TrySetResult();

    /// <summary>Returns a task which completes once <paramref name="count"/> operations matched the fault.</summary>
    public Task WaitForMatchesAsync(int count)
    {
        lock (_matchWaiters)
        {
            if (_matches >= count)
            {
                return Task.CompletedTask;
            }

            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _matchWaiters.Add((count, completion));
            return completion.Task;
        }
    }

    /// <summary>Counts a matching operation and returns whether the fault fires for it.</summary>
    internal bool OnMatch()
    {
        int matches;
        lock (_matchWaiters)
        {
            matches = ++_matches;
            foreach (var waiter in _matchWaiters.Where(waiter => waiter.Count <= matches).ToList())
            {
                _matchWaiters.Remove(waiter);
                waiter.Completion.TrySetResult();
            }
        }

        if (Occurrence != Every && matches - 1 != Occurrence)
        {
            return false;
        }

        _fired.TrySetResult();
        return true;
    }

    public override string ToString() => $"{Point} #{Occurrence} on {Target}{(Subject is null ? "" : $" ({Subject})")}";
}

/// <summary>
/// Injects infrastructure faults into journal storage, outbox delivery, inbox sinks and handlers, and Durable Jobs wake-ups
/// of the grains which a test targets.
/// </summary>
/// <remarks>
/// Each fault fires once, on the operation which it counts to, so a test enumerates the fault points of a run by their
/// occurrence indexes. A counter is a fault which never fires. The journal decorator wraps the silo's journal storage, the
/// grain call filter intercepts outbox deliveries and messaging wake-ups, and grains register a
/// <see cref="FaultInjectingInboxParticipant"/> ahead of the durable task runtime's sink and handler.
/// </remarks>
public sealed class DurableTaskFaultInjector
{
    private readonly List<InjectedFault> _faults = [];

    /// <summary>Registers the injector's journal storage decorator, grain call filter, and services on a silo.</summary>
    /// <remarks>Call after the silo's journal storage provider is registered.</remarks>
    public void Configure(ISiloBuilder siloBuilder)
    {
        siloBuilder.Services.AddSingleton(this);
        var inner = siloBuilder.Services.Last(static descriptor => descriptor.ServiceType == typeof(IJournalStorageProvider));
        var innerFactory = inner.ImplementationFactory
            ?? throw new InvalidOperationException("The journal storage provider must be registered with a factory.");
        siloBuilder.Services.RemoveAll<IJournalStorageProvider>();
        siloBuilder.Services.AddSingleton<IJournalStorageProvider>(services =>
            new FaultInjectingJournalStorageProvider((IJournalStorageProvider)innerFactory(services), this));
        siloBuilder.AddOutgoingGrainCallFilter(new FaultInjectingCallFilter(this));
        siloBuilder.AddIncomingGrainCallFilter(new FaultInjectingReceiverFilter(this));
    }

    /// <summary>Arms a fault which fires on the <paramref name="occurrence"/>th matching operation, counting from zero.</summary>
    public InjectedFault Arm(FaultPoint point, GrainId target, int occurrence, string? subject = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(occurrence, InjectedFault.Every);
        return Add(new(point, target, subject, occurrence));
    }

    /// <summary>Counts the operations of a fault point on a grain, without failing any of them.</summary>
    public InjectedFault Count(FaultPoint point, GrainId target, string? subject = null) =>
        Add(new(point, target, subject, int.MaxValue));

    /// <summary>Removes every armed fault and counter, and releases hung calls.</summary>
    public void Reset()
    {
        lock (_faults)
        {
            foreach (var fault in _faults)
            {
                fault.Release();
            }

            _faults.Clear();
        }
    }

    /// <summary>Counts an operation on <paramref name="targetKey"/> and returns the fault which fires for it, if any.</summary>
    internal InjectedFault? OnOperation(FaultPoint point, string targetKey, string? subject = null)
    {
        InjectedFault? firing = null;
        lock (_faults)
        {
            foreach (var fault in _faults)
            {
                if (fault.Point == point
                    && string.Equals(fault.TargetKey, targetKey, StringComparison.Ordinal)
                    && (fault.Subject is null || string.Equals(fault.Subject, subject, StringComparison.Ordinal))
                    && fault.OnMatch())
                {
                    firing ??= fault;
                }
            }
        }

        return firing;
    }

    private InjectedFault Add(InjectedFault fault)
    {
        lock (_faults)
        {
            _faults.Add(fault);
        }

        return fault;
    }

    private sealed class FaultInjectingJournalStorageProvider(IJournalStorageProvider inner, DurableTaskFaultInjector injector)
        : IJournalStorageProvider, IJournalStorageCatalog
    {
        public IJournalStorage CreateStorage(JournalId journalId) =>
            new FaultInjectingJournalStorage(inner.CreateStorage(journalId), journalId.Value, injector);

        public IAsyncEnumerable<JournalCatalogEntry> ListAsync(JournalCatalogListOptions? options = null, CancellationToken cancellationToken = default) =>
            ((IJournalStorageCatalog)inner).ListAsync(options, cancellationToken);
    }

    private sealed class FaultInjectingJournalStorage(IJournalStorage inner, string journalKey, DurableTaskFaultInjector injector)
        : IJournalStorage
    {
        public bool IsCompactionRequested => inner.IsCompactionRequested;

        public ValueTask ReadAsync(IJournalStorageConsumer consumer, CancellationToken cancellationToken)
        {
            if (injector.OnOperation(FaultPoint.JournalRead, journalKey) is { } fault)
            {
                throw new InjectedFaultException($"journal read failed ({fault}).");
            }

            return inner.ReadAsync(consumer, cancellationToken);
        }

        public ValueTask<bool> CreateIfNotExistsAsync(IReadOnlyDictionary<string, string>? metadata = null, CancellationToken cancellationToken = default) =>
            inner.CreateIfNotExistsAsync(metadata, cancellationToken);

        public ValueTask<IJournalMetadata?> GetMetadataAsync(CancellationToken cancellationToken = default) =>
            inner.GetMetadataAsync(cancellationToken);

        public ValueTask<IJournalMetadata?> UpdateMetadataAsync(
            IReadOnlyDictionary<string, string>? set = null,
            IEnumerable<string>? remove = null,
            string? expectedETag = null,
            CancellationToken cancellationToken = default) =>
            inner.UpdateMetadataAsync(set, remove, expectedETag, cancellationToken);

        public ValueTask ReplaceAsync(ReadOnlySequence<byte> value, CancellationToken cancellationToken) =>
            WriteAsync(static (storage, value, cancellationToken) => storage.ReplaceAsync(value, cancellationToken), value, cancellationToken);

        public ValueTask AppendAsync(ReadOnlySequence<byte> value, CancellationToken cancellationToken) =>
            WriteAsync(static (storage, value, cancellationToken) => storage.AppendAsync(value, cancellationToken), value, cancellationToken);

        public ValueTask DeleteAsync(CancellationToken cancellationToken) =>
            WriteAsync(static (storage, _, cancellationToken) => storage.DeleteAsync(cancellationToken), default, cancellationToken);

        private async ValueTask WriteAsync(
            Func<IJournalStorage, ReadOnlySequence<byte>, CancellationToken, ValueTask> write,
            ReadOnlySequence<byte> value,
            CancellationToken cancellationToken)
        {
            // Both write faults count the same operations, so an occurrence index names the same write in either mode.
            var before = injector.OnOperation(FaultPoint.JournalWriteBeforeDurable, journalKey);
            var after = injector.OnOperation(FaultPoint.JournalWriteAfterDurable, journalKey);
            if (before is not null)
            {
                throw new InjectedFaultException($"journal write failed before it was durable ({before}).");
            }

            await write(inner, value, cancellationToken);
            if (after is not null)
            {
                throw new InjectedFaultException($"journal write was durable, and its acknowledgement was lost ({after}).");
            }
        }
    }

    private sealed class FaultInjectingCallFilter(DurableTaskFaultInjector injector) : IOutgoingGrainCallFilter
    {
        private const string MessagingPumpJobName = "orleans.messaging.pump";

        public Task Invoke(IOutgoingGrainCallContext context)
        {
            var declaringType = context.InterfaceMethod?.DeclaringType;
            if (declaringType == typeof(IMessagingExtension) && context.Request.GetArgument(0) is InboxMessage message)
            {
                return InvokeDeliveryAsync(context, message);
            }

            if (declaringType?.Name == "IDurableJobReceiverExtension"
                && context.Request.GetArgument(0) is IJobRunContext { Job.Name: MessagingPumpJobName }
                && injector.OnOperation(FaultPoint.JobWakeupFails, context.TargetId.ToString()) is { } wakeup)
            {
                return Task.FromException(new InjectedFaultException($"the messaging wake-up failed ({wakeup})."));
            }

            return context.Invoke();
        }

        private async Task InvokeDeliveryAsync(IOutgoingGrainCallContext context, InboxMessage message)
        {
            var target = context.TargetId.ToString();
            var subject = message.Envelope.Subject;
            var thrown = injector.OnOperation(FaultPoint.DeliveryThrows, target, subject);
            var lost = injector.OnOperation(FaultPoint.DeliveryResponseLost, target, subject);
            var hung = injector.OnOperation(FaultPoint.DeliveryHangs, target, subject);
            if (thrown is not null)
            {
                throw new InjectedFaultException($"the delivery call failed ({thrown}).");
            }

            if (hung is not null)
            {
                var cancellationToken = context.Request.GetArgument(1) is CancellationToken token ? token : CancellationToken.None;
                await hung.Released.WaitAsync(cancellationToken);
                throw new TimeoutException($"{InjectedFaultException.MessagePrefix}the delivery call timed out ({hung}).");
            }

            try
            {
                await context.Invoke();
            }
            finally
            {
                injector.OnOperation(FaultPoint.DeliveryCompleted, target, subject);
            }

            if (lost is not null)
            {
                throw new InjectedFaultException($"the delivery response was lost after the receiver processed it ({lost}).");
            }
        }
    }
}

/// <summary>Holds admitted delivery calls at their receiver.</summary>
internal sealed class FaultInjectingReceiverFilter(DurableTaskFaultInjector injector) : IIncomingGrainCallFilter
{
    public async Task Invoke(IIncomingGrainCallContext context)
    {
        if (context.InterfaceMethod?.DeclaringType == typeof(IMessagingExtension)
            && context.Request.GetArgument(0) is InboxMessage message
            && injector.OnOperation(FaultPoint.DeliveryHeldAtReceiver, context.TargetId.ToString(), message.Envelope.Subject) is { } hold)
        {
            await hold.Released;
        }

        await context.Invoke();
    }
}

/// <summary>
/// A sink and a handler which a grain registers ahead of the durable task runtime's, so the injector can fail the inbox's
/// offer or dispatch of a message; otherwise both decline it.
/// </summary>
/// <remarks>The inbox calls <see cref="CanHandle"/> once for each dispatch, immediately before the chosen handler.</remarks>
public sealed class FaultInjectingInboxParticipant(DurableTaskFaultInjector injector, IGrainContext grainContext)
    : IInboxMessageSink, IInboxHandler
{
    private InjectedFault? _dispatchFault;

    public bool TryAccept(InboxMessage message, out DeliveryResult result)
    {
        result = default;
        if (injector.OnOperation(FaultPoint.InboxSinkThrows, grainContext.GrainId.ToString(), message.Envelope.Subject) is { } fault)
        {
            throw new InjectedFaultException($"the inbox sink failed ({fault}).");
        }

        return false;
    }

    public bool CanHandle(IInboxHandlerContext context)
    {
        _dispatchFault = injector.OnOperation(FaultPoint.InboxHandlerThrows, grainContext.GrainId.ToString(), context.Message.Envelope.Subject);
        return _dispatchFault is not null;
    }

    public ValueTask HandleAsync(IInboxHandlerContext context, CancellationToken cancellationToken) =>
        throw new InjectedFaultException($"the inbox handler failed ({_dispatchFault}).");
}
