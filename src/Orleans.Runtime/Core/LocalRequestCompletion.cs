using System.Reflection;
using Orleans.Serialization.Invocation;

namespace Orleans.Runtime;

/// <summary>
/// The body of a local request whose response is reported to a completion source other than the request itself.
/// </summary>
/// <remarks>
/// Message processing observes this envelope as the request, and grain call filters observe <see cref="Request"/>.
/// </remarks>
internal sealed class LocalRequestCompletion(IInvokable request, IResponseCompletionSource completion) : IInvokable, ILocalRequest
{
    public IInvokable Request { get; } = request;

    public bool Complete(Response response)
    {
        completion.Complete(response);
        return true;
    }

    public object? GetTarget() => Request.GetTarget();

    public void SetTarget(ITargetHolder holder) => Request.SetTarget(holder);

    public ValueTask<Response> Invoke() => Request.Invoke();

    public int GetArgumentCount() => Request.GetArgumentCount();

    public object? GetArgument(int index) => Request.GetArgument(index);

    public void SetArgument(int index, object? value) => Request.SetArgument(index, value!);

    public string GetMethodName() => Request.GetMethodName();

    public string GetInterfaceName() => Request.GetInterfaceName();

    public string GetActivityName() => Request.GetActivityName();

    public MethodInfo GetMethod() => Request.GetMethod();

    public Type GetInterfaceType() => Request.GetInterfaceType();

    public TimeSpan? GetDefaultResponseTimeout() => Request.GetDefaultResponseTimeout();

    public CancellationToken GetCancellationToken() => Request.GetCancellationToken();

    public bool TryCancel() => Request.TryCancel();

    public bool IsCancellable => Request.IsCancellable;

    public void Dispose() => Request.Dispose();

    public override string? ToString() => Request.ToString();
}
