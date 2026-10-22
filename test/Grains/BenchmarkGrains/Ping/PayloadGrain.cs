using BenchmarkGrainInterfaces.Ping;

namespace BenchmarkGrains.Ping;

public sealed class PayloadGrain : Grain, IPayloadGrain
{
    public ValueTask Run(byte[] payload) => default;
}
