using System.Runtime.CompilerServices;
using Orleans;
using Orleans.Invocation;
using Orleans.Runtime;
using Orleans.Serialization.Invocation;

namespace CustomGrainCallReturnType;

[InvokableBaseType(
    typeof(GrainReference),
    typeof(GrainCall<>),
    typeof(GrainCallRequest<>))]
public sealed class GrainCall<T>
{
    private readonly Task<T?> _task;

    private GrainCall(Task<T?> task) => _task = task;

    public bool IsCompleted => _task.IsCompleted;

    public Task<T?> AsTask() => _task;

    public TaskAwaiter<T?> GetAwaiter() => _task.GetAwaiter();

    public static GrainCall<T> FromResult(T? value) =>
        new(Task.FromResult(value));

    public static GrainCall<T> FromTask(Task<T?> task) =>
        new(task);

    internal static GrainCall<T> FromInvocation(ValueTask<T?> invocation) =>
        new(invocation.AsTask());
}

[SerializerTransparent]
[ReturnValueProxy(nameof(InitializeRequest))]
public abstract class GrainCallRequest<T> : RequestBase
{
    [NonSerialized]
    private readonly IGrainReferenceRuntime _runtime;

    [GeneratedActivatorConstructor]
    protected GrainCallRequest(IGrainReferenceRuntime runtime) =>
        _runtime = runtime;

    public GrainCall<T> InitializeRequest(GrainReference proxy) =>
        GrainCall<T>.FromInvocation(
            _runtime.InvokeMethodAsync<T>(proxy, this, Options));

    public sealed override ValueTask<Response> Invoke()
    {
        try
        {
            return CompleteAsync(InvokeInner());
        }
        catch (Exception exception)
        {
            return ValueTask.FromResult(Response.FromException(exception));
        }
    }

    private static async ValueTask<Response> CompleteAsync(GrainCall<T> call)
    {
        try
        {
            return Response.FromResult(await call);
        }
        catch (Exception exception)
        {
            return Response.FromException(exception);
        }
    }

    protected abstract GrainCall<T> InvokeInner();
}
