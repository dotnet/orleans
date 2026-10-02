using System.Threading.Tasks;
using Orleans;
using Orleans.Serialization.Invocation;
using Orleans.Serialization.Cloning;
using Orleans.Serialization.Serializers;
using Orleans.Runtime;

namespace Orleans.NativeAotSmoke;

public interface IRpcResponses : IGrainWithIntegerKey
{
    [Id(0)]
    Task<bool> Boolean();

    [Id(1)]
    ValueTask<int> Integer();

    [Id(2)]
    Task<RpcResponsePayload> Payload();

    [Id(3)]
    Task<RpcGeneratedValue<int>> Value();

    [Id(4)]
    Task<RpcResponseBox<byte>> Bytes();
}

[GenerateSerializer]
public struct RpcGeneratedValue<T>
{
    [Id(0)]
    public T Value { get; set; }
}

[GenerateSerializer]
public sealed class RpcResponseBox<T>
{
    [Id(0)]
    public T[] Value { get; set; } = null!;
}

[GenerateSerializer, Immutable]
public sealed class RpcTupleReference
{
    [Id(0)]
    public int Value { get; set; }
}

[DefaultInvokableBaseType(typeof(Task<>), typeof(TaskRequest<>))]
public abstract class RpcTupleProxyBase
{
    protected RpcTupleProxyBase(ICodecProvider provider, CopyContextPool pool)
    {
        CodecProvider = provider;
        CopyContextPool = pool;
    }

    protected ICodecProvider CodecProvider { get; }
    protected CopyContextPool CopyContextPool { get; }
    protected T GetInvokable<T>() where T : class, IInvokable, new() => new T();
    protected ValueTask<T> InvokeAsync<T>(IInvokable body) => default;
    protected ValueTask InvokeAsync(IInvokable body) => default;
    protected void Invoke(IInvokable body) { }
}

[GenerateMethodSerializers(typeof(RpcTupleProxyBase))]
public interface IRpcTupleArguments
{
    Task<int> Accept(System.Collections.Generic.List<System.Tuple<RpcTupleReference, System.DateTime>> input);
}

[GenerateSerializer]
public sealed class RpcResponsePayload
{
    [Id(0)]
    public int Value { get; set; }

    [Id(1)]
    public RpcResponsePayload Left { get; set; } = null!;

    [Id(2)]
    public RpcResponsePayload Right { get; set; } = null!;

    [Id(3)]
    public Response<RpcResponsePayload> Envelope { get; set; } = null!;
}
