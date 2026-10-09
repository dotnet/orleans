using System.Collections.Concurrent;
using System.Linq.Expressions;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Runtime;

namespace ReleasedPeer;

public sealed record MessageSnapshot(
    string Id, string SendingSilo, string SendingGrain, string TargetGrain,
    string TargetSilo, int ForwardCount,
    string Context, string Direction, string Result, string Stage);

/// <summary>
/// Read-only observation, bound independently to each runtime's own Message type.
/// Reflection is test-only: the public shared contract has no runtime-internal dependencies.
/// </summary>
public sealed class RuntimeProbe
{
    private readonly ConcurrentDictionary<string, TaskCompletionSource<MessageSnapshot>> _waits = new();
    public ConcurrentQueue<MessageSnapshot> Messages { get; } = new();
    public Action<MessageSnapshot>? Observed { get; set; }

    public void Install(IServiceProvider services)
    {
        var runtime = Assembly.Load("Orleans.Runtime");
        var messageCenterType = runtime.GetType("Orleans.Runtime.Messaging.MessageCenter", throwOnError: true)!;
        var center = services.GetRequiredService(messageCenterType);
        var sniff = messageCenterType.GetProperty("SniffIncomingMessage")!;
        var sniffField = messageCenterType.GetField("sniffIncomingMessageHandler", BindingFlags.Instance | BindingFlags.NonPublic)!;
        sniffField.SetValue(center, Delegate.Combine(
            (Delegate?)sniff.GetValue(center), CreateObserver(sniff.PropertyType, nameof(ObserveSniff))));

        // The statistics observer runs AFTER activation.ReceiveMessage, so a request
        // event is an admission/queue barrier, not merely receipt of socket bytes.
        var observer = messageCenterType.GetField("_messageObserver", BindingFlags.Instance | BindingFlags.NonPublic);
        if (observer is not null)
        {
            observer.SetValue(center, Delegate.Combine(
                (Delegate?)observer.GetValue(center), CreateObserver(observer.FieldType, nameof(Observe))));
        }
    }

    public Task<MessageSnapshot> WaitForRequest(string context)
        => _waits.GetOrAdd(context, _ => new(TaskCreationOptions.RunContinuationsAsynchronously)).Task;

    private Delegate CreateObserver(Type delegateType, string method)
    {
        var argument = Expression.Parameter(delegateType.GenericTypeArguments[0], "message");
        return Expression.Lambda(delegateType,
            Expression.Call(Expression.Constant(this), method, Type.EmptyTypes, Expression.Convert(argument, typeof(object))),
            argument).Compile();
    }

    public void ObserveSniff(object message) => ObserveCore(message, "received");

    public void Observe(object message) => ObserveCore(message, "admitted");

    private void ObserveCore(object message, string stage)
    {
        var type = message.GetType();
        object? Get(string name) => type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(message);
        string Text(string name) => Get(name)?.ToString() ?? "";
        string Address(string name) => (Get(name) as SiloAddress)?.ToParsableString() ?? "";
        var context = Get("RequestContextData") as IDictionary<string, object>;
        var marker = context?.TryGetValue(ProbeControl.ContextKey, out var value) == true ? value as string ?? "" : "";
        if (marker.Length == 0)
        {
            return;
        }

        var snapshot = new MessageSnapshot(
            Text("Id"), Address("SendingSilo"), Text("SendingGrain"), Text("TargetGrain"),
            Address("TargetSilo"), (int)(Get("ForwardCount") ?? 0),
            marker, Text("Direction"), Text("Result"), stage);
        Messages.Enqueue(snapshot);
        Observed?.Invoke(snapshot);
        if (snapshot.Direction == "Request" && stage == "admitted")
        {
            _waits.GetOrAdd(marker, _ => new(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult(snapshot);
        }
    }
}
