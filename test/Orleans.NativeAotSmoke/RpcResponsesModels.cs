using System.Threading.Tasks;
using Orleans;
using Orleans.Serialization.Invocation;

namespace Orleans.NativeAotSmoke;

public interface IRpcResponses : IGrainWithIntegerKey
{
    [Id(0)]
    Task<bool> Boolean();

    [Id(1)]
    ValueTask<int> Integer();

    [Id(2)]
    Task<RpcResponsePayload> Payload();
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
