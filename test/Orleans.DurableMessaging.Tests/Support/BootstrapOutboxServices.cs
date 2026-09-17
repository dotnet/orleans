using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Orleans.Journaling;
using Orleans.Runtime;

namespace Orleans.DurableMessaging.Tests.Support;

internal static class BootstrapOutboxServices
{
    public const string JobName = "orleans.messaging.outbox-flush";
    public const string StateName = "__orleans.durable-messaging.outbox";
    public const string ObserverName = "__orleans.durable-messaging.outbox-observer";

    public static void Add(IServiceCollection services)
    {
        var outboxType = ReceiverTestServices.GetImplementationType("DurableOutbox");
        var endpointType = ReceiverTestServices.GetImplementationType("DurableMessagingJournalEndpoint");
        services.RemoveAll<IDurableOutbox>();
        services.TryAddScoped(outboxType);
        services.AddScoped<IDurableOutbox>(provider => (IDurableOutbox)provider.GetRequiredService(outboxType));
        services.RemoveAllKeyed(endpointType, ObserverName);
        services.AddKeyedScoped(endpointType, ObserverName, (provider, _) =>
        {
            var outbox = provider.GetRequiredService<IDurableOutbox>();
            if (provider.GetRequiredService<IGrainContext>().GrainInstance is FailingBootstrapGrain)
            {
                var observation = provider.GetRequiredService<BootstrapObservation>();
                observation.Manager = provider.GetRequiredService<IJournaledStateManager>();
                observation.Inbox = provider.GetRequiredService<IDurableInbox>();
                observation.Outbox = outbox;
                observation.Extension = provider.GetRequiredService(ReceiverTestServices.GetImplementationType("DurableInboxExtension"));
                observation.ExpectedFailure = new IOException("Expected bootstrap endpoint construction failure.");
                throw observation.ExpectedFailure;
            }
            var finalize = outboxType.GetMethod("FinalizeWrite")!.CreateDelegate<Action<CancellationToken>>(outbox);
            return ActivatorUtilities.CreateInstance(provider, endpointType, (IJournaledStateObserver)outbox, finalize);
        });
    }
}

public sealed class BootstrapDeliveryProbe : IOutgoingGrainCallFilter
{
    private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ConcurrentDictionary<GrainId, TaskCompletionSource<(Guid MessageId, int Value)>> _outputs = new();

    public async Task Invoke(IOutgoingGrainCallContext context)
    {
        if (context.TargetId == BootstrapState.OutputTarget && context.MethodName == nameof(IDurableInboxExtension.DeliverAsync))
        {
            await _release.Task.WaitAsync(context.Request.GetCancellationToken());
        }
        await context.Invoke();
    }

    public void Release() => _release.TrySetResult();
    public Task<(Guid MessageId, int Value)> WaitForOutputAsync(GrainId sender) =>
        GetOutput(sender).Task.WaitAsync(TimeSpan.FromSeconds(30));
    public void OnOutput(GrainId sender, Guid messageId, int value) => GetOutput(sender).TrySetResult((messageId, value));
    private TaskCompletionSource<(Guid MessageId, int Value)> GetOutput(GrainId sender) =>
        _outputs.GetOrAdd(sender, static _ => new(TaskCreationOptions.RunContinuationsAsynchronously));
}

public interface IBootstrapOutputGrain : IGrainWithStringKey
{
    Task<int> GetMessageCountAsync();
}

[GrainType("bootstrap-output")]
public sealed class BootstrapOutputGrain : Grain, IBootstrapOutputGrain, IDurableMessagingGrain, IInboxHandler, IJournaledStateObserver
{
    private readonly IDurableDictionary<Guid, int> _values;
    private readonly BootstrapDeliveryProbe _probe;
    private (GrainId Sender, Guid MessageId, int Value)? _committing;

    public BootstrapOutputGrain(IJournaledStateManager manager, IDurableInbox inbox,
        [FromKeyedServices("bootstrap-output-values")] IDurableDictionary<Guid, int> values, BootstrapDeliveryProbe probe)
    {
        _values = values;
        _probe = probe;
        inbox.RegisterHandler("output", this);
        manager.RegisterObserver(this);
    }

    public bool CanHandle(IInboxHandlerContext context) => context.Envelope.RouteKey == "output";
    public ValueTask HandleAsync(IInboxHandlerContext context, CancellationToken cancellationToken)
    {
        if (!context.Envelope.Data.TryGetBody<int>(out var value))
        {
            throw new InvalidOperationException("The bootstrap output must contain an integer.");
        }
        _values.Add(context.Envelope.MessageId, value);
        _committing = (context.Envelope.SenderId, context.Envelope.MessageId, value);
        return default;
    }

    public Task<int> GetMessageCountAsync() => Task.FromResult(_values.Count);
    public void OnWriteStarted() { }
    public void OnRecoveryCompleted() { }
    public void OnWriteCompleted()
    {
        if (_committing is { } output)
        {
            _committing = null;
            _probe.OnOutput(output.Sender, output.MessageId, output.Value);
        }
    }
}
